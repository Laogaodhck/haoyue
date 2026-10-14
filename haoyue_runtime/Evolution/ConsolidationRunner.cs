using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Haoyue.Runtime.Agents;
using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Coordination;
using Haoyue.Runtime.Data;
using Haoyue.Runtime.Events;
using Haoyue.Runtime.Providers;
using Haoyue.Runtime.Projects;
using Haoyue.Runtime.Sessions;
using Haoyue.Runtime.Workspaces;
using Microsoft.Extensions.DependencyInjection;

namespace Haoyue.Runtime.Evolution;

/// <summary>Executes one consolidation turn; overridable in tests.</summary>
public delegate Task<AgentTurnResult> ConsolidationTurnRunner(
    WorkspaceInfo workspace, AgentSession session, string prompt, CancellationToken ct);

/// <summary>Outcome summary of one <see cref="ConsolidationRunner.RunAsync"/> invocation.</summary>
public sealed record ConsolidationRunResult(
    int SessionsScanned,
    int FactsAdded,
    int FactsSuperseded,
    string? SessionId,
    string? Error,
    int MemoryPromoted = 0,
    string? MemoryNote = null);

/// <summary>
/// H-C 记忆固化引擎：把「经历」变「记忆」的自动化沉淀链路。定时扫描近期更新过的
/// 会话（全局 + 全部已注册项目工作区），在一次隔离回合里提炼候选事实，按来源
/// 工作区写回 FactsStore——幂等去重与 M-D 冲突仲裁由存储层兜底，模型只负责判断
/// 「什么值得记住」。模型标了 durable 的高置信条目额外晋升到该工作区的 MEMORY.md
/// （只读模式下不写，条数与额度由 MemoryFileWriter/MemoryBudget 兜底），否则固化永远只能
/// 写事实层，策展记忆层只能靠用户手抄。与 ReflectionRunner 同构：隔离回合、JSON
/// 输出契约、单航班、墙钟预算、anti-self-feeding（自己的固化会话不再被扫描）。
/// 低置信事实照常入库，注入侧自带「待确认」标注，不把推断当定论。
/// </summary>
public sealed class ConsolidationRunner
{
    private static readonly Regex JsonFence = new(
        "```\\s*json\\s*\\n(?<body>.*?)```", RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>单次扫描的会话数上限：固化是背景杂务，不能无限膨胀。</summary>
    private const int MaxScanSessions = 30;
    /// <summary>单会话摘要体积上限：只取标题 + 首条诉求 + 末条结论，杜绝整段历史回灌。</summary>
    private const int MaxEntryChars = 1_200;
    /// <summary>固化会话标题标记：跨进程识别自己的产物，避免自我回喂。</summary>
    internal const string SessionTitle = "记忆固化（后台）";
    /// <summary>单轮可晋升进 MEMORY.md 的条数上限：策展文件被后台写手灌满是最坏结局。</summary>
    private const int MaxMemoryPromotionsPerPass = 5;
    /// <summary低于此置信度不晋升：MEMORY.md 每回合整段注入，把推断当定论会持续误导。</summary>
    private const double DurableMinConfidence = 0.85;

    private readonly HaoyueRuntime _runtime;
    private readonly IFileLockCoordinator _fileLocks;
    private readonly ILlmHttpFactory _sharedHttp;
    private readonly CircuitBreaker _sharedBreaker;
    private readonly LocalModelCache? _sharedLocalModels;
    private readonly ConsolidationTurnRunner _turnRunner;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HashSet<string> _ownSessionIds = new(StringComparer.OrdinalIgnoreCase);

    public ConsolidationRunner(
        HaoyueRuntime runtime,
        IFileLockCoordinator fileLocks,
        ILlmHttpFactory sharedHttp,
        CircuitBreaker sharedBreaker,
        ConsolidationTurnRunner? turnRunner = null,
        LocalModelCache? sharedLocalModels = null)
    {
        _runtime = runtime;
        _fileLocks = fileLocks;
        _sharedHttp = sharedHttp;
        _sharedBreaker = sharedBreaker;
        _turnRunner = turnRunner ?? RunIsolatedTurnAsync;
        _sharedLocalModels = sharedLocalModels;
    }

    /// <summary>
    /// Runs one consolidation pass. A pass still running from a previous trigger is
    /// rejected immediately (single-flight, mirrors the scheduler's discipline).
    /// Every completed pass — empty, failed, or successful — publishes a
    /// <see cref="MemoryConsolidationCompletedEvent"/>.
    /// </summary>
    public async Task<ConsolidationRunResult> RunAsync(string trigger, CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false))
            return new ConsolidationRunResult(0, 0, 0, null, "固化 turn 已在进行中");

