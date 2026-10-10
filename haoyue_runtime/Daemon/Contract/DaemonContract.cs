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
    /// <summary>方法名不在契约目录中。</summary>
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
    /// <summary>服务端未预期的异常。</summary>
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
    JsonObject? Result = null)
{
    /// <summary>params 是否已指定 JSON Schema（渐进补全：未指定为 pending）。</summary>
    public string SchemaStatus => Params is null ? "pending" : "specified";
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
    ];

    /// <summary>请求的终态事件（错误终态 error 对所有方法隐式可用）。</summary>
    public static readonly string[] TerminalEvents = ["pong", "result", "done", "cancelled", "error", "bye"];

    /// <summary>服务端主动广播事件（不属于任何请求的响应）。</summary>
    public static readonly string[] BroadcastEvents =
    [
        "schedule.updated", "schedule.upcoming", "task.updated", "evolution.reflected", "mcp.updated", "turn.interrupted",
    ];

    private static readonly Lazy<IReadOnlyList<DaemonMethodDoc>> _methods = new(BuildCatalog);

    public static IReadOnlyList<DaemonMethodDoc> Methods => _methods.Value;

    public static IReadOnlyList<string> MethodNames => _methods.Value.Select(m => m.Name).ToList();

    private static DaemonMethodDoc M(
        string name, string category, string description,
        string[]? terminal = null, JsonObject? parameters = null, JsonObject? result = null) =>
        new(name, category, description, terminal ?? ["result"], parameters, result);

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
        ("apiKey", SString("API Key"), false),
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

    private static List<DaemonMethodDoc> BuildCatalog() =>
    [
        // core
        M("ping", "core", "探活，返回 pong", ["pong"], EmptyParams),
        M("protocol.info", "core", "协议版本、能力与方法清单", parameters: EmptyParams, result: new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["version"] = SString("协议版本"),
                ["transport"] = SString("传输格式"),
                ["capabilities"] = SArray("能力标签"),
                ["methods"] = SArray("方法名清单"),
            },
        }),
        M("contract.export", "core", "导出完整契约文档（JSON Schema）", parameters: EmptyParams),
        M("events.recent", "core", "查询事件溯源日志", parameters: SObject(
            ("limit", SInteger("返回条数，缺省 100"), false))),
        M("shutdown", "core", "请求 daemon 优雅退出", ["bye"], EmptyParams),

        // chat
        M("chat", "chat", "发起一次 Agent 回合（流式事件 + done 终态）", ["done", "cancelled"], ChatParams(false)),
        M("agent.runTurn", "chat", "chat 的显式命名别名", ["done", "cancelled"], ChatParams(false)),
        M("agent.steer", "chat", "向活动回合注入引导消息", parameters: ChatParams(true)),
        M("agent.cancel", "chat", "取消活动回合；缺省取消本连接全部", parameters: SObject(
            ("requestId", SInteger("目标回合请求 id"), false),
            ("sessionId", SString("按会话过滤"), false))),
        M("agent.undo", "chat", "撤销上一回合的文件修改", parameters: IdOnlyParams),
        M("agent.interrupted", "chat", "查询 daemon 崩溃时被中断的会话 id", parameters: EmptyParams),
        M("feedback.turn", "chat", "提交回合负反馈，进入进化信号", parameters: SObject(
            ("sessionId", SString("回合所属会话 id"), true),
            ("kind", SString("反馈类型，缺省 negative"), false),
            ("reason", SString("负反馈原因"), false))),

        // workspace
        M("workspace.get", "workspace", "查询当前工作区与模式", parameters: EmptyParams),
        M("workspace.open", "workspace", "切换工作区", parameters: SObject(
            ("path", SString("工作区根目录"), true))),
        M("workspace.init", "workspace", "初始化工作区（AGENTS.md 与配置结构）", parameters: EmptyParams),
        M("agent.mode.get", "workspace", "查询当前 Agent 模式", parameters: EmptyParams),
        M("agent.mode.switch", "workspace", "切换 Agent 模式", parameters: SObject(
            ("mode", SString("目标模式", "plan", "readonly", "edit", "auto"), true))),

        // config
        M("config.save", "config", "配置字段级补丁（多端单写者收敛）", parameters: SObject(
            ("fields", SString("递归字段补丁对象"), true))),
        M("config.status", "config", "配置一致性诊断", parameters: EmptyParams),
        M("config.rebuild", "config", "重建配置存储", parameters: EmptyParams),
        M("routing.get", "config", "查询智能路由配置", parameters: EmptyParams),
        M("routing.set", "config", "更新智能路由配置", parameters: SObject(
            ("failoverEnabled", SBoolean("故障转移开关"), true),
            ("deepSeekOptimizationEnabled", SBoolean("DeepSeek 优化开关"), false))),
        M("advanced.get", "config", "查询高级配置", parameters: EmptyParams),
        M("advanced.set", "config", "更新高级配置", parameters: AdvancedSet),
        M("agent.config.get", "config", "查询智能体设置", parameters: EmptyParams),
        M("agent.config.set", "config", "部分更新智能体设置（未传字段保持原值）", parameters: SObject(
            ("networkEnabled", SBoolean("网页搜索（联网工具）开关"), false),
            ("delegationEnabled", SBoolean("探索者智能体（delegate_task 委派）开关"), false),
            ("autoVerify", SBoolean("自动验证与修复开关"), false),
            ("visionModel", SString("图像回合优先使用的模型 ref；空串恢复自动选择"), false))),
        M("prompt.optimize", "config", "提示词优化（无状态改写）", parameters: SObject(
            ("text", SString("待优化提示词"), true),
            ("model", SString("改写用模型 ref，缺省活跃模型"), false))),

        // provider
        M("provider.list", "provider", "列出全部提供商", parameters: EmptyParams),
        M("provider.upsert", "provider", "创建或更新提供商", parameters: ProviderUpsert),
        M("provider.use", "provider", "切换活跃提供商", parameters: IdParams),
        M("provider.remove", "provider", "删除提供商", parameters: IdParams),
        M("provider.test", "provider", "连通性探测", parameters: SObject(
            ("id", SString("只测指定提供商，缺省测全部"), false))),
        M("provider.models.fetch", "provider", "从远端拉取模型列表", parameters: SObject(
            ("id", SString("提供商 id"), true),
            ("url", SString("模型列表地址，缺省用 provider.modelListUrl"), false))),

        // model
        M("local.models", "model", "扫描本地 GGUF 模型", parameters: SObject(
            ("directory", SString("模型目录，缺省用解析后的默认目录"), false))),
        M("model.list", "model", "当前提供商的模型 id 清单", parameters: EmptyParams),
        M("model.catalog", "model", "模型目录（含能力标签）", parameters: EmptyParams),
        M("model.switch", "model", "切换活跃模型", parameters: SObject(
            ("model", SString("模型 id"), true))),
        M("model.test", "model", "单模型连通性测试", parameters: SObject(
            ("model", SString("模型 ref"), true))),
        M("model.status", "model", "本地模型预检状态", parameters: SObject(
            ("model", SString("模型 ref"), true))),
        M("model.update", "model", "更新模型元数据", parameters: SObject(
            ("provider", SString("提供商 id"), true),
            ("id", SString("模型 id"), true),
            ("alias", SString("显示别名"), false),
            ("contextWindow", SInteger("上下文窗口（>0）"), false),
            ("maxOutput", SInteger("最大输出 token（>0）"), false),
            ("vision", SBoolean("是否支持视觉"), false))),

        // schedule
        M("schedule.list", "schedule", "列出定时任务", parameters: EmptyParams),
        M("schedule.create", "schedule", "新建定时任务", parameters: ScheduleUpsert),
        M("schedule.update", "schedule", "更新定时任务", parameters: ScheduleUpsert),
        M("schedule.toggle", "schedule", "启停定时任务", parameters: IdParams),
        M("schedule.delete", "schedule", "删除定时任务", parameters: IdParams),
        M("schedule.run", "schedule", "立即执行一次定时任务", parameters: IdParams),

        // task（后台任务队列：完整 agent 回合转后台执行，状态经 task.updated 广播）
        M("task.start", "task", "提交后台 agent 回合（立即返回任务快照）", parameters: SObject(
            ("message", SString("后台任务的提示词"), true),
            ("title", SString("会话标题（可选）"), false),
            ("workspace", SString("工作区路径（缺省为全局工作区）"), false))),
        M("task.get", "task", "查询单个后台任务", parameters: IdParams),
        M("task.list", "task", "列出后台任务（新→旧）", parameters: EmptyParams),
        M("task.cancel", "task", "取消运行中的后台任务", parameters: IdParams),

        // mcp
        M("mcp.list", "mcp", "列出 MCP 服务器", parameters: EmptyParams),
        M("mcp.upsert", "mcp", "创建或更新 MCP 服务器", parameters: SObject(
            ("name", SString("服务器名"), true),
            ("scope", SString("写入作用域", "workspace", "global"), false),
            ("server", McpServerInput, true))),
        M("mcp.remove", "mcp", "删除 MCP 服务器", parameters: SObject(
            ("name", SString("服务器名"), true),
            ("scope", SString("作用域", "workspace", "global"), false))),
        M("mcp.reload", "mcp", "重载 MCP 服务器", parameters: EmptyParams),

        // skill
        M("skill.list", "skill", "列出已装技能", parameters: EmptyParams),
        M("skill.import", "skill", "导入技能目录", parameters: SObject(
            ("path", SString("技能目录路径"), true))),
        M("skill.toggle", "skill", "启停技能", parameters: SObject(
            ("name", SString("技能名"), true),
            ("enabled", SBoolean("启用或停用"), true))),
        M("skill.official.list", "skill", "列出官方技能库", parameters: EmptyParams),
        M("skill.official.install", "skill", "安装官方技能", parameters: SObject(
            ("slug", SString("官方技能 slug"), true))),
        M("expert.list", "skill", "列出内置领域专家", parameters: EmptyParams),

        // knowledge
        M("knowledge.list", "knowledge", "列出知识条目（含笔记本/来源归属）", parameters: Scope(
            ("limit", SInteger("1..2000，缺省 500"), false),
            ("tag", SString("仅返回携带该标签的条目"), false),
            ("notebookId", SInteger("仅列出该笔记本的条目"), false))),
        M("knowledge.search", "knowledge", "高容错知识检索", parameters: Scope(
            ("query", SString("检索词"), true),
            ("tag", SString("仅返回携带该标签的条目"), false),
            ("notebookId", SInteger("限定单笔记本"), false),
            ("limit", SInteger("1..100，缺省 50"), false))),
        M("knowledge.save", "knowledge", "保存知识条目（同题 upsert，或按 id 原地更新）", parameters: Scope(
            ("title", SString("条目标题"), true),
            ("content", SString("条目内容（≤8000 字符）"), true),
            ("tags", SString("逗号分隔标签"), false),
            ("id", SInteger("按 id 原地更新（含改名），缺省走同题 upsert"), false),
            ("notebookId", SInteger("新条目归属笔记本，缺省默认笔记本"), false))),
        M("knowledge.delete", "knowledge", "删除知识条目", parameters: Scope(
            ("id", SInteger("条目 id"), true))),
        M("knowledge.import", "knowledge", "批量导入文件到知识库", parameters: Scope(
            ("paths", SArray("文件路径（≥1 个，≤10 MB/个）"), true))),
        M("knowledge.tags", "knowledge", "聚合标签与出现次数", parameters: Scope(
            ("notebookId", SInteger("仅聚合该笔记本"), false))),
        M("knowledge.export", "knowledge", "导出整个范围为 Markdown 文本", parameters: Scope()),
        M("knowledge.synonyms.get", "knowledge", "读取检索同义词表", parameters: EmptyParams),
        M("knowledge.synonyms.save", "knowledge", "写入检索同义词表（热重载）", parameters: SObject(
            ("content", SString("synonyms.txt 全文"), true))),
        M("knowledge.notebook.list", "knowledge", "列出知识笔记本", parameters: Scope()),
        M("knowledge.notebook.save", "knowledge", "创建或更新笔记本（同库内同名拒绝）", parameters: Scope(
            ("name", SString("笔记本名称"), true),
            ("description", SString("笔记本描述"), false),
            ("id", SInteger("按 id 更新名称/描述，缺省创建"), false))),
        M("knowledge.notebook.delete", "knowledge", "删除笔记本（含其来源与条目；默认笔记本不可删）", parameters: Scope(
            ("id", SInteger("笔记本 id"), true))),
        M("knowledge.source.add", "knowledge", "添加来源（text/url/file），长文自动分块入库", parameters: Scope(
            ("kind", SString("text | url | file"), true),
            ("notebookId", SInteger("目标笔记本，缺省默认笔记本"), false),
            ("title", SString("text 来源标题"), false),
            ("content", SString("text 来源内容（≤200000 字符）"), false),
            ("url", SString("url 来源链接（http/https）"), false),
            ("tags", SString("条目标签，缺省按来源类型生成"), false),
            ("paths", SArray("file 来源文件路径（≥1 个，≤10 MB/个）"), false))),
        M("knowledge.source.list", "knowledge", "列出笔记本内的来源", parameters: Scope(
            ("notebookId", SInteger("笔记本 id"), true))),
        M("knowledge.source.read", "knowledge", "窗口化读取来源全文", parameters: Scope(
            ("id", SInteger("来源 id"), true),
            ("offset", SInteger("起始字符偏移，缺省 0"), false),
            ("maxChars", SInteger("最多返回字符数（≤50000，缺省 50000）"), false))),
        M("knowledge.source.delete", "knowledge", "删除来源及其分块条目（手工条目保留）", parameters: Scope(
            ("id", SInteger("来源 id"), true))),
        M("knowledge.source.refresh", "knowledge", "重新抓取/读取来源并重建分块条目", parameters: Scope(
            ("id", SInteger("来源 id"), true))),
        M("knowledge.retrieve", "knowledge", "跨笔记本带出处检索", parameters: Scope(
            ("query", SString("检索词"), true),
            ("notebookIds", SArray("限定笔记本 id 列表"), false),
            ("sourceIds", SArray("限定来源 id 列表"), false),
            ("tag", SString("仅返回携带该标签的条目"), false),
            ("limit", SInteger("1..50，缺省 8"), false))),

        // memory
        M("memory.get", "memory", "读取 MEMORY.md 长期记忆", parameters: KnowledgeScope),
        M("memory.save", "memory", "写入 MEMORY.md 长期记忆", parameters: Scope(
            ("content", SString("MEMORY.md 全文"), true))),
        M("rules.list", "memory", "列出 AGENTS.md 工作区规则", parameters: KnowledgeScope),
        M("rules.save", "memory", "写入工作区规则", parameters: Scope(
            ("content", SString("规则文件内容"), true),
            ("path", SString("目标规则文件相对路径，缺省根 AGENTS.md"), false))),
        M("rules.delete", "memory", "删除工作区规则文件", parameters: Scope(
            ("path", SString("目标规则文件相对路径，缺省根 AGENTS.md"), false))),

        // usage
        M("usage.get", "usage", "查询用量聚合", parameters: SObject(
            ("days", SInteger("统计最近 N 天，缺省全量"), false))),
        M("usage.timeline", "usage", "查询按日用量时间线", parameters: SObject(
            ("days", SInteger("1..90，缺省 14"), false))),

        // session
        M("session.list", "session", "列出会话头", parameters: SessionExt(
            ("includeArchived", SBoolean("是否包含归档会话"), false))),
        M("session.search", "session", "全文检索会话", parameters: SessionExt(
            ("query", SString("检索词"), true),
            ("includeArchived", SBoolean("是否包含归档会话"), false),
            ("limit", SInteger("1..50，缺省 20"), false))),
        M("session.get", "session", "读取完整会话（含消息）", parameters: SessionExt(
            ("id", SString("会话 id"), true))),
        M("session.truncate", "session", "截断会话消息", parameters: SessionExt(
            ("id", SString("会话 id"), true),
            ("keepCount", SInteger("保留消息条数（>=0）"), true))),
        M("session.fork", "session", "从既有会话分叉新会话", parameters: SessionExt(
            ("id", SString("源会话 id"), true),
            ("keepCount", SInteger("保留消息条数，缺省 0"), false),
            ("title", SString("新会话标题"), false))),
        M("session.update", "session", "更新会话元数据", parameters: SessionExt(
            ("id", SString("会话 id"), true),
            ("title", SString("新标题"), false),
            ("reasoningLevel", SString("推理深度", ReasoningValues), false),
            ("networkEnabled", SBoolean("联网开关"), false),
            ("expertId", SString("绑定专家 persona id（空串解除绑定）"), false))),
        M("session.archive", "session", "归档会话", parameters: SessionExt(
            ("id", SString("会话 id"), true),
            ("archived", SBoolean("true 归档 / false 恢复，缺省 true"), false))),
        M("session.delete", "session", "删除会话", parameters: SessionExt(
            ("id", SString("会话 id"), true))),
        M("session.resume", "session", "恢复会话为连接的遗留会话", parameters: SessionExt(
            ("id", SString("会话 id"), true))),
        M("session.new", "session", "新建会话", parameters: SessionExt(
            ("reasoningLevel", SString("推理深度", ReasoningValues), false),
            ("expertId", SString("绑定专家 persona id（expert.list 可查）"), false))),

        // project
        M("project.list", "project", "列出注册项目", parameters: EmptyParams),
        M("project.upsert", "project", "注册或更新项目", parameters: SObject(
            ("path", SString("项目根目录（禁止用户主目录/状态目录）"), true),
            ("id", SString("更新已有项目时指定"), false),
            ("name", SString("显示名"), false))),
        M("project.remove", "project", "移除项目", parameters: SObject(
            ("id", SString("项目 id"), true),
            ("keepSessions", SBoolean("true 保留会话历史，缺省 false"), false))),

        // evolution
        M("evolution.inspect", "evolution", "聚合失败信号的缺陷报告（含健康分、趋势与分布）", parameters: SObject(
            ("limit", SInteger("扫描事件条数，缺省 DefaultScanLimit"), false))),
        M("evolution.reflect", "evolution", "发起反思回合产出技能草稿", parameters: EmptyParams),
        M("evolution.pending-list", "evolution", "待审技能草稿清单（含状态与草稿文件内容）", parameters: EmptyParams),
        M("evolution.decide", "evolution", "人工终审技能草稿（adopt 前可用 prompt 改写提示词）", parameters: SObject(
            ("fingerprint", SString("缺陷指纹"), true),
            ("decision", SString("裁决", "adopt", "reject", "defer"), true),
            ("prompt", SString("adopt 时改写技能提示词（可选）"), false))),
        M("evolution.history", "evolution", "反思回合历史（触发方式与处理结果）", parameters: SObject(
            ("limit", SInteger("返回条数，缺省 20"), false))),
        M("evolution.stats", "evolution", "进化效果统计（采纳率、技能使用与缺陷复发）", parameters: EmptyParams),
        M("evolution.config.get", "evolution", "读取自动反思配置", parameters: EmptyParams),
        M("evolution.config.set", "evolution", "更新自动反思配置", parameters: SObject(
            ("autoReflect", SBoolean("是否按间隔自动运行反思回合"), false),
            ("intervalMinutes", SInteger("自动反思间隔（分钟，30..10080）"), false),
            ("thresholdEnabled", SBoolean("缺陷数达标即自动反思（与定时并存）"), false),
            ("thresholdSignals", SInteger("触发反思的缺陷数阈值（1..50）"), false))),

        // diagnostics
        M("doctor", "diagnostics", "健康检查（人类可读文本）", parameters: EmptyParams),
        M("doctor.run", "diagnostics", "运行健康检查（结构化结果）", parameters: EmptyParams),
        M("lock.list", "diagnostics", "列出文件写锁", parameters: EmptyParams),

        // system
        M("factory.reset", "system", "工厂重置（清空全部数据）", parameters: SObject(
            ("keepSessions", SBoolean("true 保留会话历史，缺省 false"), false))),
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
                ["terminalEvents"] = ToArray(method.TerminalEvents.Append("error")),
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
                    ("details", SString("结构化元数据；error 事件必含 code"), false)),
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
