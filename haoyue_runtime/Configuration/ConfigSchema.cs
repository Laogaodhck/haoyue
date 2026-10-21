using System.Text.Json.Nodes;

namespace Haoyue.Runtime.Configuration;

/// <summary>
/// 统一配置源 Schema 管理器（Single Source of Truth）。
/// 全部受管配置字段的类型、默认值、取值范围、枚举值在此集中登记；
/// <see cref="DefaultHaoyueConfig"/> 是种子基线，ConfigStore 负责加载/迁移/校验执行，
/// CLI（Profile 简洁视图）与 Runtime（Provider 详细视图）复用同一登记表——
/// 禁止在任何其他位置再手写默认值或范围校验。
/// </summary>
public static class ConfigSchema
{
    /// <summary>当前 schema 版本。升级时：CurrentVersion + 1，并在 <see cref="Migrations"/> 登记迁移动作。</summary>
    public const int CurrentVersion = 1;

    /// <summary>配置视图归属：cli = CLI Profile 简洁视图管理的字段；runtime = Runtime 详细视图；both = 两端共同管理。</summary>
    public const string ViewCli = "cli";
    public const string ViewRuntime = "runtime";
    public const string ViewBoth = "both";

    /// <summary>
    /// 单个受管字段的登记条目。<paramref name="Path"/> 为 camelCase 点分路径（与 config.json 键一致）。
    /// <paramref name="Default"/> 为 null 表示「用户数据型」字段（如 temperature），缺省时不强制回填，仅在越界时钳制。
    /// </summary>
    public sealed record ConfigField(
        string Path,
        string Type,
        JsonNode? Default,
        double? Min = null,
        double? Max = null,
        string[]? AllowedValues = null,
        string View = ViewRuntime);

