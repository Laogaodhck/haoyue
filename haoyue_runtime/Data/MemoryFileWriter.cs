namespace Haoyue.Runtime.Data;

/// <summary>
/// The only write path for a workspace MEMORY.md. Both the memory_save tool and the
/// background consolidation pass append through here, so both inherit the same three
/// guards: content dedupe, <see cref="MemoryBudget"/>, and an atomic temp+move write.
/// The file is user-curated through the desktop memory editor and has no backup —
/// a half-completed overwrite is silent data loss, and an unbounded append just
/// ends up truncated (now announced) inside the system prompt.
/// </summary>
public static class MemoryFileWriter
{
    public enum Status { Written, Duplicate, Rejected }

    /// <param name="Result">Whether the bullet reached the file.</param>
    /// <param name="Line">The bullet that was (or would have been) appended.</param>
    /// <param name="Message">Human-readable reason for Duplicate/Rejected, or the
    /// near-limit warning that accompanies a successful write.</param>
    public sealed record Append(Status Result, string Line, string? Message = null);

    // memory_save 与后台固化可能同时落到同一份 MEMORY.md：读-改-写必须串行化，
    // 否则后写者会用旧内容覆盖先写者的条目（静默丢记忆）。
    private static readonly SemaphoreSlim WriteGate = new(1, 1);

    public static async Task<Append> AppendAsync(
        string file, string content, string? topic, CancellationToken ct)
    {
        var line = $"- {DateTimeOffset.Now:yyyy-MM-dd} {(string.IsNullOrWhiteSpace(topic) ? "" : $"[{topic?.Trim()}] ")}{content}";
        await WriteGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var crossProcess = await CrossProcessLock.AcquireAsync(file, ct).ConfigureAwait(false);
            Directory.CreateDirectory(Path.GetDirectoryName(file) ?? ".");
            var existing = File.Exists(file) ? await File.ReadAllTextAsync(file, ct).ConfigureAwait(false) : "";
            if (existing.Split('\n').Any(l => l.Contains(content, StringComparison.OrdinalIgnoreCase)))
                return new Append(Status.Duplicate, line, "该内容已存在于工作区记忆中，未重复写入");
            if (existing.Length > 0 && !existing.EndsWith('\n')) existing += "\n";
            var updated = existing + line + "\n";
            if (MemoryBudget.Overflow(updated) is { } overflow)
                return new Append(Status.Rejected, line, overflow);
            await WriteAtomicAsync(file, updated, ct).ConfigureAwait(false);
            return new Append(Status.Written, line, MemoryBudget.ApproachingWarning(updated));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new Append(Status.Rejected, line, $"无法写入记忆文件：{ex.Message}");
        }
        finally
        {
            WriteGate.Release();
        }
    }

    /// <summary>
    /// 尽力而为的跨进程互斥（CLI 与 daemon 可能各持一份运行时会写同一个工作区）。
    /// 拿不到锁就照常继续：宁可回到「最后一次写入胜出」，也不让记忆写入因锁竞争失败。
    /// </summary>
    private sealed class CrossProcessLock(FileStream? stream) : IDisposable
    {
        private const int MaxAttempts = 10;
        private static readonly TimeSpan AttemptInterval = TimeSpan.FromMilliseconds(100);

        public static async Task<CrossProcessLock> AcquireAsync(string target, CancellationToken ct)
        {
            var lockFile = Path.Combine(
                Path.GetDirectoryName(target) ?? ".",
                $".{Path.GetFileName(target)}.lock");
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(lockFile)!);
                for (var attempt = 0; attempt < MaxAttempts; attempt++)
                {
                    try
                    {
                        return new CrossProcessLock(new FileStream(
                            lockFile, FileMode.OpenOrCreate,
                            FileAccess.ReadWrite, FileShare.None));
                    }
                    catch (IOException) when (attempt < MaxAttempts - 1)
                    {
                        await Task.Delay(AttemptInterval, ct).ConfigureAwait(false);
                    }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            return new CrossProcessLock(null);
        }

        public void Dispose()
        {
            try { stream?.Dispose(); } catch (IOException) { }
        }
    }

    private static async Task WriteAtomicAsync(string path, string content, CancellationToken ct)
    {
        var temp = Path.Combine(
            Path.GetDirectoryName(path) ?? ".",
            $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temp, content, ct).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { }
        }
    }
}
