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
    public static ConnectorInstallResult Install(
        IConfigStore configStore, ConnectorPackage package, IReadOnlyDictionary<string, string>? values = null)
    {
        // 供应链完整性（P2）：安装前校验清单与内置本地服务器未被篡改。
        var manifest = ConnectorCatalog.ResolveRoot() is { } root
            ? Path.Combine(root, package.Id, "connector.json")
            : null;
        if (manifest is not null && File.Exists(manifest))
            ConnectorIntegrity.Verify(manifest);

        var result = InstantiateDetailed(package, values ?? new Dictionary<string, string>());

        var servers = configStore.Config.Mcp.Servers;
        servers[package.Id] = result.Server;
        configStore.Save();
        return result;
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
    public static McpServerConfig Instantiate(ConnectorPackage package, IReadOnlyDictionary<string, string> values) =>
        InstantiateDetailed(package, values).Server;

    /// <summary>
    /// 用 inputs 值实例化 server 配置，并回报**没有取到值**的占位符键。
    /// 解析不到的占位符绝不落盘（旧行为会把 <c>${KEY}</c> 字面量写进配置交给子进程）：
    /// env/header 键直接删除、args 项整项丢弃；命令与 URL 是启动必需项，缺值直接抛可读错误。
    /// </summary>
    public static ConnectorInstallResult InstantiateDetailed(
        ConnectorPackage package, IReadOnlyDictionary<string, string> values)
    {
        // 1. 校验必填项（空白值视同未提供）。
        var missing = (package.Inputs ?? [])
            .Where(input => input.Required
                && !(values.TryGetValue(input.Key, out var provided) && !string.IsNullOrWhiteSpace(provided))
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

        // 3. 深拷贝 server 配置并替换占位符；未解析的键记入 unfilled 并从配置中剔除。
        var unfilled = new SortedSet<string>(StringComparer.Ordinal);
        var server = Clone(package.Server);

        server.Command = ResolveOrDrop(package.Server.Command, secret: false);
        if (package.Server.Command is not null && server.Command is null)
            throw UnresolvedPlaceholders(package, unfilled);
        server.Url = ResolveOrDrop(package.Server.Url, secret: false);
        if (package.Server.Url is not null && server.Url is null)
            throw UnresolvedPlaceholders(package, unfilled);

        if (server.Args is { } args)
        {
            var keptArgs = new List<string>(args.Count);
            foreach (var arg in args)
            {
                var value = ResolveOrDrop(arg, secret: false);
                if (value is null)
                {
                    // 参数值缺失时连同紧邻的选项开关一起丢弃，避免留下 `--repository` 这类悬空开关。
                    if (keptArgs.Count > 0 && IsLoneOption(keptArgs[^1])) keptArgs.RemoveAt(keptArgs.Count - 1);
                    continue;
                }
                keptArgs.Add(value);
            }
            server.Args = keptArgs;
        }
        server.Env = ResolveMap(server.Env);
        server.Headers = ResolveMap(server.Headers);

        server.MutatingTools = package.MutatingTools is { Count: > 0 } ? [.. package.MutatingTools] : null;
        server.ReadOnlyTools = package.ReadOnlyTools is { Count: > 0 } ? [.. package.ReadOnlyTools] : null;
        server.TrustReadOnly = package.TrustReadOnly;
        return new ConnectorInstallResult(
            package.Id, server, [.. unfilled.Select(key => InputLabel(package, key))]);

        static string InputLabel(ConnectorPackage package, string key) =>
            (package.Inputs ?? []).FirstOrDefault(input => input.Key == key) is { Title.Length: > 0 } input
                ? $"{key}（{input.Title}）"
                : key;

        /// <summary>形如 <c>--repository</c> / <c>-y</c> 的独立开关（不带 = 值）。</summary>
        static bool IsLoneOption(string arg) =>
            arg.Length > 1 && arg[0] == '-' && !arg.Contains('=');

        static ConnectorException UnresolvedPlaceholders(ConnectorPackage package, IEnumerable<string> keys) =>
            new($"连接器 {package.Id} 的启动配置引用了未提供的配置项：" +
                string.Join("、", keys.Select(key => InputLabel(package, key))) +
                "。请通过 --set KEY=value 或安装表单补齐后重试。");

        Dictionary<string, string>? ResolveMap(Dictionary<string, string>? source)
        {
            if (source is null) return null;
            var kept = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (key, template) in source)
            {
                var value = ResolveOrDrop(template, secret: true);
                if (value is not null) kept[key] = value;
            }
            return kept.Count > 0 ? kept : null;
        }

        /// <summary>替换占位符；任一处取不到值则返回 null（调用方据此丢弃该键/参数）。</summary>
        string? ResolveOrDrop(string? template, bool secret)
        {
            if (template is null) return null;
            var missed = new List<string>();
            var replaced = s_placeholder.Replace(template, match =>
            {
                var key = match.Groups[1].Value;
                if (!resolved.TryGetValue(key, out var value) || string.IsNullOrWhiteSpace(value))
                {
                    missed.Add(key);
                    return match.Value;
                }
                var input = (package.Inputs ?? []).FirstOrDefault(i => i.Key == key);
                return secret && input is { Secret: true }
                    ? SecretResolver.Encrypt($"connector:{package.Id}:{key}", value)
                    : value;
            });
            if (missed.Count == 0) return replaced;
            foreach (var key in missed)
                unfilled.Add(key);
            return null;
        }
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

/// <summary>
/// 一次连接器安装的结果：已写入 mcp.servers 的配置 + 没有取到值、因此被剔除的配置项
/// （<see cref="UnfilledInputs"/> 为空表示安装完整；非空时相关功能可能降级，需在 UI 明确提示）。
/// </summary>
public sealed record ConnectorInstallResult(
    string Id, McpServerConfig Server, IReadOnlyList<string> UnfilledInputs)
{
    public bool IsComplete => UnfilledInputs.Count == 0;
}

/// <summary>连接器安装/运行期错误（可读消息，直接面向用户）。</summary>
public sealed class ConnectorException(string message) : Exception(message);
