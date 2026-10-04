using System.Security.Cryptography;
using System.Text;
using Haoyue.Runtime.Configuration;

namespace Haoyue.Runtime.Daemon;

/// <summary>
/// Handshake token shared between the daemon and its local clients. The named
/// pipe / unix socket only guards the endpoint by user session, so any process
/// of the same user could otherwise drive the agent. The token is persisted to
/// <see cref="HaoyuePaths.DaemonTokenFile"/>: on Unix the file mode is restricted
/// to the owner, on Windows the profile directory's default ACL already keeps
/// the file private to the current user and administrators.
/// </summary>
public static class DaemonAuth
{
    /// <summary>Length of the generated token: 32 random bytes as 64 hex characters.</summary>
    private const int TokenLength = 64;

    /// <summary>
    /// Returns the current handshake token, creating (or replacing, when the
    /// stored file is corrupt) the token file on first use.
    /// </summary>
    public static string LoadOrCreateToken()
    {
        try
        {
            if (File.ReadAllText(HaoyuePaths.DaemonTokenFile).Trim() is { Length: TokenLength } existing)
                return existing;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Missing or unreadable file: fall through and (re)create the token.
        }

        Directory.CreateDirectory(HaoyuePaths.Home);
        var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(TokenLength / 2));
        File.WriteAllText(HaoyuePaths.DaemonTokenFile, token);
        if (!OperatingSystem.IsWindows())
        {
            try
            {
                File.SetUnixFileMode(HaoyuePaths.DaemonTokenFile, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }
            catch (PlatformNotSupportedException)
            {
                // Some unix flavours may not support the syscall; the profile
                // directory permissions remain the effective boundary.
            }
        }
        return token;
    }

    /// <summary>
    /// Constant-time token comparison so a probing process cannot time its way
    /// to a valid token byte by byte.
    /// </summary>
    public static bool TokensEqual(string expected, string provided)
    {
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var providedBytes = Encoding.UTF8.GetBytes(provided);
        return expectedBytes.Length == providedBytes.Length
            && CryptographicOperations.FixedTimeEquals(expectedBytes, providedBytes);
    }
}
