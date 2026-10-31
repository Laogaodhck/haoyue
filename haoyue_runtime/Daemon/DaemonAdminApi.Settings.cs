using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Haoyue.Runtime.Configuration;

namespace Haoyue.Runtime.Daemon;

/// <summary>
/// 设置面（settings surface）：把此前「只能手改 config.json」的配置收进 RPC。
///
/// 拆到独立 partial 文件的原因是整个 <c>DaemonAdminApi</c>（含 partial）有类级行数
/// 硬预算（ADR-20261015-giant-class-budget.md），继续往里堆会直接顶红 DocsFactsTests。
/// 这里集中三件事：
/// 1) 路由与容灾全字段（回退链 / 负载均衡 / 重试与熔断 / 本地→云回退）；
/// 2) 智能体专家参数（步数、思考预算、工具输出上限、委派配额、记忆固化、保留期…）；
/// 3) 配置文档读写（全局 ~/.haoyue/config.json 与工作区 &lt;ws&gt;/.haoyue/config.json），
///    给桌面端「专家参数 JSON 编辑器」与「项目属性 → 智能体」提供单一通道。
/// </summary>
internal sealed partial class DaemonAdminApi
{
    private static readonly string[] LoadBalanceModes =
        ["priority", "roundRobin", "leastUsed", "lowestCost", "fastest", "sticky"];

    // ------------------------------------------------------------------ routing

    private JsonObject RoutingJson()
    {
        var routing = runtime.ConfigStore.Config.Routing;
        return new JsonObject
        {
            ["failoverEnabled"] = routing.FailoverEnabled,
            ["deepSeekOptimizationEnabled"] = routing.DeepSeekOptimizationEnabled,
            // 2.7 本地→云故障转移（默认关：防 surprise billing）。
            ["localFailoverToCloud"] = routing.LocalFailoverToCloud,
            ["fallback"] = new JsonArray(routing.Fallback.Select(item => (JsonNode)item).ToArray()),
            ["loadBalance"] = routing.LoadBalance,
            ["retry"] = new JsonObject
            {
                ["maxAttempts"] = routing.Retry.MaxAttempts,
                ["baseDelaySeconds"] = routing.Retry.BaseDelaySeconds,
                ["maxDelaySeconds"] = routing.Retry.MaxDelaySeconds,
                ["fallbackRetryAttempts"] = routing.Retry.FallbackRetryAttempts,
                ["circuitBreakThreshold"] = routing.Retry.CircuitBreakThreshold,
                ["circuitCooldownSeconds"] = routing.Retry.CircuitCooldownSeconds,
                ["chainBudgetSeconds"] = routing.Retry.ChainBudgetSeconds,
            },
        };
    }

