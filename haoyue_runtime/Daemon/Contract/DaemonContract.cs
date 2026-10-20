using System.Text.Json.Nodes;

namespace Haoyue.Runtime.Daemon;

/// <summary>
/// 跨语言契约错误码（唯一事实源）。daemon 的每个 error 事件必须在
/// details.code 携带这里的 wire 值；TS 侧按 code 分支处理，data 文本仅供展示。
/// </summary>
public enum DaemonErrorCode
{
    /// <summary>未分类失败（兜底）。新代码应尽量给出更精确的码。</summary>
    RequestFailed,
    /// <summary>请求行不是合法 JSON，或 id/method 字段类型错误。</summary>
    InvalidRequest,
    /// <summary>方法名不在契约目录中（客户端调用了服务端不认识的方法，通常是版本不匹配）。</summary>
    UnknownMethod,
    /// <summary>参数缺失、类型错误或取值非法。</summary>
    InvalidParams,
    /// <summary>目标资源（会话、回合、任务等）不存在。</summary>
    NotFound,
    /// <summary>与当前状态冲突（如会话已有活动回合）。</summary>
    Conflict,
    /// <summary>握手 token 缺失或不匹配。</summary>
    Unauthorized,
    /// <summary>客户端声明的协议版本与 daemon 不兼容（主版本号不一致）。</summary>
    VersionMismatch,
    /// <summary>
    /// 方法已命中契约、但执行期抛错（服务端实现故障）。details.method 指明失败的方法，
    /// 与「方法不存在」的 <see cref="UnknownMethod"/> 严格区分。
    /// </summary>
    InternalError,
}

public static class DaemonErrorCodeExtensions
{
    /// <summary>契约 wire 值（camelCase）。新增枚举值必须同步 <see cref="DaemonContract"/>。</summary>
    public static string ToWire(this DaemonErrorCode code) => code switch
    {
        DaemonErrorCode.RequestFailed => "requestFailed",
        DaemonErrorCode.InvalidRequest => "invalidRequest",
        DaemonErrorCode.UnknownMethod => "unknownMethod",
        DaemonErrorCode.InvalidParams => "invalidParams",
        DaemonErrorCode.NotFound => "notFound",
        DaemonErrorCode.Conflict => "conflict",
        DaemonErrorCode.Unauthorized => "unauthorized",
        DaemonErrorCode.VersionMismatch => "versionMismatch",
        DaemonErrorCode.InternalError => "internalError",
        _ => "requestFailed",
    };
}

/// <summary>契约目录中的单个方法文档。</summary>
public sealed record DaemonMethodDoc(
    string Name,
    string Category,
    string Description,
    string[] TerminalEvents,
    JsonObject? Params = null,
    JsonObject? Result = null,
    string[]? ErrorCodes = null)
{
    /// <summary>params 是否已指定 JSON Schema（渐进补全：未指定为 pending）。</summary>
    public string SchemaStatus => Params is null ? "pending" : "specified";

    /// <summary>result 是否已指定 JSON Schema（未指定为 pending）。</summary>
    public string ResultSchemaStatus => Result is null ? "pending" : "specified";

    /// <summary>
    /// 该方法可能返回的契约错误码（wire 值）。error 终态本身对所有方法隐式可用；
    /// daemon 对未预期异常统一回 <see cref="DaemonErrorCode.InternalError"/>，
    /// 因此导出时自动并入，逐方法声明只写「该方法特有的失败模式」。
    /// 信封解析失败（非法 JSON）在所有方法之前发生，走 invalidRequest，不在此列。
    /// </summary>
    public string[] ExportedErrorCodes
    {
        get
        {
            var fallback = DaemonErrorCode.InternalError.ToWire();
            if (ErrorCodes is not { Length: > 0 }) return [fallback];
            return ErrorCodes.Contains(fallback, StringComparer.Ordinal)
                ? ErrorCodes
                : [.. ErrorCodes, fallback];
        }
    }

    /// <summary>是否已显式声明错误码（含「无特殊失败模式」的空声明 E()）。</summary>
    public bool ErrorCodesDeclared => ErrorCodes is not null;
}

/// <summary>
/// Daemon JSONL 协议的唯一契约源（Contract First）。
/// 方法目录、错误码、事件名、信封结构均以此处为准：
/// <list type="bullet">
/// <item><see cref="DaemonServer.ProtocolInfoJson"/> 的 methods 数组由本目录派生，禁止手工维护第二份清单；</item>
/// <item>新增方法 = 在 <see cref="Methods"/> 登记 + switch 分发 + 测试；</item>
/// <item>TS 侧通过 contracts/daemon-contract.json 快照生成类型，不手抄结构。</item>
/// </list>
/// </summary>
public static class DaemonContract
{
    public const string ContractVersion = "1.0";

    /// <summary>
    /// 协议版本兼容规则（版本契约）：主版本号必须一致（不一致 = Breaking Change，拒绝连接）；
    /// 客户端次版本号高于 daemon 时允许连接但返回升级提示。
    /// 返回 false 时 <paramref name="warning"/> 为 null；返回 true 时 warning 可为 null（完全兼容）。
    /// </summary>
    public static bool IsProtocolCompatible(string daemonVersion, string clientVersion, out string? warning)
    {
        warning = null;
        var daemon = ParseVersion(daemonVersion);
        var client = ParseVersion(clientVersion);
        if (daemon is null || client is null) return true; // 版本串无法解析：不做强制（向前容忍）
        if (client.Value.Major != daemon.Value.Major)
        {
            return false;
        }
        if (client.Value.Minor > daemon.Value.Minor)
        {
            warning = $"客户端协议版本 {clientVersion} 高于 daemon {daemonVersion}，" +
                      $"新增能力不可用；请升级 haoyue daemon。";
        }
        return true;
    }

    private static (int Major, int Minor)? ParseVersion(string version)
    {
        var parts = version.Split('.');
        if (parts.Length < 2
            || !int.TryParse(parts[0], out var major)
            || !int.TryParse(parts[1], out var minor))
        {
            return null;
        }
        return (major, minor);
    }

    /// <summary>回合过程中的流式中间事件（非终态）。</summary>
    public static readonly string[] StreamEvents =
    [
        "delta", "thinking", "steer", "image_view", "status", "tool_start",
        "tool_done", "file_diff", "model_start", "usage", "workflow", "plan_update",
        // 内核告警（执行策略拒绝、上下文压缩降级、输出截断、空回答…）。此前 WarningEvent
        // 只在运行时内部发布，既不在契约也不在 daemon 转发分支里，用户在桌面看不到
        // 「为什么被拦」。
        "warning",
        // 「本次允许」审批请求（P1-2.3）：内置高危守卫拦截执行类工具时挂起调用，
        // 用户选择经 agent.respondApproval 回传。
        "approval_request",
    ];

    /// <summary>请求的终态事件（错误终态 error 对所有方法隐式可用）。</summary>
    public static readonly string[] TerminalEvents = ["pong", "result", "done", "cancelled", "error", "bye"];

    /// <summary>服务端主动广播事件（不属于任何请求的响应）。</summary>
    public static readonly string[] BroadcastEvents =
    [
        "schedule.updated", "schedule.upcoming", "task.updated", "evolution.reflected", "mcp.updated", "turn.interrupted",
        "memory.consolidated", "model.load.progress",
    ];

    private static readonly Lazy<IReadOnlyList<DaemonMethodDoc>> _methods = new(BuildCatalog);

    public static IReadOnlyList<DaemonMethodDoc> Methods => _methods.Value;

    public static IReadOnlyList<string> MethodNames => _methods.Value.Select(m => m.Name).ToList();

    private static DaemonMethodDoc M(
        string name, string category, string description,
        string[]? terminal = null, JsonObject? parameters = null, JsonObject? result = null,
        string[]? errors = null) =>
        new(name, category, description, terminal ?? ["result"], parameters, result, errors);

    /// <summary>逐方法错误码声明辅助（wire 值）；导出时自动并入 internalError 兜底。</summary>
    private static string[] E(params DaemonErrorCode[] codes) =>
        codes.Select(DaemonErrorCodeExtensions.ToWire).ToArray();

    // ---- 可复用的参数 Schema 构件（JSON Schema 2020-12 子集） ----

    private static JsonObject SObject(params (string Name, JsonObject Schema, bool Required)[] properties)
    {
        var props = new JsonObject();
        var required = new JsonArray();
        foreach (var (name, schema, isRequired) in properties)
        {
            // 共享 schema 实例（如 EmptyParams/ImageArray）会被多个方法引用，插入前必须克隆。
            props[name] = (JsonObject)schema.DeepClone();
            if (isRequired) required.Add((JsonNode)name);
        }
        var schemaObject = new JsonObject { ["type"] = "object", ["properties"] = props };
        if (required.Count > 0) schemaObject["required"] = required;
        return schemaObject;
    }

    private static JsonObject SString(string description, params string[] allowed)
    {
        var schema = new JsonObject { ["type"] = "string", ["description"] = description };
        if (allowed.Length > 0)
        {
            var values = new JsonArray();
            foreach (var value in allowed) values.Add((JsonNode)value);
            schema["enum"] = values;
        }
        return schema;
    }

    private static JsonObject SInteger(string description) =>
        new() { ["type"] = "integer", ["description"] = description };

    private static JsonObject SNumber(string description) =>
        new() { ["type"] = "number", ["description"] = description };

    private static JsonObject SBoolean(string description) =>
        new() { ["type"] = "boolean", ["description"] = description };

    private static JsonObject SArray(string description, JsonObject? items = null)
    {
        var schema = new JsonObject { ["type"] = "array", ["description"] = description };
        if (items is not null) schema["items"] = items;
        return schema;
    }

    private static readonly JsonObject EmptyParams = SObject();

    private static readonly JsonObject SessionRef = SObject(
        ("workspace", SString("工作区路径；缺省用当前工作区"), false),
        ("global", SBoolean("true 时使用全局工作区"), false));

    private static readonly JsonObject ScheduleUpsert = SObject(
        ("id", SString("更新已有任务时必填；缺省为新建"), false),
        ("name", SString("任务名称"), true),
        ("prompt", SString("调度回合的提示词"), true),
        ("cron", SString("cron 表达式"), true),
        ("workspace", SString("工作区路径"), false),
        ("enabled", SBoolean("缺省 true"), false));

    private static readonly JsonObject IdParams = SObject(
        ("id", SString("资源 id"), true));

    private static readonly JsonObject IdOnlyParams = SObject(
        ("id", SString("会话 id"), true));

    private static readonly JsonObject ProviderUpsert = SObject(
        ("id", SString("提供商标识"), true),
        ("kind", SString("提供商类型", "openai", "anthropic", "local"), false),
        ("name", SString("显示名"), false),
        ("baseUrl", SString("API 基础地址"), false),
        ("proxy", SString("代理地址"), false),
        ("enabled", SBoolean("是否启用"), false),
        ("priority", SInteger("路由优先级"), false),
        ("timeoutSeconds", SInteger("请求超时（秒）"), false),
        ("modelListUrl", SString("模型列表地址"), false),
        ("modelsDirectory", SString("本地模型目录"), false),
        ("promptCaching", SBoolean("提示缓存开关"), false),
        ("apiKey", SString("API Key（留空保持已存值；响应不回传明文）"), false),
        ("clearApiKey", SBoolean("清除已存 API Key"), false),
        ("modelDetails", SArray("模型明细列表", SObject(
            ("id", SString("模型 id"), true),
            ("alias", SString("显示别名"), false),
            ("contextWindow", SInteger("上下文窗口"), false),
            ("maxOutput", SInteger("最大输出 token"), false),
            ("vision", SBoolean("是否支持视觉"), false))), false));

