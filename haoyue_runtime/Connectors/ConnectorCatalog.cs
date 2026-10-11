using System.Text.Json;
using System.Text.Json.Serialization;
using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Secrets;

namespace Haoyue.Runtime.Connectors;

/// <summary>连接器包清单（connector.json）。规范见 connectors/schema/connector.schema.json。</summary>
public sealed class ConnectorPackage
{
    public int SchemaVersion { get; set; } = 1;
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public string? Icon { get; set; }
    public string Category { get; set; } = "";
    public List<string>? Tags { get; set; }
    public string? Homepage { get; set; }

    /// <summary>true = 随 haoyue 分发、无 npm/uvx 外部依赖（内置本地服务器承载）。</summary>
    public bool? Offline { get; set; }

    public McpServerConfig Server { get; set; } = new();
    public List<ConnectorInput>? Inputs { get; set; }
    public List<string>? MutatingTools { get; set; }
    public List<string>? ReadOnlyTools { get; set; }
    public bool TrustReadOnly { get; set; }
}

/// <summary>安装时向用户收集的一个配置项；secret 项以 secret: 前缀加密落盘。</summary>
public sealed class ConnectorInput
{
    public string Key { get; set; } = "";
    public string Title { get; set; } = "";
    public string? Description { get; set; }
    public bool Required { get; set; }
    public string? Default { get; set; }
    public bool Secret { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true)]
[JsonSerializable(typeof(ConnectorPackage))]
internal sealed partial class ConnectorJsonContext : JsonSerializerContext;

/// <summary>
/// 内置连接器目录：扫描连接器根目录（connectors/*/connector.json）。
/// 解析顺序：环境变量 HAOYUE_CONNECTORS_DIR → 可执行文件相对 connectors/ → 当前目录 connectors/。
/// </summary>
public static class ConnectorCatalog
{
    public const string BundledServerRelativePath = "servers/haoyue_local_server.py";

    public static string? ResolveRoot()
    {
        var candidates = new List<string>();
        var env = Environment.GetEnvironmentVariable("HAOYUE_CONNECTORS_DIR");
        if (!string.IsNullOrWhiteSpace(env))
            candidates.Add(env);

        var exeDir = AppContext.BaseDirectory;
        if (!string.IsNullOrEmpty(exeDir))
            candidates.Add(Path.Combine(exeDir, "connectors"));
        candidates.Add(Path.Combine(Directory.GetCurrentDirectory(), "connectors"));

        foreach (var candidate in candidates)
            if (File.Exists(Path.Combine(candidate, "schema", "connector.schema.json")))
                return Path.GetFullPath(candidate);
        return null;
    }

    /// <summary>加载目录中全部合法连接器包；单个包解析失败跳过（不影响其余）。</summary>
    public static List<ConnectorPackage> LoadAll(string? root = null)
    {
        root ??= ResolveRoot();
        if (root is null || !Directory.Exists(root))
            return [];

        var packages = new List<ConnectorPackage>();
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            var manifest = Path.Combine(dir, "connector.json");
            if (!File.Exists(manifest))
                continue;
            try
            {
                var package = JsonSerializer.Deserialize(
                    File.ReadAllText(manifest), ConnectorJsonContext.Default.ConnectorPackage);
                if (package is { SchemaVersion: 1 } valid && !string.IsNullOrWhiteSpace(valid.Id))
                    packages.Add(valid);
            }
            catch (JsonException)
            {
                // 目录里的损坏包不阻断目录加载。
            }
        }
        return packages.OrderBy(p => p.Id, StringComparer.Ordinal).ToList();
    }

    public static ConnectorPackage? Find(string id, string? root = null) =>
        LoadAll(root).FirstOrDefault(p => p.Id.Equals(id, StringComparison.OrdinalIgnoreCase));

    /// <summary>内置本地服务器（connectors/servers/haoyue_local_server.py）的绝对路径。</summary>
    public static string? ResolveBundledServer(string? root = null)
    {
        root ??= ResolveRoot();
        var path = root is null ? null : Path.Combine(root, BundledServerRelativePath.Replace('/', Path.DirectorySeparatorChar));
        return path is not null && File.Exists(path) ? path : null;
    }
}

/// <summary>
/// 连接器安装器：解析占位符 → secret 值加密 → 写入全局配置 mcp.servers 并持久化。
/// 安装即完成：MCP 传输与工具注册由既有 McpManager 机制在下次重载时接管。
/// </summary>
public static class ConnectorInstaller
{
    public static void Install(IConfigStore configStore, ConnectorPackage package, IReadOnlyDictionary<string, string>? values = null)
    {
        var server = Instantiate(package, values ?? new Dictionary<string, string>());

        var servers = configStore.Config.Mcp.Servers;
        servers[package.Id] = server;
        configStore.Save();
    }