        ConsolidationRunResult result;
        try
        {
            result = await RunCoreAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            result = new ConsolidationRunResult(0, 0, 0, null, ex.Message);
        }
        finally
        {
            _gate.Release();
        }
        _runtime.Events.Publish(new MemoryConsolidationCompletedEvent(
            result.SessionId ?? "-", result.SessionsScanned, result.FactsAdded, result.FactsSuperseded,
            result.Error, result.MemoryPromoted, result.MemoryNote));
        return result;
    }

    private async Task<ConsolidationRunResult> RunCoreAsync(CancellationToken ct)
    {
        var lookbackDays = Math.Clamp(
            _runtime.ConfigStore.Config.Agent.MemoryConsolidationLookbackDays, 1, 30);
        var entries = ScanRecentSessions(lookbackDays);
        if (entries.Count == 0)
            return new ConsolidationRunResult(0, 0, 0, null, null);

        // 固化回合跑在 runtime 当前工作区（daemon 下即全局工作区）；事实按来源
        // 会话所在的工作区分别写回，不把项目事实混进全局层。
        var workspace = _runtime.Workspace;
        var session = _runtime.Sessions.Create(
            workspace,
            reasoningLevel: _runtime.ConfigStore.Config.Agent.ReasoningLevel,
            networkEnabled: true);
        _runtime.Sessions.UpdateMetadata(workspace, session.Header.Id, title: SessionTitle);
        var sessionId = session.Header.Id;
        lock (_ownSessionIds) _ownSessionIds.Add(sessionId);

        // Hard wall-clock budget shared with scheduled turns; a hung consolidation
        // turn must never wedge the timer.
        var timeoutSeconds = Math.Clamp(
            _runtime.ConfigStore.Config.Agent.ScheduledTurnTimeoutSeconds, 60, 86_400);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        AgentTurnResult result;
        try
        {
            result = await _turnRunner(workspace, session, BuildPrompt(entries), budget.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            var cancelled = ct.IsCancellationRequested ? "固化已被取消" : $"固化超过 {timeoutSeconds} 秒，已中止";
            return new ConsolidationRunResult(entries.Count, 0, 0, sessionId, cancelled);
        }
        catch (Exception ex)
        {
            return new ConsolidationRunResult(entries.Count, 0, 0, sessionId, ex.Message);
        }

        if (result.Error is not null || result.Cancelled)
            return new ConsolidationRunResult(
                entries.Count, 0, 0, sessionId, result.Error ?? "固化 turn 被取消");

        var (facts, parseError) = ParseFacts(result.Text);
        if (parseError is not null)
            return new ConsolidationRunResult(entries.Count, 0, 0, sessionId, parseError);

        var store = new FactsStore();
        var added = 0;
        var merged = 0;
        var promoted = 0;
        var skipped = new List<string>();
        var memoryMode = MemoryMode.Normalize(_runtime.ConfigStore.Config.Agent.MemoryMode);
        foreach (var fact in facts)
        {
            if (fact.Entry < 1 || fact.Entry > entries.Count) continue;
            var source = entries[fact.Entry - 1].Workspace;
            var before = store.ListActiveForRoot(source.Root).Count;
            store.Add(
                source.Root, fact.Content, fact.Topic,
                source: "agent", confidence: fact.Confidence, supersedes: fact.Supersedes);
            var after = store.ListActiveForRoot(source.Root).Count;
            if (after > before) added++;
            else merged++; // 幂等刷新或仲裁合并：净增长为零

            if (!fact.Durable) continue;
            if (promoted >= MaxMemoryPromotionsPerPass)
            {
                skipped.Add("本轮晋升条数已达上限");
                continue;
            }
            // 手动管理模式下 MEMORY.md 归用户所有，后台写手不得插手；注入侧同时带只读契约。
            if (memoryMode == MemoryMode.Manual)
            {
                skipped.Add("记忆为手动管理模式，未改写 MEMORY.md");
                continue;
            }
            if (fact.Confidence < DurableMinConfidence)
            {
                skipped.Add($"低置信条目未晋升（{fact.Confidence:0.0} < {DurableMinConfidence:0.0}）");
                continue;
            }
            var append = await MemoryFileWriter.AppendAsync(
                source.MemoryFile, fact.Content, fact.Topic, ct).ConfigureAwait(false);
            if (append.Result == MemoryFileWriter.Status.Written) promoted++;
            else skipped.Add(append.Message ?? append.Result.ToString());
        }
        return new ConsolidationRunResult(
            entries.Count, added, merged, sessionId, null, promoted,
            skipped.Count > 0 ? string.Join("；", skipped.Distinct(StringComparer.OrdinalIgnoreCase)) : null);
    }

    // ---------------------------------------------------------------- scan

    /// <summary>One scanned session: which workspace it belongs to + a compact digest.
    /// The whole WorkspaceInfo (not just the root) travels with it because durable
    /// promotions need that workspace's MEMORY.md path, not only facts.json.</summary>
    internal sealed record SessionDigest(WorkspaceInfo Workspace, string Digest)
    {
        public string Root => Workspace.Root;
    }

    /// <summary>
    /// Scans recently-updated sessions across the runtime's current workspace plus
    /// every registered project. Own consolidation sessions are excluded by title
    /// (cross-process) and by in-memory id set (this process).
    /// </summary>
    private List<SessionDigest> ScanRecentSessions(int lookbackDays)
    {
        var cutoff = DateTimeOffset.UtcNow.AddDays(-lookbackDays);
        var digests = new List<SessionDigest>();
        var seenScopes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var workspace in EnumerateWorkspaces())
        {
            var scope = HaoyueDatabase.ScopeKey(workspace);
            if (!seenScopes.Add(scope)) continue;
            foreach (var header in _runtime.Sessions.List(workspace))
            {
                if (header.UpdatedAt < cutoff) continue;
                if (IsOwnSession(header)) continue;
                var session = _runtime.Sessions.Load(workspace, header.Id);
                if (session is null || session.Messages.Count == 0) continue;
                digests.Add(new SessionDigest(workspace, DigestOf(session)));
                if (digests.Count >= MaxScanSessions) return digests;
            }
        }
        return digests;
    }

    private IEnumerable<WorkspaceInfo> EnumerateWorkspaces()
    {
        yield return _runtime.Workspace;
        foreach (var project in _runtime.Projects.List())
        {
            WorkspaceInfo info;
            try { info = _runtime.Workspaces.Detect(project.Path); }
            catch (Exception)
            {
                // 项目目录已被删除/移动：跳过而非中断整轮固化。
                continue;
            }
            yield return info;
        }
    }

    private bool IsOwnSession(SessionHeader header)
    {
        if (string.Equals(header.Title, SessionTitle, StringComparison.Ordinal)) return true;
        lock (_ownSessionIds) return _ownSessionIds.Contains(header.Id);
    }

    /// <summary>标题 + 首条用户诉求 + 末条助手结论，超长截断。</summary>
    private static string DigestOf(AgentSession session)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(session.Header.Title))
            sb.AppendLine($"标题：{session.Header.Title}");
        var firstUser = session.Messages.FirstOrDefault(
            m => m.Role == ChatRole.User && !string.IsNullOrWhiteSpace(m.Text));
        if (firstUser is not null) sb.AppendLine($"诉求：{Clip(firstUser.Text)}");
        var lastAssistant = session.Messages.LastOrDefault(
            m => m.Role == ChatRole.Assistant && !string.IsNullOrWhiteSpace(m.Text));
        if (lastAssistant is not null) sb.AppendLine($"结论：{Clip(lastAssistant.Text)}");
        var text = sb.ToString().Trim();
        return text.Length > MaxEntryChars ? text[..MaxEntryChars] + "…" : text;
    }

    private static string Clip(string text) =>
        text.Length > 400 ? text[..400] + "…" : text;

    // ---------------------------------------------------------------- prompt & parse

    internal string BuildPrompt(IReadOnlyList<SessionDigest> entries)
    {
        var sb = new StringBuilder();
        sb.AppendLine("你正处于【记忆固化模式】——这是把近期会话经验沉淀为项目事实的后台 turn。");
        sb.AppendLine("你不是在为用户执行任务；你的产出会被程序自动解析并写入事实库，必须严格遵守文末的输出契约。");
        sb.AppendLine();
        sb.AppendLine($"## 待固化的会话（{entries.Count} 条，编号即 entry，括号内为事实归属的工作区）");
        for (var i = 0; i < entries.Count; i++)
        {
            sb.AppendLine($"{i + 1}. [工作区 {entries[i].Root}]");
            foreach (var line in entries[i].Digest.Split('\n'))
                sb.AppendLine("   " + line.TrimEnd());
        }
        sb.AppendLine();
        sb.AppendLine("## 你的任务");
        sb.AppendLine("逐条判断是否存在值得长期记住的事实：构建/测试命令、部署约定、环境要点、关键决定、用户明确提出的偏好。");
        sb.AppendLine("- 只沉淀跨回合有用且稳定的事实；一次性中间状态、寒暄、任务进度都不要。");
        sb.AppendLine("- content 用一句话说清；topic 用简短英文标签（build / test / deploy / convention / env / decision / preference 等）。");
        sb.AppendLine("- confidence：用户明确说过的填 0.9~1.0，从代码或日志推断出的填 0.5~0.8。");
        sb.AppendLine("- 纠正旧事实时，supersedes 填旧事实的完整内容（可选）。");
        sb.AppendLine("- 只有「值得长期放在策展记忆里」的条目才加 durable:true：用户明确表达过的偏好、长期有效的项目约定。");
        sb.AppendLine($"  durable 条目会同时追加进该工作区的 MEMORY.md（每回合整段注入系统提示），所以宁缺勿滥，"
            + $"一轮最多晋升 {MaxMemoryPromotionsPerPass} 条；低置信（<{DurableMinConfidence:0.0}）"
            + "与手动管理模式下程序会自行拒绝，不需要你重复标注。");
        sb.AppendLine("- 没有值得沉淀的内容就返回空数组，不要硬凑。");
        sb.AppendLine();
        sb.AppendLine("## 输出契约（必须遵守）");
        sb.AppendLine("回复必须以一个 ```json 围栏代码块结尾，且只包含以下形态：");
        sb.AppendLine("{\"facts\":[{\"entry\":1,\"content\":\"构建用 pnpm build\",\"topic\":\"build\",\"confidence\":0.9},"
            + "{\"entry\":2,\"content\":\"回复统一使用中文\",\"topic\":\"preference\",\"confidence\":1.0,\"durable\":true}]}");
        sb.AppendLine("{\"facts\":[]}");
        return sb.ToString();
    }

    internal sealed record ParsedFact(
        int Entry, string Content, string? Topic, double Confidence, string? Supersedes, bool Durable = false);

    /// <summary>Extracts the trailing ```json conclusion block; null facts means parse failure.</summary>
    internal static (List<ParsedFact>? Facts, string? Error) ParseFacts(string text)
    {
        Match? last = null;
        foreach (var match in JsonFence.Matches(text).Cast<Match>())
            last = match;
        if (last is null)
            return (null, "固化输出缺少 ```json 结论块");

        var body = last.Groups["body"].Value;
        var start = body.IndexOf('{');
        var end = body.LastIndexOf('}');
        if (start < 0 || end <= start)
            return (null, "结论块不是合法 JSON 对象");

        try
        {
            var node = JsonNode.Parse(body[start..(end + 1)]);
            var array = node?["facts"] as JsonArray;
            if (array is null)
                return (null, "结论块缺少 facts 数组");
            var facts = new List<ParsedFact>();
            foreach (var item in array)
            {
                if (item is not JsonObject obj) continue;
                var content = obj["content"]?.GetValue<string>()?.Trim();
                var entry = obj["entry"]?.GetValue<int?>();
                if (string.IsNullOrEmpty(content) || entry is null) continue;
                var confidence = obj["confidence"]?.GetValue<double?>() ?? 0.8;
                facts.Add(new ParsedFact(
                    entry.Value,
                    content,
                    obj["topic"]?.GetValue<string>()?.Trim(),
                    Math.Clamp(confidence, 0, 1),
                    obj["supersedes"]?.GetValue<string>()?.Trim(),
                    ReadBool(obj["durable"])));
            }
            return (facts, null);
        }
        catch (JsonException ex)
        {
            return (null, $"结论块 JSON 解析失败：{ex.Message}");
        }
        catch (InvalidOperationException ex)
        {
            // entry/confidence 类型不符（字符串塞进数字字段等）。
            return (null, $"结论块字段类型异常：{ex.Message}");
        }
    }

    /// <summary>Tolerant boolean read: models emit durable as true / "true" / 1 interchangeably,
    /// and a GetValue&lt;bool&gt; throw on one stray field would discard the whole pass.</summary>
    private static bool ReadBool(JsonNode? node) => node?.GetValueKind() switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String => node.GetValue<string>().Trim().ToLowerInvariant() is "true" or "1" or "yes",
        JsonValueKind.Number => node.GetValue<double>() >= 1,
        _ => false,
    };

    /// <summary>Default turn host: an isolated runtime sharing daemon infrastructure.</summary>
    private async Task<AgentTurnResult> RunIsolatedTurnAsync(
        WorkspaceInfo workspace, AgentSession session, string prompt, CancellationToken ct)
    {
        var owner = $"{session.Header.Id}/consolidation";
        await using var turnRuntime = HaoyueRuntime.CreateIsolated(workspace, _fileLocks, owner, services =>
        {
            services.AddSingleton<ILlmHttpFactory>(_sharedHttp);
            services.AddSingleton(_sharedBreaker);
            if (_sharedLocalModels is not null) services.AddSingleton(_sharedLocalModels);
        });
        turnRuntime.Prompts.SetWorkspaceRoot(workspace.IsGlobal ? null : workspace.PromptsDir);
        turnRuntime.Skills.Attach(workspace);
        return await turnRuntime.Agent.RunTurnAsync(session, workspace, prompt, ct).ConfigureAwait(false);
    }
}
