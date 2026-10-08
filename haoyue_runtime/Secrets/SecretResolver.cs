using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;

namespace Haoyue.Runtime.Secrets;

/// <summary>
/// Credential store behind the "secret:" value prefix. Values written by
/// <see cref="Encrypt"/> carry a scheme tag and are resolved back to plaintext
/// right before an MCP transport is created — never earlier, and the plaintext
/// is never persisted or echoed.
///
/// Schemes:
///   secret:dpapi:&lt;base64&gt;   Windows DPAPI (CurrentUser scope; same user, same machine).
///   secret:keyring:&lt;id&gt;    freedesktop Secret Service via the secret-tool CLI (Linux).
///   (no prefix)              plaintext passthrough — legacy configs keep working unchanged.
/// </summary>
public static class SecretResolver
{
    public const string Prefix = "secret:";
    private const string DpapiScheme = "secret:dpapi:";
    private const string KeyringScheme = "secret:keyring:";
    private const string KeyringService = "haoyue-mcp";

    public static bool IsSecret(string? value) =>
        value is not null && value.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>
    /// Resolves a possibly-prefixed value to its plaintext. Plaintext values pass
    /// through unchanged so legacy configs and user-typed values keep working.
    /// Unresolvable secrets return null — callers must treat that as "credential
    /// lost" rather than sending the ciphertext to a server.
    /// </summary>
    public static string? Resolve(string? value)
    {
        if (string.IsNullOrEmpty(value) || !value.StartsWith(Prefix, StringComparison.Ordinal))
            return value;

        if (value.StartsWith(DpapiScheme, StringComparison.Ordinal))
        {
            return OperatingSystem.IsWindows()
                ? DpapiUnprotect(value[DpapiScheme.Length..])
                : null;
        }

        if (value.StartsWith(KeyringScheme, StringComparison.Ordinal))
        {
            return OperatingSystem.IsLinux()
                ? KeyringLookup(value[KeyringScheme.Length..])
                : null;
        }

        // Unknown scheme: never forward ciphertext as if it were the credential.
        return null;
    }

    /// <summary>
    /// Encrypts a credential for storage. On Windows this is DPAPI; on Linux the
    /// Secret Service when secret-tool is available; otherwise the value is stored
    /// as plaintext (documented limitation — the config file should then be
    /// protected by filesystem permissions alone).
    /// </summary>
    public static string Encrypt(string purposeId, string value)
    {
        if (string.IsNullOrEmpty(value) || IsSecret(value))
            return value; // already stored / nothing to protect — idempotent.

        if (OperatingSystem.IsWindows())
            return DpapiScheme + DpapiProtect(value);

        if (OperatingSystem.IsLinux() && KeyringStore(purposeId, value))
            return KeyringScheme + purposeId;

        return value;
    }

    // ---- Windows DPAPI (crypt32) ----

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int CbData;
        public IntPtr PbData;
    }

    private const int CryptProtectUiForbidden = 0x1;

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptProtectData(
        ref DataBlob pDataIn, string? szDataDescr, IntPtr pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DataBlob pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CryptUnprotectData(
        ref DataBlob pDataIn, IntPtr ppszDataDescr, IntPtr pOptionalEntropy,
        IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DataBlob pDataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);

    private static string DpapiProtect(string plaintext)
    {
        var bytes = Encoding.UTF8.GetBytes(plaintext);
        var input = new DataBlob { CbData = bytes.Length, PbData = Marshal.AllocHGlobal(bytes.Length) };
        try
        {
            Marshal.Copy(bytes, 0, input.PbData, bytes.Length);
            if (!CryptProtectData(ref input, "haoyue mcp credential", IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out var output))
                throw new CryptographicException($"DPAPI protect failed (win32 error {Marshal.GetLastWin32Error()}).");
            try
            {
                var protectedBytes = new byte[output.CbData];
                Marshal.Copy(output.PbData, protectedBytes, 0, output.CbData);
                return Convert.ToBase64String(protectedBytes);
            }
            finally
            {
                LocalFree(output.PbData);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(input.PbData);
        }
    }

    private static string? DpapiUnprotect(string base64)
    {
        byte[] cipher;
        try { cipher = Convert.FromBase64String(base64); }
        catch (FormatException) { return null; }

        var input = new DataBlob { CbData = cipher.Length, PbData = Marshal.AllocHGlobal(cipher.Length) };
        try
        {
            Marshal.Copy(cipher, 0, input.PbData, cipher.Length);
            if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CryptProtectUiForbidden, out var output))
                return null;
            try
            {
                var plain = new byte[output.CbData];
                Marshal.Copy(output.PbData, plain, 0, output.CbData);
                return Encoding.UTF8.GetString(plain);
            }
            finally
            {
                LocalFree(output.PbData);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(input.PbData);
        }
    }

    // ---- Linux Secret Service (secret-tool CLI) ----

    private static bool KeyringStore(string id, string value)
    {
        if (FindSecretTool() is not { } secretTool) return false;
        var psi = new ProcessStartInfo
        {
            FileName = secretTool,
            ArgumentList = { "store", "--label=haoyue mcp credential", "service", KeyringService, "id", id },
            RedirectStandardInput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        try
        {
            using var process = Process.Start(psi);
            if (process is null) return false;
            process.StandardInput.Write(value);
            process.StandardInput.Close();
            process.WaitForExit(5000);
            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static string? KeyringLookup(string id)
    {
        if (FindSecretTool() is not { } secretTool) return null;
        var psi = new ProcessStartInfo
        {
            FileName = secretTool,
            ArgumentList = { "lookup", "service", KeyringService, "id", id },
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        try
        {
            using var process = Process.Start(psi);
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            return process.ExitCode == 0 && output.Length > 0 ? output.TrimEnd('\n') : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static string? FindSecretTool()
    {
        foreach (var dir in new[] { "/usr/bin", "/usr/local/bin", "/snap/bin" })
        {
            var candidate = Path.Combine(dir, "secret-tool");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }
}
