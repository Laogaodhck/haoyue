using System.Text.RegularExpressions;
using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Data;
using Haoyue.Runtime.Providers;
using Xunit.Abstractions;

namespace Haoyue.Tests;

/// <summary>
/// 混合检索通道（词法 0.6 + 语义 0.4，带绝对相似度门槛）的质量评测。
///
/// 此前的语义测试用「查询串 → 预置向量」的查表替身，只证明管道接通，
/// 从未度量过召回：换任何排序逻辑都能通过。本评测改用 <see cref="NgramEmbeddingClient"/>
/// ——向量由文本自身计算（词 + 汉字二元组、IDF 加权、L2 归一），
/// 与 <see cref="KnowledgeSearchEvaluationTests"/> 共用同一份 24 题标注，
/// 于是正例召回（HitRate@1/@3、MRR）与负例拒绝（必须返回空）第一次有了可回归的数字。
///
/// 诚实边界：n-gram 替身的余弦≈词面重叠，它没有同义词知识，因此本评测度量的是
/// 「融合不破坏词法召回、不把负例捞成答案」，不是真实语义理解能力。
/// 真实 embedding 模型的度量走
/// <see cref="Evaluation_HybridChannel_RealEmbeddingProvider_ScoredOnLabeledQueries"/>
/// （默认跳过，需 HAOYUE_EVAL_EMBEDDING=1 且配置了 embedding 模型）。
/// </summary>
public class HybridRetrievalEvaluationTests : IDisposable
{
    private const string Scope = "eval-hybrid";
    private const string ModelKey = "eval-ngram";

    private readonly ITestOutputHelper _output;
    private readonly string _dir;
    private readonly HaoyueDatabase _database;
    private readonly KnowledgeStore _store;
    private readonly KnowledgeSemanticIndex _index;

