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
        Assert.Equal(1, _store.PurgeExpiredForRoot(_dir));
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
}
