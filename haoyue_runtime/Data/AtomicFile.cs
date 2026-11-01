namespace Haoyue.Runtime.Data;

/// <summary>
/// Temp file in the target directory + rename. Readers never observe a half-written
/// file and a crash cannot truncate the previous good copy — important for the
/// policy/auth files the daemon re-reads on every request.
/// </summary>
public static class AtomicFile
{
    public static void WriteAllText(string path, string contents, UnixFileMode? unixMode = null)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = Path.Combine(directory ?? ".", $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temp, contents);
            if (unixMode is { } mode)
            {
                TrySetUnixMode(temp, mode);
            }

            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { }
        }
    }

    public static async Task WriteAllTextAsync(string path, string contents, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temp = Path.Combine(directory ?? ".", $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await File.WriteAllTextAsync(temp, contents, ct).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { }
        }
    }

    private static void TrySetUnixMode(string path, UnixFileMode mode)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path, mode);
        }
        catch (PlatformNotSupportedException)
        {
            // Some unix flavours do not expose the syscall; directory permissions remain the boundary.
        }
    }
}