    public HybridRetrievalEvaluationTests(ITestOutputHelper output)
    {
        _output = output;
        _dir = Path.Combine(Path.GetTempPath(), "haoyue-kb-hybrid-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _database = new HaoyueDatabase(Path.Combine(_dir, "store.db"));
        _store = new KnowledgeStore(_database);
        _index = new KnowledgeSemanticIndex(_database, new LlmHttpFactory());
        foreach (var (title, content, tags) in KnowledgeEvalData.Dataset)
            _store.Save(Scope, title, content, tags);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>
    /// 语料文本（标题 + 标签 + 正文）——替身的词表只由它决定，
    /// 查询里出现语料没有的特征即视为零向量。
    /// </summary>
    private IReadOnlyList<string> CorpusTexts =>
        [.. KnowledgeEvalData.Dataset.Select(d => $"{d.Title} {d.Tags} {d.Content}")];

    private async Task<Dictionary<string, IReadOnlyList<KnowledgeEntry>>> RunHybridAsync(
        IEmbeddingClient client, int limit = 10)
    {
        var entries = _store.LoadScope(Scope);
        var results = new Dictionary<string, IReadOnlyList<KnowledgeEntry>>(StringComparer.Ordinal);
        foreach (var (_, query, _) in KnowledgeEvalData.Scenarios)
        {
            if (results.ContainsKey(query)) continue;
            var outcome = await _index.SearchHybridAsync(
                Scope, entries, query, limit, client, ModelKey, CancellationToken.None).ConfigureAwait(false);
            results[query] = outcome.Entries;
        }
        return results;
    }

    private static IReadOnlyList<KnowledgeEntry> Lexical(KnowledgeStore store, string query, int limit) =>
        store.Search(Scope, query, limit);

    [Fact]
    public async Task Evaluation_HybridChannel_RecallNotWorseThanLexical_AndNegativesStayEmpty()
    {
        var client = new NgramEmbeddingClient(CorpusTexts);
        var hybrid = await RunHybridAsync(client);

        var hybridMetrics = KnowledgeEvalData.Score((query, limit) => hybrid[query].Take(limit).ToList());
        var lexicalMetrics = KnowledgeEvalData.Score((query, limit) => Lexical(_store, query, limit));

        foreach (var line in KnowledgeEvalData.PerQuery("混合", (query, limit) => hybrid[query].Take(limit).ToList()))
            _output.WriteLine(line);
        _output.WriteLine($"词法通道: {lexicalMetrics}");
        _output.WriteLine($"混合通道: {hybridMetrics}");
        _output.WriteLine($"替身嵌入调用次数: {client.EmbedCalls}，向量维度: {client.Dimension}");

        // 语义通道只能补召回，不能把词法已经排好的前三挤下去。
        Assert.True(hybridMetrics.Hit3 >= lexicalMetrics.Hit3,
            $"混合通道 HitRate@3 {hybridMetrics.Hit3:P1} 低于词法 {lexicalMetrics.Hit3:P1}：融合权重或门槛使语义信号覆盖了词面证据");
        Assert.True(hybridMetrics.Mrr >= lexicalMetrics.Mrr - 0.02,
            $"混合通道 MRR {hybridMetrics.Mrr:P3} 明显低于词法 {lexicalMetrics.Mrr:P3}（容忍 0.02）");

        // 负例必须仍为空：单通道绝对门槛（MinLexicalScore / MinSemanticSimilarity）存在的唯一
        // 就是「库里没有答案时返回空」，否则最不离谱的条目会被当成答案送回模型。
        var leaked = KnowledgeEvalData.Negatives
            .Where(n => hybrid[n.Query].Count > 0)
            .Select(n => $"\"{n.Query}\" → [{string.Join(", ", hybrid[n.Query].Select(e => e.Title))}]")
            .ToList();
        Assert.True(leaked.Count == 0, "负例被语义通道捞成命中：\n" + string.Join("\n", leaked));
        Assert.Equal(1.0, hybridMetrics.NegativeRejection);
    }

    [Fact]
    public async Task Evaluation_HybridChannel_EveryPositiveScenario_Top3ContainsExpected()
    {
        var client = new NgramEmbeddingClient(CorpusTexts);
        var entries = _store.LoadScope(Scope);
        var failures = new List<string>();
        foreach (var (group, query, expected) in KnowledgeEvalData.Scenarios.Where(s => s.ExpectedTitles.Length > 0))
        {
            var outcome = await _index.SearchHybridAsync(
                Scope, entries, query, 3, client, ModelKey + "-perquery", CancellationToken.None).ConfigureAwait(false);
            var titles = outcome.Entries.Select(e => e.Title).ToList();
            if (!expected.Any(titles.Contains))
                failures.Add($"[{group}] \"{query}\" 期望 {string.Join("/", expected)}，实际 [{string.Join(", ", titles)}]");
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    public void NgramEmbeddingClient_VectorsAreComputedFromText_NotLookedUp()
    {
        // 这道测试钉住「替身不是查表」这件事本身：查表替身只有预置的那几条字符串有向量，
        // 任何新文本都落到默认零向量；计算型替身必须给未见过的文本产出非零向量，
        // 并且相似度随特征重叠单调变化。
        var client = new NgramEmbeddingClient(CorpusTexts);
        Assert.True(client.Dimension > 2, $"词表维度应来自语料而非固定小表，实际 {client.Dimension}");

        var seen = client.Embed("部署流程 docker 发布 8080");
        var unseen = client.Embed("打包发布流程 docker 部署");
        Assert.True(Norm(unseen) > 0, "语料里没出现过的组合也应得到非零向量（查表替身做不到）");
        Assert.Equal(seen.Length, unseen.Length);

        var query = client.Embed("docker 发布");
        var sameTopic = client.Embed("使用 docker compose 发布到服务器");
        var otherTopic = client.Embed("登录失败先检查账号密码");
        Assert.True(Cos(query, sameTopic) > Cos(query, otherTopic),
            "余弦必须随特征重叠变化：docker 发布应更靠近 docker 部署条目");
        Assert.True(Cos(query, otherTopic) < KnowledgeTuning.MinSemanticSimilarity,
            "无关主题必须落进语义门槛之下，否则负例兜不住");

        // 确定性：同一文本两次嵌入结果一致（评测数字必须可回归）。
        Assert.Equal(client.Embed("pnpm 前端"), client.Embed("pnpm 前端"));
    }

    [Fact]
    public async Task Evaluation_HybridChannel_RealEmbeddingProvider_ScoredOnLabeledQueries()
    {
        if (Environment.GetEnvironmentVariable("HAOYUE_EVAL_EMBEDDING") != "1")
        {
            _output.WriteLine("跳过：真实 embedding 评测需要 HAOYUE_EVAL_EMBEDDING=1 且 ~/.haoyue/config.json 里配好 embedding 模型。" +
                              "默认不参与回归（网络与模型可用性会左右数字），n-gram 替身那条测试度量的是融合通道本身。");
            return;
        }
        var config = new ConfigStore().Config;
        var route = _index.CreateRoute(config);
        if (route is null)
        {
            _output.WriteLine("跳过：HAOYUE_EVAL_EMBEDDING=1 但配置里没有可用的 embedding 模型" +
                              $"（{_index.Status(Scope, _store.LoadScope(Scope), config).Reason}）。");
            return;
        }

        var hybrid = await RunHybridAsync(route.Client);
        var metrics = KnowledgeEvalData.Score((query, limit) => hybrid[query].Take(limit).ToList());
        foreach (var line in KnowledgeEvalData.PerQuery($"真实模型 {route.CacheKey}",
                     (query, limit) => hybrid[query].Take(limit).ToList()))
            _output.WriteLine(line);
        _output.WriteLine($"真实 embedding: {metrics}");

        var leaked = KnowledgeEvalData.Negatives
            .Where(n => hybrid[n.Query].Count > 0)
            .Select(n => $"\"{n.Query}\" → [{string.Join(", ", hybrid[n.Query].Select(e => e.Title))}]")
            .ToList();
        Assert.True(leaked.Count == 0, "真实模型下负例仍应被门槛挡住：\n" + string.Join("\n", leaked));
        Assert.True(metrics.Hit3 >= 0.8,
            $"真实 embedding 模型 HitRate@3 {metrics.Hit3:P1} 低于 80%，语义通道没有带来可用召回");
    }

    private static double Cos(double[] a, double[] b)
    {
        var dot = 0d;
        for (var i = 0; i < a.Length; i++) dot += a[i] * b[i];
        return dot / (Norm(a) * Norm(b));
    }

    private static double Norm(double[] v) => Math.Sqrt(v.Sum(x => x * x));

    /// <summary>
    /// 确定性向量替身：文本 → (词 + 汉字二元组) 特征，IDF 加权、L2 归一。
    /// 词表固定来自构造时传入的语料，语料外的特征直接丢弃——因此「库里根本没有的词」
    /// 得到零向量，语义通道对它保持沉默，这与真实模型的行为不同，也正是本替身
    /// 只能度量融合通道、不能声称度量语义理解的原因。
    /// </summary>
    internal sealed class NgramEmbeddingClient : IEmbeddingClient
    {
        private static readonly Regex LatinWord = new(@"[a-z0-9][a-z0-9._%+\-/]*", RegexOptions.Compiled);
        private static readonly Regex CjkRun = new(@"[\u4e00-\u9fff]{1,}", RegexOptions.Compiled);

        private readonly Dictionary<string, int> _index;
        private readonly double[] _idf;

        public int EmbedCalls;
        public int Dimension => _index.Count;

        public NgramEmbeddingClient(IEnumerable<string> corpus)
        {
            var documents = corpus.Select(Features).ToList();
            var docCount = Math.Max(1, documents.Count);
            var df = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var features in documents)
                foreach (var feature in features.Distinct(StringComparer.Ordinal))
                    df[feature] = df.TryGetValue(feature, out var n) ? n + 1 : 1;

            _index = new Dictionary<string, int>(StringComparer.Ordinal);
            _idf = new double[df.Count];
            var i = 0;
            foreach (var (feature, frequency) in df.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                _index[feature] = i;
                _idf[i] = Math.Log((docCount + 1.0) / (frequency + 0.5));
                i++;
            }
        }

        public Task<double[][]> EmbedBatchAsync(IReadOnlyList<string> inputs, CancellationToken ct)
        {
            EmbedCalls += inputs.Count;
            return Task.FromResult<double[][]>([.. inputs.Select(EmbedOne)]);
        }

        public double[] Embed(string text) => EmbedOne(text);

        private double[] EmbedOne(string text)
        {
            var vector = new double[_index.Count];
            foreach (var feature in Features(text).GroupBy(f => f, StringComparer.Ordinal))
            {
                if (!_index.TryGetValue(feature.Key, out var slot)) continue;
                vector[slot] = (1 + Math.Log(feature.Count())) * _idf[slot];
            }
            return vector;
        }

        /// <summary>特征 = 拉丁词 + 汉字二元组（单字成串时保留单字，避免短查询整条落空）。</summary>
        private static IEnumerable<string> Features(string text)
        {
            var normalized = text.ToLowerInvariant();
            foreach (Match word in LatinWord.Matches(normalized))
                yield return word.Value;
            foreach (Match run in CjkRun.Matches(normalized))
            {
                var value = run.Value;
                if (value.Length == 1) { yield return value; continue; }
                for (var i = 0; i + 1 < value.Length; i++)
                    yield return value.Substring(i, 2);
            }
        }
    }
}