    /// <summary>
    /// 回退链条目只做「去空白 + 去重」，不校验模型是否存在：模型被删掉后链里会留一条
    /// 死引用，若保存时直接报错，用户改任何别的字段都会连带失败（无法自愈）。读侧把
    /// 解析不到的条目照原样返回，由界面标成「已失效」。
    /// </summary>
    private string ApplyRoutingJson(JsonObject parameters)
    {
        var routing = runtime.ConfigStore.Config.Routing;

        if (parameters["failoverEnabled"] is JsonValue failoverValue && failoverValue.TryGetValue<bool>(out var failover))
            routing.FailoverEnabled = failover;

        if (parameters["deepSeekOptimizationEnabled"] is JsonValue deepSeekValue
            && deepSeekValue.TryGetValue<bool>(out var deepSeek))
            routing.DeepSeekOptimizationEnabled = deepSeek;

        if (parameters["localFailoverToCloud"] is JsonValue localValue
            && localValue.TryGetValue<bool>(out var localFailover))
            routing.LocalFailoverToCloud = localFailover;

        if (parameters["loadBalance"] is JsonValue loadBalanceValue)
        {
            if (!loadBalanceValue.TryGetValue<string>(out var loadBalance))
                throw new DaemonRequestException("params.loadBalance must be a string");
            var normalized = (loadBalance ?? "").Trim();
            if (!LoadBalanceModes.Contains(normalized, StringComparer.Ordinal))
                throw new DaemonRequestException(
                    $"params.loadBalance must be one of: {string.Join(", ", LoadBalanceModes)}");
            routing.LoadBalance = normalized;
        }

        if (parameters["fallback"] is JsonArray fallbackArray)
        {
            var chain = new List<string>();
            foreach (var node in fallbackArray)
            {
                if (node is not JsonValue value || !value.TryGetValue<string>(out var item))
                    throw new DaemonRequestException("params.fallback must be an array of model refs");
                var trimmed = (item ?? "").Trim();
                if (trimmed.Length == 0 || chain.Contains(trimmed, StringComparer.OrdinalIgnoreCase)) continue;
                chain.Add(trimmed);
            }
            routing.Fallback = chain;
        }

        if (parameters["retry"] is JsonObject retryPatch)
        {
            var retry = routing.Retry;
            retry.MaxAttempts = OptionalInt(retryPatch, "maxAttempts", retry.MaxAttempts, 1, 10);
            retry.BaseDelaySeconds = OptionalDouble(retryPatch, "baseDelaySeconds", retry.BaseDelaySeconds, 0, 60);
            retry.MaxDelaySeconds = OptionalDouble(retryPatch, "maxDelaySeconds", retry.MaxDelaySeconds, 0, 600);
            retry.FallbackRetryAttempts = OptionalInt(retryPatch, "fallbackRetryAttempts", retry.FallbackRetryAttempts, 0, 5);
            retry.CircuitBreakThreshold = OptionalInt(retryPatch, "circuitBreakThreshold", retry.CircuitBreakThreshold, 0, 100);
            retry.CircuitCooldownSeconds = OptionalDouble(retryPatch, "circuitCooldownSeconds", retry.CircuitCooldownSeconds, 0, 3600);
            retry.ChainBudgetSeconds = OptionalDouble(retryPatch, "chainBudgetSeconds", retry.ChainBudgetSeconds, 1, 3600);
        }

        return SetRoutingPersisted();
    }

    private string SetRoutingPersisted()
    {
        runtime.ConfigStore.Save();
        return RoutingJson().ToJsonString();
    }

    // ------------------------------------------------------------ agent surface

