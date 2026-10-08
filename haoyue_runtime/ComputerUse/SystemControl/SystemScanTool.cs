using System.Text;
using System.Text.Json.Nodes;
using Haoyue.Runtime.Prompts;
using Haoyue.Runtime.Tools;
using Haoyue.Runtime.Tools.Builtin;

namespace Haoyue.Runtime.ComputerUse.SystemControl;

/// <summary>
/// computer_scan: hardware inventory (via the platform adapter) and filesystem
/// structure scanning (pure C#, cross-platform). Scan results locate targets for
/// follow-up actions executed through computer_exec or the file tools.
/// Filesystem walking is permission-tolerant: unreadable directories are skipped
/// and reported instead of failing the whole scan.
/// </summary>
public sealed class SystemScanTool(IPromptProvider prompts, ISystemControlAdapter adapter) : BuiltinTool(prompts)
{
    public override string Name => "computer_scan";
    public override string StatusLabel => "Scanning system";
    public override bool Mutating => false;
    public override bool RequiresWorkspace => false;

    public override JsonObject ParameterSchema => ToolSchema.Object(
        ("target", ToolSchema.String("Scan target: 'hardware' (CPU/memory/GPU/disks/network) or 'filesystem' (directory structure)", "hardware", "filesystem"), true),
        ("path", ToolSchema.String("Absolute path to scan (filesystem target only; defaults to the user profile)"), false),
        ("max_depth", ToolSchema.Integer("Maximum directory depth for filesystem scans (default 3, max 10)"), false),
        ("max_entries", ToolSchema.Integer("Maximum entries returned for filesystem scans (default 500, max 5000)"), false),
        ("show_hidden", ToolSchema.Boolean("Include hidden/system entries in filesystem scans (default false)"), false));

    public override async Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolContext context, CancellationToken ct)
    {
        var target = GetString(arguments, "target")?.Trim().ToLowerInvariant();
        switch (target)
        {
            case "hardware":
            {
                var report = await adapter.ScanHardwareAsync(ct).ConfigureAwait(false);
                context.Events.Publish(new Haoyue.Runtime.Events.ComputerActionEvent(
                    Action: "scan_hardware", X: null, Y: null,
                    Target: adapter.PlatformName, Success: true, Error: null, Base64Screenshot: null));
                return ToolResult.Ok($"[平台] {adapter.PlatformName}\n{report}", $"Scanned hardware ({adapter.PlatformName})");
            }

            case "filesystem":
            {
                var path = GetString(arguments, "path");
                var maxDepth = Math.Clamp(GetInt(arguments, "max_depth") ?? 3, 1, 10);
                var maxEntries = Math.Clamp(GetInt(arguments, "max_entries") ?? 500, 1, 5000);
                var showHidden = GetBool(arguments, "show_hidden");

                try
                {
                    var result = ScanFilesystem(path, maxDepth, maxEntries, showHidden);
                    context.Events.Publish(new Haoyue.Runtime.Events.ComputerActionEvent(
                        Action: "scan_filesystem", X: null, Y: null,
                        Target: result.RootPath, Success: true, Error: null, Base64Screenshot: null));
                    return ToolResult.Ok(result.Report, $"Scanned {result.RootPath}");
                }
                catch (Exception ex) when (ex is DirectoryNotFoundException or ArgumentException)
                {
                    return ToolResult.Fail(ex.Message);
                }
            }

            default:
                return ToolResult.Fail("target 必须是 'hardware' 或 'filesystem'。");
        }
    }

    internal static (string RootPath, string Report) ScanFilesystem(string? path, int maxDepth, int maxEntries, bool showHidden)
    {
        var root = string.IsNullOrWhiteSpace(path)
            ? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
            : Path.GetFullPath(path);

        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"路径不存在: {root}");

        var sb = new StringBuilder();
        var stats = (Dirs: 0, Files: 0, Bytes: 0L, Skipped: 0);
        var largest = new List<(string Path, long Bytes)>();
        var entries = 0;
        var truncated = false;

        sb.AppendLine($"[根目录] {root}");
        sb.AppendLine($"[深度上限] {maxDepth}  [条目上限] {maxEntries}  [隐藏项] {(showHidden ? "包含" : "跳过")}");

        WalkDirectory(new DirectoryInfo(root), 0);

        if (stats.Skipped > 0)
            sb.AppendLine($"\n[权限不足跳过] {stats.Skipped} 个目录（需要更高权限读取）");
        if (truncated)
            sb.AppendLine($"[截断] 已达条目上限 {maxEntries}，可调大 max_entries 或缩小范围后重新扫描");

        sb.AppendLine($"\n[汇总] 目录 {stats.Dirs} 个，文件 {stats.Files} 个，共 {FormatBytes(stats.Bytes)}");
        if (largest.Count > 0)
        {
            sb.AppendLine("[最大的文件]");
            foreach (var (file, bytes) in largest.OrderByDescending(x => x.Bytes).Take(10))
                sb.AppendLine($"  {FormatBytes(bytes).PadLeft(10)}  {file}");
        }

        return (root, sb.ToString());

        void WalkDirectory(DirectoryInfo dir, int depth)
        {
            if (truncated || entries >= maxEntries) { truncated |= entries >= maxEntries; return; }

            IEnumerable<FileSystemInfo> children;
            try
            {
                children = dir.EnumerateFileSystemInfos();
            }
            catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
            {
                stats.Skipped++;
                return;
            }

            foreach (var entry in children)
            {
                if (truncated || entries >= maxEntries) { truncated |= entries >= maxEntries; break; }

                var isHidden = IsHidden(entry);
                if (isHidden && !showHidden) continue;

                entries++;
                var indent = new string(' ', depth * 2);

                if (entry is DirectoryInfo sub)
                {
                    stats.Dirs++;
                    sb.AppendLine($"{indent}{sub.Name}/");
                    if (depth + 1 < maxDepth)
                        WalkDirectory(sub, depth + 1);
                }
                else
                {
                    long bytes = 0;
                    try { bytes = ((FileInfo)entry).Length; } catch (Exception) { /* unreadable size: report 0 */ }
                    stats.Files++;
                    stats.Bytes += bytes;
                    largest.Add((entry.FullName, bytes));
                    sb.AppendLine($"{indent}{entry.Name}  ({FormatBytes(bytes)})");
                }
            }
        }
    }

    private static bool IsHidden(FileSystemInfo entry) =>
        entry.Name.StartsWith(".", StringComparison.Ordinal) ||
        (entry.Attributes & System.IO.FileAttributes.Hidden) != 0 ||
        (entry.Attributes & System.IO.FileAttributes.System) != 0;

    internal static string FormatBytes(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):F2} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):F1} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):F1} KB",
        _ => $"{bytes} B",
    };
}