    // ---- 登记表：新增配置字段必须在此登记，否则属于未受管字段（校验不覆盖、文档不展示） ----
    private static readonly List<ConfigField> Registry =
    [
        // 根：模型选择三件套（CLI Profile 核心）
        New("provider", "string", null, view: ViewBoth),
        New("model", "string", null, view: ViewBoth),
        New("temperature", "double", null, min: 0, max: 2, view: ViewBoth),

        // 提供商清单是用户数据本体（含 API key 引用、模型表）：登记容器本身，
        // 子项由 ProviderConfig / ConfigStore 逐项容错，不做默认值回填。
        New("providers", "array", null, view: ViewBoth),

        // agent
        New("agent.maxSteps", "int", 40, min: 1, max: 500),
        New("agent.autoVerify", "bool", true, view: ViewBoth),
        New("agent.maxRepairAttempts", "int", 3, min: 0, max: 10),
        New("agent.personality", "enum", "pragmatic", allowed: ["pragmatic", "friendly"]),
        New("agent.enableContextCompaction", "bool", true),
        New("agent.maxOutputContinuations", "int", 6, min: 0, max: 20),
        // 回合预算闸门：0 = 不限。此前任何回合都能无限烧 token/费用/时间（只有步数上限），
        // 模型卡在一个长任务上时用户只能手动停止。
        New("agent.turnTokenBudget", "int", 0, min: 0, max: 100_000_000),
        New("agent.turnCostBudget", "double", 0.0, min: 0, max: 100_000),
        New("agent.turnWallClockSeconds", "int", 0, min: 0, max: 86_400),
        New("agent.mode", "enum", "edit", allowed: ["edit", "plan", "readonly", "auto"], view: ViewBoth),
        New("agent.systemPrompt", "string", "system/default"),
        New("agent.thinkingBudgetTokens", "int", 16_384, min: 0, max: 200_000),
        New("agent.maxToolOutputChars", "int", 60_000, min: 1_000, max: 1_000_000),
        New("agent.bashTimeoutSeconds", "int", 180, min: 5, max: 3_600),
        New("agent.scheduledTurnTimeoutSeconds", "int", 1_800, min: 60, max: 86_400),
        New("agent.language", "enum", "auto", allowed: ["auto", "zh", "en"], view: ViewBoth),
        New("agent.memoryMode", "enum", "auto", allowed: ["auto", "manual"]),
        // 事实层每回合注入条数上限：整块灌入会随库增大稀释提示，故按相关性取 top-K。
        New("agent.factsInjectTopK", "int", 12, min: 1, max: 50),
        New("agent.networkEnabled", "bool", true),
        New("agent.visionModel", "string", null),
        New("agent.delegationEnabled", "bool", true),
        New("agent.delegationMaxDepth", "int", 1, min: 1, max: 4),
        New("agent.reasoningLevel", "enum", "high",
            allowed: ["none", "low", "medium", "high", "max", "xhigh", "ultra"], view: ViewBoth),
        New("agent.rulesEnabled", "bool", true),
        // 会话保留天数：0 = 关闭自动归档与孤儿清理（L-D 维护周期）。
        New("agent.sessionRetentionDays", "int", 90, min: 0, max: 3_650),
        New("agent.scheduleWebhookUrl", "string", null, view: ViewBoth),
        // H-C 记忆固化：定时把近期会话沉淀为项目事实；lookback 与 ConsolidationRunner 的钳制同值。
        New("agent.memoryConsolidationEnabled", "bool", true),
        New("agent.memoryConsolidationLookbackDays", "int", 3, min: 1, max: 30),
        // 本地模型驻留/并发/回收：显存与内存的直接旋钮，越界值只会造成 OOM 或空转。
        New("agent.localModelMaxResident", "int", 2, min: 1, max: 16),
        New("agent.localModelConcurrency", "int", 1, min: 1, max: 16),
        New("agent.localModelIdleUnloadMinutes", "int", 20, min: 0, max: 1_440),
        // 启动预热默认开启：本地模型首字延迟是体验主项（实测 -39%），关闭须显式选择。
        New("agent.localModelPreload", "bool", true),
    // 工具执行策略闸门（运行时强制，模型无法绕过）。规则列表本身是用户数据。
    New("agent.toolPolicy.enabled", "bool", true),
    New("agent.toolPolicy.builtinBashGuard", "bool", true),
    New("agent.toolPolicy.allow", "array", null),
    New("agent.toolPolicy.deny", "array", null),
    // 执行前需用户同意的工具清单：默认 computer_exec（一启用即 profile 级无沙箱 shell）。
    // 用户数据型（数组），空列表 = 仅守卫规则触发审批。
    New("agent.toolPolicy.requireConsentFor", "array", null),
        // Windows Job Object 沙箱；0 表示该项不限制。
        New("agent.bashSandbox.enabled", "bool", false),
        New("agent.bashSandbox.memoryLimitMb", "int", 2_048, min: 0, max: 262_144),
        New("agent.bashSandbox.maxProcesses", "int", 256, min: 0, max: 4_096),
        New("agent.bashSandbox.uiRestrictions", "bool", true),
        // 用户数据型（数组，无默认值）：登记是为了进入文档与漂移治理，
        // 路径解析仍由 PathGuard 逐项容错。
        New("agent.extraReadableRoots", "array", null, view: ViewBoth),

        // routing
        New("routing.fallback", "array", null, view: ViewBoth),
        New("routing.failoverEnabled", "bool", true),
        New("routing.deepSeekOptimizationEnabled", "bool", false),
        New("routing.loadBalance", "enum", "priority",
            allowed: ["priority", "roundRobin", "leastUsed", "lowestCost", "fastest", "sticky"]),
        New("routing.retry.maxAttempts", "int", 3, min: 1, max: 10),
        New("routing.retry.baseDelaySeconds", "double", 1.0, min: 0.1, max: 60),
        New("routing.retry.maxDelaySeconds", "double", 20.0, min: 1, max: 600),
        New("routing.retry.fallbackRetryAttempts", "int", 0, min: 0, max: 5),
        New("routing.retry.circuitBreakThreshold", "int", 4, min: 1, max: 20),
        New("routing.retry.circuitCooldownSeconds", "double", 60.0, min: 1, max: 3_600),
        New("routing.retry.chainBudgetSeconds", "double", 120.0, min: 10, max: 3_600),

        // 知识库语义层：semanticEnabled 关闭或没有可用 embedding 模型时检索自动退回纯词法，
        // 关闭原因经 knowledge.semantic.status / 检索 summary 对用户可见。
        New("knowledge.semanticEnabled", "bool", true),
        New("knowledge.embeddingModel", "string", null),

        // 图像生成（image_generate 工具）。未配置可用图像模型时工具自动隐藏。
        New("imageGen.model", "string", null),
        New("imageGen.size", "string", "1024x1024"),

        // 文档渲染（doc_render 工具）：空路径 = 按内置探测链自动定位。
        New("docRender.enabled", "bool", true),
        New("docRender.toolkitPath", "string", ""),
        New("docRender.pythonPath", "string", ""),

        // 外部访问白名单：空列表 = 不限制（用户数据型）。
        New("web.allowedSites", "array", null),

        // 进化引擎：反思触发（产出始终等人工采纳）。
        New("evolution.autoReflect", "bool", false),
        New("evolution.intervalMinutes", "int", 360, min: 30, max: 10_080),
        New("evolution.thresholdEnabled", "bool", false),
        New("evolution.thresholdSignals", "int", 5, min: 1, max: 50),

        // Computer Use 子系统的独立开关组（默认关闭，启用不影响其余能力）。
        New("computerUse.enabled", "bool", false),
        New("computerUse.driver", "enum", "auto", allowed: ["auto", "windows", "linux", "mac", "vision"]),
        New("computerUse.shellEnabled", "bool", true),
        New("computerUse.shellSandboxed", "bool", true),
        New("computerUse.systemControlEnabled", "bool", true),
        New("computerUse.pythonPath", "string", ""),
        New("computerUse.maxStepsPerTurn", "int", 30, min: 0, max: 1_000),
        New("computerUse.actionDelayMs", "int", 300, min: 0, max: 10_000),

        // Network Ops 插件（默认关闭）；allowMutating 关闭后只允许只读查询。
        New("networkOps.enabled", "bool", false),
        New("networkOps.allowMutating", "bool", true),
        New("networkOps.timeoutSeconds", "int", 60, min: 1, max: 600),

        // 按服务器名键控的字典：容器整体登记为用户数据，子项由 McpConfig 逐项容错。
        New("mcp.servers", "object", null),
    ];

