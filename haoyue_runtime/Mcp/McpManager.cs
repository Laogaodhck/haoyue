using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Haoyue.Runtime.Agents;
using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Connectors;
using Haoyue.Runtime.Prompts;
using Haoyue.Runtime.Secrets;
using Haoyue.Runtime.Tools;
using Haoyue.Runtime.Workspaces;

namespace Haoyue.Runtime.Mcp;

public sealed record McpServerStatus(string Name, string Transport, bool Connected, int ToolCount, string? Error)
{
    /// <summary>True while a background reconnect for this server is still running.</summary>
    public bool Connecting { get; init; }

    /// <summary>B3: prompts/resources skipped by the registration caps, so drops are
    /// visible in mcp.list instead of silently disappearing from the system prompt.</summary>
    public IReadOnlyList<string>? Warnings { get; init; }
}

public interface IMcpManager : IAsyncDisposable
{
    /// <summary>Connects every enabled MCP server and registers discovered tools and prompts.</summary>
    Task<IReadOnlyList<McpServerStatus>> ConnectAllAsync(WorkspaceInfo workspace, CancellationToken ct, IProgress<string>? progress = null);

    /// <summary>
    /// Marks every configured server as connecting and clears the previous error text,
    /// so clients can render a pending state while a background reconnect runs.
    /// </summary>
    void MarkConnecting(WorkspaceInfo workspace);

    IReadOnlyDictionary<string, McpServerConfig> LoadServerConfigs(WorkspaceInfo workspace);

    IReadOnlyList<McpServerStatus> Status { get; }
}

