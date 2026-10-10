using System.Text.Json.Nodes;
using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Data;
using Haoyue.Runtime.Events;
using Haoyue.Runtime.Providers;
using Haoyue.Runtime.Prompts;
using Haoyue.Runtime.Tools;
using Haoyue.Runtime.Tools.Builtin;
using Xunit.Abstractions;

namespace Haoyue.Tests;

/// <summary>
/// Reproducible evaluation for knowledge search hit rate. A fixed Chinese/English
/// dataset plus 24 labeled queries (baseline keywords, unspaced CJK questions,
/// synonyms, full-width input, multi-word combos, negatives, typos) is scored
/// against both the current ranker and a verbatim copy of the legacy algorithm.
/// Run with: dotnet test --filter KnowledgeSearchEvaluation
/// </summary>
public class KnowledgeSearchEvaluationTests
{
    private readonly ITestOutputHelper _output;

    public KnowledgeSearchEvaluationTests(ITestOutputHelper output) => _output = output;

    // Dataset index +1 == the entry id assigned by sequential Save calls.
    private static readonly (string Title, string Content, string Tags)[] Dataset =
    [
        ("构建命令", "运行 python build.py --platform windows 打包，输出到 publish 目录", "build,打包"),
        ("数据库连接", "PostgreSQL 连接字符串在 appsettings.json 的 ConnectionStrings 节点配置，默认端口 5432", "数据库,配置"),
        ("部署流程", "使用 docker compose up -d 发布到服务器，默认端口 8080", "部署,docker,发布"),
        ("登录问题", "登录失败先检查账号密码，验证码 5 分钟内有效", "登录,账号"),
        ("版本管理", "version.py 统一管理版本号，改一处同步四个文件", "版本,version"),
        ("测试命令", "dotnet test 运行全部单元测试，pnpm test 运行前端测试", "测试,test"),
        ("包管理约定", "前端统一使用 pnpm，禁止 npm install，锁文件是 pnpm-lock.yaml", "pnpm,前端"),
        ("端口冲突", "daemon 默认端口 7860 被占用时用 --port 参数换一个", "端口,port"),
        ("日志位置", "运行日志在 %APPDATA%/haoyue/logs 目录下，按天滚动", "日志,log"),
        ("快捷键", "Ctrl+K 打开命令面板，Ctrl+N 新建会话，Esc 关闭弹窗", "快捷键,shortcut"),
        ("API 密钥", "模型 API key 配置在 settings 中，也支持环境变量 HAOYUE_API_KEY", "api,密钥,key"),
        ("中文编码", "Windows 控制台 GBK 乱码时执行 chcp 65001 切换 UTF-8", "编码,乱码"),
        ("全角符号", "代码里出现全角标点会编译失败，注意输入法状态", "全角,标点"),
        ("错别字对照", "「登陆」应为「登录」，「帐号」应为「账号」", "错别字"),
        ("性能优化", "大文件读取用流式处理，避免一次性载入内存", "性能,优化"),
        ("卸载步骤", "控制面板卸载后，手动删除 %APPDATA% 下的残留目录", "卸载,清理"),
    ];

    private static readonly (string Group, string Query, string[] ExpectedTitles)[] Scenarios =
    [
        // A: baseline single keywords — both algorithms must pass.
        ("A-基线", "打包", ["构建命令"]),
        ("A-基线", "python", ["构建命令"]),
        ("A-基线", "数据库", ["数据库连接"]),
        ("A-基线", "docker", ["部署流程"]),
        ("A-基线", "pnpm", ["包管理约定"]),
        // B: unspaced CJK questions — the legacy algorithm's main blind spot.
        ("B-CJK长查询", "连接字符串怎么配置", ["数据库连接"]),
        ("B-CJK长查询", "端口被占用怎么办", ["端口冲突"]),
        ("B-CJK长查询", "怎么打包发布", ["部署流程", "构建命令"]),
        ("B-CJK长查询", "日志文件在哪里", ["日志位置"]),
        ("B-CJK长查询", "大文件 内存", ["性能优化"]),
        // C: synonyms — "编译" finds "构建/打包" entries, typos like "登陆" find "登录".
        ("C-同义词", "编译", ["构建命令"]),
        ("C-同义词", "部署", ["部署流程"]),
        ("C-同义词", "上线流程", ["部署流程"]),
        ("C-同义词", "登入失败", ["登录问题"]),
        ("C-同义词", "帐号密码错误", ["登录问题", "错别字对照"]),
        ("C-同义词", "设置数据库", ["数据库连接"]),
        // D: full-width input and casing.
        ("D-全角大小写", "ＰＹＴＨＯＮ", ["构建命令"]),
        ("D-全角大小写", "DOTNET TEST", ["测试命令"]),
        ("D-全角大小写", "ｐｎｐｍ", ["包管理约定"]),
        // E: multi-token queries exercising coverage ranking.
        ("E-多词组合", "api key 环境变量", ["API 密钥"]),
        ("E-多词组合", "快捷键 命令面板", ["快捷键"]),
        // F: negatives — both algorithms must return nothing.
        ("F-负例", "不存在的词条", []),
        ("F-负例", "完全无关的查询词组", []),
        // G: typos handled by the edit-distance fallback on title/tags.
        ("G-错别字", "pinpm", ["包管理约定"]),
    ];

    [Fact]
    public void Evaluation_CurrentRanker_BeatsLegacyBaseline()
    {
        var store = BuildStore();
        var current = Evaluate((scope, query, limit) => store.Search(scope, query, limit));
        var legacy = Evaluate((scope, query, limit) => LegacySearch(store, scope, query, limit));

        _output.WriteLine($"场景数: {Scenarios.Length}");
        _output.WriteLine($"当前算法: HitRate@1={current.Hit1:P1}  HitRate@3={current.Hit3:P1}  MRR={current.Mrr:P1}");
        _output.WriteLine($"旧算法基线: HitRate@1={legacy.Hit1:P1}  HitRate@3={legacy.Hit3:P1}  MRR={legacy.Mrr:P1}");

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
        foreach (var (group, query, expected) in Scenarios)
        {
            var titles = store.Search("eval", query, 3).Select(e => e.Title).ToList();
            var hit = expected.Length == 0
                ? titles.Count == 0
                : expected.Any(titles.Contains);
            _output.WriteLine($"[{group}] \"{query}\" → [{string.Join(", ", titles)}] {(hit ? "✓" : "✗ MISS")}");
            if (!hit)
                failures.Add($"[{group}] \"{query}\" 期望 {string.Join("/", expected)}，实际 [{string.Join(", ", titles)}]");
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

    private static (double Hit1, double Hit3, double Mrr) Evaluate(
        Func<string, string, int, IReadOnlyList<KnowledgeEntry>> search)
    {
        var hit1 = 0;
        var hit3 = 0;
        double reciprocalSum = 0;
        foreach (var (_, query, expected) in Scenarios)
        {
            var titles = search("eval", query, 10).Select(e => e.Title).ToList();
            if (expected.Length == 0)
            {
                // Negative queries: returning nothing counts as a hit.
                if (titles.Count == 0)
                {
                    hit1++;
                    hit3++;
                    reciprocalSum += 1;
                }
                continue;
            }

            var first = titles.FindIndex(expected.Contains) + 1;
            if (first >= 1)
            {
                if (first == 1) hit1++;
                if (first <= 3) hit3++;
                reciprocalSum += 1.0 / first;
            }
        }
        var total = Scenarios.Length;
        return (hit1 / (double)total, hit3 / (double)total, reciprocalSum / total);
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
