using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Haoyue.Runtime.Agents;
using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Coordination;
using Haoyue.Runtime.Events;
using Haoyue.Runtime.Providers;
using Haoyue.Runtime.Sessions;
using Haoyue.Runtime.Workspaces;
using Microsoft.Extensions.DependencyInjection;

namespace Haoyue.Runtime.Evolution;

/// <summary>Executes one reflection turn; overridable in tests.</summary>
public delegate Task<AgentTurnResult> ReflectionTurnRunner(
    WorkspaceInfo workspace, AgentSession session, string prompt, CancellationToken ct);

/// <summary>Outcome summary of one <see cref="ReflectionRunner.RunAsync"/> invocation.</summary>
public sealed record ReflectionRunResult(
    int Processed,
    int Candidates,
    int NoAction,
    int Skipped,
    int Failed,
    string? SessionId,
    string? Error);

/// <summary>
/// Evolution engine stages E2+E3: turns aggregated defect reports into candidate
/// skill drafts through a dedicated reflection turn. The turn runs in an isolated
/// runtime (same pattern as scheduled turns, sharing the daemon's HTTP pool and
/// file locks) and is contractually confined to the labs directory; TurnScope
/// bookkeeping already active in every turn rolls back anything that strays.
/// Anti-self-feeding: fingerprints already in the decision log are skipped, and
/// the reflection session id is recorded so the aggregator excludes its events.
/// Safety red line: reflection may only produce skill-layer data assets
/// (skill.yaml / prompt.txt / rationale.md) — never runtime code.
/// </summary>
public sealed class ReflectionRunner
{
    private static readonly Regex JsonFence = new(
        "```\\s*json\\s*\\n(?<body>.*?)```", RegexOptions.Singleline | RegexOptions.Compiled);

    private readonly HaoyueRuntime _runtime;
    private readonly IFileLockCoordinator _fileLocks;
    private readonly ILlmHttpFactory _sharedHttp;
    private readonly CircuitBreaker _sharedBreaker;
    private readonly LocalModelCache? _sharedLocalModels;
    private readonly EvolutionStore _store;
    private readonly ReflectionTurnRunner _turnRunner;
    private readonly string _labsRoot;
    private readonly string _candidatesRoot;
    private readonly string _skillsRoot;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ReflectionRunner(
        HaoyueRuntime runtime,
        IFileLockCoordinator fileLocks,
        ILlmHttpFactory sharedHttp,
        CircuitBreaker sharedBreaker,
        EvolutionStore? store = null,
        ReflectionTurnRunner? turnRunner = null,
        LocalModelCache? sharedLocalModels = null,
        string? labsRoot = null,
        string? candidatesRoot = null,
        string? skillsRoot = null)
    {
        _runtime = runtime;
        _fileLocks = fileLocks;
        _sharedHttp = sharedHttp;
        _sharedBreaker = sharedBreaker;
        _sharedLocalModels = sharedLocalModels;
        _store = store ?? new EvolutionStore(runtime.Database);
        _turnRunner = turnRunner ?? RunIsolatedTurnAsync;
        _labsRoot = labsRoot ?? HaoyuePaths.LabsDir;
        _candidatesRoot = candidatesRoot ?? HaoyuePaths.SkillsCandidatesDir;
        _skillsRoot = skillsRoot ?? HaoyuePaths.SkillsDir;
    }