public sealed class McpManager(
    IConfigStore configStore,
    IToolRegistry toolRegistry,
    IPromptRegistry promptRegistry,
    Func<string, McpServerConfig, string?, IMcpTransport>? transportFactory = null) : IMcpManager
{
    /// <summary>
    /// Upper bound of resource contributions registered per server: a file-tree
    /// server may expose hundreds of resources and each one would fire a
    /// resources/read round-trip on every prompt composition.
    /// </summary>
    private const int MaxResourcesPerServer = 16;

    /// <summary>B3: prompt contributions per server are capped the same way — a server
    /// exposing hundreds of prompts would otherwise flood the system prompt and eat
    /// the conversation-history budget before B1's total enforcement kicks in.</summary>
    private const int MaxPromptsPerServer = 24;

    /// <summary>B3: hard global cap across all servers. Combined with the 10k-token
    /// per-fragment fit this bounds worst-case injected MCP prompt text.</summary>
    private const int MaxPromptContributionsTotal = 48;

    private readonly Func<string, McpServerConfig, string?, IMcpTransport> _transportFactory = transportFactory ?? CreateTransport;
    /// <summary>
    /// 6.2 按服务器名归属的连接与注册（原先是扁平 _clients/_registrations）：
    /// 两段式灰度需要「只切换成功的服务器、保留失败服务器旧连接与旧工具集」，
    /// 归属信息是前提。槽位建立/替换只在 _statusGate 内进行。
    /// </summary>
    private readonly Dictionary<string, ServerSlot> _slots = new(StringComparer.Ordinal);
    private readonly List<McpServerStatus> _status = [];
    private readonly SemaphoreSlim _connectionGate = new(1, 1);
    private readonly Lock _statusGate = new();

    /// <summary>B3: prompt contributions registered across all servers this reload.</summary>
    private int _promptContributionCount;

    /// <summary>一个 MCP 服务器的已建立连接与注册句柄（registration Dispose = 反注册）。</summary>
    private sealed class ServerSlot
    {
        public List<IDisposable> Registrations { get; } = [];
        public List<McpClient> Clients { get; } = [];
        public int PromptCount { get; set; }
    }

    public IReadOnlyList<McpServerStatus> Status
    {
        get { lock (_statusGate) return _status.ToList(); }
    }

    public IReadOnlyDictionary<string, McpServerConfig> LoadServerConfigs(WorkspaceInfo workspace)
    {
        var servers = new Dictionary<string, McpServerConfig>(StringComparer.OrdinalIgnoreCase);

        foreach (var (name, server) in configStore.Config.Mcp.Servers)
            servers[name] = server;

        // 工作区配置是仓库内容，克隆后打开即生效——只允许受信工作区参与合并。
        // 未受信时连 .haoyue/config.json 都不加载（WorkspaceManager 已置 Config=null），
        // 并跳过 mcp/servers.json，避免「打开一次就执行其中的命令」。
        if (!workspace.Trusted) return servers;

        // <workspace>/mcp/servers.json
        var serversFile = Path.Combine(workspace.McpDir, "servers.json");
        if (File.Exists(serversFile))
        {
            try
            {
                var fileConfig = JsonSerializer.Deserialize(
                    File.ReadAllText(serversFile), HaoyueJsonContext.Default.McpConfig);
                foreach (var (name, server) in fileConfig?.Servers ?? [])
                    servers[name] = server;
            }
            catch (JsonException) { }
        }

        // Workspace .haoyue/config.json wins.
        foreach (var (name, server) in workspace.Config?.Mcp?.Servers ?? [])
            servers[name] = server;

        return servers;
    }

    public void MarkConnecting(WorkspaceInfo workspace)
    {
        var configs = LoadServerConfigs(workspace);
        lock (_statusGate)
        {
            _status.Clear();
            foreach (var (name, server) in configs)
            {
                _status.Add(new McpServerStatus(
                    name, server.Transport, Connected: false, ToolCount: 0, server.Enabled ? null : "disabled")
                {
                    Connecting = server.Enabled,
                });
            }
        }
    }

    public async Task<IReadOnlyList<McpServerStatus>> ConnectAllAsync(
        WorkspaceInfo workspace, CancellationToken ct, IProgress<string>? progress = null)
    {
        await _connectionGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // 物化配置快照：并行连接时保持稳定的枚举顺序（status 按此顺序返回）。
            var configs = LoadServerConfigs(workspace).ToList();
            var enabled = new List<(string Name, McpServerConfig Server, int Index)>();
            var statuses = new McpServerStatus?[configs.Count];
            for (var index = 0; index < configs.Count; index++)
            {
                var (name, server) = configs[index];
                if (!server.Enabled)
                {
                    statuses[index] = new McpServerStatus(name, server.Transport, false, 0, "disabled");
                    continue;
                }
                enabled.Add((name, server, index));
            }

            // ---- 第一段：探测。建立连接并发现能力，全程不触碰现有槽位与工具注册表。
            // 任一服务器失败只影响它自己——旧连接、旧工具集原封不动。
            var total = enabled.Count;
            // 串行连接时每个 stdio 服务器的进程冷启动（npx/uvx 解析+拉起）+ initialize
            // 握手要 20-40 秒，四个服务器串行轻松吃掉两分钟——这正是桌面端回合长时间
            // 停在"正在准备"的主导因素之一。并行连接后总耗时降为最慢单个服务器的耗时。
            var probes = await Task.WhenAll(enabled.Select(async entry =>
            {
                var (name, server, index) = entry;
                progress?.Report($"连接 MCP 服务器 {name}（共 {total} 个）");
                try
                {
                    var (status, client, discovery) = await ConnectAndDiscoverAsync(name, server, workspace.Root, ct)
                        .ConfigureAwait(false);
                    progress?.Report($"MCP 服务器 {name} 就绪（{status.ToolCount} tools）");
                    return (Entry: entry, Status: status, Client: (McpClient?)client, Discovery: discovery, Error: (string?)null);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw; // the caller cancelled the whole reload
                }
                catch (Exception ex)
                {
                    progress?.Report($"MCP 服务器 {name} 连接失败");
                    var detail = DescribeConnectionError(ex);
                    return (Entry: entry, Status: new McpServerStatus(name, server.Transport, false, 0, detail),
                        Client: (McpClient?)null, Discovery: (Discovery?)null, Error: (string?)detail);
                }
            })).ConfigureAwait(false);

            // ---- 第二段：切换。成功的服务器原子换槽（只断自己的旧连接）；失败的服务器
            // 保留旧槽（旧工具集继续可用）；配置中消失/禁用的服务器才整体回收。
            var retired = new List<ServerSlot>();
            lock (_statusGate)
            {
                var keptNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                // 本轮实际注册的 prompt 数（全局上限 MaxPromptContributionsTotal 按此累计，
                // 切换完成后以各槽位 PromptCount 汇总校准）。
                var registeredPrompts = 0;
                foreach (var probe in probes)
                {
                    var (entry, status, client, discovery, error) = probe;
                    var (name, server, index) = entry;
                    if (client is null || discovery is null)
                    {
                        // 灰度保留：旧连接与旧工具集继续服务，状态行如实说明。
                        if (_slots.TryGetValue(name, out var existing))
                        {
                            keptNames.Add(name);
                            statuses[index] = status with
                            {
                                Error = $"{error}（已保留上一次成功连接的工具集）",
                                ToolCount = existing.Registrations.Count > 0 ? StatusToolCount(name) : 0,
                            };
                        }
                        else
                        {
                            statuses[index] = status;
                        }
                        continue;
                    }

                    // 先释放旧槽位的注册（同步反注册，工具名立即回收），再注册新的：
                    // ToolRegistry.Register 对同名工具抛异常——顺序反过来会在 reload 时
                    // 撞上「mcp__server__tool 已注册」。旧 client 的异步释放留到锁外。
                    if (_slots.Remove(name, out var old))
                    {
                        foreach (var registration in old.Registrations) registration.Dispose();
                        old.Registrations.Clear();
                        retired.Add(old);
                    }
                    var (slot, warnings) = RegisterSlot(name, server, client, discovery, ref registeredPrompts);
                    _slots[name] = slot;
                    keptNames.Add(name);
                    statuses[index] = warnings.Count > 0
                        ? status with { Warnings = [.. status.Warnings ?? [], .. warnings] }
                        : status;
                }

                foreach (var name in _slots.Keys.Where(name => !keptNames.Contains(name)).ToList())
                    if (_slots.Remove(name, out var gone)) retired.Add(gone);

                _promptContributionCount = _slots.Values.Sum(slot => slot.PromptCount);
                _status.Clear();
                _status.AddRange(statuses.Where(status => status is not null).Select(status => status!));
            }

            // 旧槽在锁外回收：registration Dispose 同步（反注册），client 释放是异步的。
            foreach (var slot in retired)
            {
                foreach (var registration in slot.Registrations) registration.Dispose();
                foreach (var client in slot.Clients) await client.DisposeAsync().ConfigureAwait(false);
            }
            return Status;
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    /// <summary>失败服务器的状态行显示保留中的工具数（注册表当前实际可用的该服务器工具）。</summary>
    private int StatusToolCount(string serverName) =>
        toolRegistry.All.Count(tool => tool.Name.StartsWith(
            McpToolAdapter.BuildName(serverName, string.Empty), StringComparison.OrdinalIgnoreCase));

    private sealed record Discovery(
        IReadOnlyList<McpTool> Tools,
        IReadOnlyList<McpPrompt> Prompts,
        IReadOnlyList<McpResource> Resources);

    /// <summary>
    /// 第一段：建立连接 + 发现能力（tools/prompts/resources 只读取，不注册）。
    /// 失败时释放本次连接并上抛——调用方据此保留该服务器的旧槽位。
    /// </summary>
    private async Task<(McpServerStatus Status, McpClient Client, Discovery Discovery)> ConnectAndDiscoverAsync(
        string name, McpServerConfig server, string? workspaceRoot, CancellationToken ct)
    {
        var remote = IsRemoteTransport(server.Transport);
        HealBundledServerArgs(name, server);
        for (var attempt = 1; ; attempt++)
        {
            IMcpTransport transport = _transportFactory(name, server, workspaceRoot);

            var client = new McpClient(name, transport);
            using var initCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            initCts.CancelAfter(TimeSpan.FromSeconds(30));

            try
            {
                await client.InitializeAsync(initCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                await client.DisposeAsync().ConfigureAwait(false);
                throw new McpException($"Server '{name}' timed out during initialization.");
            }
            catch (McpUnauthorizedException) when (remote && attempt == 1 && server.OAuthDisabled != true)
            {
                // First contact with an OAuth-protected server: authorize (silent
                // refresh first, interactive browser otherwise), then rebuild the
                // transport with the fresh token and retry exactly once.
                await client.DisposeAsync().ConfigureAwait(false);
                if (!await TryAuthorizeAsync(name, server, ct).ConfigureAwait(false))
                    throw new McpException(
                        $"Server '{name}' 需要浏览器授权但未完成：请点击重连再次发起授权，或在设置中改用 Authorization 请求头。");
                continue;
            }
            catch
            {
                await client.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            try
            {
                var tools = await client.ListToolsAsync(ct).ConfigureAwait(false);
                var prompts = await client.ListPromptsAsync(ct).ConfigureAwait(false);
                var resources = await client.ListResourcesAsync(ct).ConfigureAwait(false);
                return (new McpServerStatus(name, server.Transport, true, tools.Count, null),
                    client, new Discovery(tools, prompts, resources));
            }
            catch
            {
                // Discovery failed after the transport connected: without this cleanup the
                // client (stdio: a live child process and two pump loops) stays alive until
                // the next reload recycles it, and repeated failed reloads accumulate orphans.
                await client.DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
    }

    /// <summary>
    /// 第二段：把探测结果注册成工具/提示/资源（纯本地操作，不触网）。注册动作在
    /// 切换时刻发生——此时旧槽位即将释放，同名工具不会出现双注册。
    /// </summary>
    private (ServerSlot Slot, List<string> Warnings) RegisterSlot(
        string name, McpServerConfig server, McpClient client, Discovery discovery, ref int registeredPrompts)
    {
        var slot = new ServerSlot();
        slot.Clients.Add(client);
        var warnings = new List<string>();

        // Tools: names are sanitized to [a-zA-Z0-9_-] and de-duplicated so one server
        // cannot register two tools under the same local name.
        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tool in discovery.Tools)
        {
            var baseName = McpToolAdapter.BuildName(name, tool.Name);
            var uniqueName = baseName;
            var suffix = 2;
            while (!usedNames.Add(uniqueName))
                uniqueName = $"{baseName}_{suffix++}";
            slot.Registrations.Add(toolRegistry.Register(new McpToolAdapter(
                client, tool, uniqueName, McpToolAdapter.ResolveMutating(server, tool.Name))));
        }

        // Prompts: resolvers degrade to null when the server is gone or rejects the
        // request, so an offline server never breaks prompt composition; text is fitted
        // to the injected-fragment token budget and wrapped in a trust boundary.
        var serverPromptCount = 0;
        foreach (var prompt in discovery.Prompts)
        {
            if (serverPromptCount >= MaxPromptsPerServer || registeredPrompts >= MaxPromptContributionsTotal)
            {
                // B3: over the cap — skipped in the registry, visible in the status
                // warnings, so users can see why a prompt never shows up.
                warnings.Add(
                    $"prompt '{prompt.Name}' 超出注册限额（每服务器 {MaxPromptsPerServer}，全局 {MaxPromptContributionsTotal}），未注入系统提示");
                continue;
            }
            serverPromptCount++;
            slot.PromptCount++;
            registeredPrompts++;
            var promptName = prompt.Name;
            slot.Registrations.Add(promptRegistry.Register(new PromptContribution(
                $"mcp:{name}:{promptName}", PromptSlot.Tool,
                async (_, token) =>
                {
                    var text = await client.GetPromptAsync(promptName, token).ConfigureAwait(false);
                    if (text is null) return null;
                    // Third-party text enters the system prompt: wrap it in a trust
                    // boundary so it is treated as data, not instructions.
                    return ContextPlanner.WrapUntrustedSource(
                        ContextPlanner.FitInjectedText(text), $"MCP server '{name}' prompt '{promptName}'");
                }, DegradeRank: 20)));
        }

        // Resources: text contents exposed as context contributions under the same
        // mcp:* id family; reads fail soft like prompts.
        foreach (var resource in discovery.Resources.Take(MaxResourcesPerServer))
        {
            if (string.IsNullOrWhiteSpace(resource.Uri)) continue;
            var uri = resource.Uri;
            slot.Registrations.Add(promptRegistry.Register(new PromptContribution(
                $"mcp:{name}:resource:{uri}", PromptSlot.Tool,
                async (_, token) =>
                {
                    var text = await client.GetResourceAsync(uri, token).ConfigureAwait(false);
                    if (text is null) return null;
                    return ContextPlanner.WrapUntrustedSource(
                        ContextPlanner.FitInjectedText(text), $"MCP server '{name}' resource '{uri}'");
                }, DegradeRank: 20)));
        }
        return (slot, warnings);
    }

    private static bool IsRemoteTransport(string transport) =>
        transport is "sse" or "http" or "streamable-http" or "streamable_http";

    /// <summary>
    /// Self-heal for offline connectors (calendar / clipboard / email / http-api /
    /// notify / rss). Their install freezes the absolute path of the bundled local
    /// MCP server (connectors/servers/haoyue_local_server.py) into mcp.servers
    /// args; when that file later moves or disappears — transient build output,
    /// app upgrade, directory rename — the child process dies instantly with
    /// "No such file or directory" and the connector stays broken until a manual
    /// reinstall. Re-resolve the current bundled server and substitute it, so an
    /// installed offline connector survives relocation. The healed path is
    /// persisted when the server entry lives in the global config.
    /// Only args that look like the bundled server AND no longer exist on disk are
    /// touched; healthy paths and all other arguments are left as-is.
    /// </summary>
    private void HealBundledServerArgs(string name, McpServerConfig server)
    {
        if (server.Args is not { Count: > 0 }) return;
        for (var i = 0; i < server.Args.Count; i++)
        {
            var arg = server.Args[i];
            if (string.IsNullOrEmpty(arg) || File.Exists(arg)) continue;
            if (!Path.GetFileName(arg).Equals("haoyue_local_server.py", StringComparison.OrdinalIgnoreCase))
                continue;

            var bundled = ConnectorCatalog.ResolveBundledServer();
            if (bundled is null || string.Equals(bundled, arg, StringComparison.OrdinalIgnoreCase))
                continue;

            server.Args[i] = bundled;
            if (configStore.Config.Mcp?.Servers is { } servers &&
                servers.TryGetValue(name, out var stored) &&
                ReferenceEquals(stored, server))
            {
                configStore.Save();
            }
        }
    }

    /// <summary>
    /// Authorization attempt for a 401-ing remote server: silent refresh first (no
    /// browser), then the interactive flow. Tokens land secret:-encrypted on the
    /// server entry and are persisted when it lives in the global config.
    /// </summary>
    private async Task<bool> TryAuthorizeAsync(string name, McpServerConfig server, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(server.Url)) return false;
        try
        {
            if (SecretResolver.Resolve(server.OAuthRefreshToken) is { Length: > 0 } refreshToken)
            {
                var refreshed = await McpOAuthFlow.RefreshAsync(
                    server.Url!, refreshToken,
                    server.OAuthClientId,
                    SecretResolver.Resolve(server.OAuthClientSecret),
                    ct).ConfigureAwait(false);
                if (refreshed is not null)
                {
                    StoreOAuthTokens(name, server, new OAuthGrant(refreshed, null, null));
                    return true;
                }
            }

            var grant = await McpOAuthFlow.AuthorizeAsync(server.Url!, ct).ConfigureAwait(false);
            StoreOAuthTokens(name, server, grant);
            return true;
        }
        catch (McpException)
        {
            throw; // actionable, user-facing flow errors (denied grant, no DCR, timeout)
        }
        catch
        {
            return false;
        }
    }

    private void StoreOAuthTokens(string name, McpServerConfig server, OAuthGrant grant)
    {
        server.OAuthAccessToken = SecretResolver.Encrypt($"mcp:oauth:{name}:access", grant.Tokens.AccessToken);
        if (grant.Tokens.RefreshToken is { Length: > 0 } refresh)
            server.OAuthRefreshToken = SecretResolver.Encrypt($"mcp:oauth:{name}:refresh", refresh);
        // A grant without a rotated refresh token keeps the previously stored one.

        if (grant.ClientId is { Length: > 0 } clientId)
            server.OAuthClientId = clientId;
        if (grant.ClientSecret is { Length: > 0 } clientSecret)
            server.OAuthClientSecret = SecretResolver.Encrypt($"mcp:oauth:{name}:client", clientSecret);

        // Persist only when the server lives in the global config (the default scope)
        // so tokens survive restarts; workspace-scoped copies keep the in-memory token
        // for the current session.
        if (configStore.Config.Mcp?.Servers is not { } servers ||
            !servers.TryGetValue(name, out var stored) ||
            !string.Equals(stored.Url, server.Url, StringComparison.OrdinalIgnoreCase))
            return;
        stored.OAuthAccessToken = server.OAuthAccessToken;
        stored.OAuthRefreshToken = server.OAuthRefreshToken;
        stored.OAuthClientId = server.OAuthClientId;
        stored.OAuthClientSecret = server.OAuthClientSecret;
        configStore.Save();
    }

    private static IMcpTransport CreateTransport(string name, McpServerConfig server, string? workspaceRoot)
    {
        // Credentials (env / headers) are stored with an optional "secret:" prefix;
        // resolve them at the last possible moment — the plaintext only exists in
        // transport memory, never in config or logs.
        var env = server.Env is null
            ? null
            : server.Env.ToDictionary(
                pair => pair.Key,
                pair => SecretResolver.Resolve(pair.Value)
                        ?? throw new McpException(
                            $"Server '{name}': environment variable '{pair.Key}' is stored as a secret that cannot be resolved on this platform. Re-enter the value in settings."));
        var headers = server.Headers is null
            ? null
            : server.Headers.ToDictionary(
                pair => pair.Key,
                pair => SecretResolver.Resolve(pair.Value)
                        ?? throw new McpException(
                            $"Server '{name}': header '{pair.Key}' is stored as a secret that cannot be resolved on this platform. Re-enter the value in settings."));

        // OAuth token from the local authorization flow rides along as a Bearer
        // header unless the user configured an explicit Authorization header.
        if (SecretResolver.Resolve(server.OAuthAccessToken) is { Length: > 0 } bearer)
        {
            headers ??= new Dictionary<string, string>(StringComparer.Ordinal);
            if (!headers.ContainsKey("Authorization"))
                headers["Authorization"] = $"Bearer {bearer}";
        }

        return server.Transport.ToLowerInvariant() switch
        {
            "stdio" when !string.IsNullOrWhiteSpace(server.Command) =>
                new StdioMcpTransport(server.Command!, server.Args, env, ResolveStdioCwd(server.Cwd, workspaceRoot)),
            "sse" or "http" or "streamable-http" or "streamable_http" when !string.IsNullOrWhiteSpace(server.Url) =>
                new HttpMcpTransport(
                    server.Url!,
                    headers: headers,
                    connectTimeout: TimeSpan.FromSeconds(Math.Clamp(server.ConnectTimeoutSeconds ?? 10, 1, 120))),
            "websocket" =>
                throw new McpException($"Transport '{server.Transport}' is reserved but not implemented yet."),
            _ => throw new McpException($"Server '{name}': invalid transport/command/url combination."),
        };
    }

    /// <summary>
    /// Working directory for a stdio child: explicit cwd (absolute, or relative to
    /// the workspace root) wins; otherwise default to the workspace root so args
    /// like `--repository .` resolve against the project the agent is working on
    /// instead of the daemon's own cwd (the install dir in the packaged desktop).
    /// A missing default falls back to the inherited cwd; a missing explicit cwd is
    /// left as-is so the transport surfaces the typo as a clear error.
    /// </summary>
    internal static string? ResolveStdioCwd(string? configured, string? workspaceRoot)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var candidate = configured.Trim();
            return Path.IsPathRooted(candidate) || workspaceRoot is null
                ? candidate
                : Path.GetFullPath(Path.Combine(workspaceRoot, candidate));
        }

        return !string.IsNullOrWhiteSpace(workspaceRoot) && Directory.Exists(workspaceRoot)
            ? workspaceRoot
            : null;
    }

    private static string DescribeConnectionError(Exception error) => error switch
    {
        Win32Exception win32 => $"无法启动 MCP 服务器进程：{win32.Message}",
        McpUnauthorizedException => "需要授权：请点击重连发起浏览器 OAuth 授权，或在设置中改用 Authorization 请求头",
        OperationCanceledException or TimeoutException => "连接超时",
        HttpRequestException http => $"连接失败：{http.Message}",
        McpException mcp => mcp.Message,
        _ => error.Message,
    };

    public async ValueTask DisposeAsync()
    {
        await _connectionGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await ResetConnectionsAsync().ConfigureAwait(false);
            lock (_statusGate) _status.Clear();
        }
        finally
        {
            _connectionGate.Release();
        }
    }

    private async ValueTask ResetConnectionsAsync()
    {
        List<ServerSlot> slots;
        lock (_statusGate)
        {
            slots = [.. _slots.Values];
            _slots.Clear();
            _promptContributionCount = 0;
        }
        foreach (var slot in slots)
        {
            foreach (var registration in slot.Registrations) registration.Dispose();
            foreach (var client in slot.Clients) await client.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>Bridges a remote MCP tool into the local tool registry as mcp__server__tool.</summary>
public sealed class McpToolAdapter : ITool
{
    private readonly McpClient _client;
    private readonly McpTool _tool;
    private readonly string _name;
    private readonly bool _isMutating;

    public McpToolAdapter(McpClient client, McpTool tool, bool isMutating = false)
        : this(client, tool, BuildName(client.ServerName, tool.Name), isMutating)
    {
    }

    public McpToolAdapter(McpClient client, McpTool tool, string name, bool isMutating = false)
    {
        _client = client;
        _tool = tool;
        _name = name;
        _isMutating = isMutating;
    }

    public string Name => _name;
    public string Description => _tool.Description ?? $"MCP tool {_tool.Name} from server {_client.ServerName}.";
    public JsonObject ParameterSchema => _tool.InputSchema;
    // Resolved once at registration time via ResolveMutating — the per-server explicit
    // lists and the fail-closed default must be stable for the adapter's lifetime.
    public bool Mutating => _isMutating;
    public string StatusLabel => $"Calling {_client.ServerName}";

    public async Task<ToolResult> ExecuteAsync(JsonObject arguments, ToolContext context, CancellationToken ct)
    {
        try
        {
            var (success, text) = await _client.CallToolAsync(_tool.Name, arguments, ct).ConfigureAwait(false);
            var output = context.TruncateTailPrioritized(text.Length == 0 ? "(no output)" : text, "MCP output");
            return success
                ? ToolResult.Ok(output, $"{_tool.Name} via {_client.ServerName}")
                : ToolResult.Fail(output);
        }
        catch (McpException ex)
        {
            return ToolResult.Fail(ex.Message);
        }
    }

    /// <summary>
    /// Builds a provider-safe local name: MCP servers may use spaces, dots or unicode
    /// in tool names, but most LLM providers only accept [a-zA-Z0-9_-] (≤64 chars).
    /// </summary>
    public static string BuildName(string serverName, string toolName)
    {
        var full = $"mcp__{SanitizeSegment(serverName)}__{SanitizeSegment(toolName)}";
        return full.Length > 64 ? full[..64] : full;
    }

    internal static string SanitizeSegment(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
            sb.Append(char.IsAsciiLetterOrDigit(ch) || ch is '_' or '-' ? ch : '_');
        return sb.Length == 0 ? "tool" : sb.ToString();
    }

    /// <summary>
    /// Decides whether an MCP tool counts as mutating. The MCP protocol exposes no
    /// mutating metadata, so the decision is: explicit per-server ReadOnlyTools list wins
    /// (config vouches the tool is safe), then the explicit MutatingTools list, then the
    /// name heuristic, and finally a fail-closed default — a tool whose name carries no
    /// read-only signal (send_email, deploy, post_message, db_query with INSERT …) is
    /// treated as mutating so readonly/plan mode refuses it, unless the server config
    /// sets <see cref="McpServerConfig.TrustReadOnly"/> to vouch for the whole server.
    /// </summary>
    public static bool ResolveMutating(McpServerConfig? server, string toolName)
    {
        if (server?.ReadOnlyTools?.Any(t => t.Equals(toolName, StringComparison.OrdinalIgnoreCase)) == true)
            return false;
        if (server?.MutatingTools?.Any(t => t.Equals(toolName, StringComparison.OrdinalIgnoreCase)) == true)
            return true;
        if (InferMutating(toolName))
            return true;
        return server?.TrustReadOnly != true;
    }

    private static bool InferMutating(string name)
    {
        var n = name.ToLowerInvariant();
        return n.Contains("write") || n.Contains("edit") || n.Contains("create") ||
               n.Contains("delete") || n.Contains("update") || n.Contains("modify") ||
               n.Contains("patch") || n.Contains("apply") || n.Contains("exec") ||
               n.Contains("run") || n.Contains("git_commit") || n.Contains("git_push");
    }
}