    /// <summary>智能体设置的完整读面（面向前端「专家参数」区，字段与 AgentConfig 一一对应）。</summary>
    private JsonObject AgentJson()
    {
        var agent = runtime.ConfigStore.Config.Agent;
        return new JsonObject
        {
            ["networkEnabled"] = agent.NetworkEnabled,
            ["delegationEnabled"] = agent.DelegationEnabled,
            ["visionModel"] = agent.VisionModel ?? "",
            ["autoVerify"] = agent.AutoVerify,
            ["turnTokenBudget"] = agent.TurnTokenBudget,
            ["turnCostBudget"] = agent.TurnCostBudget,
            ["turnWallClockSeconds"] = agent.TurnWallClockSeconds,
            ["compactionTailRatio"] = agent.CompactionTailRatio,
            // 以下为「专家参数」：会改变成本 / 安全 / 数据留存，此前只能手改 config.json。
            ["maxSteps"] = agent.MaxSteps,
            ["thinkingBudgetTokens"] = agent.ThinkingBudgetTokens,
            ["maxToolOutputChars"] = agent.MaxToolOutputChars,
            ["bashTimeoutSeconds"] = agent.BashTimeoutSeconds,
            ["scheduledTurnTimeoutSeconds"] = agent.ScheduledTurnTimeoutSeconds,
            ["maxRepairAttempts"] = agent.MaxRepairAttempts,
            ["maxOutputContinuations"] = agent.MaxOutputContinuations,
            ["enableContextCompaction"] = agent.EnableContextCompaction,
            ["personality"] = agent.Personality,
            ["systemPrompt"] = agent.SystemPrompt,
            ["delegationMaxDepth"] = agent.DelegationMaxDepth,
            ["delegationMaxPerTurn"] = agent.DelegationMaxPerTurn,
            ["factsInjectTopK"] = agent.FactsInjectTopK,
            ["extraReadableRoots"] = new JsonArray(agent.ExtraReadableRoots.Select(p => (JsonNode)p).ToArray()),
            ["sessionRetentionDays"] = agent.SessionRetentionDays,
            ["memoryConsolidationEnabled"] = agent.MemoryConsolidationEnabled,
            ["memoryConsolidationLookbackDays"] = agent.MemoryConsolidationLookbackDays,
            ["scheduleWebhookUrl"] = agent.ScheduleWebhookUrl ?? "",
            ["localModelPreload"] = agent.LocalModelPreload,
            ["localModelMaxResident"] = agent.LocalModelMaxResident,
            ["localModelConcurrency"] = agent.LocalModelConcurrency,
            ["localModelIdleUnloadMinutes"] = agent.LocalModelIdleUnloadMinutes,
            // 5.6 工具执行策略 + Bash 沙箱：安全配置零手改 config.json。
            ["toolPolicy"] = SerializeToolPolicy(agent.ToolPolicy),
            ["bashSandbox"] = new JsonObject
            {
                ["enabled"] = agent.BashSandbox.Enabled,
                ["memoryLimitMb"] = agent.BashSandbox.MemoryLimitMb,
                ["maxProcesses"] = agent.BashSandbox.MaxProcesses,
                ["uiRestrictions"] = agent.BashSandbox.UiRestrictions,
            },
        };
    }

