using System.Diagnostics;

namespace Haoyue.Runtime.Providers;

/// <summary>一次 GPU 探测结果（MB 计）。Name 用于日志展示，容量用于装载档位计算。</summary>
public sealed record GpuInfo(string Name, long TotalMib, long FreeMib);

/// <summary>
/// NVIDIA GPU 能力探测（P2）。通过 nvidia-smi 查询型号与显存总量/可用量——这是 CUDA
/// 后端真实可用的显存口径（已含系统占用），比 WMI 的适配器容量更贴近装载决策需要。
/// 结果缓存 5 秒：装载决策不需要更高频率，同时避免每次请求都拉起子进程。
/// 无 NVIDIA GPU、nvidia-smi 不存在或输出异常时返回 null——调用方按纯 CPU 档处理，
/// 绝不猜测显存。
/// </summary>
public static class GpuProbe
{
    private static readonly object Gate = new();
    private static (GpuInfo? Info, DateTimeOffset At)? _cached;

    /// <summary>缓存有效期（可用显存短时间内的漂移可忽略）。</summary>
    public static TimeSpan CacheTtl { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>nvidia-smi 可执行文件名（测试可替换探测目标）。</summary>
    public static string Executable { get; set; } = "nvidia-smi";

    public static GpuInfo? Probe()
    {
        lock (Gate)
        {
            if (_cached is { } cached && DateTimeOffset.UtcNow - cached.At < CacheTtl)
                return cached.Info;
            var info = ProbeUncached();
            _cached = (info, DateTimeOffset.UtcNow);
            return info;
        }
    }

    /// <summary>测试钩子：绕过缓存与子进程，直接解析 nvidia-smi 输出。</summary>
    public static GpuInfo? TryParse(string output)
    {
        // 预期一行：Name, TotalMiB, FreeMiB（nounits 后是裸数字）。取第一条。
        var line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault(l => l.Contains(','));
        if (line is null) return null;
        var parts = line.Split(',');
        if (parts.Length < 3) return null;

        var name = parts[0].Trim();
        if (name.Length == 0) return null;
        if (!TryMib(parts[1], out var total) || !TryMib(parts[2], out var free)) return null;
        if (total <= 0 || free < 0 || free > total) return null;
        return new GpuInfo(name, total, free);
    }

    private static bool TryMib(string text, out long mib)
    {
        mib = 0;
        var digits = new string(text.Trim().Where(char.IsDigit).ToArray());
        return digits.Length > 0 && long.TryParse(digits, out mib) && mib > 0;
    }

    private static GpuInfo? ProbeUncached()
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = Executable,
                    Arguments = "--query-gpu=name,memory.total,memory.free --format=csv,noheader,nounits",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                },
            };
            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(3000);
            return TryParse(output);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            // nvidia-smi 不存在（无 NVIDIA GPU / 驱动未装）是常态路径，静默返回 CPU 档。
            return null;
        }
    }
}