    private static ConfigField New(string path, string type, object? @default,
        double? min = null, double? max = null, string[]? allowed = null, string view = ViewRuntime) =>
        new(path, type, @default is null ? null : JsonValue.Create(@default), min, max, allowed, view);

    /// <summary>只读登记表快照（供文档导出与漂移测试使用）。</summary>
    public static IReadOnlyList<ConfigField> Fields => Registry;

    /// <summary>按点分路径取登记条目。</summary>
    public static ConfigField? Find(string path) =>
        Registry.FirstOrDefault(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase));

    /// <summary>CLI Profile 视图受管的字段集合（简洁层：模型选择 + 模式 + 语言）。</summary>
    public static IReadOnlyList<ConfigField> CliViewFields =>
        Registry.Where(f => f.View is ViewCli or ViewBoth).ToList();

    // ---- schema 版本迁移：fromVersion → 迁移动作（就地改写 JsonObject） ----
    private static readonly Dictionary<int, Action<JsonObject>> Migrations = new()
    {
        // v0 → v1：首次引入 schemaVersion 登记；旧 profiles 结构迁移由 ConfigStore.MigrateLegacyProfiles 承担。
        [0] = static _ => { },
    };

    /// <summary>
    /// 对解析出的 config.json 根对象做规范化 + 校验：
    /// 未知/类型错误的受管字段回填默认值、数值越界钳制、枚举非法值回退默认，
    /// 全部动作产生告警文案但不抛异常（配置永远可用）。
    /// 返回告警列表；<paramref name="normalized"/> 为 true 表示对象被就地修正。
    /// </summary>
    public static (IReadOnlyList<string> Warnings, bool Normalized) Normalize(JsonObject root)
    {
        var warnings = new List<string>();
        var normalized = false;

        var diskVersion = ReadSchemaVersion(root);
        if (diskVersion > CurrentVersion)
        {
            warnings.Add($"配置文件 schemaVersion={diskVersion} 高于当前支持版本 {CurrentVersion}，" +
                         "请升级 haoyue；未知新字段将被忽略。");
        }

        foreach (var field in Registry)
        {
            var segments = field.Path.Split('.');
            JsonObject? node = root;
            foreach (var seg in segments.Take(segments.Length - 1))
            {
                if (node is null || !node.TryGetPropertyValue(seg, out var child) || child is not JsonObject obj)
                {
                    node = null;
                    break;
                }
                node = obj;
            }
            if (node is null) continue; // 段不存在：反序列化时由类型默认值兜底，无需回写

            var leaf = segments[^1];
            var exists = node.TryGetPropertyValue(leaf, out var value);
            if (!exists || value is null || value is JsonObject)
            {
                // 字段缺失 / 显式 null / 结构不是标量：有默认值的受管字段回填默认，用户数据型跳过。
                if (field.Default is not null && (!exists || value is null or JsonObject))
                {
                    if (exists) warnings.Add($"{field.Path} 类型应为 {field.Type}，已回退默认值。");
                    node[leaf] = field.Default.DeepClone();
                    normalized = true;
                }
                continue;
            }

            if (WrongType(value, field.Type))
            {
                if (field.Default is null) continue; // 用户数据型：类型异常不强制回退，交由业务层容错
                warnings.Add($"{field.Path} 类型应为 {field.Type}，已回退默认值。");
                node[leaf] = field.Default.DeepClone();
                normalized = true;
                continue;
            }

            switch (field.Type)
            {
                case "double" or "int" when value is JsonValue num
                    && num.TryGetValue<double>(out var number):
                    if (field.Min is { } min && number < min)
                    {
                        warnings.Add($"{field.Path}={number} 低于下限 {min}，已钳制。");
                        node[leaf] = field.Type == "int" ? JsonValue.Create((int)min) : JsonValue.Create(min);
                        normalized = true;
                    }
                    else if (field.Max is { } max && number > max)
                    {
                        warnings.Add($"{field.Path}={number} 超过上限 {max}，已钳制。");
                        node[leaf] = field.Type == "int" ? JsonValue.Create((int)max) : JsonValue.Create(max);
                        normalized = true;
                    }
                    break;

                case "enum" when value is JsonValue s && s.TryGetValue<string>(out var str):
                    if (field.AllowedValues is { } allowed && !allowed.Contains(str, StringComparer.Ordinal))
                    {
                        var fallback = (field.Default as JsonValue)?.GetValue<string>();
                        warnings.Add($"{field.Path}=\"{str}\" 不是合法值（{string.Join("|", allowed)}），已回退 {fallback}。");
                        node[leaf] = field.Default!.DeepClone();
                        normalized = true;
                    }
                    break;
            }
        }

        return (warnings, normalized);
    }

    private static bool WrongType(JsonNode value, string expected) => expected switch
    {
        "int" => value is not JsonValue iv || !iv.TryGetValue<int>(out _),
        "double" => value is not JsonValue dv || !dv.TryGetValue<double>(out _),
        "bool" => value is not JsonValue bv || !bv.TryGetValue<bool>(out _),
        "string" or "enum" => value is not JsonValue sv || !sv.TryGetValue<string>(out _),
        "array" => value is not JsonArray,
        _ => false,
    };

    /// <summary>读取磁盘文件的 schemaVersion（缺省 0 = 前 schema 时代的遗留文件）。</summary>
    public static int ReadSchemaVersion(JsonObject root) =>
        root.TryGetPropertyValue("schemaVersion", out var v) && v is JsonValue num
        && num.TryGetValue<int>(out var version) ? version : 0;

    /// <summary>把磁盘文件从其记录的版本逐级迁移到当前版本，返回执行的迁移日志。</summary>
    public static IReadOnlyList<string> MigrateToCurrent(JsonObject root)
    {
        var log = new List<string>();
        var version = ReadSchemaVersion(root);
        while (version < CurrentVersion)
        {
            if (Migrations.TryGetValue(version, out var migrate))
            {
                migrate(root);
                log.Add($"schemaVersion {version} → {version + 1}");
            }
            version++;
        }
        if (root["schemaVersion"] is null || ReadSchemaVersion(root) != CurrentVersion)
        {
            root["schemaVersion"] = CurrentVersion;
        }
        return log;
    }

    /// <summary>导出全部受管字段的 Schema 清单（JSON，供文档与工具展示）。</summary>
    public static string ExportSchemaJson()
    {
        var fields = new JsonArray();
        foreach (var field in Registry)
        {
            var entry = new JsonObject
            {
                ["path"] = field.Path,
                ["type"] = field.Type,
                ["view"] = field.View,
            };
            if (field.Default is not null) entry["default"] = field.Default.DeepClone();
            if (field.Min is { } min) entry["min"] = min;
            if (field.Max is { } max) entry["max"] = max;
            if (field.AllowedValues is { } allowed)
                entry["allowedValues"] = new JsonArray(allowed.Select(a => (JsonNode)a).ToArray());
            fields.Add(entry);
        }
        return new JsonObject
        {
            ["schemaVersion"] = CurrentVersion,
            ["fields"] = fields,
        }.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }
}