    private static readonly string[] ReasoningValues =
        ["none", "low", "medium", "high", "max", "xhigh", "ultra"];

    private static readonly JsonObject ImageItem = SObject(
        ("name", SString("文件名"), false),
        ("mediaType", SString("MIME 类型"), false),
        ("data", SString("base64 内容"), true),
        ("sizeBytes", SInteger("字节数"), false));

    private static readonly JsonObject ImageArray = SArray("图片附件（base64 data）", ImageItem);

    private static JsonObject ChatParams(bool includeSteer)
    {
        var builder = new List<(string, JsonObject, bool)>
        {
            ("message", SString("用户消息文本（message 或 prompt 二选一）"), false),
            ("prompt", SString("message 的别名"), false),
            ("images", ImageArray, false),
            ("sessionId", SString("续聊的会话 id"), false),
            ("workspace", SString("工作区路径"), false),
            ("global", SBoolean("使用全局工作区"), false),
            ("reasoningLevel", SString("推理深度", ReasoningValues), false),
            ("expertId", SString("绑定专家 persona id（expert.list 可查；空串解除绑定）"), false),
        };
        if (includeSteer) builder.Add(("requestId", SInteger("目标回合请求 id"), false));
        return SObject(builder.ToArray());
    }

    // ---- 逐方法参数 Schema（均按 DaemonAdminApi / DaemonServer 实际 params[...] 读取核实） ----

    private static readonly JsonObject AdvancedSet = SObject(
        ("failoverEnabled", SBoolean("路由故障转移开关"), false),
        ("networkEnabled", SBoolean("联网开关"), false),
        ("deepSeekOptimizationEnabled", SBoolean("DeepSeek 优化开关"), false),
        ("computerUseEnabled", SBoolean("Computer Use 开关"), false),
        ("computerUseDriver", SString("Computer Use 驱动"), false),
        ("networkOpsEnabled", SBoolean("网络运维插件开关"), false),
        ("networkOpsAllowMutating", SBoolean("网络运维：允许变更类命令"), false),
        ("language", SString("界面语言"), false),
        ("rulesEnabled", SBoolean("AGENTS.md 规则注入开关"), false),
        ("memoryMode", SString("记忆模式"), false),
        ("localInference", SObject(
            ("providerId", SString("本地提供商 id"), false),
            ("gpuLayers", SInteger("GPU 卸载层数，0 为纯 CPU"), false),
            ("contextLength", SInteger("上下文长度"), false),
            ("kvCacheQuantization", SString("KV 缓存量化"), false),
            ("flashAttention", SBoolean("Flash Attention 开关"), false)), false));

    private static readonly JsonObject McpServerInput = SObject(
        ("transport", SString("传输方式（stdio / sse）"), false),
        ("command", SString("stdio 启动命令"), false),
        ("url", SString("远程服务地址"), false),
        ("cwd", SString("stdio 子进程工作目录；留空默认当前工作区根目录"), false),
        ("enabled", SBoolean("是否启用"), false),
        ("args", SArray("命令参数"), false),
        ("env", SString("环境变量对象"), false));

    private static readonly JsonObject KnowledgeScope = SObject(
        ("workspace", SString("工作区路径；缺省用当前工作区"), false),
        ("global", SBoolean("true 时使用全局知识库"), false));

    /// <summary>知识/记忆方法通用：workspace/global 选择器 + 逐方法字段。</summary>
    private static JsonObject Scope(params (string Name, JsonObject Schema, bool Required)[] extras) =>
        ExtendSchema(KnowledgeScope, extras);

    /// <summary>会话方法通用：workspace/global 选择器 + 逐方法字段。</summary>
    private static JsonObject SessionExt(params (string Name, JsonObject Schema, bool Required)[] extras) =>
        ExtendSchema(SessionRef, extras);

    /// <summary>克隆基础选择器 schema 并追加逐方法字段；required 键可能不存在，需按需创建。</summary>
    private static JsonObject ExtendSchema(
        JsonObject baseSchema, (string Name, JsonObject Schema, bool Required)[] extras)
    {
        var clone = (JsonObject)baseSchema.DeepClone();
        var props = clone["properties"]!.AsObject();
        foreach (var (name, schema, isRequired) in extras)
            props[name] = (JsonObject)schema.DeepClone();
        var requiredFields = extras.Where(item => item.Required).Select(item => item.Name).ToList();
        if (requiredFields.Count == 0) return clone;
        var required = clone["required"]?.AsArray() ?? new JsonArray();
        foreach (var field in requiredFields) required.Add((JsonNode)field);
        clone["required"] = required;
        return clone;
    }

    // ---- 可复用的 result Schema 构件（JSON Schema 2020-12 子集） ----
    // result schema 描述的是终态事件 data 字段（JSON 文本）解析后的形状。
    // additionalProperties 不设限（默认 true）：服务端加字段不破坏既有客户端，
    // 但已登记字段的类型受本 schema 约束。

    /// <summary>data 为纯文本（"ok"、"started"、id 回显、撤销摘要等）。</summary>
    private static readonly JsonObject RText = new() { ["type"] = "string" };

    /// <summary>data 为任意 JSON 对象（结构随配置演进，仅约束顶层为对象）。</summary>
    private static readonly JsonObject RAnyObject = new() { ["type"] = "object" };

    /// <summary>data 为任意 JSON 数组。</summary>
    private static readonly JsonObject RAnyArray = new() { ["type"] = "array" };

    private static JsonObject RObject(params (string Name, JsonObject Schema)[] properties)
    {
        var props = new JsonObject();
        foreach (var (name, schema) in properties) props[name] = (JsonObject)schema.DeepClone();
        return new JsonObject { ["type"] = "object", ["properties"] = props };
    }

    private static JsonObject RArray(JsonObject itemSchema) =>
        // 共享 schema 实例（如 ProviderResult/SessionHeaderResult）会被多个方法引用，
        // 插入前必须克隆：JsonNode 只允许一个父节点，直接挂第二个数组上会抛
        // "The node already has a parent"。
        new() { ["type"] = "array", ["items"] = (JsonObject)itemSchema.DeepClone() };

    /// <summary>{ ok, message } 确认payload。</summary>
    private static readonly JsonObject ROk = RObject(
        ("ok", SBoolean("是否成功")),
        ("message", SString("人类可读说明")));

    /// <summary>会话头（SessionHeader 序列化形状）。</summary>
    private static readonly JsonObject SessionHeaderResult = RObject(
        ("id", SString("会话 id")),
        ("title", SString("会话标题")),
        ("workspace", SString("归属工作区路径")),
        ("archived", SBoolean("是否已归档")),
        ("reasoningLevel", SString("推理深度")),
        ("networkEnabled", SBoolean("会话级联网开关")),
        ("expertId", SString("绑定的专家 persona id")),
        ("llmRounds", SInteger("LLM 调用轮数")),
        ("executionSteps", SInteger("工具执行步数")),
        ("inputTokens", SInteger("最近一次输入 token")),
        ("totalInputTokens", SInteger("累计输入 token")),
        ("cachedInputTokens", SInteger("累计缓存命中输入 token")),
        ("outputTokens", SInteger("累计输出 token")),
        ("outputElapsedMs", SInteger("累计输出耗时毫秒")),
        ("createdAt", SString("创建时间（ISO 8601）")),
        ("updatedAt", SString("更新时间（ISO 8601）")));

    /// <summary>提供商（ProviderJson 形状；apiKey 只回持凭状态，明文永不跨管道）。</summary>
    private static readonly JsonObject ProviderResult = RObject(
        ("id", SString("提供商 id")),
        ("name", SString("显示名")),
        ("kind", SString("类型", "openai", "anthropic", "local")),
        ("baseUrl", SString("API 地址")),
        ("apiKeyConfigured", SBoolean("是否已配置 API Key（不回传明文）")),
        ("models", SArray("模型 id 清单")),
        ("modelDetails", RArray(RObject(
            ("id", SString("模型 id")),
            ("alias", SString("显示别名")),
            ("contextWindow", SInteger("上下文窗口 token")),
            ("maxOutput", SInteger("最大输出 token")),
            ("vision", SBoolean("视觉能力")),
            ("toolCalling", SBoolean("工具调用能力")),
            ("localPath", SString("本地 GGUF 路径")),
            ("mmprojPath", SString("多模态投影器路径")),
            ("load", SString("加载配置对象；null=自动"))))),
        ("enabled", SBoolean("是否启用")),
        ("priority", SInteger("路由优先级")),
        ("timeoutSeconds", SInteger("超时秒数")),
        ("modelListUrl", SString("模型列表地址")),
        ("modelsDirectory", SString("模型目录")),
        ("defaultModelsDirectory", SString("解析后的默认模型目录")),
        ("promptCaching", SBoolean("提示词缓存开关")),
        ("proxy", SString("代理地址")),
        ("active", SBoolean("是否为活跃提供商")));

    /// <summary>模型目录项（model.catalog）。</summary>
    private static readonly JsonObject ModelCatalogResult = RArray(RObject(
        ("ref", SString("模型 ref（provider/id）")),
        ("active", SBoolean("是否为活跃模型")),
        ("provider", SString("提供商 id")),
        ("providerEnabled", SBoolean("提供商是否启用")),
        ("id", SString("模型 id")),
        ("alias", SString("显示别名")),
        ("contextWindow", SInteger("上下文窗口 token")),
        ("maxOutput", SInteger("最大输出 token")),
        ("tags", SArray("标签")),
        ("capabilities", RObject(
            ("streaming", SBoolean("流式输出")),
            ("tools", SBoolean("工具调用")),
            ("thinking", SBoolean("思考段")),
            ("vision", SBoolean("视觉输入")),
            ("reasoning", SBoolean("推理")),
            ("maxReasoningLevel", SString("最高推理深度")),
            ("mcp", SBoolean("MCP 工具"))))));

    /// <summary>定时任务（ScheduledTask 序列化形状）。</summary>
    private static readonly JsonObject ScheduledTaskResult = RObject(
        ("id", SString("任务 id")),
        ("name", SString("任务名")),
        ("workspace", SString("工作区路径；空=全局")),
        ("prompt", SString("回合提示词")),
        ("cron", SString("cron 表达式")),
        ("enabled", SBoolean("是否启用")),
        ("lastRunAt", SString("上次执行时间")),
        ("nextRunAt", SString("下次执行时间")),
        ("lastStatus", SString("上次执行状态")),
        ("lastError", SString("上次失败原因")),
        ("lastOutput", SString("上次输出摘要")),
        ("createdAt", SString("创建时间")),
        ("updatedAt", SString("更新时间")));