    /// <summary>
    /// 专家参数写入（由 <see cref="SetAgentConfig"/> 在保存前调用）。数值统一钳制而非报错：
    /// 用户把上限调大是「想更宽松」的表达，不该因此拒绝整次保存（与回合预算同口径）。
    /// 枚举/路径这类「写错就静默失效」的字段才显式报错。
    /// </summary>
    private void ApplyAgentSurface(JsonObject parameters)
    {
        var agent = runtime.ConfigStore.Config.Agent;

        agent.MaxSteps = OptionalInt(parameters, "maxSteps", agent.MaxSteps, 1, 500);
        agent.ThinkingBudgetTokens = OptionalInt(parameters, "thinkingBudgetTokens", agent.ThinkingBudgetTokens, 0, 200_000);
        agent.MaxToolOutputChars = OptionalInt(parameters, "maxToolOutputChars", agent.MaxToolOutputChars, 1_000, 500_000);
        agent.BashTimeoutSeconds = OptionalInt(parameters, "bashTimeoutSeconds", agent.BashTimeoutSeconds, 1, 3_600);
        agent.ScheduledTurnTimeoutSeconds = OptionalInt(parameters, "scheduledTurnTimeoutSeconds", agent.ScheduledTurnTimeoutSeconds, 30, 86_400);
        agent.MaxRepairAttempts = OptionalInt(parameters, "maxRepairAttempts", agent.MaxRepairAttempts, 0, 10);
        agent.MaxOutputContinuations = OptionalInt(parameters, "maxOutputContinuations", agent.MaxOutputContinuations, 0, 50);
        agent.DelegationMaxDepth = OptionalInt(parameters, "delegationMaxDepth", agent.DelegationMaxDepth, 1, 4);
        agent.DelegationMaxPerTurn = OptionalInt(parameters, "delegationMaxPerTurn", agent.DelegationMaxPerTurn, 1, 64);
        agent.FactsInjectTopK = OptionalInt(parameters, "factsInjectTopK", agent.FactsInjectTopK, 0, 100);
        // 0 = 关闭自动归档（不删数据，只是不再自动隐藏）；上限 10 年防止手滑输入天文数字。
        agent.SessionRetentionDays = OptionalInt(parameters, "sessionRetentionDays", agent.SessionRetentionDays, 0, 3_650);
        agent.MemoryConsolidationLookbackDays = OptionalInt(parameters, "memoryConsolidationLookbackDays", agent.MemoryConsolidationLookbackDays, 1, 30);
        // 显存治理：与 LocalModelCache.Configure 的钳制区间保持一致。
        agent.LocalModelMaxResident = OptionalInt(parameters, "localModelMaxResident", agent.LocalModelMaxResident, 1, 16);
        agent.LocalModelConcurrency = OptionalInt(parameters, "localModelConcurrency", agent.LocalModelConcurrency, 1, 16);
        agent.LocalModelIdleUnloadMinutes = OptionalInt(parameters, "localModelIdleUnloadMinutes", agent.LocalModelIdleUnloadMinutes, 0, 1_440);

        if (Bool(parameters, "enableContextCompaction") is { } compaction) agent.EnableContextCompaction = compaction;
        if (Bool(parameters, "memoryConsolidationEnabled") is { } consolidation) agent.MemoryConsolidationEnabled = consolidation;
        if (Bool(parameters, "localModelPreload") is { } preload) agent.LocalModelPreload = preload;

        if (parameters["personality"] is JsonValue personalityValue)
        {
            if (!personalityValue.TryGetValue<string>(out var personality))
                throw new DaemonRequestException("params.personality must be a string");
            var normalized = (personality ?? "").Trim().ToLowerInvariant();
            if (normalized is not ("pragmatic" or "friendly"))
                throw new DaemonRequestException("params.personality must be pragmatic or friendly");
            agent.Personality = normalized;
        }

        if (parameters["systemPrompt"] is JsonValue promptValue)
        {
            if (!promptValue.TryGetValue<string>(out var promptKey))
                throw new DaemonRequestException("params.systemPrompt must be a prompt key string");
            var trimmed = (promptKey ?? "").Trim();
            if (trimmed.Length == 0)
                throw new DaemonRequestException("params.systemPrompt 不能为空（提示词键名，例如 system/default）");
            agent.SystemPrompt = trimmed;
        }

        if (parameters["extraReadableRoots"] is JsonArray rootsArray)
        {
            var roots = new List<string>();
            foreach (var node in rootsArray)
            {
                if (node is not JsonValue value || !value.TryGetValue<string>(out var raw))
                    throw new DaemonRequestException("params.extraReadableRoots must be an array of absolute paths");
                var trimmed = (raw ?? "").Trim();
                if (trimmed.Length == 0) continue;
                if (!Path.IsPathFullyQualified(trimmed))
                    throw new DaemonRequestException($"额外可读目录必须是绝对路径：{trimmed}");
                roots.Add(Path.GetFullPath(trimmed));
            }
            agent.ExtraReadableRoots = roots;
        }

        if (parameters["scheduleWebhookUrl"] is JsonValue webhookValue)
        {
            if (!webhookValue.TryGetValue<string>(out var webhook))
                throw new DaemonRequestException("params.scheduleWebhookUrl must be a string");
            var trimmed = (webhook ?? "").Trim();
            if (trimmed.Length == 0)
            {
                agent.ScheduleWebhookUrl = null;
            }
            else
            {
                if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
                    || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                    throw new DaemonRequestException("params.scheduleWebhookUrl 必须是 http(s) 绝对地址");
                agent.ScheduleWebhookUrl = trimmed;
            }
        }
    }

    private static bool? Bool(JsonObject parameters, string key)
    {
        if (parameters[key] is not JsonValue value) return null;
        if (!value.TryGetValue<bool>(out var flag))
            throw new DaemonRequestException($"params.{key} must be a boolean");
        return flag;
    }

    // ------------------------------------------------------------ public surface

