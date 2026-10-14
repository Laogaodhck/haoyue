using System.Text.Json.Nodes;
using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Data;
using Haoyue.Runtime.Events;
using Haoyue.Runtime.Prompts;
using Haoyue.Runtime.Tools;
using Haoyue.Runtime.Tools.Builtin;
using Haoyue.Runtime.Workspaces;

namespace Haoyue.Tests;

/// <summary>记忆分层·项目事实层（FactsStore + FactSaveTool）单元测试。</summary>
public sealed class FactsStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "haoyue-tests", "facts-" + Guid.NewGuid().ToString("N"));
    private readonly FactsStore _store = new();

    public FactsStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Add_ListActive_RoundTripsWithConfidenceAndTtl()
    {
        _store.Add(_dir, "构建命令是 dotnet build", "build", source: "user", confidence: 1.0);
        _store.Add(_dir, "当前正在发布 v2", "release", confidence: 0.6, ttlDays: 1);

        var active = _store.ListActiveForRoot(_dir);
        Assert.Equal(2, active.Count);
        Assert.Contains(active, f => f.Content == "构建命令是 dotnet build" && f.Source == "user" && f.ExpiresAt is null);
        Assert.Contains(active, f => f.ExpiresAt is not null && f.Confidence == 0.6);
        Assert.True(File.Exists(Path.Combine(_dir, ".haoyue", "facts.json")));
    }

    [Fact]
    public void Add_IsIdempotentOnSameContent_AndUpgradesConfidence()
    {
        _store.Add(_dir, "部署走 CI", confidence: 0.6);
        var again = _store.Add(_dir, "部署走 CI", confidence: 0.9);

        Assert.Single(_store.ListActiveForRoot(_dir));
        Assert.Equal(0.9, again.Confidence);
    }

    [Fact]
    public void ExpiredFacts_AreHidden_AndPurged()
    {
        var entry = _store.Add(_dir, "临时事实", ttlDays: 1);
        // 直接改文件把过期时间拨回过去，避免测试等待。
        var file = FactsStore.FactsFileForRoot(_dir);
        var raw = File.ReadAllText(file).Replace(
            DateTimeOffset.UtcNow.AddDays(1).ToString("yyyy-MM-dd"), DateTimeOffset.UtcNow.AddDays(-1).ToString("yyyy-MM-dd"));
        File.WriteAllText(file, raw);

        Assert.DoesNotContain(_store.ListActiveForRoot(_dir), f => f.Id == entry.Id);
        Assert.Equal(1, _store.CompactForRoot(_dir));
        Assert.Empty(_store.ListActiveForRoot(_dir));
    }

    [Fact]
    public void CapacityLimit_EvictsLowConfidenceFirst()
    {
        for (var i = 0; i < FactsStore.CapacityPerWorkspace; i++)
            _store.Add(_dir, $"事实 {i}", confidence: 0.9);
        _store.Add(_dir, "低置信可淘汰", confidence: 0.1);

        var active = _store.ListActiveForRoot(_dir);
        Assert.Equal(FactsStore.CapacityPerWorkspace, active.Count);
        Assert.DoesNotContain(active, f => f.Content == "低置信可淘汰");
    }

    [Fact]
    public void Search_RanksByTermOverlap_AndIgnoresExpired()
    {
        _store.Add(_dir, "构建命令是 dotnet build", "build", confidence: 1.0);
        _store.Add(_dir, "部署使用 docker compose", "deploy", confidence: 1.0);

        var hits = _store.SearchForRoot(_dir, "build 构建命令");
        Assert.NotEmpty(hits);
        Assert.Equal("构建命令是 dotnet build", hits[0].Content);
    }

    [Fact]
    public void Forget_RemovesByIdOrContent()
    {
        var entry = _store.Add(_dir, "要被遗忘的事实");
        Assert.True(_store.ForgetForRoot(_dir, entry.Id));
        Assert.Empty(_store.ListActiveForRoot(_dir));
        Assert.False(_store.ForgetForRoot(_dir, entry.Id));
    }

    // ---------------------------------------------------------------- lifecycle

    [Fact]
    public void SupersededFacts_AreTombstonesUntilRetentionEnds_ThenDeleted()
    {
        var old = _store.Add(_dir, "部署走 Jenkins", "deploy", confidence: 1.0);
        _store.Add(_dir, "部署走 GitHub Actions", "deploy", confidence: 1.0, supersedes: old.Id);

        // 保留期内：不注入，但文件里仍有审计痕迹。
        Assert.Contains("Jenkins", File.ReadAllText(FactsStore.FactsFileForRoot(_dir)));
        Assert.DoesNotContain(_store.ListActiveForRoot(_dir), f => f.Content == "部署走 Jenkins");

        // 把墓碑时间拨到保留期之外：必须真的删掉，否则墓碑会一路堆积在文件里。
        SetField(old.Id, "supersededAt", DaysAgo(FactsStore.TombstoneRetentionDays + 5));
        Assert.Equal(1, _store.CompactForRoot(_dir));
        Assert.DoesNotContain("Jenkins", File.ReadAllText(FactsStore.FactsFileForRoot(_dir)));
    }

    [Fact]
    public void Capacity_CountsOnlyActiveFacts_SoTombstonesCannotEvictThem()
    {
        // 129 条高置信墓碑 + 1 条低置信活跃事实：旧口径按整体置信度取前 100，
        // 会把唯一活跃的事实当成"最不值得留"的那条删掉。
        var file = FactsStore.FactsFileForRoot(_dir);
        var rows = new JsonArray();
        for (var i = 0; i < 129; i++)
        {
            rows.Add(Node($"f{i}", $"历史部署方式 {i}", 1.0, supersededBy: "live"));
        }
        rows.Add(Node("live", "现行部署方式", 0.5));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, rows.ToJsonString());

        _store.CompactForRoot(_dir);

        var active = _store.ListActiveForRoot(_dir);
        Assert.Single(active);
        Assert.Equal("现行部署方式", active[0].Content);
    }

    [Fact]
    public void UnusedFacts_DecayInConfidence_AndUserFactsAreExempt()
    {
        var agentFact = _store.Add(_dir, "推断出来的构建命令", confidence: 0.8);
        var userFact = _store.Add(_dir, "用户明确要求的命名规范", source: "user", confidence: 1.0);
        var stale = DaysAgo(FactsStore.UnusedDecayDays + 1);
        SetField(agentFact.Id, "createdAt", stale);
        SetField(userFact.Id, "createdAt", stale);

        _store.CompactForRoot(_dir);

        var after = _store.ListActiveForRoot(_dir);
        Assert.True(after.Single(f => f.Id == agentFact.Id).Confidence < 0.8);
        Assert.Equal(1.0, after.Single(f => f.Id == userFact.Id).Confidence);
    }

    [Fact]
    public void Demote_LowersConfidenceAndCountsDenial_WithoutDeleting()
    {
        var entry = _store.Add(_dir, "错误的假设", confidence: 0.9);

        var demoted = _store.Demote(_dir, entry.Id);

        Assert.NotNull(demoted);
        Assert.Equal(0.5, demoted!.Confidence);
        Assert.Equal(1, demoted.DenialCount);
        Assert.Contains(_store.ListActiveForRoot(_dir), f => f.Id == entry.Id);
        Assert.Null(_store.Demote(_dir, "不存在的事实原文"));
    }

    [Fact]
    public void SelectForInjection_TakesRelevantTopK_AndKeepsUserFactsPinned()
    {
        _store.Add(_dir, "构建命令是 dotnet build", "build", confidence: 0.9);
        _store.Add(_dir, "无关的厨房偏好", "life", confidence: 0.9);
        _store.Add(_dir, "回复统一用中文", "language", source: "user", confidence: 1.0);

        var selection = _store.SelectForInjection(_dir, "dotnet build 构建命令", topK: 2);

        Assert.Contains(selection.Facts, f => f.Content == "构建命令是 dotnet build");
        Assert.Contains(selection.Facts, f => f.Content == "回复统一用中文"); // pinned 用户指令
        Assert.DoesNotContain(selection.Facts, f => f.Content == "无关的厨房偏好");
        Assert.Equal(3, selection.ActiveTotal);
        // 被注入过 = 有使用记录，衰减判定才有依据。
        Assert.All(selection.Facts, f => Assert.True(f.UseCount >= 1 && f.LastUsedAt is not null));
    }

    [Fact]
    public void SelectForInjection_WithoutQuery_FallsBackToConfidenceOrder()
    {
        _store.Add(_dir, "低置信推断", confidence: 0.4);
        _store.Add(_dir, "高置信事实", confidence: 1.0);

        var selection = _store.SelectForInjection(_dir, null, topK: 1);

        Assert.Single(selection.Facts);
        Assert.Equal("高置信事实", selection.Facts[0].Content);
    }

    [Fact]
    public void Search_RecordsUsageSoHitCountsFeedDecay()
    {
        var entry = _store.Add(_dir, "缓存失效策略是 write-through", "cache", confidence: 0.9);

        _store.SearchForRoot(_dir, "write-through 缓存");
        _store.SearchForRoot(_dir, "write-through 缓存");

        var after = _store.ListActiveForRoot(_dir).Single(f => f.Id == entry.Id);
        Assert.Equal(2, after.UseCount);
        Assert.NotNull(after.LastUsedAt);
    }

    [Fact]
    public void Search_KeepsTombstonesAndExpiredRowsInTheFile()
    {
        // 检索命中会回写使用计数，但绝不能顺手把非活跃行当垃圾丢掉。
        var old = _store.Add(_dir, "被取代的旧说法", "deploy", confidence: 1.0);
        _store.Add(_dir, "被取代的旧说法 已纠正", "deploy", confidence: 1.0, supersedes: old.Id);

        _store.SearchForRoot(_dir, "deploy");

        // 回写使用计数时落盘的必须是全量列表：只存活跃集会顺手删掉墓碑与过期行。
        var rows = JsonNode.Parse(File.ReadAllText(FactsStore.FactsFileForRoot(_dir))!)!.AsArray();
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, f => (string?)f!["supersededBy"] == null);
        Assert.Contains(rows, f => (string?)f!["supersededBy"] is not null);
    }

    /// <summary>改写 facts.json 里某条事实的字段，用来伪造「很久没用」或「墓碑已过期」。</summary>
    private void SetField(string id, string field, string? value)
    {
        var file = FactsStore.FactsFileForRoot(_dir);
        var rows = JsonNode.Parse(File.ReadAllText(file)!)!.AsArray();
        rows.Single(f => f!["id"]!.GetValue<string>() == id)[field] = value is null ? null : JsonValue.Create(value);
        File.WriteAllText(file, rows.ToJsonString());
    }

    private static string DaysAgo(int days) => DateTimeOffset.UtcNow.AddDays(-days).ToString("yyyy-MM-ddTHH:mm:sszzz");

    private static JsonObject Node(string id, string content, double confidence, string? supersededBy = null)
    {
        var node = new JsonObject
        {
            ["id"] = id,
            ["content"] = content,
            ["confidence"] = confidence,
            ["createdAt"] = DaysAgo(1),
        };
        if (supersededBy is not null)
        {
            node["supersededBy"] = supersededBy;
            node["supersededAt"] = DaysAgo(1);
        }
        return node;
    }

    // ---------------------------------------------------------------- tool

    [Fact]
    public async Task FactSaveTool_PersistsFact_AndReportsExpiry()
    {
        var tool = new FactSaveTool(new FilePromptProvider());
        var workspace = new WorkspaceManager().CreateGlobal(_dir);
        var context = new ToolContext
        {
            Workspace = workspace,
            Events = new EventBus(),
            Agent = new AgentConfig(),
        };

        var result = await tool.ExecuteAsync(
            new JsonObject { ["content"] = "测试命令是 pnpm test", ["topic"] = "test", ["confidence"] = 1.0 },
            context, CancellationToken.None);

        Assert.True(result.Success, result.Output);
        Assert.Contains("长期有效", result.Output);
        var facts = new FactsStore().ListActiveForRoot(_dir);
        Assert.Single(facts);
        Assert.Equal("test", facts[0].Topic);
    }

    [Fact]
    public async Task FactSaveTool_RequiresContent()
    {
        var tool = new FactSaveTool(new FilePromptProvider());
        var context = new ToolContext
        {
            Workspace = new WorkspaceManager().CreateGlobal(_dir),
            Events = new EventBus(),
            Agent = new AgentConfig(),
        };
        var result = await tool.ExecuteAsync(new JsonObject(), context, CancellationToken.None);
        Assert.False(result.Success);
    }

    [Fact]
    public async Task FactSearchTool_ZeroHits_PublishesMissEvent_AndSaysWhetherLayerIsEmpty()
    {
        // 空命中必须可被进化引擎看见，并同时区分「库是空的」与「有内容但检索不到」。
        var emptyEvents = new EventBus();
        var emptyContext = new ToolContext
        {
            Workspace = new WorkspaceManager().CreateGlobal(_dir),
            Events = emptyEvents,
            Agent = new AgentConfig(),
        };
        using (var subscription = emptyEvents.Subscribe())
        {
            var empty = await new FactSearchTool(new FilePromptProvider()).ExecuteAsync(
                new JsonObject { ["query"] = "灰度比例" }, emptyContext, CancellationToken.None);
            Assert.True(empty.Success);
            Assert.Contains("事实层还是空的", empty.Output);
            var miss = Assert.IsType<RetrievalMissEvent>(await subscription.Reader.ReadAsync());
            Assert.Equal(0, miss.CorpusSize);
            Assert.Equal("fact_search", miss.ToolName);
        }

        _store.Add(_dir, "构建命令是 dotnet build", "build", confidence: 1.0);
        var stockedEvents = new EventBus();
        var stockedContext = new ToolContext
        {
            Workspace = new WorkspaceManager().CreateGlobal(_dir),
            Events = stockedEvents,
            Agent = new AgentConfig(),
        };
        using (var subscription = stockedEvents.Subscribe())
        {
            var missResult = await new FactSearchTool(new FilePromptProvider()).ExecuteAsync(
                new JsonObject { ["query"] = "灰度比例" }, stockedContext, CancellationToken.None);
            Assert.Contains("1 条活跃事实", missResult.Output);
            var miss = Assert.IsType<RetrievalMissEvent>(await subscription.Reader.ReadAsync());
            Assert.Equal(1, miss.CorpusSize);
            Assert.Contains("零命中", miss.Reason);
        }
    }
}