    /// <summary>MCP 服务器（McpServerJson 形状；token 只回持凭状态）。</summary>
    private static readonly JsonObject McpServerResult = RObject(
        ("name", SString("服务器名")),
        ("scope", SString("配置作用域", "workspace", "global")),
        ("transport", SString("传输方式")),
        ("command", SString("stdio 启动命令")),
        ("args", SArray("命令参数")),
        ("cwd", SString("工作目录")),
        ("url", SString("远程服务地址")),
        ("envKeys", SArray("环境变量键名（值不回传）")),
        ("headerKeys", SArray("请求头键名（值不回传）")),
        ("connectTimeoutSeconds", SInteger("连接超时秒数")),
        ("mutatingTools", SArray("变更类工具名")),
        ("readOnlyTools", SArray("只读工具名")),
        ("trustReadOnly", SBoolean("只读免审开关")),
        ("oauthConfigured", SBoolean("是否已配置 OAuth（值不回传）")),
        ("oauthDisabled", SBoolean("是否停用 OAuth")),
        ("enabled", SBoolean("是否启用")),
        ("connected", SBoolean("当前是否已连接")),
        ("connecting", SBoolean("是否正在连接")),
        ("toolCount", SInteger("已发现工具数")),
        ("error", SString("最近连接错误")),
        ("warnings", SArray("连接告警")));

    /// <summary>技能（skill.list 形状）。</summary>
    private static readonly JsonObject SkillResult = RArray(RObject(
        ("name", SString("技能名")),
        ("description", SString("技能描述")),
        ("version", SString("版本")),
        ("enabled", SBoolean("是否启用")),
        ("directory", SString("技能目录")),
        ("scope", SString("归属", "workspace", "global"))));

    /// <summary>官方技能库条目。</summary>
    private static readonly JsonObject OfficialSkillResult = RArray(RObject(
        ("slug", SString("官方 slug")),
        ("name", SString("名称")),
        ("description", SString("描述")),
        ("version", SString("版本")),
        ("tags", SArray("标签")),
        ("installed", SBoolean("是否已安装")),
        ("enabled", SBoolean("是否启用"))));

    /// <summary>领域专家 persona。</summary>
    private static readonly JsonObject ExpertResult = RArray(RObject(
        ("id", SString("专家 id")),
        ("name", SString("名称")),
        ("title", SString("头衔")),
        ("avatar", SString("头像")),
        ("bio", SString("简介")),
        ("domains", SArray("领域标签")),
        ("skills", SArray("关联技能")),
        ("prompt", SString(" persona 提示词"))));

    /// <summary>知识条目（KnowledgeInfoPayload 形状：范围 + 语义状态 + 条目数组）。</summary>
    private static readonly JsonObject KnowledgeInfoResult = RObject(
        ("workspace", SString("知识范围根目录")),
        ("isGlobal", SBoolean("是否全局范围")),
        ("count", SInteger("条目数")),
        ("semantic", RObject(
            ("enabled", SBoolean("语义检索是否启用")),
            ("reason", SString("未启用原因")))),
        ("entries", RArray(RObject(
            ("id", SInteger("条目 id")),
            ("title", SString("标题")),
            ("content", SString("内容")),
            ("tags", SArray("标签")),
            ("createdAt", SString("创建时间")),
            ("updatedAt", SString("更新时间")),
            ("notebookId", SInteger("笔记本 id")),
            ("notebookName", SString("笔记本名")),
            ("sourceId", SInteger("来源 id（来源分块条目才有）")),
            ("sourceTitle", SString("来源标题")),
            ("sourceKind", SString("来源类型")),
            ("sourceLocator", SString("来源定位符")),
            ("chunkOrdinal", SInteger("分块序号")),
            ("score", SNumber("检索相关度（检索类方法才有）"))))));

    /// <summary>知识标签聚合（knowledge.tags）。</summary>
    private static readonly JsonObject KnowledgeTagsResult = RObject(
        ("workspace", SString("知识范围根目录")),
        ("isGlobal", SBoolean("是否全局范围")),
        ("total", SInteger("标签总数")),
        ("tags", RArray(RObject(
            ("tag", SString("标签")),
            ("count", SInteger("出现次数"))))));

    /// <summary>笔记本清单（KnowledgeNotebooksPayload）。</summary>
    private static readonly JsonObject KnowledgeNotebooksResult = RObject(
        ("workspace", SString("知识范围根目录")),
        ("isGlobal", SBoolean("是否全局范围")),
        ("count", SInteger("笔记本数")),
        ("notebooks", RArray(RObject(
            ("id", SInteger("笔记本 id")),
            ("name", SString("名称")),
            ("description", SString("描述")),
            ("default", SBoolean("是否默认笔记本")),
            ("createdAt", SString("创建时间"))))));

    /// <summary>来源清单（KnowledgeSourcesPayload；SourceNode 形状）。</summary>
    private static readonly JsonObject KnowledgeSourcesResult = RObject(
        ("workspace", SString("知识范围根目录")),
        ("isGlobal", SBoolean("是否全局范围")),
        ("notebookId", SInteger("笔记本 id")),
        ("count", SInteger("来源数")),
        ("sources", RArray(RObject(
            ("id", SInteger("来源 id")),
            ("notebookId", SInteger("笔记本 id")),
            ("kind", SString("来源类型", "text", "url", "file")),
            ("title", SString("标题")),
            ("locator", SString("定位符（url/文件路径）")),
            ("chunkCount", SInteger("分块数")),
            ("charCount", SInteger("字符数")),
            ("createdAt", SString("创建时间")),
            ("updatedAt", SString("更新时间")),
            ("content", SString("全文（仅单来源操作返回）"))))));

    /// <summary>来源操作回执（SourceActionPayload）。</summary>
    private static readonly JsonObject SourceActionResult = RObject(
        ("workspace", SString("知识范围根目录")),
        ("isGlobal", SBoolean("是否全局范围")),
        ("notebookId", SInteger("笔记本 id")),
        ("source", RObject(
            ("id", SInteger("来源 id")),
            ("notebookId", SInteger("笔记本 id")),
            ("kind", SString("来源类型")),
            ("title", SString("标题")),
            ("locator", SString("定位符")),
            ("chunkCount", SInteger("分块数")),
            ("charCount", SInteger("字符数")),
            ("content", SString("全文")))),
        ("entries", SInteger("生成的条目数")));

    /// <summary>来源窗口读取（knowledge.source.read）。</summary>
    private static readonly JsonObject SourceReadResult = RObject(
        ("workspace", SString("知识范围根目录")),
        ("isGlobal", SBoolean("是否全局范围")),
        ("content", SString("窗口内容")),
        ("offset", SInteger("起始字符偏移")),
        ("totalChars", SInteger("全文总字符数")),
        ("source", RObject(
            ("id", SInteger("来源 id")),
            ("notebookId", SInteger("笔记本 id")),
            ("kind", SString("来源类型")),
            ("title", SString("标题")),
            ("locator", SString("定位符")),
            ("chunkCount", SInteger("分块数")),
            ("charCount", SInteger("字符数")))));

    /// <summary>语义索引状态（knowledge.semantic.status）。</summary>
    private static readonly JsonObject SemanticStatusResult = RObject(
        ("workspace", SString("知识范围根目录")),
        ("isGlobal", SBoolean("是否全局范围")),
        ("enabled", SBoolean("语义检索是否启用")),
        ("model", SString("嵌入模型 ref")),
        ("total", SInteger("条目总数")),
        ("embedded", SInteger("已嵌入条数")),
        ("pending", SInteger("待嵌入条数")),
        ("reason", SString("未启用原因")));

    /// <summary>语义索引分批构建（knowledge.semantic.build）。</summary>
    private static readonly JsonObject SemanticBuildResult = RObject(
        ("workspace", SString("知识范围根目录")),
        ("isGlobal", SBoolean("是否全局范围")),
        ("embedded", SInteger("本批已嵌入条数")),
        ("failed", SInteger("本批失败条数")),
        ("total", SInteger("条目总数")),
        ("pending", SInteger("剩余待嵌入条数")),
        ("model", SString("嵌入模型 ref")),
        ("error", SString("失败原因")));

    /// <summary>同义词表（knowledge.synonyms.get/save）。</summary>
    private static readonly JsonObject KnowledgeSynonymsResult = RObject(
        ("path", SString("synonyms.txt 路径")),
        ("exists", SBoolean("文件是否存在")),
        ("content", SString("文件全文")));

    /// <summary>知识导出（knowledge.export）。</summary>
    private static readonly JsonObject KnowledgeExportResult = RObject(
        ("workspace", SString("知识范围根目录")),
        ("isGlobal", SBoolean("是否全局范围")),
        ("count", SInteger("条目数")),
        ("markdown", SString("Markdown 全文")));

    /// <summary>知识批量导入（knowledge.import）。</summary>
    private static readonly JsonObject KnowledgeImportResult = RObject(
        ("importedFiles", SInteger("导入文件数")),
        ("importedEntries", SInteger("导入条目数")),
        ("files", RArray(RObject(
            ("file", SString("文件路径")),
            ("entries", SInteger("该文件导入条目数"))))));

    /// <summary>MEMORY.md 读取（memory.get）。</summary>
    private static readonly JsonObject MemoryResult = RObject(
        ("path", SString("MEMORY.md 路径")),
        ("isGlobal", SBoolean("是否全局范围")),
        ("exists", SBoolean("文件是否存在")),
        ("content", SString("文件全文")),
        ("chars", SInteger("字符数")),
        ("lines", SInteger("行数")),
        ("charLimit", SInteger("字符上限")),
        ("lineLimit", SInteger("行数上限")),
        ("warning", SString("超限告警")));

    /// <summary>工作区规则（rules.list；rules.save/delete 仅回 path）。</summary>
    private static readonly JsonObject RulesResult = RObject(
        ("workspace", SString("工作区根目录")),
        ("isGlobal", SBoolean("是否全局范围")),
        ("path", SString("规则文件路径")),
        ("isRoot", SBoolean("是否根 AGENTS.md")),
        ("content", SString("文件全文")),
        ("files", RArray(SString("规则文件清单"))));

    /// <summary>用量聚合行（usage.get 返回按 provider/model 分组的数组）。</summary>
    private static readonly JsonObject UsageResult = RArray(RObject(
        ("provider", SString("提供商 id")),
        ("model", SString("模型 id")),
        ("calls", SInteger("调用次数")),
        ("failures", SInteger("失败次数")),
        ("inputTokens", SInteger("输入 token")),
        ("totalInputTokens", SInteger("含缓存的总输入 token")),
        ("cachedInputTokens", SInteger("缓存命中 token")),
        ("cacheCreationInputTokens", SInteger("缓存写入 token")),
        ("outputTokens", SInteger("输出 token")),
        ("totalTokens", SInteger("总 token")),
        ("avgLatencyMs", SInteger("平均延迟毫秒")),
        ("successRate", SNumber("成功率"))));

    /// <summary>用量时间线（usage.timeline 返回按日数组）。</summary>
    private static readonly JsonObject UsageTimelineResult = RArray(RObject(
        ("date", SString("日期（yyyy-MM-dd）")),
        ("totalTokens", SInteger("当日总 token")),
        ("inputTokens", SInteger("输入 token")),
        ("totalInputTokens", SInteger("含缓存的总输入 token")),
        ("cachedInputTokens", SInteger("缓存命中 token")),
        ("outputTokens", SInteger("输出 token")),
        ("calls", SInteger("调用次数")),
        ("failures", SInteger("失败次数")),
        ("avgLatencyMs", SNumber("平均延迟毫秒"))));