    /// <summary>
    /// Runs one reflection pass. A pass still running from a previous trigger is
    /// rejected immediately (mirrors the scheduler's single-flight discipline).
    /// Every completed pass — empty, failed, or successful — lands in the
    /// evolution_runs history tagged with its <paramref name="trigger"/>.
    /// </summary>
    public async Task<ReflectionRunResult> RunAsync(string trigger, CancellationToken ct)
    {
        if (!await _gate.WaitAsync(0, ct).ConfigureAwait(false))
            return new ReflectionRunResult(0, 0, 0, 0, 0, null, "反思 turn 已在进行中");
        ReflectionRunResult result;
        try
        {
            result = await RunCoreAsync(ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            RecordRun(trigger, new ReflectionRunResult(0, 0, 0, 0, 0, null, ex.Message));
            throw;
        }
        finally
        {
            _gate.Release();
        }
        RecordRun(trigger, result);
        return result;
    }

    private void RecordRun(string trigger, ReflectionRunResult result) =>
        _store.RecordRun(new EvolutionStore.EvolutionRun(
            0, result.SessionId, trigger, result.Processed, result.Candidates,
            result.NoAction, result.Skipped, result.Failed, result.Error, DateTimeOffset.UtcNow));

    private async Task<ReflectionRunResult> RunCoreAsync(CancellationToken ct)
    {
        var exclude = _store.ReflectionSessionIds();
        var reports = new DefectAggregator(_runtime.Database)
            .Aggregate(DefectAggregator.DefaultScanLimit, exclude);
        var pending = reports
            .Where(r => _store.Get(r.Fingerprint) is not { } existing || !EvolutionStatus.BlocksRetry(existing.Status))
            .ToList();
        var skipped = reports.Count - pending.Count;
        if (pending.Count == 0)
            return new ReflectionRunResult(0, 0, 0, skipped, 0, null, null);

        ResetLabs();
        var workspace = _runtime.Workspaces.CreateGlobal();
        var session = _runtime.Sessions.Create(
            workspace,
            reasoningLevel: _runtime.ConfigStore.Config.Agent.ReasoningLevel,
            networkEnabled: true);
        _runtime.Sessions.UpdateMetadata(workspace, session.Header.Id, title: "自我反思（进化引擎）");
        var sessionId = session.Header.Id;

        // Hard wall-clock budget shared with scheduled turns; a hung reflection
        // turn must never wedge the engine.
        var timeoutSeconds = Math.Clamp(
            _runtime.ConfigStore.Config.Agent.ScheduledTurnTimeoutSeconds, 60, 86_400);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        AgentTurnResult result;
        try
        {
            result = await _turnRunner(workspace, session, BuildPrompt(pending), budget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            RecordFailed(pending, sessionId, ct.IsCancellationRequested ? "反思已被取消" : $"反思超过 {timeoutSeconds} 秒，已中止");
            Publish(sessionId, 0, 0, skipped, pending.Count, "cancelled");
            return new ReflectionRunResult(0, 0, 0, skipped, pending.Count, sessionId, "cancelled");
        }
        catch (Exception ex)
        {
            RecordFailed(pending, sessionId, ex.Message);
            Publish(sessionId, 0, 0, skipped, pending.Count, ex.Message);
            return new ReflectionRunResult(0, 0, 0, skipped, pending.Count, sessionId, ex.Message);
        }

        if (result.Error is not null || result.Cancelled)
        {
            var reason = result.Error ?? "反思 turn 被取消";
            RecordFailed(pending, sessionId, reason);
            Publish(sessionId, 0, 0, skipped, pending.Count, reason);
            return new ReflectionRunResult(0, 0, 0, skipped, pending.Count, sessionId, reason);
        }

        var (action, skillName, summary, parseError) = ParseConclusion(result.Text);
        if (parseError is not null || action is null)
        {
            RecordFailed(pending, sessionId, parseError ?? "反思输出缺少结论块");
            Publish(sessionId, 0, 0, skipped, pending.Count, parseError);
            return new ReflectionRunResult(0, 0, 0, skipped, pending.Count, sessionId, parseError);
        }

        var now = DateTimeOffset.UtcNow;
        var candidates = 0;
        var noAction = 0;
        foreach (var report in pending)
        {
            if (action == EvolutionAction.NewSkill || action == EvolutionAction.ReviseSkill)
            {
                var promoted = PromoteCandidate(skillName, report, sessionId, summary, now, out var error);
                if (promoted is null)
                {
                    RecordFailed(pending, sessionId, error);
                    Publish(sessionId, 0, 0, skipped, pending.Count, error);
                    return new ReflectionRunResult(0, 0, 0, skipped, pending.Count, sessionId, error);
                }
                _store.Record(new EvolutionRecord(
                    report.Fingerprint, report.Kind.ToString(), EvolutionStatus.Candidate,
                    promoted, sessionId, summary, now, now));
                candidates++;
            }
            else
            {
                _store.Record(new EvolutionRecord(
                    report.Fingerprint, report.Kind.ToString(), EvolutionStatus.NoAction,
                    null, sessionId, summary, now, now));
                noAction++;
            }
        }

        Publish(sessionId, pending.Count, candidates, noAction, skipped, null);
        return new ReflectionRunResult(pending.Count, candidates, noAction, skipped, 0, sessionId, null);
    }

    private void Publish(string sessionId, int processed, int candidates, int noAction, int skipped, string? error) =>
        _runtime.Events.Publish(new EvolutionReflectionCompletedEvent(
            sessionId, processed, candidates, noAction, skipped, error));

    private void RecordFailed(IReadOnlyList<DefectReport> reports, string sessionId, string reason)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var report in reports)
            _store.Record(new EvolutionRecord(
                report.Fingerprint, report.Kind.ToString(), EvolutionStatus.Failed,
                null, sessionId, reason, now, now));
    }

