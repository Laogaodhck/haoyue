using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

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
///   secret:file:&lt;id&gt;      AES-GCM entry in ~/.haoyue/secrets.json (Linux fallback when no
///                          keyring is available; file mode 0600, key derived from the
///                          machine identifier — documented as weaker than DPAPI).
///   (no prefix)              plaintext passthrough — legacy configs keep working unchanged.
/// </summary>
public static class SecretResolver
{
    public const string Prefix = "secret:";
    private const string DpapiScheme = "secret:dpapi:";
    private const string KeyringScheme = "secret:keyring:";
    internal const string FileScheme = "secret:file:";
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

        if (value.StartsWith(FileScheme, StringComparison.Ordinal))
        {
            return FileStoreLookup(value[FileScheme.Length..]);
        }

        // Unknown scheme: never forward ciphertext as if it were the credential.
        return null;
    }

    /// <summary>
    /// Encrypts a credential for storage. On Windows this is DPAPI; on Linux the
    /// Secret Service when secret-tool is available, otherwise the AES-GCM file
    /// store — credentials are never silently downgraded to plaintext on Linux.
    /// </summary>
    public static string Encrypt(string purposeId, string value)
    {
        if (string.IsNullOrEmpty(value) || IsSecret(value))
            return value; // already stored / nothing to protect — idempotent.

        if (OperatingSystem.IsWindows())
            return DpapiScheme + DpapiProtect(value);

        if (OperatingSystem.IsLinux())
        {
            if (KeyringStore(purposeId, value))
                return KeyringScheme + purposeId;
            return FileScheme + FileStoreStore(purposeId, value);
        }

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

    // ---- AES-GCM file store (Linux fallback when no Secret Service is available) ----
    //
    // Entries live in ~/.haoyue/secrets.json (mode 0600 on Linux) as
    // { "version": 1, "entries": { "<id>": "<base64 nonce||ciphertext||tag>" } }.
    // The key is HKDF-SHA256 over the machine identifier (/etc/machine-id, Windows
    // MachineGuid), so a copied secrets.json is useless on another machine. The
    // entry id is bound as AES-GCM associated data, preventing entry swaps. This
    // is deliberately documented as weaker than DPAPI: /etc/machine-id is
    // world-readable, so root or same-user read access to both files suffices
    // to recover credentials.

    private const string SecretsFileName = "secrets.json";
    private const string SecretsKdfSalt = "haoyue-mcp-secrets-v1";
    private const string SecretsKdfInfo = "mcp-credential";
    private const int FileKeyByteCount = 32;
    private const int FileNonceByteCount = 12;
    private const int FileTagByteCount = 16;

    private static readonly JsonSerializerOptions SecretsJsonOptions = new() { WriteIndented = true };

    internal static string SecretsFilePath() =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".haoyue", SecretsFileName);

    /// <summary>
    /// Stores a credential in the file store and returns the entry id for the
    /// secret:file: prefix. IO or key-derivation failures throw — saving must
    /// fail loudly rather than silently storing plaintext (hard constraint).
    /// </summary>
    internal static string FileStoreStore(string id, string value)
    {
        var machineId = MachineIdentifierBytes()
            ?? throw new InvalidOperationException(
                "No machine identifier is available for the file credential store on this platform.");
        var path = SecretsFilePath();
        JsonObject root;
        try
        {
            root = File.Exists(path)
                ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject()
                : new JsonObject();
        }
        catch (JsonException)
        {
            root = new JsonObject(); // corrupt store: rebuild; resolution already returns null for it.
        }
        if (root["entries"] is not JsonObject entries)
        {
            entries = new JsonObject();
            root["entries"] = entries;
        }
        entries[id] = FileStoreSeal(machineId, id, value);
        root["version"] = 1;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, root.ToJsonString(SecretsJsonOptions));
        if (OperatingSystem.IsLinux())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        return id;
    }

    /// <summary>Resolves an entry id; every failure mode returns null, never ciphertext.</summary>
    internal static string? FileStoreLookup(string id)
    {
        var path = SecretsFilePath();
        if (!File.Exists(path)) return null;
        var entries = ReadEntries(path);
        if (entries?[id] is not JsonValue payload || !payload.TryGetValue<string>(out var blob) || blob is null)
            return null;
        var machineId = MachineIdentifierBytes();
        return machineId is null ? null : FileStoreOpen(machineId, id, blob);
    }

    private static JsonObject? ReadEntries(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            return (JsonNode.Parse(File.ReadAllText(path)) as JsonObject)?["entries"] as JsonObject;
        }
        catch (JsonException)
        {
            return null; // corrupt store: resolution yields null; the next write rebuilds it.
        }
    }

    private static string FileStoreSeal(byte[] machineId, string id, string plaintext)
    {
        using var aes = new AesGcm(DeriveFileStoreKey(machineId), FileTagByteCount);
        var nonce = RandomNumberGenerator.GetBytes(FileNonceByteCount);
        var plain = Encoding.UTF8.GetBytes(plaintext);
        var cipher = new byte[plain.Length];
        var tag = new byte[FileTagByteCount];
        aes.Encrypt(nonce, plain, cipher, tag, Encoding.UTF8.GetBytes(id));
        var payload = new byte[nonce.Length + cipher.Length + tag.Length];
        Buffer.BlockCopy(nonce, 0, payload, 0, nonce.Length);
        Buffer.BlockCopy(cipher, 0, payload, nonce.Length, cipher.Length);
        Buffer.BlockCopy(tag, 0, payload, nonce.Length + cipher.Length, tag.Length);
        return Convert.ToBase64String(payload);
    }

    private static string? FileStoreOpen(byte[] machineId, string id, string blob)
    {
        byte[] payload;
        try { payload = Convert.FromBase64String(blob); }
        catch (FormatException) { return null; }
        if (payload.Length < FileNonceByteCount + FileTagByteCount) return null;

        try
        {
            using var aes = new AesGcm(DeriveFileStoreKey(machineId), FileTagByteCount);
            var nonce = payload[..FileNonceByteCount];
            var cipher = payload[FileNonceByteCount..^FileTagByteCount];
            var tag = payload[^FileTagByteCount..];
            var plain = new byte[cipher.Length];
            aes.Decrypt(nonce, cipher, tag, plain, Encoding.UTF8.GetBytes(id));
            return Encoding.UTF8.GetString(plain);
        }
        catch (AuthenticationTagMismatchException)
        {
            return null;
        }
    }

    private static byte[] DeriveFileStoreKey(byte[] machineId) =>
        HKDF.DeriveKey(
            HashAlgorithmName.SHA256,
            machineId,
            FileKeyByteCount,
            Encoding.UTF8.GetBytes(SecretsKdfSalt),
            Encoding.UTF8.GetBytes(SecretsKdfInfo));

    private static byte[]? MachineIdentifierBytes()
    {
        if (OperatingSystem.IsWindows())
        {
            var guid = Registry.GetValue(
                @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Cryptography", "MachineGuid", null) as string;
            return string.IsNullOrEmpty(guid) ? null : Encoding.UTF8.GetBytes(guid);
        }
        if (OperatingSystem.IsLinux())
        {
            foreach (var path in new[] { "/etc/machine-id", "/var/lib/dbus/machine-id" })
            {
                if (!File.Exists(path)) continue;
                var id = File.ReadAllText(path).Trim();
                if (id.Length > 0) return Encoding.UTF8.GetBytes(id);
            }
        }
        return null;
    }
}