    /// <summary>体检结果项（doctor.run：运行时检查 + 提供商探测两类）。</summary>
    private static readonly JsonObject DoctorCheckResult = RArray(RObject(
        ("name", SString("检查项")),
        ("ok", SBoolean("是否通过")),
        ("detail", SString("详情")),
        ("kind", SString("检查类型", "runtime", "provider"))));

    /// <summary>本地模型扫描（local.models）。</summary>
    private static readonly JsonObject LocalModelsResult = RObject(
        ("directory", SString("扫描目录")),
        ("exists", SBoolean("目录是否存在")),
        ("models", RArray(RObject(
            ("id", SString("模型 id")),
            ("path", SString("GGUF 路径")),
            ("sizeBytes", SInteger("文件字节数")),
            ("displayName", SString("显示名")),
            ("architecture", SString("架构")),
            ("trainedContext", SInteger("训练上下文长度")),
            ("contextWindow", SInteger("上下文窗口 token")),
            ("maxOutput", SInteger("最大输出 token"))))));

    /// <summary>模型连通性测试（model.test / provider.test 条目）。</summary>
    private static readonly JsonObject ModelTestResult = RObject(
        ("model", SString("模型 ref")),
        ("success", SBoolean("是否成功")),
        ("detail", SString("详情")),
        ("latencyMs", SInteger("延迟毫秒")));

    /// <summary>提供商连通性测试（provider.test）。</summary>
    private static readonly JsonObject ProviderTestResult = RArray(RObject(
        ("id", SString("提供商 id")),
        ("online", SBoolean("是否在线")),
        ("latencyMs", SInteger("延迟毫秒")),
        ("detail", SString("详情"))));

    /// <summary>模型预检状态（model.status）。</summary>
    private static readonly JsonObject ModelStatusResult = RObject(
        ("model", SString("模型 ref")),
        ("provider", SString("提供商 id")),
        ("kind", SString("提供商类型")),
        ("path", SString("模型文件路径")),
        ("exists", SBoolean("文件是否存在")),
        ("sizeBytes", SInteger("文件字节数")),
        ("reason", SString("不可用原因")));

    /// <summary>模型加载配置页（model.config.get）。</summary>
    private static readonly JsonObject ModelConfigResult = RObject(
        ("provider", SString("提供商 id")),
        ("providerName", SString("提供商名")),
        ("providerKind", SString("提供商类型")),
        ("id", SString("模型 id")),
        ("alias", SString("显示别名")),
        ("contextWindow", SInteger("上下文窗口 token")),
        ("maxOutput", SInteger("最大输出 token")),
        ("vision", SBoolean("视觉能力")),
        ("localPath", SString("GGUF 路径")),
        ("mmprojPath", SString("投影器路径")),
        ("load", SString("加载配置对象；null=自动")),
        ("providerDefaults", RObject(
            ("gpuLayers", SInteger("默认 GPU 层数")),
            ("threads", SInteger("默认线程数")))));

    /// <summary>模型预热/元数据更新回执（model.preload / model.update）。</summary>
    private static readonly JsonObject ModelMutationResult = RObject(
        ("model", SString("模型 ref")),
        ("path", SString("模型文件路径")),
        ("resident", SBoolean("是否常驻")),
        ("loadReport", RObject(
            ("attempts", SInteger("加载尝试次数")),
            ("degradedToCpu", SBoolean("是否降级为 CPU")),
            ("verified", SBoolean("是否验证通过")),
            ("loadDurationMs", SInteger("加载耗时毫秒")),
            ("effectiveGpuLayers", SInteger("实际生效 GPU 层数")),
            ("note", SString("备注")))),
        ("provider", SString("提供商 id")),
        ("id", SString("模型 id")),
        ("alias", SString("显示别名")),
        ("contextWindow", SInteger("上下文窗口 token")),
        ("maxOutput", SInteger("最大输出 token")),
        ("vision", SBoolean("视觉能力")),
        ("mmprojPath", SString("投影器路径")),
        ("load", SString("加载配置对象；null=自动")));

    /// <summary>本地权重卸载（model.unload）。</summary>
    private static readonly JsonObject ModelUnloadResult = RObject(
        ("unloaded", SArray("本次卸载的模型 ref")),
        ("resident", SArray("仍常驻的模型 ref")));

    /// <summary>后台任务快照（BackgroundTaskInfo）。</summary>
    private static readonly JsonObject BackgroundTaskResult = RObject(
        ("id", SString("任务 id")),
        ("prompt", SString("回合提示词")),
        ("workspaceRoot", SString("工作区根目录")),
        ("status", SString("状态", "running", "completed", "failed", "cancelled")),
        ("sessionId", SString("关联会话 id")),
        ("createdAt", SString("创建时间（ISO 8601）")),
        ("endedAt", SString("结束时间（ISO 8601）")),
        ("error", SString("失败原因")),
        ("output", SString("输出摘要")));

    /// <summary>注册项目（project.list / project.upsert）。</summary>
    private static readonly JsonObject ProjectResult = RObject(
        ("id", SString("项目 id")),
        ("path", SString("项目根目录")),
        ("name", SString("显示名")),
        ("createdAt", SString("创建时间")),
        ("updatedAt", SString("更新时间")));

    /// <summary>工作区（workspace.get / workspace.open）。</summary>
    private static readonly JsonObject WorkspaceResult = RObject(
        ("path", SString("工作区根目录")),
        ("name", SString("工作区名")),
        ("projectKinds", SArray("项目类型标签")),
        ("mode", SString("Agent 模式", "plan", "readonly", "edit", "auto")));

    /// <summary>配置一致性诊断（config.status）。</summary>
    private static readonly JsonObject ConfigStatusResult = RObject(
        ("hasAnomaly", SBoolean("是否存在配置异常")),
        ("detail", SString("异常详情")),
        ("backupFile", SString("备份配置文件路径")),
        ("configFile", SString("配置文件路径")),
        ("schemaVersion", SInteger("配置 schema 版本")),
        ("diskSchemaVersion", SInteger("磁盘配置版本")),
        ("validationWarnings", SArray("校验告警")),
        ("secretWarnings", SArray("凭据告警（明文迁移失败等）")),
        ("secretStorage", SString("凭据存储方式")));

    /// <summary>路由配置（routing.get/set）。</summary>
    private static readonly JsonObject RoutingResult = RObject(
        ("failoverEnabled", SBoolean("故障转移开关")),
        ("deepSeekOptimizationEnabled", SBoolean("DeepSeek 优化开关")));

    /// <summary>智能体设置（agent.config.get/set）。</summary>
    private static readonly JsonObject AgentConfigResult = RObject(
        ("networkEnabled", SBoolean("联网开关")),
        ("delegationEnabled", SBoolean("委派开关")),
        ("visionModel", SString("视觉模型 ref")),
        ("autoVerify", SBoolean("自动验证开关")),
        ("turnTokenBudget", SInteger("单回合 token 上限")),
        ("turnCostBudget", SNumber("单回合费用上限")),
        ("turnWallClockSeconds", SInteger("单回合墙钟上限（秒）")));

    /// <summary>高级配置（advanced.get/set）。</summary>
    private static readonly JsonObject AdvancedResult = RObject(
        ("networkEnabled", SBoolean("联网开关")),
        ("failoverEnabled", SBoolean("故障转移开关")),
        ("deepSeekOptimizationEnabled", SBoolean("DeepSeek 优化开关")),
        ("computerUseEnabled", SBoolean("计算机使用开关")),
        ("computerUseDriver", SString("计算机使用驱动")),
        ("networkOpsEnabled", SBoolean("网络运维开关")),
        ("networkOpsAllowMutating", SBoolean("允许变更类网络操作")),
        ("language", SString("语言")),
        ("rulesEnabled", SBoolean("工作区规则开关")),
        ("memoryMode", SString("记忆模式")),
        ("localInference", RObject(
            ("providerId", SString("本地提供商 id")),
            ("gpuLayers", SInteger("GPU 卸载层数")),
            ("contextLength", SInteger("上下文长度")),
            ("kvCacheQuantization", SString("KV 缓存量化")),
            ("flashAttention", SBoolean("FlashAttention 开关")))));

    /// <summary>网站白名单（web.allowedGet/Set）。</summary>
    private static readonly JsonObject WebAccessResult = RObject(
        ("allowedSites", SArray("允许的站点列表")),
        ("mode", SString("当前模式", "allowlist", "unrestricted")),
        ("localhostAlwaysAllowed", SBoolean("localhost 是否始终允许")));

    /// <summary>自动反思配置（evolution.config.get/set）。</summary>
    private static readonly JsonObject EvolutionConfigResult = RObject(
        ("autoReflect", SBoolean("自动反思开关")),
        ("intervalMinutes", SInteger("反思间隔（分钟）")),
        ("thresholdEnabled", SBoolean("缺陷数阈值触发开关")),
        ("thresholdSignals", SInteger("缺陷数阈值")));

    /// <summary>缺陷聚合报告（evolution.inspect）。</summary>
    private static readonly JsonObject EvolutionInspectResult = RObject(
        ("scanned", SInteger("扫描事件条数")),
        ("generatedAt", SString("生成时间（ISO 8601）")),
        ("reports", RArray(RObject(
            ("fingerprint", SString("缺陷指纹")),
            ("kind", SString("缺陷类型")),
            ("severity", SString("严重度")),
            ("firstSeen", SString("首次出现时间")),
            ("lastSeen", SString("最近出现时间")),
            ("occurrences", SInteger("出现次数")),
            ("toolName", SString("相关工具")),
            ("errorSummary", SString("错误摘要")),
            ("sessionId", SString("相关会话 id"))))),
        ("health", RObject(
            ("score", SNumber("健康分")),
            ("grade", SString("健康等级")))),
        ("trend", RArray(RObject(
            ("date", SString("日期")),
            ("toolFailures", SInteger("工具失败数")),
            ("gaps", SInteger("断层数")),
            ("verificationFailures", SInteger("验证失败数")),
            ("feedback", SInteger("反馈数")),
            ("corrections", SInteger("纠正数")),
            ("cancels", SInteger("取消数")),
            ("retries", SInteger("重试数"))))),
        ("distribution", RObject(
            ("byKind", SString("按缺陷类型的计数（键为类型名）")),
            ("topTools", RArray(RObject(
                ("tool", SString("工具名")),
                ("count", SInteger("次数"))))))));

    /// <summary>进化效果统计（evolution.stats）。</summary>
    private static readonly JsonObject EvolutionStatsResult = RObject(
        ("runs", SInteger("反思回合数")),
        ("candidatesProduced", SInteger("产出候选数")),
        ("adopted", SInteger("采纳数")),
        ("rejected", SInteger("拒绝数")),
        ("deferred", SInteger("搁置数")),
        ("noAction", SInteger("无动作数")),
        ("failed", SInteger("失败数")),
        ("adoptionRate", SNumber("采纳率")),
        ("skills", RArray(RObject(
            ("fingerprint", SString("缺陷指纹")),
            ("skillName", SString("技能名")),
            ("adoptedAt", SString("采纳时间")),
            ("kind", SString("缺陷类型")),
            ("usageCount", SInteger("使用次数")),
            ("resolved", SBoolean("缺陷是否已消除"))))),
        ("generatedAt", SString("生成时间（ISO 8601）")));