    /// <summary>Clears stale labs scratch from previous (failed or consumed) runs.</summary>
    private void ResetLabs()
    {
        try
        {
            if (Directory.Exists(_labsRoot)) Directory.Delete(_labsRoot, recursive: true);
        }
        catch (IOException) { }
        Directory.CreateDirectory(_labsRoot);
    }

    // ---------------------------------------------------------------- approval (P3)

    /// <summary>One draft file bundled with a candidate for client-side preview.</summary>
    public sealed record CandidateFile(string Name, string Content);

    /// <summary>One draft awaiting human review (candidate or deferred).</summary>
    public sealed record CandidateSummary(
        string Fingerprint, string Kind, string SkillName, string? CandidateDir,
        string? Summary, DateTimeOffset CreatedAt, string Status,
        IReadOnlyList<CandidateFile>? Files = null);

    /// <summary>Lists recorded drafts (candidate or deferred) whose directories still exist.</summary>
    public IReadOnlyList<CandidateSummary> ListCandidates()
    {
        return _store.List(null)
            .Where(r => (r.Status == EvolutionStatus.Candidate || r.Status == EvolutionStatus.Deferred) &&
                        r.CandidateDir is not null && Directory.Exists(r.CandidateDir))
            .Select(r => new CandidateSummary(
                r.Fingerprint, r.Kind, EvolutionAnalytics.SkillNameFromDir(r.CandidateDir) ?? "-",
                r.CandidateDir, r.Summary, r.CreatedAt, r.Status,
                ReadDraftFiles(r.CandidateDir!)))
            .ToList();
    }

    private const int MaxDraftFileChars = 20_000;
    private static readonly string[] DraftFileNames = ["skill.yaml", "skill.yml", "prompt.txt", "rationale.md"];

    private static IReadOnlyList<CandidateFile>? ReadDraftFiles(string candidateDir)
    {
        var files = new List<CandidateFile>();
        foreach (var name in DraftFileNames)
        {
            try
            {
                var text = File.ReadAllText(Path.Combine(candidateDir, name));
                if (text.Length > MaxDraftFileChars)
                    text = text[..MaxDraftFileChars] + "…（已截断）";
                files.Add(new CandidateFile(name, text));
            }
            catch (IOException) { }
        }
        return files.Count > 0 ? files : null;
    }

    /// <summary>
    /// Applies a human decision. Adopt moves the candidate directory into the live
    /// skills root (SkillManager hot-loads it on the next scan), optionally with a
    /// human-edited prompt written over the draft's prompt.txt; reject deletes the
    /// draft; defer parks it (still adoptable/rejectable later). Adopt and reject
    /// close the fingerprint so the defect is never re-processed.
    /// </summary>
    public EvolutionRecord Decide(string fingerprint, string decision, string? promptOverride = null)
    {
        var record = _store.Get(fingerprint)
            ?? throw new InvalidOperationException("未找到该进化指纹的记录");
        if (record.Status is not (EvolutionStatus.Candidate or EvolutionStatus.Deferred))
            throw new InvalidOperationException($"该记录状态为 {record.Status}，不可审批");

        if (decision == "defer")
        {
            return record.Status == EvolutionStatus.Deferred
                ? record
                : _store.Decide(fingerprint, EvolutionStatus.Deferred)!;
        }
        if (decision is not ("adopt" or "reject"))
            throw new InvalidOperationException($"未知决策：{decision}（应为 adopt / reject / defer）");

        var dir = record.CandidateDir;
        if (dir is not null && Directory.Exists(dir))
        {
            if (decision == "adopt")
            {
                if (!string.IsNullOrWhiteSpace(promptOverride))
                    File.WriteAllText(Path.Combine(dir, "prompt.txt"), promptOverride);
                var skillName = EvolutionAnalytics.SkillNameFromDir(dir)
                    ?? throw new InvalidOperationException($"候选目录命名异常，请手动处理：{dir}");
                var destination = Path.Combine(_skillsRoot, skillName);
                if (Directory.Exists(destination))
                    throw new InvalidOperationException($"技能目录已存在，请手动处理：{destination}");
                Directory.CreateDirectory(_skillsRoot);
                Directory.Move(dir, destination);
            }
            else
            {
                try { Directory.Delete(dir, recursive: true); }
                catch (IOException) { /* keep the dir; status is still closed */ }
            }
        }

        return _store.Decide(fingerprint, decision == "adopt" ? EvolutionStatus.Adopted : EvolutionStatus.Rejected)!;
    }

