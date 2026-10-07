using Haoyue.Runtime;
using Haoyue.Runtime.Agents;
using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Coordination;
using Haoyue.Runtime.Data;
using Haoyue.Runtime.Events;
using Haoyue.Runtime.Evolution;
using Haoyue.Runtime.Providers;
using Microsoft.Data.Sqlite;

namespace Haoyue.Tests;

public sealed class EvolutionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "haoyue-evolution-tests", Guid.NewGuid().ToString("N"));
    private readonly string _labs;
    private readonly string _candidates;
    private readonly HaoyueRuntime _runtime;

    public EvolutionTests()
    {
        Directory.CreateDirectory(_dir);
        _labs = Path.Combine(_dir, "labs");
        _candidates = Path.Combine(_dir, "candidates");
        _runtime = HaoyueRuntime.Create(
            _dir,
            new ConfigStore(Path.Combine(_dir, "config.json"), Path.Combine(_dir, "state.json")),
            Path.Combine(_dir, "runtime.db"));
    }

    private EvolutionStore NewStore() => new(_runtime.Database);

    private void Append(RuntimeEvent evt, DateTimeOffset? timestamp = null) =>
        _runtime.Database.AppendEvent(
            timestamp ?? evt.Timestamp,
            evt.GetType().Name,
            EventJournal.Serialize(evt));

    /// <summary>Seeds a tool-failure cluster (session s1) into the journal.</summary>
    private void SeedBashFailureCluster(string sessionId = "s1", string error = "error: command failed")
    {
        var start = DateTimeOffset.UtcNow.AddMinutes(-10);
        Append(new TurnStartedEvent(sessionId, "运行任务"), start);
        for (var i = 0; i < 3; i++)
            Append(new ToolCallCompletedEvent($"c{i}", "bash", false, error, TimeSpan.Zero),
                start.AddMinutes(i));
        Append(new TurnCompletedEvent(sessionId, false, null), start.AddMinutes(5));
    }

    private ReflectionRunner NewRunner(EvolutionStore store, ReflectionTurnRunner? runner = null, string? skillsRoot = null) => new(
        _runtime, new FileLockCoordinator(), new LlmHttpFactory(),
        new CircuitBreaker(new RetryConfig()), store, runner,
        labsRoot: _labs, candidatesRoot: _candidates, skillsRoot: skillsRoot);

    // ---------------------------------------------------------------- store

    [Fact]
    public void Store_RecordGetDecide_RoundTrips()
    {
        var store = NewStore();
        var now = DateTimeOffset.UtcNow;
        store.Record(new EvolutionRecord("fp1", "ToolFailureCluster", EvolutionStatus.Candidate,
            "dir", "s9", "结论", now, now));

        var record = store.Get("fp1")!;
        Assert.Equal(EvolutionStatus.Candidate, record.Status);
        Assert.Equal("s9", record.SessionId);
        Assert.Equal("dir", record.CandidateDir);
        Assert.Null(store.Get("missing"));

        store.Decide("fp1", EvolutionStatus.Adopted);
        Assert.Equal(EvolutionStatus.Adopted, store.Get("fp1")!.Status);
        Assert.Equal("ToolFailureCluster", store.Get("fp1")!.Kind); // kind untouched by decision
        Assert.Null(store.Decide("missing", EvolutionStatus.Rejected));

        Assert.Single(store.List(EvolutionStatus.Adopted));
        Assert.Equal(["s9"], store.ReflectionSessionIds());
    }

    [Fact]
    public void Store_FailedStatus_DoesNotBlockRetry_OthersDo()
    {
        Assert.False(EvolutionStatus.BlocksRetry(EvolutionStatus.Failed));
        Assert.True(EvolutionStatus.BlocksRetry(EvolutionStatus.Candidate));
        Assert.True(EvolutionStatus.BlocksRetry(EvolutionStatus.NoAction));
        Assert.True(EvolutionStatus.BlocksRetry(EvolutionStatus.Rejected));
    }

    // ---------------------------------------------------------------- parsing

    [Fact]
    public void ParseConclusion_AcceptsTheThreeActions_AndRejectsMalformed()
    {
        var (action, name, _, error) = ReflectionRunner.ParseConclusion(
            "分析正文……\n```json\n{\"action\":\"no-action\",\"summary\":\"环境问题\"}\n```");
        Assert.Null(error);
        Assert.Equal(EvolutionAction.NoAction, action);

        (action, name, _, error) = ReflectionRunner.ParseConclusion(
            "前言\n```json\n{\"action\":\"new-skill\",\"skillName\":\"fix-bash\",\"summary\":\"ok\"}\n```\n尾注");
        Assert.Null(error);
        Assert.Equal(EvolutionAction.NewSkill, action);
        Assert.Equal("fix-bash", name);

        (_, _, _, error) = ReflectionRunner.ParseConclusion("没有结论块");
        Assert.NotNull(error);

        (_, _, _, error) = ReflectionRunner.ParseConclusion("```json\n{\"action\":\"new-skill\"}\n```");
        Assert.NotNull(error); // missing skillName

        (_, _, _, error) = ReflectionRunner.ParseConclusion("```json\n{\"action\":\"destroy-all\"}\n```");
        Assert.NotNull(error); // unknown action

        (_, _, _, error) = ReflectionRunner.ParseConclusion("```json\n{broken\n```");
        Assert.NotNull(error);
    }

    [Fact]
    public void BuildPrompt_ContainsReportsLabsPathAndContract()
    {
        var runner = NewRunner(NewStore());
        var report = new DefectReport("abcd1234abcd1234", DefectKind.ToolFailureCluster,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, 3, "bash", "error: boom", "s1");
        var prompt = runner.BuildPrompt([report]);

        Assert.Contains("abcd1234abcd1234", prompt);
        Assert.Contains("bash", prompt);
        Assert.Contains(_labs.Replace("\\", "/"), prompt);
        Assert.Contains("always-listed: false", prompt);
        Assert.Contains("\"action\":\"new-skill\"", prompt);
    }

    // ---------------------------------------------------------------- reflection flow

    [Fact]
    public async Task RunAsync_NewSkill_PromotesDraftToCandidates_AndRecords()
    {
        SeedBashFailureCluster();
        var store = NewStore();
        var turns = 0;
        var runner = NewRunner(store, (workspace, session, prompt, ct) =>
        {
            turns++;
            var draft = Path.Combine(_labs, "fix-bash-retry");
            Directory.CreateDirectory(draft);
            File.WriteAllText(Path.Combine(draft, "skill.yaml"),
                "name: fix-bash-retry\ndescription: \"避免 bash 重试死循环\"\ntriggers:\n  - \"bash 失败\"\nallowed-tools:\n  - read\n  - bash\n");
            File.WriteAllText(Path.Combine(draft, "prompt.txt"), "遇到命令失败时先检查退出码……");
            File.WriteAllText(Path.Combine(draft, "rationale.md"), "缺陷：bash 连续失败。");
            return Task.FromResult(new AgentTurnResult(
                "已分析缺陷。\n```json\n{\"action\":\"new-skill\",\"skillName\":\"fix-bash-retry\",\"summary\":\"新增重试技能\"}\n```",
                false, null));
        });

        var result = await runner.RunAsync(CancellationToken.None);

        Assert.Null(result.Error);
        Assert.Equal(1, turns);
        Assert.Equal(1, result.Candidates);
        Assert.Equal(0, result.Skipped);

        var candidateDir = Path.Combine(_candidates, "fix-bash-retry-");
        var actual = Directory.GetDirectories(_candidates).Single();
        Assert.StartsWith(candidateDir, actual);
        var manifest = File.ReadAllText(Path.Combine(actual, "skill.yaml"));
        Assert.Contains("always-listed: false", manifest); // budget protection enforced
        var rationale = File.ReadAllText(Path.Combine(actual, "rationale.md"));
        Assert.Contains("进化指纹", rationale);
        Assert.Contains("反思会话", rationale);

        var record = store.Get(store.List(EvolutionStatus.Candidate)[0].Fingerprint)!;
        Assert.Equal(EvolutionStatus.Candidate, record.Status);
        Assert.NotNull(record.SessionId);
    }

    [Fact]
    public async Task RunAsync_SecondPass_SkipsRecordedFingerprints()
    {
        SeedBashFailureCluster();
        var store = NewStore();
        var turns = 0;
        var runner = NewRunner(store, (workspace, session, prompt, ct) =>
        {
            turns++;
            return Task.FromResult(new AgentTurnResult(
                "```json\n{\"action\":\"no-action\",\"summary\":\"环境问题\"}\n```", false, null));
        });

        var first = await runner.RunAsync(CancellationToken.None);
        Assert.Equal(1, first.NoAction);
        var second = await runner.RunAsync(CancellationToken.None);

        Assert.Equal(1, turns); // turn not re-run
        Assert.Equal(0, second.Processed);
        Assert.Equal(1, second.Skipped);
    }

    [Fact]
    public async Task RunAsync_FailedReflection_IsRetriableOnNextPass()
    {
        SeedBashFailureCluster();
        var store = NewStore();
        var turns = 0;
        ReflectionTurnRunner runnerDelegate = (workspace, session, prompt, ct) =>
        {
            turns++;
            var text = turns == 1
                ? "输出缺少结论块"
                : "```json\n{\"action\":\"no-action\",\"summary\":\"一次性失误\"}\n```";
            return Task.FromResult(new AgentTurnResult(text, false, null));
        };
        var runner = NewRunner(store, runnerDelegate);

        var first = await runner.RunAsync(CancellationToken.None);
        Assert.NotNull(first.Error);
        Assert.Equal(EvolutionStatus.Failed, store.List(EvolutionStatus.Failed).Single().Status);

        var second = await runner.RunAsync(CancellationToken.None);
        Assert.Equal(2, turns);
        Assert.Equal(1, second.NoAction); // failed rows do not block the retry
        Assert.Equal(0, second.Skipped);
    }

    [Fact]
    public async Task RunAsync_WithoutPendingDefects_DoesNotRunTurn()
    {
        var turns = 0;
        var runner = NewRunner(NewStore(), (workspace, session, prompt, ct) =>
        {
            turns++;
            return Task.FromResult(new AgentTurnResult("", false, null));
        });

        var result = await runner.RunAsync(CancellationToken.None);

        Assert.Equal(0, turns);
        Assert.Equal(0, result.Processed);
    }

    // ---------------------------------------------------------------- anti-self-feeding

    [Fact]
    public void Aggregate_ExcludesReflectionSessionsFromTheDecisionLog()
    {
        SeedBashFailureCluster("s1");
        SeedBashFailureCluster("s2", "error: different failure");

        // The decision log records a reflection turn whose session is s2.
        var store = NewStore();
        store.Record(new EvolutionRecord("fp", "ToolFailureCluster", EvolutionStatus.Failed,
            null, "s2", "反思失败", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

        var reports = new DefectAggregator(_runtime.Database)
            .Aggregate(DefectAggregator.DefaultScanLimit, store.ReflectionSessionIds());

        var cluster = Assert.Single(reports);
        Assert.Equal("s1", cluster.SessionId);
        Assert.Contains("command failed", cluster.ErrorSummary);
    }

    [Fact]
    public void ReflectionEvent_IsJournaled()
    {
        Assert.True(EventJournal.IsPersistent(new EvolutionReflectionCompletedEvent("s1", 1, 1, 0, 0)));
    }

    // ---------------------------------------------------------------- approval (P3)

    private async Task<string> SeedCandidateAsync(string skillName = "fix-bash-retry")
    {
        SeedBashFailureCluster();
        var store = NewStore();
        var runner = NewRunner(store, (workspace, session, prompt, ct) =>
        {
            var draft = Path.Combine(_labs, skillName);
            Directory.CreateDirectory(draft);
            File.WriteAllText(Path.Combine(draft, "skill.yaml"),
                $"name: {skillName}\ndescription: \"x\"\ntriggers:\n  - \"bash 失败\"\nalways-listed: false\n");
            File.WriteAllText(Path.Combine(draft, "prompt.txt"), "内容");
            File.WriteAllText(Path.Combine(draft, "rationale.md"), "缺陷来源。");
            return Task.FromResult(new AgentTurnResult(
                $"```json\n{{\"action\":\"new-skill\",\"skillName\":\"{skillName}\",\"summary\":\"新增技能\"}}\n```",
                false, null));
        });
        await runner.RunAsync(CancellationToken.None);
        return store.List(EvolutionStatus.Candidate).Single().Fingerprint;
    }

    [Fact]
    public async Task Decide_Reject_DeletesDraft_AndClosesFingerprint()
    {
        var fingerprint = await SeedCandidateAsync();
        var runner = NewRunner(NewStore());
        var dir = runner.ListCandidates().Single().CandidateDir!;
        Assert.True(Directory.Exists(dir));

        var record = runner.Decide(fingerprint, adopt: false);

        Assert.Equal(EvolutionStatus.Rejected, record.Status);
        Assert.False(Directory.Exists(dir));
        Assert.Empty(runner.ListCandidates());
        Assert.Throws<InvalidOperationException>(() => runner.Decide(fingerprint, adopt: true));
    }

    [Fact]
    public async Task Decide_Adopt_MovesDraftIntoSkillsRoot()
    {
        var fingerprint = await SeedCandidateAsync("my-evolved-skill");
        var skillsRoot = Path.Combine(_dir, "skills-root");
        var runner = NewRunner(NewStore(), skillsRoot: skillsRoot);

        var record = runner.Decide(fingerprint, adopt: true);

        Assert.Equal(EvolutionStatus.Adopted, record.Status);
        Assert.True(Directory.Exists(Path.Combine(skillsRoot, "my-evolved-skill")));
        Assert.True(File.Exists(Path.Combine(skillsRoot, "my-evolved-skill", "skill.yaml")));
        Assert.Empty(runner.ListCandidates());
    }

    [Fact]
    public async Task Decide_Adopt_IntoExistingSkillDirectory_FailsWithoutDataLoss()
    {
        var fingerprint = await SeedCandidateAsync("existing-skill");
        var skillsRoot = Path.Combine(_dir, "skills-root");
        Directory.CreateDirectory(Path.Combine(skillsRoot, "existing-skill"));
        File.WriteAllText(Path.Combine(skillsRoot, "existing-skill", "prompt.txt"), "原有技能");
        var runner = NewRunner(NewStore(), skillsRoot: skillsRoot);
        var candidateDir = runner.ListCandidates().Single().CandidateDir!;

        Assert.Throws<InvalidOperationException>(() => runner.Decide(fingerprint, adopt: true));

        // Candidate draft must survive a failed adoption.
        Assert.True(Directory.Exists(candidateDir));
        Assert.Equal(EvolutionStatus.Candidate, NewStore().Get(fingerprint)!.Status);
    }

    // ---------------------------------------------------------------- feedback (P4)

    [Fact]
    public void Aggregate_NegativeFeedback_BecomesItsOwnDefectReport()
    {
        var start = DateTimeOffset.UtcNow.AddMinutes(-5);
        Append(new UserFeedbackEvent("s1", "negative", "方向错了"), start);
        Append(new UserFeedbackEvent("s1", "negative", "又错了"), start.AddMinutes(1));
        Append(new UserFeedbackEvent("s1", "positive"), start.AddMinutes(2)); // not a defect

        var reports = new DefectAggregator(_runtime.Database).Aggregate();

        Assert.Equal(2, reports.Count);
        Assert.All(reports, r =>
        {
            Assert.Equal(DefectKind.UserNegativeFeedback, r.Kind);
            Assert.Equal("s1", r.SessionId);
            Assert.Equal(1, r.Occurrences);
        });
        Assert.NotEqual(reports[0].Fingerprint, reports[1].Fingerprint); // distinct per event
        Assert.Contains("方向错了", reports[0].ErrorSummary);
        Assert.DoesNotContain("positive", string.Join("|", reports.Select(r => r.ErrorSummary)));
    }

    [Fact]
    public async Task NegativeFeedback_Defect_IsReflectedOnAndClosedByNoAction()
    {
        Append(new UserFeedbackEvent("s1", "negative", "答非所问"), DateTimeOffset.UtcNow.AddMinutes(-5));
        var store = NewStore();
        var runner = NewRunner(store, (workspace, session, prompt, ct) =>
        {
            Assert.Contains("答非所问", prompt); // feedback reason reaches the reflection prompt
            return Task.FromResult(new AgentTurnResult(
                "```json\n{\"action\":\"no-action\",\"summary\":\"一次性失误\"}\n```", false, null));
        });

        var result = await runner.RunAsync(CancellationToken.None);

        Assert.Equal(1, result.NoAction);
        Assert.Empty(await Task.FromResult(runner.ListCandidates()));
        // The record's session id is the reflection turn itself (for aggregator
        // exclusion); the defect's origin session (s1) only appears in the prompt.
        var record = store.List(EvolutionStatus.NoAction).Single();
        Assert.Equal(result.SessionId, record.SessionId);
    }

    [Fact]
    public void FeedbackEvent_IsJournaled()
    {
        Assert.True(EventJournal.IsPersistent(new UserFeedbackEvent("s1", "negative")));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { }
    }
}