    /// <summary>反思回合历史（evolution.history）。</summary>
    private static readonly JsonObject EvolutionHistoryResult = RArray(RObject(
        ("id", SString("回合 id")),
        ("sessionId", SString("会话 id")),
        ("trigger", SString("触发方式")),
        ("processed", SInteger("处理信号数")),
        ("candidates", SInteger("产出候选数")),
        ("noAction", SBoolean("是否无动作")),
        ("error", SString("失败原因")),
        ("startedAt", SString("开始时间")),
        ("endedAt", SString("结束时间"))));

    /// <summary>待审技能草稿（evolution.pending-list）。</summary>
    private static readonly JsonObject EvolutionCandidateResult = RArray(RObject(
        ("fingerprint", SString("缺陷指纹")),
        ("kind", SString("缺陷类型")),
        ("skillName", SString("技能名")),
        ("candidateDir", SString("草稿目录")),
        ("summary", SString("摘要")),
        ("createdAt", SString("创建时间（ISO 8601）")),
        ("status", SString("状态", "pending", "adopted", "rejected", "deferred")),
        ("files", RArray(RObject(
            ("name", SString("文件名")),
            ("content", SString("文件内容")))))));

    /// <summary>连接器目录（connectors.list；install/uninstall 返回同一形状）。</summary>
    private static readonly JsonObject ConnectorListResult = RObject(
        ("catalogAvailable", SBoolean("连接器目录是否可用")),
        ("root", SString("连接器目录路径")),
        ("connectors", RArray(RObject(
            ("id", SString("连接器包 id")),
            ("name", SString("名称")),
            ("description", SString("描述")),
            ("icon", SString("图标")),
            ("category", SString("分类")),
            ("tags", SArray("标签")),
            ("offline", SBoolean("是否离线安装")),
            ("homepage", SString("主页")),
            ("installed", SBoolean("是否已安装")),
            ("enabled", SBoolean("是否启用")),
            ("inputs", RArray(RObject(
                ("key", SString("配置项键名")),
                ("title", SString("标题")),
                ("description", SString("描述")),
                ("required", SBoolean("是否必填")),
                ("secret", SBoolean("是否加密存储")),
                ("hasDefault", SBoolean("是否有默认值")))))))));

    /// <summary>文件写锁（lock.list）。</summary>
    private static readonly JsonObject LockResult = RArray(RObject(
        ("workspace", SString("工作区根目录")),
        ("file", SString("锁定的文件")),
        ("owner", SString("持有者")),
        ("acquiredAt", SString("获取时间"))));

    /// <summary>会话读取（session.get：头字段 + 消息数组）。</summary>
    private static readonly JsonObject SessionGetResult = RObject(
        ("id", SString("会话 id")),
        ("title", SString("会话标题")),
        ("workspace", SString("归属工作区路径")),
        ("archived", SBoolean("是否已归档")),
        ("reasoningLevel", SString("推理深度")),
        ("networkEnabled", SBoolean("会话级联网开关")),
        ("expertId", SString("绑定的专家 persona id")),
        ("llmRounds", SInteger("LLM 调用轮数")),
        ("executionSteps", SInteger("工具执行步数")),
        ("inputTokens", SInteger("最近一次输入 token")),
        ("totalInputTokens", SInteger("累计输入 token")),
        ("cachedInputTokens", SInteger("累计缓存命中输入 token")),
        ("outputTokens", SInteger("累计输出 token")),
        ("outputElapsedMs", SInteger("累计输出耗时毫秒")),
        ("createdAt", SString("创建时间（ISO 8601）")),
        ("updatedAt", SString("更新时间（ISO 8601）")),
        ("messages", RArray(RObject(
            ("role", SString("角色", "user", "assistant", "tool")),
            ("text", SString("消息文本")),
            ("timestamp", SInteger("时间戳（Unix 毫秒）")),
            ("images", RArray(RObject(
                ("id", SString("图片 id")),
                ("name", SString("文件名")),
                ("mediaType", SString("MIME 类型")),
                ("data", SString("恒为 null：字节改用 session.image 按需读取")),
                ("sizeBytes", SInteger("字节数"))))),
            ("thinking", SString("思考段文本")),
            ("modelRef", SString("模型 ref")),
            ("viewedImages", RArray(RObject(
                ("id", SString("图片 id")),
                ("name", SString("文件名"))))),
            ("toolCalls", RArray(RObject(
                ("id", SString("调用 id")),
                ("name", SString("工具名"))))),
            ("toolCallId", SString("工具调用 id（role=tool）")),
            ("toolName", SString("工具名（role=tool）")),
            ("toolSuccess", SBoolean("工具是否成功（role=tool）")),
            ("toolDiff", SString("文件 diff（role=tool）")),
            ("toolFilePath", SString("变更文件路径（role=tool）"))))),
        ("total", SInteger("会话消息总条数")),
        ("firstIndex", SInteger("本页首条消息在整个会话中的序号（从 0 起）")),
        ("hasMore", SBoolean("是否还有更早的消息未返回（供「加载更早」判断）")));