    /// <summary>config.get：全局或工作区配置文档（密钥不回传）。</summary>
    public string GetConfig(JsonObject parameters) => GetConfigDocument(parameters);

    /// <summary>config.save 的工作区分支；全局分支仍由 DaemonServer 直接走 ConfigStore.ApplyPatch。</summary>
    public string SaveWorkspaceConfig(JsonObject parameters, JsonObject fields) =>
        SaveWorkspaceConfigPatch(WorkspaceRootFrom(parameters)!, fields);

    // ------------------------------------------------------------- config document

    /// <summary>
    /// 配置文档读写。scope=workspace 时操作 &lt;workspace&gt;/.haoyue/config.json，
    /// 否则操作全局 ~/.haoyue/config.json。密钥一律不回传（<c>apiKey</c> 整体移除，
    /// 只给 <c>apiKeyConfigured</c>），写侧照旧走「字段补丁 + 深合并」，因此编辑器
    /// 不回传的字段不会被清空。
    /// </summary>
    private string GetConfigDocument(JsonObject parameters)
    {
        var root = WorkspaceRootFrom(parameters);
        if (root is null)
        {
            var document = JsonSerializer.SerializeToNode(
                runtime.ConfigStore.Config, HaoyueJsonContext.Default.HaoyueConfig) as JsonObject ?? new JsonObject();
            RedactSecrets(document);
            return new JsonObject
            {
                ["scope"] = "global",
                ["path"] = HaoyuePaths.ConfigFile,
                ["exists"] = File.Exists(HaoyuePaths.ConfigFile),
                ["config"] = document,
            }.ToJsonString();
        }

        var file = WorkspaceConfigFile(root);
        JsonObject config;
        if (File.Exists(file))
        {
            try
            {
                config = JsonNode.Parse(File.ReadAllText(file)) as JsonObject ?? new JsonObject();
            }
            catch (JsonException)
            {
                throw new DaemonRequestException($"工作区配置文件不是合法 JSON：{file}", DaemonErrorCode.Conflict);
            }
        }
        else
        {
            config = new JsonObject();
        }

        return new JsonObject
        {
            ["scope"] = "workspace",
            ["path"] = file,
            ["exists"] = File.Exists(file),
            ["config"] = config,
        }.ToJsonString();
    }

    /// <summary>
    /// 工作区配置补丁写入。写盘后再把结果同步回内存中的 <c>Workspace.Config</c>，
    /// 否则本轮之后的回合仍然读旧快照——那正是「改了设置不生效」的经典坑。
    /// </summary>
    private string SaveWorkspaceConfigPatch(string root, JsonObject patch)
    {
        var file = WorkspaceConfigFile(root);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);

        WorkspaceConfig current;
        if (File.Exists(file))
        {
            try
            {
                current = JsonSerializer.Deserialize(File.ReadAllText(file), HaoyueJsonContext.Default.WorkspaceConfig)
                          ?? new WorkspaceConfig();
            }
            catch (JsonException)
            {
                throw new DaemonRequestException($"工作区配置文件不是合法 JSON：{file}", DaemonErrorCode.Conflict);
            }
        }
        else
        {
            current = new WorkspaceConfig();
        }

        var node = JsonSerializer.SerializeToNode(current, HaoyueJsonContext.Default.WorkspaceConfig) as JsonObject
                   ?? new JsonObject();
        MergePatch(node, patch);
        var updated = node.Deserialize(HaoyueJsonContext.Default.WorkspaceConfig) ?? current;
        File.WriteAllText(file, JsonSerializer.Serialize(updated, HaoyueJsonContext.Default.WorkspaceConfig));

        if (SameRoot(root, runtime.Workspace.Root) && runtime.Workspace.Config is { } live)
            CopyWorkspaceConfig(updated, live);