    public static bool Uninstall(IConfigStore configStore, string id)
    {
        var servers = configStore.Config.Mcp.Servers;
        if (!servers.Remove(id))
            return false;
        configStore.Save();
        return true;
    }

    public static bool IsInstalled(IConfigStore configStore, string id) =>
        configStore.Config.Mcp.Servers.ContainsKey(id);

    /// <summary>用 inputs 值实例化 server 配置：替换 ${KEY} 占位符并处理加密项。</summary>
    public static McpServerConfig Instantiate(ConnectorPackage package, IReadOnlyDictionary<string, string> values)
    {
        // 1. 校验必填项。
        var missing = (package.Inputs ?? [])
            .Where(input => input.Required
                && !values.ContainsKey(input.Key)
                && string.IsNullOrWhiteSpace(input.Default))
            .Select(input => $"{input.Key}（{input.Title}）")
            .ToList();
        if (missing.Count > 0)
            throw new ConnectorException($"连接器 {package.Id} 缺少必填配置：{string.Join("、", missing)}");

        // 2. 合成完整取值表（默认值 + 传入值 + 内置服务器路径自动解析）。
        var resolved = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var input in package.Inputs ?? [])
        {
            if (values.TryGetValue(input.Key, out var value))
            {
                resolved[input.Key] = value;
            }
            else if (string.Equals(input.Key, "LOCAL_SERVER", StringComparison.Ordinal))
            {
                // 内置本地服务器路径由目录自动解析，用户无需手填。
                var bundled = ConnectorCatalog.ResolveBundledServer();
                if (bundled is null)
                    throw new ConnectorException(
                        $"连接器 {package.Id} 依赖内置本地服务器，但未找到 {ConnectorCatalog.BundledServerRelativePath}。" +
                        "请设置 HAOYUE_CONNECTORS_DIR 指向连接器目录。");
                resolved[input.Key] = bundled;
            }
            else if (!string.IsNullOrWhiteSpace(input.Default))
            {
                resolved[input.Key] = input.Default;
            }
        }

        // 3. 深拷贝 server 配置并替换占位符；secret 项走 SecretResolver.Encrypt。
        var server = Clone(package.Server);
        server.Command = Substitute(server.Command);
        server.Url = Substitute(server.Url);
        if (server.Args is { } args)
            server.Args = [.. args.Select(a => Substitute(a) ?? string.Empty)];
        if (server.Env is { } env)
            server.Env = env.ToDictionary(
                pair => pair.Key, pair => SubstituteSecret(pair.Value));
        if (server.Headers is { } headers)
            server.Headers = headers.ToDictionary(
                pair => pair.Key, pair => SubstituteSecret(pair.Value));

        server.MutatingTools = package.MutatingTools is { Count: > 0 } ? [.. package.MutatingTools] : null;
        server.ReadOnlyTools = package.ReadOnlyTools is { Count: > 0 } ? [.. package.ReadOnlyTools] : null;
        server.TrustReadOnly = package.TrustReadOnly;
        return server;

        string? Substitute(string? template) =>
            template is null ? null : s_placeholder.Replace(template, match =>
                resolved.TryGetValue(match.Groups[1].Value, out var v) ? v : match.Value);

        string SubstituteSecret(string template) =>
            s_placeholder.Replace(template, match =>
            {
                var key = match.Groups[1].Value;
                if (!resolved.TryGetValue(key, out var value))
                    return match.Value;
                var input = (package.Inputs ?? []).FirstOrDefault(i => i.Key == key);
                return input is { Secret: true }
                    ? SecretResolver.Encrypt($"connector:{package.Id}:{key}", value)
                    : value;
            });
    }

    private static readonly System.Text.RegularExpressions.Regex s_placeholder =
        new(@"\$\{([A-Z0-9_]+)\}", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static McpServerConfig Clone(McpServerConfig source) => new()
    {
        Transport = source.Transport,
        Command = source.Command,
        Args = source.Args is { } args ? [.. args] : null,
        Env = source.Env is { } env ? new Dictionary<string, string>(env) : null,
        Url = source.Url,
        Headers = source.Headers is { } headers ? new Dictionary<string, string>(headers) : null,
        ConnectTimeoutSeconds = source.ConnectTimeoutSeconds,
        Enabled = source.Enabled,
    };
}

/// <summary>连接器安装/运行期错误（可读消息，直接面向用户）。</summary>
public sealed class ConnectorException(string message) : Exception(message);
