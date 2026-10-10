using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Data;
using Haoyue.Runtime.Providers;
using Haoyue.Runtime.Tools;
using System.Text.Json.Nodes;

namespace Haoyue.Tests;

/// <summary>
/// 语义检索层（KnowledgeSemanticIndex）：混合排序、向量缓存、降级行为。
/// Embedding 客户端以确定性的测试替身注入，不访问网络。
/// </summary>
public class KnowledgeSemanticTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "haoyue-kb-sem-" + Guid.NewGuid().ToString("N"));
    private readonly KnowledgeStore _store;
    private readonly KnowledgeSemanticIndex _index;

    public KnowledgeSemanticTests()
    {
        Directory.CreateDirectory(_dir);
        var database = new HaoyueDatabase(Path.Combine(_dir, "store.db"));
        _store = new KnowledgeStore(database);
        _index = new KnowledgeSemanticIndex(database, new LlmHttpFactory());
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>测试替身：文本 → 固定维度向量。按关键词表映射，向量空间确定且可预言。</summary>
    private sealed class FakeEmbeddingClient(IReadOnlyDictionary<string, double[]> vectors) : IEmbeddingClient
    {
        public int CallCount;

        public Task<double[][]> EmbedBatchAsync(IReadOnlyList<string> inputs, CancellationToken ct)
        {
            CallCount += inputs.Count;
            return Task.FromResult(inputs.Select(input =>
                vectors.TryGetValue(input.Trim(), out var vector) ? vector : new double[] { 0, 0 }
            ).ToArray());
        }
    }

    [Fact]
    public async Task HybridSearch_SemanticOnlyMatch_BeatsNothing()
    {
        // 词条 A 词法上与查询零重叠；词条 B 词法命中。向量空间里 A 与查询同向。
        var a = _store.Save("w", "机器学习入门", "梯度下降与损失函数", null).Entry;
        var b = _store.Save("w", "数据库索引", "B+树与查询优化", null).Entry;

        var client = new FakeEmbeddingClient(new Dictionary<string, double[]>
        {
            ["深度学习训练技巧"] = [1, 0],
            ["机器学习入门\n梯度下降与损失函数"] = [0.9, 0.1],
            ["数据库索引\nB+树与查询优化"] = [0, 1],
        });

        var matches = await _index.SearchHybridAsync(
            "w", _store.LoadScope("w"), "深度学习训练技巧", 8, client, "test-model", CancellationToken.None);

        Assert.NotEmpty(matches);
        Assert.Equal(a.Id, matches[0].Id);
        // 零相似度（余弦 0、词法 0）的词条不应进入结果。
        Assert.DoesNotContain(matches, entry => entry.Id == b.Id);
    }

    [Fact]
    public async Task HybridSearch_VectorsAreCached_SecondSearchSkipsEmbedding()
    {
        _store.Save("w", "部署流程", "docker compose up -d", null);
        var client = new FakeEmbeddingClient(new Dictionary<string, double[]>
        {
            ["部署流程\ndocker compose up -d"] = [1, 0],
            ["怎么发布服务"] = [0.95, 0.05],
        });

        var first = await _index.SearchHybridAsync(
            "w", _store.LoadScope("w"), "怎么发布服务", 8, client, "test-model", CancellationToken.None);
        var callsAfterFirst = client.CallCount;

        var second = await _index.SearchHybridAsync(
            "w", _store.LoadScope("w"), "怎么发布服务", 8, client, "test-model", CancellationToken.None);

        Assert.Single(first);
        Assert.Single(second);
        Assert.True(client.CallCount == callsAfterFirst + 1,
            $"expected only the query embedding on the second call, got {client.CallCount - callsAfterFirst} extra embeddings");
    }

    [Fact]
    public async Task HybridSearch_EmbeddingFailure_DegradesToLexical()
    {
        _store.Save("w", "构建命令", "pnpm build", null);
        _store.Save("w", "无关词条", "别的主题", null);
        var failing = new ThrowingEmbeddingClient();

        // 客户端全程抛异常：混合检索必须仍返回词法命中，而不是空结果或异常。
        var matches = await _index.SearchHybridAsync(
            "w", _store.LoadScope("w"), "构建", 8, failing, "test-model", CancellationToken.None);

        Assert.NotEmpty(matches);
        Assert.Equal("构建命令", matches[0].Title);
    }

    private sealed class ThrowingEmbeddingClient : IEmbeddingClient
    {
        public Task<double[][]> EmbedBatchAsync(IReadOnlyList<string> inputs, CancellationToken ct) =>
            throw new HttpRequestException("simulated outage");
    }

    [Fact]
    public void CreateClient_Disabled_ReturnsNull()
    {
        var config = new HaoyueConfig();
        config.Knowledge.SemanticEnabled = false;
        Assert.Null(_index.CreateClient(config));
    }

    [Fact]
    public void CreateClient_AutoSelectsFirstEmbeddingCapableModel()
    {
        var config = new HaoyueConfig
        {
            Providers =
            [
                new ProviderConfig
                {
                    Id = "p1",
                    BaseUrl = "https://api.example.com/v1",
                    Models = [new ModelConfig { Id = "chat-model" }],
                },
                new ProviderConfig
                {
                    Id = "p2",
                    BaseUrl = "https://embed.example.com/v1",
                    Models = [new ModelConfig { Id = "embed-model", Capabilities = new ModelCapabilities { Embedding = true } }],
                },
            ],
        };
        var client = _index.CreateClient(config);
        Assert.IsType<OpenAiEmbeddingClient>(client);
    }

    [Fact]
    public void CreateClient_ExplicitReference_MissingProvider_ReturnsNull()
    {
        var config = new HaoyueConfig();
        config.Knowledge.EmbeddingModel = "ghost/model";
        Assert.Null(_index.CreateClient(config));
    }

    [Fact]
    public void Cosine_IdenticalVectors_IsOne()
    {
        Assert.Equal(1.0, KnowledgeSemanticIndex.Cosine([0.3, 0.4], [0.3, 0.4]), 6);
        Assert.Equal(0.0, KnowledgeSemanticIndex.Cosine([1, 0], [0, 1]), 6);
    }
}