        return new JsonObject
        {
            ["scope"] = "workspace",
            ["path"] = file,
            ["exists"] = true,
            ["applied"] = runtime.Workspace.Config is not null || !SameRoot(root, runtime.Workspace.Root),
        }.ToJsonString();
    }

    /// <summary>
    /// 内存同步：<c>WorkspaceInfo.Config</c> 是 init-only，不能整体换掉，只能逐字段拷贝。
    /// 新增工作区配置字段时这里必须同步（否则界面改完当轮不生效）。
    /// </summary>
    private static void CopyWorkspaceConfig(WorkspaceConfig from, WorkspaceConfig to)
    {
        to.Provider = from.Provider;
        to.Model = from.Model;
        to.Temperature = from.Temperature;
        to.Mode = from.Mode;
        to.SystemPrompt = from.SystemPrompt;
        to.Personality = from.Personality;
        to.Language = from.Language;
        to.DisabledSkills = from.DisabledSkills;
        to.DisabledTools = from.DisabledTools;
        to.Mcp = from.Mcp;
        to.AutoVerify = from.AutoVerify;
        to.VerifyCommand = from.VerifyCommand;
        to.VerifyCommands = from.VerifyCommands;
    }

    /// <summary>递归深合并（与 ConfigStore.ApplyPatch 的 MergePatch 同语义）。</summary>
    private static void MergePatch(JsonObject target, JsonObject patch)
    {
        foreach (var (name, value) in patch)
        {
            if (value is JsonObject patchObject && target[name] is JsonObject targetObject)
            {
                MergePatch(targetObject, patchObject);
                continue;
            }
            target[name] = value is null ? null : JsonNode.Parse(value.ToJsonString());
        }
    }

    /// <summary>凭据永不回传：apiKey 整体移除（只报是否已配置），Authorization 类头按名屏蔽。</summary>
    private static void RedactSecrets(JsonObject document)
    {
        if (document["Providers"] is not JsonArray providers) return;
        foreach (var node in providers)
        {
            if (node is not JsonObject provider) continue;
            var hasKey = provider["ApiKey"] is JsonValue keyValue
                         && keyValue.TryGetValue<string>(out var key)
                         && !string.IsNullOrWhiteSpace(key);
            provider.Remove("ApiKey");
            provider["ApiKeyConfigured"] = hasKey;

            if (provider["Headers"] is not JsonObject headers) continue;
            foreach (var (headerName, _) in headers.ToList())
            {
                if (IsSecretHeader(headerName)) headers[headerName] = "";
            }
        }
    }

    // ------------------------------------------------- provider / model advanced (3.7)

    /// <summary>
    /// provider.upsert 的高级字段：自定义鉴权头、组织、思考档位映射、KV 前缀复用、线程数。
    /// 这些字段此前只能在配置文件里手改。头的写入口是「整表提交 + 空值沿用」——密钥类的值
    /// 读侧不回传（见 <see cref="RedactedHeaders"/>），因此界面提交空串必须理解为保持原值。
    /// </summary>
    private void ApplyProviderAdvanced(JsonObject parameters, ProviderConfig provider)
    {
        if (parameters.ContainsKey("organization")) provider.Organization = Text(parameters, "organization");
        if (parameters["localPrefixReuse"] is JsonValue reuse && reuse.TryGetValue<bool>(out var prefixReuse))
            provider.LocalPrefixReuse = prefixReuse;
        if (parameters["threads"] is JsonValue threads)
            provider.Threads = threads.TryGetValue<int>(out var count) && count > 0 ? count : null;
        if (parameters.ContainsKey("headers"))
            provider.Headers = MergeHeaders(provider.Headers, parameters["headers"] as JsonObject);
        if (parameters.ContainsKey("reasoningEffortMap"))
            provider.ReasoningEffortMap = StringMap(parameters["reasoningEffortMap"] as JsonObject, keepExisting: null);
    }

    /// <summary>模型级高级字段：备用回退模型与定价（定价进入费用预算与用量统计口径）。</summary>
    private static void ApplyModelAdvanced(JsonObject node, ModelConfig model)
    {
        if (node.ContainsKey("fallbackModelId")) model.FallbackModelId = Text(node, "fallbackModelId");
        if (node["inputPricePerMTok"] is JsonValue input && input.TryGetValue<decimal>(out var inputPrice))
            model.InputPricePerMTok = inputPrice;
        if (node["outputPricePerMTok"] is JsonValue output && output.TryGetValue<decimal>(out var outputPrice))
            model.OutputPricePerMTok = outputPrice;
    }

    /// <summary>头表合并：空值沿用已存值（密钥类头读侧被屏蔽），未出现的键视为删除。</summary>
    private static Dictionary<string, string>? MergeHeaders(
        Dictionary<string, string>? existing,
        JsonObject? incoming)
    {
        if (incoming is null) return null; // 显式 null：清空全部自定义头
        var merged = StringMap(incoming, existing) ?? [];
        return merged.Count > 0 ? merged : null;
    }

    /// <summary>JSON 对象 → 字符串字典；值留空且 keepExisting 有对应键时保留原值。</summary>
    private static Dictionary<string, string>? StringMap(
        JsonObject? source,
        Dictionary<string, string>? keepExisting)
    {
        if (source is null) return null;
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, node) in source)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            var value = node is JsonValue json && json.TryGetValue<string>(out var text) ? (text ?? "").Trim() : "";
            if (value.Length == 0)
            {
                if (keepExisting is not null && keepExisting.TryGetValue(name.Trim(), out var kept)) map[name.Trim()] = kept;
                continue;
            }
            map[name.Trim()] = value;
        }
        return map.Count > 0 ? map : null;
    }

    private static JsonObject? RedactedHeaders(Dictionary<string, string>? headers)
    {
        if (headers is null || headers.Count == 0) return null;
        var node = new JsonObject();
        foreach (var (name, value) in headers)
            node[name] = IsSecretHeader(name) ? "" : value;
        return node;
    }

    private static JsonObject? StringMapJson(Dictionary<string, string>? map)
    {
        if (map is null || map.Count == 0) return null;
        var node = new JsonObject();
        foreach (var (name, value) in map) node[name] = value;
        return node;
    }

    private static string? Text(JsonObject node, string key)
    {
        if (node[key] is not JsonValue value || !value.TryGetValue<string>(out var text)) return null;
        var trimmed = (text ?? "").Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }

    private static bool IsSecretHeader(string name) =>
        name.Contains("authorization", StringComparison.OrdinalIgnoreCase)
        || name.Contains("api-key", StringComparison.OrdinalIgnoreCase)
        || name.Contains("apikey", StringComparison.OrdinalIgnoreCase)
        || name.Contains("token", StringComparison.OrdinalIgnoreCase)
        || name.Contains("key", StringComparison.OrdinalIgnoreCase);

    private static string WorkspaceConfigFile(string root) => Path.Combine(root, ".haoyue", "config.json");

    private static bool SameRoot(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    /// <summary>params.workspace 缺省/空串 = 全局配置；否则必须是已存在目录的绝对路径。</summary>
    private static string? WorkspaceRootFrom(JsonObject parameters)
    {
        if (parameters["workspace"] is not JsonValue value) return null;
        if (!value.TryGetValue<string>(out var raw))
            throw new DaemonRequestException("params.workspace must be a directory path string");
        var trimmed = (raw ?? "").Trim();
        if (trimmed.Length == 0) return null;
        if (!Path.IsPathFullyQualified(trimmed))
            throw new DaemonRequestException($"params.workspace 必须是绝对路径：{trimmed}");
        var root = Path.GetFullPath(trimmed);
        if (!Directory.Exists(root))
            throw new DaemonRequestException($"工作区目录不存在：{root}", DaemonErrorCode.NotFound);
        return root;
    }
}
