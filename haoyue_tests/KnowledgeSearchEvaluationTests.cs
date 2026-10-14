using System.Text.Json.Nodes;
using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Data;
using Haoyue.Runtime.Events;
using Haoyue.Runtime.Providers;
using Haoyue.Runtime.Prompts;
using Haoyue.Runtime.Tools;
using Haoyue.Runtime.Tools.Builtin;
using Xunit.Abstractions;
using static Haoyue.Tests.KnowledgeEvalData;

namespace Haoyue.Tests;

/// <summary>
/// Reproducible evaluation for knowledge search hit rate. A fixed Chinese/English
/// dataset plus 24 labeled queries (baseline keywords, unspaced CJK questions,
/// synonyms, full-width input, multi-word combos, negatives, typos) is scored
/// against both the current ranker and a verbatim copy of the legacy algorithm.
/// 数据集与打分器见 <see cref="KnowledgeEvalData"/>，混合通道 counterpart 见
/// <see cref="HybridRetrievalEvaluationTests"/>——两条通道共用同一份标注才谈得上对照。
/// Run with: dotnet test --filter KnowledgeSearchEvaluation
/// </summary>
public class KnowledgeSearchEvaluationTests
{
    private readonly ITestOutputHelper _output;

    public KnowledgeSearchEvaluationTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Evaluation_CurrentRanker_BeatsLegacyBaseline()
    {
        var store = BuildStore();
        var current = KnowledgeEvalData.Score((query, limit) => store.Search("eval", query, limit));
        var legacy = KnowledgeEvalData.Score((query, limit) => LegacySearch(store, "eval", query, limit));

        _output.WriteLine($"场景数: {KnowledgeEvalData.Scenarios.Length}");
        _output.WriteLine($"当前算法: {current}");
        _output.WriteLine($"旧算法基线: {legacy}");

        Assert.True(current.Hit3 > legacy.Hit3,
            $"expected HitRate@3 {current.Hit3:P1} > legacy {legacy.Hit3:P1}");
        Assert.True(current.Mrr >= legacy.Mrr, "MRR must not regress");
        Assert.Equal(1.0, current.Hit3, 3);
    }

    [Fact]
    public void Evaluation_CurrentRanker_EveryScenario_Top3ContainsExpected()
    {
        var store = BuildStore();
        var failures = new List<string>();
        foreach (var line in KnowledgeEvalData.PerQuery("词法", (query, limit) => store.Search("eval", query, limit)))
        {
            _output.WriteLine(line);
            if (line.Contains("MISS"))
                failures.Add(line);
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    public async Task Evaluation_SearchTool_SurfacesMatchForUnspacedCjkQuestion()
    {
        var workspaceRoot = Path.Combine(Path.GetTempPath(), "haoyue-kb-eval-ws-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspaceRoot);
        var store = new KnowledgeStore(new HaoyueDatabase(
            Path.Combine(workspaceRoot, "eval.db")));
        var context = new ToolContext
        {
            Workspace = new Haoyue.Runtime.Workspaces.WorkspaceInfo
            {
                Root = workspaceRoot,
                ProjectKinds = [],
            },
            Events = new EventBus(),
            Agent = new AgentConfig(),
        };
        store.Save(HaoyueDatabase.ScopeKey(context.Workspace), "端口冲突",
            "daemon 默认端口 7860 被占用时用 --port 参数换一个", "端口,port");

        var db2 = new HaoyueDatabase(Path.Combine(Path.GetTempPath(), "haoyue-kb-eval-sem-" + Guid.NewGuid().ToString("N") + ".db"));
        var tool = new KnowledgeSearchTool(
            store,
            new KnowledgeSemanticIndex(db2, new LlmHttpFactory()),
            new ConfigStore(
                Path.Combine(Path.GetTempPath(), "haoyue-kb-eval-cfg-" + Guid.NewGuid().ToString("N")),
                Path.Combine(Path.GetTempPath(), "haoyue-kb-eval-cfg-" + Guid.NewGuid().ToString("N"))),
            new FilePromptProvider());
        var result = await tool.ExecuteAsync(
            new JsonObject { ["query"] = "端口被占用怎么办" }, context, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Contains("端口冲突", result.Output);
    }

    private KnowledgeStore BuildStore()
    {
        var dir = Path.Combine(Path.GetTempPath(), "haoyue-kb-eval-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var store = new KnowledgeStore(new HaoyueDatabase(Path.Combine(dir, "eval.db")));
        foreach (var (title, content, tags) in Dataset)
            store.Save("eval", title, content, tags);
        return store;
    }

    /// <summary>
    /// Verbatim copy of the pre-optimization KnowledgeStore.Search: space-split
    /// terms, whole-term substring Contains with title+3/content+2/tags+2, score
    /// then recency ordering. Kept here as the evaluation baseline only.
    /// </summary>
    private static IReadOnlyList<KnowledgeEntry> LegacySearch(
        KnowledgeStore store, string scope, string query, int limit)
    {
        var entries = store.List(scope, 200);
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (terms.Length == 0) return [];

        var candidates = new List<(KnowledgeEntry Entry, int Score)>();
        foreach (var entry in entries)
        {
            var score = 0;
            foreach (var term in terms)
            {
                if (entry.Title.Contains(term, StringComparison.OrdinalIgnoreCase)) score += 3;
                if (entry.Content.Contains(term, StringComparison.OrdinalIgnoreCase)) score += 2;
                if (entry.Tags?.Contains(term, StringComparison.OrdinalIgnoreCase) == true) score += 2;
            }
            if (score > 0) candidates.Add((entry, score));
        }

        return candidates
            .OrderByDescending(c => c.Score)
            .ThenByDescending(c => c.Entry.UpdatedAt, StringComparer.Ordinal)
            .Take(limit)
            .Select(c => c.Entry)
            .ToList();
    }
}