    private static List<DaemonMethodDoc> BuildCatalog() =>
    [
        // core
        M("ping", "core", "探活，返回 pong", ["pong"], EmptyParams, result: RText,
            errors: E()),
        M("protocol.info", "core", "协议版本、能力与方法清单", parameters: EmptyParams, result: RObject(
            ("version", SString("协议版本")),
            ("transport", SString("传输格式")),
            ("capabilities", SArray("能力标签")),
            ("methods", SArray("方法名清单")),
            ("versionWarning", SString("客户端版本偏高时的升级提示"))),
            errors: E()),
        M("contract.export", "core", "导出完整契约文档（JSON Schema）", parameters: EmptyParams, result: RAnyObject,
            errors: E()),
        M("events.recent", "core", "查询事件溯源日志（重连补播：稳定事件名 + camelCase 载荷 + 会话归属）", parameters: SObject(
            ("limit", SInteger("返回条数，缺省 100"), false),
            ("after", SInteger("只返回 id 大于该值的行，即客户端见过的最后一条台账 id"), false)),
            result: RAnyArray,
            errors: E(DaemonErrorCode.InvalidParams)),
        M("shutdown", "core", "请求 daemon 优雅退出", ["bye"], EmptyParams, result: RText,
            errors: E()),

        // chat
        M("chat", "chat", "发起一次 Agent 回合（流式事件 + done 终态）", ["done", "cancelled"], ChatParams(false),
            result: RText, errors: E(DaemonErrorCode.InvalidParams, DaemonErrorCode.Conflict)),
        M("agent.runTurn", "chat", "chat 的显式命名别名", ["done", "cancelled"], ChatParams(false),
            result: RText, errors: E(DaemonErrorCode.InvalidParams, DaemonErrorCode.Conflict)),
        M("agent.steer", "chat", "向活动回合注入引导消息", parameters: ChatParams(true),
            result: RText, errors: E(DaemonErrorCode.InvalidParams, DaemonErrorCode.NotFound)),
        M("agent.respondApproval", "chat", "响应「本次允许」审批请求（内置高危守卫挂起的工具调用）", parameters: SObject(
            ("callId", SString("审批请求对应的工具调用 id"), true),
            ("approve", SBoolean("true=本次允许；false/缺省=拒绝"), false)),
            result: RText, errors: E(DaemonErrorCode.InvalidParams, DaemonErrorCode.NotFound)),
        M("agent.cancel", "chat", "取消活动回合；缺省取消本连接全部", parameters: SObject(
            ("requestId", SInteger("目标回合请求 id"), false),
            ("sessionId", SString("按会话过滤"), false)),
            result: RText, errors: E(DaemonErrorCode.InvalidParams, DaemonErrorCode.NotFound)),
        M("agent.undo", "chat", "撤销上一回合的文件修改", parameters: IdOnlyParams,
            result: RText, errors: E(DaemonErrorCode.InvalidParams, DaemonErrorCode.Conflict)),
        M("agent.interrupted", "chat", "查询 daemon 崩溃时被中断的会话 id", parameters: EmptyParams,
            result: RArray(SString("会话 id")), errors: E()),
        M("feedback.turn", "chat", "提交回合负反馈，进入进化信号", parameters: SObject(
            ("sessionId", SString("回合所属会话 id"), true),
            ("kind", SString("反馈类型，缺省 negative"), false),
            ("reason", SString("负反馈原因"), false)),
            result: RObject(("recorded", SBoolean("是否已记录"))),
            errors: E(DaemonErrorCode.InvalidParams)),

        // workspace
        M("workspace.get", "workspace", "查询当前工作区与模式", parameters: EmptyParams,
            result: WorkspaceResult, errors: E()),
        M("workspace.open", "workspace", "切换工作区", parameters: SObject(
            ("path", SString("工作区根目录"), true)),
            result: WorkspaceResult,
            errors: E(DaemonErrorCode.InvalidParams, DaemonErrorCode.NotFound)),
        M("workspace.files", "workspace", "列举工作区内文件，供输入框 @ 引用挑选（已跳过构建产物与凭证文件，只给相对路径）",
            parameters: Scope(
                ("query", SString("模糊匹配词；留空则返回最近修改的文件"), false),
                ("limit", SInteger("返回条数上限，默认 30，最大 100"), false)),
            result: RArray(SString("工作区相对路径")),
            errors: E(DaemonErrorCode.InvalidParams)),
        M("workspace.init", "workspace", "初始化工作区（AGENTS.md 与配置结构）", parameters: EmptyParams,
            result: RObject(
                ("path", SString("工作区根目录")),
                ("created", SBoolean("是否新建了目录结构"))),
            errors: E()),
        M("agent.mode.get", "workspace", "查询当前 Agent 模式", parameters: EmptyParams,
            result: RText, errors: E()),
        M("agent.mode.switch", "workspace", "切换 Agent 模式", parameters: SObject(
            ("mode", SString("目标模式", "plan", "readonly", "edit", "auto"), true)),
            result: RText, errors: E(DaemonErrorCode.InvalidParams)),

        // config
        M("config.save", "config", "配置字段级补丁（多端单写者收敛）", parameters: SObject(
            ("fields", SString("递归字段补丁对象"), true)),
            result: RText, errors: E(DaemonErrorCode.InvalidParams, DaemonErrorCode.Conflict)),
        M("config.status", "config", "配置一致性诊断", parameters: EmptyParams,
            result: ConfigStatusResult, errors: E()),
        M("config.rebuild", "config", "重建配置存储", parameters: EmptyParams,
            result: ROk, errors: E(DaemonErrorCode.Conflict)),
        M("routing.get", "config", "查询智能路由配置", parameters: EmptyParams,
            result: RoutingResult, errors: E()),
        M("routing.set", "config", "更新智能路由配置", parameters: SObject(
            ("failoverEnabled", SBoolean("故障转移开关"), true),
            ("deepSeekOptimizationEnabled", SBoolean("DeepSeek 优化开关"), false)),
            result: RoutingResult, errors: E(DaemonErrorCode.InvalidParams)),
        M("advanced.get", "config", "查询高级配置", parameters: EmptyParams,
            result: AdvancedResult, errors: E()),
        M("advanced.set", "config", "更新高级配置", parameters: AdvancedSet,
            result: AdvancedResult, errors: E(DaemonErrorCode.InvalidParams)),
        M("web.allowedGet", "config", "查询允许的网站白名单（外部网站访问）", parameters: EmptyParams,
            result: WebAccessResult, errors: E()),
        M("web.allowedSet", "config", "整体替换允许的网站白名单（无效条目整体拒绝），立即生效", parameters: SObject(
            ("allowedSites", SArray("站点条目列表（localhost 始终允许）", SString("形如 https://example.com 或 https://*.example.com（允许子域）；仅 http/https")), true)),
            result: WebAccessResult, errors: E(DaemonErrorCode.InvalidParams)),
        M("agent.config.get", "config", "查询智能体设置", parameters: EmptyParams,
            result: AgentConfigResult, errors: E()),
        M("agent.config.set", "config", "部分更新智能体设置（未传字段保持原值）", parameters: SObject(
            ("networkEnabled", SBoolean("网页搜索（联网工具）开关"), false),
            ("delegationEnabled", SBoolean("探索者智能体（delegate_task 委派）开关"), false),
            ("autoVerify", SBoolean("自动验证与修复开关"), false),
            ("visionModel", SString("图像回合优先使用的模型 ref；空串恢复自动选择"), false),
            ("turnTokenBudget", SInteger("单回合 token 上限（输入+输出）；0 为不限"), false),
            ("turnCostBudget", SNumber("单回合费用上限（按模型定价折算）；0 为不限"), false),
            ("turnWallClockSeconds", SInteger("单回合墙钟上限（秒）；0 为不限"), false)),
            result: AgentConfigResult, errors: E(DaemonErrorCode.InvalidParams)),
        M("prompt.optimize", "config", "提示词优化（无状态改写）", parameters: SObject(
            ("text", SString("待优化提示词"), true),
            ("model", SString("改写用模型 ref，缺省活跃模型"), false)),
            result: RText, errors: E(DaemonErrorCode.InvalidParams, DaemonErrorCode.NotFound)),

        // provider
        M("provider.list", "provider", "列出全部提供商", parameters: EmptyParams,
            result: RArray(ProviderResult), errors: E()),
        M("provider.upsert", "provider", "创建或更新提供商", parameters: ProviderUpsert,
            result: ProviderResult,
            errors: E(DaemonErrorCode.InvalidParams, DaemonErrorCode.Conflict)),
        M("provider.use", "provider", "切换活跃提供商", parameters: IdParams,
            result: RText, errors: E(DaemonErrorCode.InvalidParams, DaemonErrorCode.NotFound)),
        M("provider.remove", "provider", "删除提供商", parameters: IdParams,
            result: RText, errors: E(DaemonErrorCode.InvalidParams, DaemonErrorCode.NotFound, DaemonErrorCode.Conflict)),
        M("provider.test", "provider", "连通性探测", parameters: SObject(
            ("id", SString("只测指定提供商，缺省测全部"), false)),
            result: ProviderTestResult,
            errors: E(DaemonErrorCode.InvalidParams, DaemonErrorCode.NotFound)),
        M("provider.models.fetch", "provider", "从远端拉取模型列表", parameters: SObject(
            ("id", SString("提供商 id"), true),
            ("url", SString("模型列表地址，缺省用 provider.modelListUrl"), false)),
            result: RArray(SString("模型 id")),
            errors: E(DaemonErrorCode.InvalidParams, DaemonErrorCode.NotFound)),

        // model
        M("local.models", "model", "扫描本地 GGUF 模型", parameters: SObject(
            ("directory", SString("模型目录，缺省用解析后的默认目录"), false)),
            result: LocalModelsResult, errors: E(DaemonErrorCode.InvalidParams)),
        M("model.list", "model", "当前提供商的模型 id 清单", parameters: EmptyParams,
            result: RArray(SString("模型 id")), errors: E()),
        M("model.catalog", "model", "模型目录（含能力标签）", parameters: EmptyParams,
            result: ModelCatalogResult, errors: E()),
        M("model.switch", "model", "切换活跃模型", parameters: SObject(
            ("model", SString("模型 id"), true)),
            result: RText, errors: E(DaemonErrorCode.InvalidParams, DaemonErrorCode.NotFound)),
        M("model.test", "model", "单模型连通性测试", parameters: SObject(
            ("model", SString("模型 ref"), true)),
            result: ModelTestResult,
            errors: E(DaemonErrorCode.InvalidParams, DaemonErrorCode.NotFound)),
        M("model.status", "model", "本地模型预检状态", parameters: SObject(
            ("model", SString("模型 ref"), true)),
            result: ModelStatusResult,
            errors: E(DaemonErrorCode.InvalidParams, DaemonErrorCode.NotFound)),
        M("model.preload", "model", "预热本地模型（权重装入驻留槽，不占推理槽；进度经 model.load.progress 广播）", parameters: SObject(
            ("model", SString("模型 ref"), true)),
            result: ModelMutationResult,
            errors: E(DaemonErrorCode.InvalidParams, DaemonErrorCode.NotFound, DaemonErrorCode.Conflict)),
        M("model.unload", "model", "卸载全部常驻本地权重（归还内存/显存；活跃推理结束后释放）", parameters: EmptyParams,
            result: ModelUnloadResult, errors: E(DaemonErrorCode.Conflict)),
        M("model.config.get", "model", "模型加载配置页全量数据（含加载设置 / mmproj / 加载报告）", parameters: SObject(
            ("provider", SString("提供商 id"), true),
            ("id", SString("模型 id"), true)),
            result: ModelConfigResult,
            errors: E(DaemonErrorCode.InvalidParams, DaemonErrorCode.NotFound)),
        M("model.update", "model", "更新模型元数据与加载配置", parameters: SObject(
            ("provider", SString("提供商 id"), true),
            ("id", SString("模型 id"), true),
            ("alias", SString("显示别名"), false),
            ("contextWindow", SInteger("上下文窗口（>0）"), false),
            ("maxOutput", SInteger("最大输出 token（>0）"), false),
            ("vision", SBoolean("是否支持视觉"), false),
            ("mmprojPath", SString("多模态投影器路径；null 清除"), false),
            ("load", SString("加载配置对象（LocalModelSettings JSON）；null 清除恢复自动"), false)),
            result: ModelMutationResult,
            errors: E(DaemonErrorCode.InvalidParams, DaemonErrorCode.NotFound)),
        M("model.benchmark", "model", "本地模型基准测试（多轮 TTFT/decode/prefill 速率与 KV 复用命中率）", parameters: SObject(
            ("model", SString("模型 ref"), true),
            ("runs", SInteger("轮数（1-10，缺省 3）"), false),
            ("maxTokens", SInteger("每轮生成上限（16-4096，缺省 256）"), false),
            ("prompt", SString("自定义基准提示词"), false)),
            result: RObject(
                ("model", SString("模型 ref")),
                ("runs", SInteger("轮数")),
                ("maxTokens", SInteger("每轮生成上限")),
                ("results", RArray(RObject(
                    ("run", SInteger("轮次")),
                    ("finishReason", SString("结束原因")),
                    ("outputChars", SInteger("输出字符数")),
                    ("ttftMs", SNumber("首 token 延迟（ms，含权重加载等待）")),
                    ("loadWaitMs", SNumber("权重加载/排队等待（ms）：冷启动首轮即用户干等的那几秒，稳态命中为 0")),
                    ("totalMs", SNumber("总耗时（ms）")),
                    ("decodeMs", SNumber("解码耗时（ms）")),
                    ("decodeTokensPerSecond", SNumber("解码速率")),
                    ("prefillTokensPerSecond", SNumber("预填充速率")),
                    ("promptTokens", SInteger("提示 token 数")),
                    ("generatedTokens", SInteger("生成 token 数")),
                    ("cachedTokens", SInteger("缓存命中 token 数")),
                    ("cacheHitRate", SNumber("缓存命中率")),
                    ("prefixReuse", SBoolean("是否发生前缀复用")),
                    ("hint", SString("性能提示")),
                    ("note", SString("无性能观测时的说明")),
                    ("error", SString("本轮失败原因"))))),
                ("aggregate", RObject(
                    ("steadyRuns", SInteger("稳态轮数（第 2 轮起）")),
                    ("ttftMsMean", SNumber("TTFT 均值（ms）")),
                    ("ttftMsP50", SNumber("TTFT P50（ms）")),
                    ("decodeTokensPerSecondMean", SNumber("解码速率均值")),
                    ("decodeTokensPerSecondP50", SNumber("解码速率 P50")),
                    ("prefillTokensPerSecondMean", SNumber("预填充速率均值")))),
                ("error", SString("整体失败原因")),
                ("tuning", RObject(
                    ("modelPath", SString("模型路径")),
                    ("tierLevel", SString("档位")),
                    ("gpuLayers", SInteger("GPU 层数")),
                    ("kvQuant", SString("KV 量化"))))),
            errors: E(DaemonErrorCode.InvalidParams, DaemonErrorCode.NotFound)),

        // schedule
        M("schedule.list", "schedule", "列出定时任务", parameters: EmptyParams,
            result: RArray(ScheduledTaskResult), errors: E()),
        M("schedule.create", "schedule", "新建定时任务", parameters: ScheduleUpsert,
            result: ScheduledTaskResult, errors: E(DaemonErrorCode.InvalidParams)),
        M("schedule.update", "schedule", "更新定时任务", parameters: ScheduleUpsert,
            result: ScheduledTaskResult,
            errors: E(DaemonErrorCode.InvalidParams, DaemonErrorCode.NotFound)),
        M("schedule.toggle", "schedule", "启停定时任务", parameters: IdParams,
            result: ScheduledTaskResult,
            errors: E(DaemonErrorCode.InvalidParams, DaemonErrorCode.NotFound)),
        M("schedule.delete", "schedule", "删除定时任务", parameters: IdParams,
            result: RText,
            errors: E(DaemonErrorCode.InvalidParams, DaemonErrorCode.NotFound)),
        M("schedule.run", "schedule", "立即执行一次定时任务", parameters: IdParams,
            result: RText,
            errors: E(DaemonErrorCode.InvalidParams, DaemonErrorCode.NotFound, DaemonErrorCode.Conflict)),

        // task（后台任务队列：完整 agent 回合转后台执行，状态经 task.updated 广播）
        M("task.start", "task", "提交后台 agent 回合（立即返回任务快照）", parameters: SObject(
            ("message", SString("后台任务的提示词"), true),
            ("title", SString("会话标题（可选）"), false),
            ("workspace", SString("工作区路径（缺省为全局工作区）"), false)),
            result: BackgroundTaskResult,
            errors: E(DaemonErrorCode.InvalidParams, DaemonErrorCode.Conflict)),
        M("task.get", "task", "查询单个后台任务", parameters: IdParams,
            result: BackgroundTaskResult,
            errors: E(DaemonErrorCode.InvalidParams, DaemonErrorCode.NotFound)),
        M("task.list", "task", "列出后台任务（新→旧）", parameters: EmptyParams,
            result: RArray(BackgroundTaskResult), errors: E()),
        M("task.cancel", "task", "取消运行中的后台任务", parameters: IdParams,
            result: BackgroundTaskResult,
            errors: E(DaemonErrorCode.InvalidParams, DaemonErrorCode.NotFound, DaemonErrorCode.Conflict)),

        // mcp
        M("mcp.list", "mcp", "列出 MCP 服务器", parameters: EmptyParams,
            result: RArray(McpServerResult), errors: E()),
        M("mcp.upsert", "mcp", "创建或更新 MCP 服务器", parameters: SObject(
            ("name", SString("服务器名"), true),
            ("scope", SString("写入作用域", "workspace", "global"), false),
            ("server", McpServerInput, true)),
            result: RArray(McpServerResult), errors: E(DaemonErrorCode.InvalidParams)),
        M("mcp.remove", "mcp", "删除 MCP 服务器", parameters: SObject(
            ("name", SString("服务器名"), true),
            ("scope", SString("作用域", "workspace", "global"), false)),
            result: RArray(McpServerResult),
            errors: E(DaemonErrorCode.InvalidParams, DaemonErrorCode.NotFound)),
        M("mcp.reload", "mcp", "重载 MCP 服务器", parameters: EmptyParams,
            result: RArray(McpServerResult), errors: E()),

        // connectors：连接器目录与一键安装（基于 MCP 分发格式）
        M("connectors.list", "connectors", "列出连接器目录与安装状态", parameters: EmptyParams,
            result: ConnectorListResult, errors: E()),
        M("connectors.install", "connectors", "一键安装连接器（占位符填充、secret 加密落盘）", parameters: SObject(
            ("id", SString("连接器包 id"), true),
            ("values", SObject(
                ("key", SString("inputs 配置项取值（键名与包 inputs.key 对应）"), false)), false)),
            result: ConnectorListResult,
            errors: E(DaemonErrorCode.InvalidParams, DaemonErrorCode.NotFound)),
        M("connectors.uninstall", "connectors", "卸载连接器", parameters: SObject(
            ("id", SString("连接器包 id"), true)),
            result: ConnectorListResult,
            errors: E(DaemonErrorCode.InvalidParams, DaemonErrorCode.NotFound)),

        // skill
        M("skill.list", "skill", "列出已装技能", parameters: EmptyParams,
            result: SkillResult, errors: E()),
        M("skill.import", "skill", "导入技能目录", parameters: SObject(
            ("path", SString("技能目录路径"), true)),
            result: SkillResult, errors: E(DaemonErrorCode.InvalidParams)),
        M("skill.toggle", "skill", "启停技能", parameters: SObject(
            ("name", SString("技能名"), true),
            ("enabled", SBoolean("启用或停用"), true)),
            result: SkillResult,
            errors: E(DaemonErrorCode.InvalidParams, DaemonErrorCode.NotFound)),
        M("skill.official.list", "skill", "列出官方技能库", parameters: EmptyParams,
            result: OfficialSkillResult, errors: E()),
        M("skill.official.install", "skill", "安装官方技能", parameters: SObject(
            ("slug", SString("官方技能 slug"), true)),
            result: OfficialSkillResult,
            errors: E(DaemonErrorCode.InvalidParams, DaemonErrorCode.NotFound)),
        M("expert.list", "skill", "列出内置领域专家", parameters: EmptyParams,
            result: ExpertResult, errors: E()),

        // knowledge
        M("knowledge.list", "knowledge", "列出知识条目（含笔记本/来源归属）", parameters: Scope(
            ("limit", SInteger("1..2000，缺省 500"), false),
            ("tag", SString("仅返回携带该标签的条目"), false),
            ("notebookId", SInteger("仅列出该笔记本的条目"), false)),
            result: KnowledgeInfoResult, errors: E(DaemonErrorCode.InvalidParams)),
        M("knowledge.search", "knowledge", "高容错知识检索（配了嵌入模型时走语义+词法混合排序）", parameters: Scope(
            ("query", SString("检索词"), true),
            ("tag", SString("仅返回携带该标签的条目"), false),
            ("notebookId", SInteger("限定单笔记本"), false),
            ("limit", SInteger("1..100，缺省 50"), false)),
            result: KnowledgeInfoResult, errors: E(DaemonErrorCode.InvalidParams)),
        M("knowledge.save", "knowledge", "保存知识条目（同题 upsert，或按 id 原地更新）", parameters: Scope(
            ("title", SString("条目标题"), true),
            ("content", SString("条目内容（≤8000 字符）"), true),
            ("tags", SString("逗号分隔标签"), false),
            ("id", SInteger("按 id 原地更新（含改名），缺省走同题 upsert"), false),
            ("notebookId", SInteger("新条目归属笔记本，缺省默认笔记本"), false)),
            result: KnowledgeInfoResult, errors: E(DaemonErrorCode.InvalidParams)),
        M("knowledge.delete", "knowledge", "删除知识条目", parameters: Scope(
            ("id", SInteger("条目 id"), true)),
            result: KnowledgeInfoResult,
            errors: E(DaemonErrorCode.InvalidParams, DaemonErrorCode.NotFound)),
        M("knowledge.import", "knowledge", "批量导入文件到知识库", parameters: Scope(
            ("paths", SArray("文件路径（≥1 个，≤10 MB/个）"), true)),
            result: KnowledgeImportResult, errors: E(DaemonErrorCode.InvalidParams)),
        M("knowledge.tags", "knowledge", "聚合标签与出现次数", parameters: Scope(
            ("notebookId", SInteger("仅聚合该笔记本"), false)),
            result: KnowledgeTagsResult, errors: E(DaemonErrorCode.InvalidParams)),
        M("knowledge.export", "knowledge", "导出整个范围为 Markdown 文本", parameters: Scope(),
            result: KnowledgeExportResult, errors: E(DaemonErrorCode.InvalidParams)),
        M("knowledge.synonyms.get", "knowledge", "读取检索同义词表", parameters: EmptyParams,
            result: KnowledgeSynonymsResult, errors: E()),
        M("knowledge.synonyms.save", "knowledge", "写入检索同义词表（热重载）", parameters: SObject(
            ("content", SString("synonyms.txt 全文"), true)),
            result: KnowledgeSynonymsResult, errors: E(DaemonErrorCode.InvalidParams)),
        M("knowledge.notebook.list", "knowledge", "列出知识笔记本", parameters: Scope(),
            result: KnowledgeNotebooksResult, errors: E(DaemonErrorCode.InvalidParams)),
        M("knowledge.notebook.save", "knowledge", "创建或更新笔记本（同库内同名拒绝）", parameters: Scope(
            ("name", SString("笔记本名称"), true),
            ("description", SString("笔记本描述"), false),
            ("id", SInteger("按 id 更新名称/描述，缺省创建"), false)),
            result: KnowledgeNotebooksResult,
            errors: E(DaemonErrorCode.InvalidParams, DaemonErrorCode.Conflict)),
        M("knowledge.notebook.delete", "knowledge", "删除笔记本（含其来源与条目；默认笔记本不可删）", parameters: Scope(
            ("id", SInteger("笔记本 id"), true)),
            result: KnowledgeNotebooksResult,
            errors: E(DaemonErrorCode.InvalidParams, DaemonErrorCode.NotFound, DaemonErrorCode.Conflict)),
        M("knowledge.source.add", "knowledge", "添加来源（text/url/file），长文自动分块入库", parameters: Scope(
            ("kind", SString("text | url | file"), true),
            ("notebookId", SInteger("目标笔记本，缺省默认笔记本"), false),
            ("title", SString("text 来源标题"), false),
            ("content", SString("text 来源内容（≤200000 字符）"), false),
            ("url", SString("url 来源链接（http/https）"), false),
            ("tags", SString("条目标签，缺省按来源类型生成"), false),
            ("paths", SArray("file 来源文件路径（≥1 个，≤10 MB/个）"), false)),
            result: SourceActionResult,
            errors: E(DaemonErrorCode.InvalidParams, DaemonErrorCode.NotFound)),
        M("knowledge.source.list", "knowledge", "列出笔记本内的来源", parameters: Scope(
            ("notebookId", SInteger("笔记本 id"), true)),
            result: KnowledgeSourcesResult,
            errors: E(DaemonErrorCode.InvalidParams, DaemonErrorCode.NotFound)),
        M("knowledge.source.read", "knowledge", "窗口化读取来源全文", parameters: Scope(
            ("id", SInteger("来源 id"), true),
            ("offset", SInteger("起始字符偏移，缺省 0"), false),
            ("maxChars", SInteger("最多返回字符数（≤50000，缺省 50000）"), false)),
            result: SourceReadResult,
            errors: E(DaemonErrorCode.InvalidParams, DaemonErrorCode.NotFound)),
        M("knowledge.source.delete", "knowledge", "删除来源及其分块条目（手工条目保留）", parameters: Scope(
            ("id", SInteger("来源 id"), true)),
            result: KnowledgeSourcesResult,
            errors: E(DaemonErrorCode.InvalidParams, DaemonErrorCode.NotFound)),
        M("knowledge.source.refresh", "knowledge", "重新抓取/读取来源并重建分块条目", parameters: Scope(
            ("id", SInteger("来源 id"), true)),
            result: SourceActionResult,
            errors: E(DaemonErrorCode.InvalidParams, DaemonErrorCode.NotFound)),
        M("knowledge.retrieve", "knowledge", "跨笔记本带出处检索（配了嵌入模型时走语义+词法混合排序）", parameters: Scope(
            ("query", SString("检索词"), true),
            ("notebookIds", SArray("限定笔记本 id 列表"), false),
            ("sourceIds", SArray("限定来源 id 列表"), false),
            ("tag", SString("仅返回携带该标签的条目"), false),
            ("limit", SInteger("1..50，缺省 8"), false)),
            result: KnowledgeInfoResult, errors: E(DaemonErrorCode.InvalidParams)),
        M("knowledge.semantic.status", "knowledge", "语义索引状态（是否启用/模型/已嵌入条数/待建条数/不可用原因）", parameters: Scope(),
            result: SemanticStatusResult, errors: E(DaemonErrorCode.InvalidParams)),
        M("knowledge.semantic.build", "knowledge", "分批建立语义索引（一次嵌入 batch 条，返回剩余 pending）", parameters: Scope(
            ("batch", SInteger("1..128，缺省 32"), false)),
            result: SemanticBuildResult, errors: E(DaemonErrorCode.InvalidParams)),

        // memory
        M("memory.get", "memory", "读取 MEMORY.md 长期记忆", parameters: KnowledgeScope,
            result: MemoryResult, errors: E()),
        M("memory.save", "memory", "写入 MEMORY.md 长期记忆", parameters: Scope(
            ("content", SString("MEMORY.md 全文"), true)),
            result: RObject(
                ("path", SString("MEMORY.md 路径")),
                ("chars", SInteger("写入字符数")),
                ("lines", SInteger("写入行数"))),
            errors: E(DaemonErrorCode.InvalidParams)),
        M("rules.list", "memory", "列出 AGENTS.md 工作区规则", parameters: KnowledgeScope,
            result: RulesResult, errors: E()),
        M("rules.save", "memory", "写入工作区规则", parameters: Scope(
            ("content", SString("规则文件内容"), true),
            ("path", SString("目标规则文件相对路径，缺省根 AGENTS.md"), false)),
            result: RObject(("path", SString("写入的规则文件路径"))),
            errors: E(DaemonErrorCode.InvalidParams)),
        M("rules.delete", "memory", "删除工作区规则文件", parameters: Scope(
            ("path", SString("目标规则文件相对路径，缺省根 AGENTS.md"), false)),
            result: RObject(("path", SString("删除的规则文件路径"))),
            errors: E(DaemonErrorCode.InvalidParams, DaemonErrorCode.NotFound)),

        // usage
        M("usage.get", "usage", "查询用量聚合", parameters: SObject(
            ("days", SInteger("统计最近 N 天，缺省全量"), false)),
            result: UsageResult, errors: E(DaemonErrorCode.InvalidParams)),
        M("usage.timeline", "usage", "查询按日用量时间线", parameters: SObject(
            ("days", SInteger("1..90，缺省 14"), false)),
            result: UsageTimelineResult, errors: E(DaemonErrorCode.InvalidParams)),

        // session
        M("session.list", "session", "列出会话头", parameters: SessionExt(
            ("includeArchived", SBoolean("是否包含归档会话"), false)),
            result: RArray(SessionHeaderResult), errors: E()),
        M("session.search", "session", "全文检索会话", parameters: SessionExt(
            ("query", SString("检索词"), true),
            ("includeArchived", SBoolean("是否包含归档会话"), false),
            ("all", SBoolean("跨全部工作区检索（忽略 workspace/global）"), false),
            ("limit", SInteger("1..50，缺省 20"), false)),
            result: RArray(RObject(
                ("id", SString("会话 id")),
                ("title", SString("会话标题")),
                ("workspace", SString("归属工作区路径")),
                ("archived", SBoolean("是否已归档")),
                ("matchCount", SInteger("命中消息数（标题命中计 1）")),
                ("snippet", SString("首条命中消息正文的窗口片段；仅标题命中时为 null")))),
            errors: E(DaemonErrorCode.InvalidParams)),
        M("session.get", "session", "分页读取会话（头字段 + 消息数组；图片字节不内联）", parameters: SessionExt(
            ("id", SString("会话 id"), true),
            ("limit", SInteger("最多返回的消息条数（1..500），缺省 0=全部"), false),
            ("before", SInteger("只返回该序号之前的消息，供「加载更早」向前翻页；缺省 0=不限制"), false)),
            result: SessionGetResult,
            errors: E(DaemonErrorCode.InvalidParams, DaemonErrorCode.NotFound)),
        M("session.image", "session", "按需读取会话中单张图片的 base64 字节", parameters: SessionExt(
            ("id", SString("会话 id"), true),
            ("imageId", SString("图片 id"), true)),
            result: RObject(
                ("id", SString("图片 id")),
                ("name", SString("文件名")),
                ("mediaType", SString("MIME 类型")),
                ("data", SString("base64 数据")),
                ("sizeBytes", SInteger("字节数"))),
            errors: E(DaemonErrorCode.InvalidParams, DaemonErrorCode.NotFound)),
        M("session.truncate", "session", "截断会话消息", parameters: SessionExt(
            ("id", SString("会话 id"), true),
            ("keepCount", SInteger("保留消息条数（>=0）"), true)),
            result: RText,
            errors: E(DaemonErrorCode.InvalidParams, DaemonErrorCode.NotFound)),
        M("session.fork", "session", "从既有会话分叉新会话", parameters: SessionExt(
            ("id", SString("源会话 id"), true),
            ("keepCount", SInteger("保留消息条数，缺省 0"), false),
            ("title", SString("新会话标题"), false)),
            result: SessionHeaderResult,
            errors: E(DaemonErrorCode.InvalidParams, DaemonErrorCode.NotFound)),
        M("session.update", "session", "更新会话元数据", parameters: SessionExt(
            ("id", SString("会话 id"), true),
            ("title", SString("新标题"), false),
            ("reasoningLevel", SString("推理深度", ReasoningValues), false),
            ("networkEnabled", SBoolean("联网开关"), false),
            ("expertId", SString("绑定专家 persona id（空串解除绑定）"), false)),
            result: SessionHeaderResult,
            errors: E(DaemonErrorCode.InvalidParams, DaemonErrorCode.NotFound)),
        M("session.archive", "session", "归档会话", parameters: SessionExt(
            ("id", SString("会话 id"), true),
            ("archived", SBoolean("true 归档 / false 恢复，缺省 true"), false)),
            result: SessionHeaderResult,
            errors: E(DaemonErrorCode.InvalidParams, DaemonErrorCode.NotFound)),
        M("session.delete", "session", "删除会话", parameters: SessionExt(
            ("id", SString("会话 id"), true)),
            result: RText,
            errors: E(DaemonErrorCode.InvalidParams, DaemonErrorCode.NotFound)),
        M("session.resume", "session", "恢复会话为连接的遗留会话", parameters: SessionExt(
            ("id", SString("会话 id"), true)),
            result: RText,
            errors: E(DaemonErrorCode.InvalidParams, DaemonErrorCode.NotFound)),
        M("session.new", "session", "新建会话", parameters: SessionExt(
            ("reasoningLevel", SString("推理深度", ReasoningValues), false),
            ("expertId", SString("绑定专家 persona id（expert.list 可查）"), false)),
            result: SessionHeaderResult, errors: E(DaemonErrorCode.InvalidParams)),

        // project
        M("project.list", "project", "列出注册项目", parameters: EmptyParams,
            result: RArray(ProjectResult), errors: E()),
        M("project.upsert", "project", "注册或更新项目", parameters: SObject(
            ("path", SString("项目根目录（禁止用户主目录/状态目录）"), true),
            ("id", SString("更新已有项目时指定"), false),
            ("name", SString("显示名"), false)),
            result: ProjectResult, errors: E(DaemonErrorCode.InvalidParams, DaemonErrorCode.Conflict)),
        M("project.remove", "project", "移除项目", parameters: SObject(
            ("id", SString("项目 id"), true),
            ("keepSessions", SBoolean("true 保留会话历史，缺省 false"), false)),
            result: RText,
            errors: E(DaemonErrorCode.InvalidParams, DaemonErrorCode.NotFound)),

        // evolution
        M("evolution.inspect", "evolution", "聚合失败信号的缺陷报告（含健康分、趋势与分布）", parameters: SObject(
            ("limit", SInteger("扫描事件条数，缺省 DefaultScanLimit"), false)),
            result: EvolutionInspectResult, errors: E(DaemonErrorCode.InvalidParams)),
        M("evolution.reflect", "evolution", "发起反思回合产出技能草稿", parameters: EmptyParams,
            result: RObject(("started", SBoolean("反思回合是否已启动"))),
            errors: E(DaemonErrorCode.Conflict)),
        M("evolution.pending-list", "evolution", "待审技能草稿清单（含状态与草稿文件内容）", parameters: EmptyParams,
            result: EvolutionCandidateResult, errors: E()),
        M("evolution.decide", "evolution", "人工终审技能草稿（adopt 前可用 prompt 改写提示词）", parameters: SObject(
            ("fingerprint", SString("缺陷指纹"), true),
            ("decision", SString("裁决", "adopt", "reject", "defer"), true),
            ("prompt", SString("adopt 时改写技能提示词（可选）"), false)),
            result: RObject(
                ("fingerprint", SString("缺陷指纹")),
                ("status", SString("裁决后的状态")),
                ("updatedAt", SString("裁决时间（ISO 8601）"))),
            errors: E(DaemonErrorCode.InvalidParams, DaemonErrorCode.NotFound)),
        M("evolution.history", "evolution", "反思回合历史（触发方式与处理结果）", parameters: SObject(
            ("limit", SInteger("返回条数，缺省 20"), false)),
            result: EvolutionHistoryResult, errors: E(DaemonErrorCode.InvalidParams)),
        M("evolution.stats", "evolution", "进化效果统计（采纳率、技能使用与缺陷复发）", parameters: EmptyParams,
            result: EvolutionStatsResult, errors: E()),
        M("evolution.config.get", "evolution", "读取自动反思配置", parameters: EmptyParams,
            result: EvolutionConfigResult, errors: E()),
        M("evolution.config.set", "evolution", "更新自动反思配置", parameters: SObject(
            ("autoReflect", SBoolean("是否按间隔自动运行反思回合"), false),
            ("intervalMinutes", SInteger("自动反思间隔（分钟，30..10080）"), false),
            ("thresholdEnabled", SBoolean("缺陷数达标即自动反思（与定时并存）"), false),
            ("thresholdSignals", SInteger("触发反思的缺陷数阈值（1..50）"), false)),
            result: EvolutionConfigResult, errors: E(DaemonErrorCode.InvalidParams)),

        // diagnostics
        M("doctor", "diagnostics", "健康检查（人类可读文本）", parameters: EmptyParams,
            result: RText, errors: E()),
        M("doctor.run", "diagnostics", "运行健康检查（结构化结果）", parameters: EmptyParams,
            result: DoctorCheckResult, errors: E()),
        M("lock.list", "diagnostics", "列出文件写锁", parameters: EmptyParams,
            result: LockResult, errors: E()),

        // system
        M("factory.reset", "system", "工厂重置（清空全部数据）", parameters: SObject(
            ("keepSessions", SBoolean("true 保留会话历史，缺省 false"), false)),
            result: ROk, errors: E(DaemonErrorCode.Conflict)),
    ];

