using System.Security.Cryptography;

namespace Haoyue.Runtime.Connectors;

/// <summary>
/// 连接器供应链完整性校验（P2）：安装连接器时把清单与内置本地服务器的 SHA256
/// 对照随目录分发的 SHA256SUMS（由 tools/generate_connector_checksums.py 生成，
/// CI 以 --check 模式锁定）。防的是「分发后 connectors/ 目录被篡改」——内置本地
/// MCP 服务器以 python 执行，是真正的代码执行面。
/// 目录中没有 SHA256SUMS（第三方/自定义目录）时跳过，不阻断安装。
/// </summary>
public static class ConnectorIntegrity
{
    public const string SumsFileName = "SHA256SUMS";

    /// <summary>
    /// 校验 <paramref name="manifestPath"/>（connector.json）与内置本地服务器的内容
    /// 哈希与 SHA256SUMS 一致；不一致抛 <see cref="ConnectorException"/>。
    /// </summary>
    public static void Verify(string manifestPath)
    {
        // manifestPath = <connectors根>/<id>/connector.json → 清单在 connectors 根。
        var packageDir = Path.GetDirectoryName(Path.GetFullPath(manifestPath));
        if (packageDir is null) return;
        var root = Path.GetDirectoryName(packageDir);
        if (root is null) return;
        var sumsPath = Path.Combine(root, SumsFileName);
        if (!File.Exists(sumsPath))
            return; // 第三方/自定义目录：无清单则不强制。

        List<(string Hash, string Path)> entries;
        try
        {
            entries = ParseSums(File.ReadAllLines(sumsPath)).ToList();
        }
        catch (FormatException ex)
        {
            throw new ConnectorException($"连接器完整性清单（{SumsFileName}）格式损坏：{ex.Message}");
        }

        var relative = Path.GetRelativePath(root, manifestPath).Replace('\\', '/');
        VerifyEntry(entries, relative, manifestPath, "连接器清单");

        // 内置本地服务器是执行面，若存在则一并校验。
        var bundledServer = ConnectorCatalog.ResolveBundledServer(root);
        if (bundledServer is not null)
            VerifyEntry(entries, ConnectorCatalog.BundledServerRelativePath, bundledServer, "内置本地 MCP 服务器");
    }

    private static void VerifyEntry(
        List<(string Hash, string Path)> entries, string relativePath, string absolutePath, string label)
    {
        var entry = entries.FirstOrDefault(e => string.Equals(e.Path, relativePath, StringComparison.OrdinalIgnoreCase));
        if (entry.Path is null)
            return; // 未纳入校验范围（如自定义包），放行。
        if (!string.Equals(entry.Hash, Sha256(absolutePath), StringComparison.OrdinalIgnoreCase))
            throw new ConnectorException(
                $"{label}完整性校验失败：{absolutePath} 的 SHA256 与 {SumsFileName} 不一致。" +
                "文件可能被篡改或损坏；请恢复原始分发内容或重新安装 haoyue。");
    }

    private static IEnumerable<(string Hash, string Path)> ParseSums(string[] lines)
    {
        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith('#')) continue;
            if (line.Length < 66)
                throw new FormatException($"行不是「<64 位十六进制><两空格><相对路径>」格式：{line}");
            var hash = line[..64];
            // 64 位十六进制远超 ulong/uint 范围，不能走数值 TryParse，逐字符校验。
            if (!hash.All(char.IsAsciiHexDigit))
                throw new FormatException($"哈希段不是 64 位十六进制：{line}");
            var rest = line[64..];
            if (rest.StartsWith("  ")) rest = rest[2..];
            else if (rest.StartsWith(" *")) rest = rest[2..]; // sha256sum 二进制模式
            else throw new FormatException($"哈希与路径之间缺少两个空格：{line}");
            yield return (hash, rest.TrimEnd());
        }
    }

    private static string Sha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }
}
