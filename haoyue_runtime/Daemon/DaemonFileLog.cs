using System.Globalization;
using System.Text;

namespace Haoyue.Runtime.Daemon;

/// <summary>
/// daemon 自身的结构化文件日志（<c>~/.haoyue/logs/daemon.log</c>）。
///
/// 存在理由：此前 daemon.log 只是桌面宿主的旁路产物——Electron 主进程把 daemon 的
/// stdout/stderr 抄进 <c>userData/logs/daemon.log</c>（见 daemon-log.ts）。CLI 前台
/// 对话、独立 <c>haoyue daemon</c>、以及任何没有桌面的场景里，启动失败、配置被拒、
/// GPU 降级、告警事件全部零落盘，「发消息没回复」类问题只能靠猜。
///
/// 与桌面旁路的边界：桌面那份是原生输出的raw镜像；本类是 daemon 自己的结构化事件
/// （启动/停止/配置变更/告警错误），两条流互补，不互相覆盖。
///
/// 纪律：日志失败绝不能影响 daemon 本身——构造与每次写入都整体容错，失败即静默
/// 降级为禁用（<see cref="IsEnabled"/> = false）。
/// </summary>
public sealed class DaemonFileLog : IDisposable
{
    /// <summary>默认文件名（位于 <see cref="Configuration.HaoyuePaths.LogsDir"/>）。</summary>
    public const string FileName = "daemon.log";

    /// <summary>滚动后的上一代文件名。</summary>
    public const string PreviousFileName = "daemon.prev.log";

    /// <summary>单文件体积上限：8MB，与桌面旁路滚动阈值一致。</summary>
    public const long MaxBytes = 8 * 1024 * 1024;

    private readonly Lock _gate = new();
    private StreamWriter? _writer;
    private FileStream? _stream;
    private long _bytesWritten;
    private bool _disposed;

    /// <summary>打开（或创建）默认位置的 daemon 日志；任何 IO 失败都返回禁用实例。</summary>
    public static DaemonFileLog OpenDefault()
        => Open(System.IO.Path.Combine(Configuration.HaoyuePaths.LogsDir, FileName));

    /// <summary>
    /// 打开指定路径的 daemon 日志（测试 seam）。目录不存在时创建；失败返回禁用实例，
    /// 调用方无需判空——禁用的实例所有写入都是无操作。
    /// </summary>
    public static DaemonFileLog Open(string path)
    {
        var log = new DaemonFileLog();
        try
        {
            var directory = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            if (File.Exists(path) && new FileInfo(path).Length >= MaxBytes)
                RollPrevious(path);
            var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
            log._stream = stream;
            log._writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
            log._bytesWritten = stream.Length;
        }
        catch (Exception)
        {
            // 磁盘满 / 权限 / 路径非法：降级为禁用，daemon 其余功能不受影响。
            log._writer?.Dispose();
            log._stream?.Dispose();
            log._writer = null;
            log._stream = null;
        }
        return log;
    }

    /// <summary>日志是否真正落盘（打开失败时为 false）。</summary>
    public bool IsEnabled
    {
        get { lock (_gate) return _writer is not null && !_disposed; }
    }

    /// <summary>写一条信息级日志。</summary>
    public void Info(string message) => Write("INFO", message, null);

    /// <summary>写一条告警级日志。</summary>
    public void Warn(string message) => Write("WARN", message, null);

    /// <summary>写一条错误级日志（可附异常详情）。</summary>
    public void Error(string message, Exception? exception = null) => Write("ERROR", message, exception);

    /// <summary>
    /// 写一条分隔标记（启动 / 停止 / 配置重建等里程碑），与桌面旁路的 marker 风格一致。
    /// </summary>
    public void Marker(string line)
    {
        lock (_gate)
        {
            if (_disposed || _writer is null) return;
            try
            {
                WriteRaw($"\n===== {line} =====\n");
            }
            catch (Exception)
            {
                // 见类注释：日志失败绝不影响 daemon。
            }
        }
    }

    /// <summary>冲刷并关闭日志文件。</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                _writer?.Flush();
                _writer?.Dispose();
                _stream?.Dispose();
            }
            catch (Exception)
            {
                // 关闭失败没有补救手段，也不该把异常抛给调用方。
            }
            _writer = null;
            _stream = null;
        }
    }

    private void Write(string level, string message, Exception? exception)
    {
        lock (_gate)
        {
            if (_disposed || _writer is null) return;
            try
            {
                var line = new StringBuilder()
                    .Append(DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fff'Z'", CultureInfo.InvariantCulture))
                    .Append(" [").Append(level).Append("] ")
                    .Append(message.ReplaceLineEndings(" "));
                if (exception is not null)
                    line.Append(" | ").Append(exception.GetType().Name).Append(": ").Append(exception.Message);
                line.Append('\n');
                RollIfNeeded(line.Length);
                WriteRaw(line.ToString());
            }
            catch (Exception)
            {
                // 单行写入失败（如磁盘刚满）：保留流，下一次写入仍可能恢复。
            }
        }
    }

    private void WriteRaw(string text)
    {
        _writer!.Write(text);
        _bytesWritten += Encoding.UTF8.GetByteCount(text);
    }

    /// <summary>写前滚动：当前文件已满时换到新一代，只保留一份历史。</summary>
    private void RollIfNeeded(int incomingChars)
    {
        if (_bytesWritten <= 0) return; // 空文件不滚动（含刚 roll 完的）
        if (_bytesWritten + incomingChars <= MaxBytes) return;
        var path = _stream!.Name;
        _writer!.Dispose();
        _stream!.Dispose();
        RollPrevious(path);
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read);
        _stream = stream;
        _writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
        _bytesWritten = stream.Length;
    }

    private static void RollPrevious(string path)
    {
        var previous = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(path) ?? ".", PreviousFileName);
        if (File.Exists(previous)) File.Delete(previous);
        File.Move(path, previous);
    }
}