    /// <summary>导出完整契约文档（JSON Schema 2020-12 风格）。快照经测试落盘 contracts/daemon-contract.json。</summary>
    public static JsonObject ExportJson()
    {
        var methods = new JsonArray();
        foreach (var method in Methods)
        {
            var doc = new JsonObject
            {
                ["name"] = method.Name,
                ["category"] = method.Category,
                ["description"] = method.Description,
                ["schemaStatus"] = method.SchemaStatus,
                ["resultSchemaStatus"] = method.ResultSchemaStatus,
                ["terminalEvents"] = ToArray(method.TerminalEvents.Append("error")),
                // 逐方法错误码（含自动并入的 internalError 兜底）；信封级 invalidRequest 对所有方法适用，不逐条列。
                ["errorCodes"] = ToArray(method.ExportedErrorCodes),
            };
            if (method.Params is not null) doc["params"] = method.Params.DeepClone();
            if (method.Result is not null) doc["result"] = method.Result.DeepClone();
            methods.Add((JsonNode)doc);
        }

        return new JsonObject
        {
            ["$schema"] = "https://json-schema.org/draft/2020-12/schema",
            ["title"] = "Haoyue Daemon Contract",
            ["contractVersion"] = ContractVersion,
            ["protocolVersion"] = DaemonServer.ProtocolVersion,
            ["transport"] = "jsonl",
            ["envelope"] = new JsonObject
            {
                ["request"] = SObject(
                    ("id", SInteger("请求 id，回显在响应中"), true),
                    ("method", SString("方法名，见 methods 目录"), true),
                    ("params", SString("参数对象，各方法 schema 见 methods[].params"), false)),
                ["response"] = SObject(
                    ("id", SInteger("回显请求 id"), true),
                    ("event", SString("事件名，终态见 methods[].terminalEvents"), true),
                    ("data", SString("事件数据（error 时为人类可读消息）"), true),
                    ("sessionId", SString("流式事件归属的会话 id"), false),
                    ("details", SString("结构化元数据；error 事件必含 code，internalError 时另含 method 指明失败的方法"), false)),
            },
            ["errorCodes"] = ErrorCodesJson(),
            ["events"] = new JsonObject
            {
                ["stream"] = ToArray(StreamEvents),
                ["terminal"] = ToArray(TerminalEvents),
                ["broadcast"] = ToArray(BroadcastEvents),
            },
            ["methods"] = methods,
        };
    }

    private static JsonArray ErrorCodesJson()
    {
        var codes = new JsonArray();
        foreach (var code in Enum.GetValues<DaemonErrorCode>())
            codes.Add((JsonNode)code.ToWire());
        return codes;
    }

    private static JsonArray ToArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values) array.Add((JsonNode)value);
        return array;
    }
}