    /// <summary>Default turn host: an isolated runtime sharing daemon infrastructure.</summary>
    private async Task<AgentTurnResult> RunIsolatedTurnAsync(
        WorkspaceInfo workspace, AgentSession session, string prompt, CancellationToken ct)
    {
        var owner = $"{session.Header.Id}/evolution";
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

    internal string BuildPrompt(IReadOnlyList<DefectReport> reports)
    {
        var sb = new StringBuilder();
        sb.AppendLine("你正处于【架构师反思模式】——这是进化引擎的一次自我反思 turn。");
        sb.AppendLine("你不是在为用户执行任务；你的产出会被程序自动解析并归档，必须严格遵守文末的输出契约。");
        sb.AppendLine();
        sb.AppendLine($"## 待消化的缺陷报告（{reports.Count} 条）");
        for (var i = 0; i < reports.Count; i++)
        {
            var r = reports[i];
            sb.AppendLine($"{i + 1}. 指纹 {r.Fingerprint} | 类型 {r.Kind} | 严重度 {r.Severity ?? "-"} | 工具 {r.ToolName ?? "-"} | 次数 {r.Occurrences} | 会话 {r.SessionId ?? "-"}");
            sb.AppendLine($"   时间 {r.FirstSeen:yyyy-MM-dd HH:mm} ~ {r.LastSeen:yyyy-MM-dd HH:mm}");
            if (!string.IsNullOrEmpty(r.ErrorSummary))
                sb.AppendLine($"   错误摘要：{r.ErrorSummary}");
        }
        sb.AppendLine();
        sb.AppendLine("## 你的任务");
        sb.AppendLine("1. 逐条分析缺陷：判断属于一次性失误、环境问题，还是可通过技能层改进避免的能力问题。");
        sb.AppendLine("2. 若缺陷可以通过新增或修订一个技能（提示词资产）来避免：");
        sb.AppendLine($"   - 在 labs 草稿目录 `{_labsRoot.Replace("\\", "/")}/<skill-name>/` 下创建三个文件：");
        sb.AppendLine("     - `skill.yaml`：manifest，含 name、description、triggers 列表（触发词）、allowed-tools 列表（工具边界），并显式写 `always-listed: false`；");
        sb.AppendLine("     - `prompt.txt`：技能提示词正文（面向未来遇到同类场景的 agent）；");
        sb.AppendLine("     - `rationale.md`：缺陷来源、改进假说与验证方式说明。");
        sb.AppendLine($"   - 只允许在 `{_labsRoot.Replace("\\", "/")}` 目录内写文件；严禁修改其他任何文件、严禁改动运行时代码。");
        sb.AppendLine("3. 若缺陷不需要技能层行动，选择 no-action 并在 summary 中归类（一次性失误 / 环境问题 / 超出能力边界）。");
        sb.AppendLine();
        sb.AppendLine("## 输出契约（必须遵守）");
        sb.AppendLine("回复必须以一个 ```json 围栏代码块结尾，且只包含以下三种形态之一：");
        sb.AppendLine("{\"action\":\"new-skill\",\"skillName\":\"kebab-case-name\",\"summary\":\"一句话结论\"}");
        sb.AppendLine("{\"action\":\"revise-skill\",\"skillName\":\"kebab-case-name\",\"summary\":\"一句话结论\"}（修订同样把完整草稿写入 labs）");
        sb.AppendLine("{\"action\":\"no-action\",\"summary\":\"一句话结论\"}");
        return sb.ToString();
    }

    /// <summary>Extracts the trailing ```json conclusion block; null action means parse failure.</summary>
    internal static (string? Action, string? SkillName, string? Summary, string? Error) ParseConclusion(string text)
    {
        Match? last = null;
        foreach (var match in JsonFence.Matches(text).Cast<Match>())
            last = match;
        if (last is null)
            return (null, null, null, "反思输出缺少 ```json 结论块");

        var body = last.Groups["body"].Value;
        var start = body.IndexOf('{');
        var end = body.LastIndexOf('}');
        if (start < 0 || end <= start)
            return (null, null, null, "结论块不是合法 JSON 对象");

        try
        {
            var node = JsonNode.Parse(body[start..(end + 1)]);
            var action = node?["action"]?.GetValue<string>()?.Trim().ToLowerInvariant();
            if (action is not (EvolutionAction.NewSkill or EvolutionAction.ReviseSkill or EvolutionAction.NoAction))
                return (null, null, null, $"未知结论 action：{action}");
            var skillName = node["skillName"]?.GetValue<string>()?.Trim();
            if ((action == EvolutionAction.NewSkill || action == EvolutionAction.ReviseSkill) &&
                string.IsNullOrWhiteSpace(skillName))
                return (null, null, null, "new-skill/revise-skill 结论缺少 skillName");
            return (action, skillName, node["summary"]?.GetValue<string>()?.Trim(), null);
        }
        catch (JsonException ex)
        {
            return (null, null, null, $"结论块 JSON 解析失败：{ex.Message}");
        }
    }

    /// <summary>
    /// Moves a validated skill draft from labs into the inert candidates directory:
    /// enforces <c>always-listed: false</c> (prompt-budget protection), stamps the
    /// rationale with provenance, and names the destination by fingerprint so
    /// repeated candidates of the same skill never collide.
    /// </summary>
    private string? PromoteCandidate(
        string? skillName, DefectReport report, string sessionId, string? summary, DateTimeOffset now, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(skillName) || skillName.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '-' and not '_'))
        {
            error = "skillName 非法（仅允许字母数字与 - _）";
            return null;
        }

        var draft = Path.Combine(_labsRoot, skillName);
        var manifest = new[] { "skill.yaml", "skill.yml" }
            .Select(name => Path.Combine(draft, name))
            .FirstOrDefault(File.Exists);
        var prompt = Path.Combine(draft, "prompt.txt");
        if (manifest is null || !File.Exists(prompt))
        {
            error = $"labs 中未找到完整技能草稿（{draft} 需要 skill.yaml + prompt.txt）";
            return null;
        }

        // Prompt-budget protection: an always-listed skill would inject into every
        // turn, so a candidate without the key is pinned to trigger-gated.
        var manifestText = File.ReadAllText(manifest);
        if (!manifestText.Contains("always-listed", StringComparison.OrdinalIgnoreCase))
            File.AppendAllText(manifest, "\nalways-listed: false\n");

        // Provenance: every candidate carries its defect origin (design: 版本号+原因描述).
        var rationalePath = Path.Combine(draft, "rationale.md");
        var provenance = new StringBuilder();
        provenance.AppendLine();
        provenance.AppendLine("---");
        provenance.AppendLine($"- 进化指纹：{report.Fingerprint}");
        provenance.AppendLine($"- 缺陷类型：{report.Kind}（{report.Occurrences} 次）");
        provenance.AppendLine($"- 来源会话：{report.SessionId ?? "-"}；反思会话：{sessionId}");
        provenance.AppendLine($"- 生成时间：{now:yyyy-MM-dd HH:mm:ss}");
        if (!string.IsNullOrEmpty(summary))
            provenance.AppendLine($"- 反思结论：{summary}");
        try
        {
            File.AppendAllText(rationalePath, provenance.ToString());
        }
        catch (IOException) { }

        Directory.CreateDirectory(_candidatesRoot);
        var destination = Path.Combine(_candidatesRoot, $"{skillName}-{report.Fingerprint[..8]}");
        if (Directory.Exists(destination))
        {
            error = $"候选目录已存在：{destination}";
            return null;
        }
        Directory.Move(draft, destination);
        return destination;
    }
}

/// <summary>Reflection conclusion actions (parsed from the trailing JSON block).</summary>
public static class EvolutionAction
{
    public const string NewSkill = "new-skill";
    public const string ReviseSkill = "revise-skill";
    public const string NoAction = "no-action";
}
