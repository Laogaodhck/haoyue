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
    private readonly HaoyueDatabase _database;
    private readonly KnowledgeStore _store;
    private readonly KnowledgeSemanticIndex _index;

    public KnowledgeSemanticTests()
    {
        Directory.CreateDirectory(_dir);
        _database = new HaoyueDatabase(Path.Combine(_dir, "store.db"));
        _store = new KnowledgeStore(_database);
        _index = new KnowledgeSemanticIndex(_database, new LlmHttpFactory());
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>测试替身：文本 → 固定维度向量。按关键词表映射，向量空间确定且可预言。</summary>
    private sealed class FakeEmbeddingClient(IReadOnlyDictionary<string, double[]> vectors) : IEmbeddingClient
    {
        public int CallCount;
        public List<string> Inputs { get; } = [];

        public Task<double[][]> EmbedBatchAsync(IReadOnlyList<string> inputs, CancellationToken ct)
        {
            CallCount += inputs.Count;
            Inputs.AddRange(inputs);
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

        var outcome = await _index.SearchHybridAsync(
            "w", _store.LoadScope("w"), "深度学习训练技巧", 8, client, "test-model", CancellationToken.None);
        var matches = outcome.Entries;

        Assert.NotEmpty(matches);
        Assert.Equal(a.Id, matches[0].Id);
        // 零相似度（余弦 0、词法 0）的词条不应进入结果。
        Assert.DoesNotContain(matches, entry => entry.Id == b.Id);
        Assert.Null(outcome.EmbeddingError);
    }

    [Fact]
    public async Task HybridSearch_WeakCosineBelowFloor_IsNotRescaledIntoAHit()
    {
        // 旧口径按候选最大值归一：top 0.9 时 0.2 的弱相关会被放大成 0.089 的满权重信号，
        // 配上任何词法残值就会被当成答案返回。绝对余弦 + 相似度门槛之下它直接归零。
        var strong = _store.Save("w", "构建命令", "pnpm build", null).Entry;
        var weak = _store.Save("w", "无关主题", "完全不相干的内容", null).Entry;

        var client = new FakeEmbeddingClient(new Dictionary<string, double[]>
        {
            ["怎么构建"] = [1, 0],
            ["构建命令\npnpm build"] = [0.99, 0.01],
            ["无关主题\n完全不相干的内容"] = [0.2, 0.98],
        });

        var matches = (await _index.SearchHybridAsync(
            "w", _store.LoadScope("w"), "怎么构建", 8, client, "test-model", CancellationToken.None)).Entries;

        Assert.Equal([strong.Id], matches.Select(entry => entry.Id).ToArray());
        Assert.DoesNotContain(matches, entry => entry.Id == weak.Id);
    }

    [Fact]
    public async Task HybridSearch_NegativeQuery_ReturnsNothing()
    {
        // 知识库里确实没有答案时，混合通道不能像旧的 Final > 0 那样把最不离谱的条目送回模型。
        _store.Save("w", "端口占用排查", "lsof -i :5173", null);
        var client = new FakeEmbeddingClient(new Dictionary<string, double[]>
        {
            ["量子色动力学重整化"] = [1, 0, 0],
            ["端口占用排查\nlsof -i :5173"] = [0, 1, 0],
        });

        var matches = (await _index.SearchHybridAsync(
            "w", _store.LoadScope("w"), "量子色动力学重整化", 8, client, "test-model", CancellationToken.None)).Entries;

        Assert.Empty(matches);
    }

    [Fact]
    public async Task HybridSearch_EmbedCap_DefersTheRestToPending()
    {
        // 单次工具调用不能把整库嵌完（本地通道逐条串行会卡死回合）：
        // 超出 MaxEmbedPerSearch 的条目计入 PendingVectors，由显式建索引通道补齐。
        const int entries = KnowledgeTuning.MaxEmbedPerSearch + 6;
        for (var i = 0; i < entries; i++)
            _store.Save("w", $"条目 {i}", $"内容 {i}", null);
        var client = new FakeEmbeddingClient(new Dictionary<string, double[]>());

        var outcome = await _index.SearchHybridAsync(
            "w", _store.LoadScope("w"), "条目", 8, client, "test-model", CancellationToken.None);

        Assert.Equal(KnowledgeTuning.MaxEmbedPerSearch, outcome.EmbeddedNow);
        Assert.Equal(6, outcome.PendingVectors);
    }

    [Fact]
    public async Task HybridSearch_EmbedsWholeBody_NeverTheTrimmedTail()
    {
        // 嵌入预算曾固定 4000 字符，而正文上限 8000、导入分块 6000——每条知识尾部对语义层永久不可见。
        var tail = "结尾关键约束";
        _store.Save("w", "长条目", new string('中', KnowledgeStore.MaxEntryContentChars - tail.Length) + tail, null);
        var client = new FakeEmbeddingClient(new Dictionary<string, double[]> { ["查询"] = [1, 0] });

        await _index.SearchHybridAsync(
            "w", _store.LoadScope("w"), "查询", 8, client, "test-model", CancellationToken.None);

        var embedded = client.Inputs.First(input => input.Contains(tail, StringComparison.Ordinal));
        Assert.True(embedded.EndsWith(tail, StringComparison.Ordinal),
            $"嵌入文本没有以正文结尾收束：…{embedded[^Math.Min(40, embedded.Length)..]}");
    }

    [Fact]
    public void EmbedBudget_CoversImportChunkAndEntryContent()
    {
        // 常量护栏：任何一侧被改大都会让另一侧重新出现「看不见的尾段」。
        Assert.True(KnowledgeSemanticIndex.MaxEmbedChars >= KnowledgeStore.MaxEntryContentChars,
            $"嵌入预算 {KnowledgeSemanticIndex.MaxEmbedChars} 必须容得下整条正文 {KnowledgeStore.MaxEntryContentChars}");
        Assert.True(KnowledgeSemanticIndex.MaxEmbedChars >= KnowledgeImport.ChunkSize,
            $"嵌入预算 {KnowledgeSemanticIndex.MaxEmbedChars} 必须容得下导入分块 {KnowledgeImport.ChunkSize}");
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

        Assert.Single(first.Entries);
        Assert.Single(second.Entries);
        Assert.True(client.CallCount == callsAfterFirst + 1,
            $"expected only the query embedding on the second call, got {client.CallCount - callsAfterFirst} extra embeddings");
    }

    [Fact]
    public async Task HybridSearch_EmbeddingFailure_DegradesToLexical_AndSaysSo()
    {
        _store.Save("w", "构建命令", "pnpm build", null);
        _store.Save("w", "无关词条", "别的主题", null);
        var failing = new ThrowingEmbeddingClient();

        // 嵌入全程抛异常：混合检索必须仍返回词法命中，而不是空结果或异常——
        // 且必须把失效原因带出来，此前是三层空 catch 静默降级，用户永远不知道自己用的是词法。
        var outcome = await _index.SearchHybridAsync(
            "w", _store.LoadScope("w"), "构建", 8, failing, "test-model", CancellationToken.None);

        Assert.NotEmpty(outcome.Entries);
        Assert.Equal("构建命令", outcome.Entries[0].Title);
        Assert.Contains("simulated outage", outcome.EmbeddingError);
    }

    private sealed class ThrowingEmbeddingClient : IEmbeddingClient
    {
        public Task<double[][]> EmbedBatchAsync(IReadOnlyList<string> inputs, CancellationToken ct) =>
            throw new HttpRequestException("simulated outage");
    }

    // ─────────────── 显式建索引：语义状态 + 分批补齐 ───────────────

    /// <summary>指向一个 HTTP 嵌入提供商的配置：CreateRoute 直接命中，不发请求。</summary>
    private static HaoyueConfig EmbeddingConfig() => new()
    {
        Providers =
        [
            new ProviderConfig
            {
                Id = "p2",
                BaseUrl = "https://embed.example.com/v1",
                Models = [new ModelConfig { Id = "embed-model", Capabilities = new ModelCapabilities { Embedding = true } }],
            },
        ],
    };

    [Fact]
    public void Status_SemanticDisabled_SaysWhyInsteadOfLookingEmpty()
    {
        _store.Save("w", "部署流程", "docker compose up", null);
        var config = EmbeddingConfig();
        config.Knowledge.SemanticEnabled = false;

        var status = _index.Status("w", _store.LoadScope("w"), config);

        Assert.False(status.Enabled);
        Assert.NotNull(status.Reason);
        Assert.Contains("semanticEnabled", status.Reason);
    }

    [Fact]
    public async Task Status_CountsCachedVectorsAgainstTheEntrySet()
    {
        for (var i = 0; i < 3; i++) _store.Save("w", $"条目 {i}", $"内容 {i}", null);
        var entries = _store.LoadScope("w");
        var config = EmbeddingConfig();

        var before = _index.Status("w", entries, config);
        Assert.True(before.Enabled);
        Assert.Equal(3, before.Total);
        Assert.Equal(0, before.Embedded);
        Assert.Equal(3, before.Pending);

        // 与 Status 用同一个缓存键命名空间（模型切换会让向量空间作废，缓存键必须来自路由本身）。
        await _index.BuildAsync("w", entries, new FakeEmbeddingClient(new Dictionary<string, double[]>()),
            _index.CreateRoute(config)!.CacheKey, 2, CancellationToken.None);

        var after = _index.Status("w", entries, config);
        Assert.Equal(2, after.Embedded);
        Assert.Equal(1, after.Pending);
    }

    [Fact]
    public async Task BuildAsync_EmbedsStableBatchesUntilPendingDrains()
    {
        for (var i = 0; i < 10; i++) _store.Save("w", $"条目 {i}", $"内容 {i}", null);
        var entries = _store.LoadScope("w");
        var client = new FakeEmbeddingClient(new Dictionary<string, double[]>());

        var first = await _index.BuildAsync("w", entries, client, "test-model", 4, CancellationToken.None);
        Assert.Equal(4, first.Embedded);
        Assert.Equal(6, first.Pending);

        var second = await _index.BuildAsync("w", entries, client, "test-model", 4, CancellationToken.None);
        Assert.Equal(4, second.Embedded);
        Assert.Equal(2, second.Pending);

        var third = await _index.BuildAsync("w", entries, client, "test-model", 4, CancellationToken.None);
        Assert.Equal(2, third.Embedded);
        Assert.Equal(0, third.Pending);

        // 已经全部有向量时不再外呼：调用方循环到 pending 归零就能停。
        var inputsAfterFullBuild = client.Inputs.Count;
        var idle = await _index.BuildAsync("w", entries, client, "test-model", 4, CancellationToken.None);
        Assert.Equal(0, idle.Embedded);
        Assert.Equal(0, idle.Pending);
        Assert.Equal(inputsAfterFullBuild, client.Inputs.Count);
    }

    [Fact]
    public async Task BuildAsync_BatchIsClampedToTheSingleRequestCeiling()
    {
        for (var i = 0; i < KnowledgeSemanticIndex.MaxBuildBatch + 2; i++)
            _store.Save("w", $"条目 {i}", $"内容 {i}", null);
        var client = new FakeEmbeddingClient(new Dictionary<string, double[]>());

        // 一次 RPC 嵌完整库会把请求顶到桌面 60 秒超时之外，批大小必须封在上限里。
        var result = await _index.BuildAsync(
            "w", _store.LoadScope("w"), client, "test-model", 100_000, CancellationToken.None);

        Assert.Equal(KnowledgeSemanticIndex.MaxBuildBatch, result.Embedded);
        Assert.Equal(2, result.Pending);
    }

    [Fact]
    public async Task BuildAsync_EmbeddingFailure_ReportsReasonInsteadOfLeavingHalfBuiltSilently()
    {
        _store.Save("w", "构建命令", "pnpm build", null);

        var result = await _index.BuildAsync(
            "w", _store.LoadScope("w"), new ThrowingEmbeddingClient(), "test-model", 8, CancellationToken.None);

        Assert.Equal(0, result.Embedded);
        Assert.Equal(1, result.Failed);
        Assert.Contains("simulated outage", result.Error);
    }

    [Fact]
    public async Task BuildAsync_NoEmbeddingRoute_ReportsDisabled()
    {
        _store.Save("w", "构建命令", "pnpm build", null);
        var config = new HaoyueConfig();
        config.Knowledge.SemanticEnabled = false;

        var result = await _index.BuildAsync("w", _store.LoadScope("w"), config, 8, CancellationToken.None);

        Assert.Equal(0, result.Embedded);
        Assert.NotNull(result.Error);
    }

    [Fact]
    public void MaxEmbedPerSearch_StaysSmallEnoughToNotStallATurn()
    {
        // 工具调用里的内联补嵌只够让刚存的几条立刻可语义召回；整库补齐属于建索引通道。
        // 这条上限被抬大就等于把「回合卡死」重新请回来。
        Assert.True(KnowledgeTuning.MaxEmbedPerSearch <= 8,
            $"检索内联补嵌上限已达 {KnowledgeTuning.MaxEmbedPerSearch}，整库补齐必须走 knowledge.semantic.build");
    }

    [Fact]
    public async Task DelegatingRoute_EmbedsWholeBatchInOneProviderCall()
    {
        // 本地 GGUF / 非 HTTP 引用走 ProviderManager。逐条 EmbedAsync 会让每条都重新租用
        // 权重，一次建索引变成 N 次加载往返——正是「像没输出」的成因之一。
        var providers = new BatchRecordingProviderManager();
        var index = new KnowledgeSemanticIndex(_database, new LlmHttpFactory(), providers);
        var config = new HaoyueConfig { Providers = [] };
        config.Knowledge.EmbeddingModel = "local/embed";

        var route = index.CreateRoute(config);
        Assert.NotNull(route);
        var vectors = await route!.Client.EmbedBatchAsync(["甲", "乙", "丙"], CancellationToken.None);

        Assert.Equal(1, providers.BatchCalls);
        Assert.Equal(0, providers.SingleCalls);
        Assert.Equal(3, vectors.Length);
        Assert.Equal(["甲", "乙", "丙"], providers.LastInputs.ToArray());
    }

    [Fact]
    public async Task EmbedBatchDefault_ImplementsItselfOverSingleEmbedForThirdPartyProviders()
    {
        IProviderManager providers = new SingleOnlyProviderManager();

        var result = await providers.EmbedBatchAsync(["甲", "乙"], null, CancellationToken.None);

        Assert.Equal(2, result!.Vectors.Count);
        Assert.Equal(2, ((SingleOnlyProviderManager)providers).SingleCalls);
    }

    /// <summary>
    /// 只实现单条嵌入的 IProviderManager（模拟第三方实现/测试替身）。
    /// 它刻意不声明 EmbedBatchAsync——接口便落到默认成员，验证逐条回退仍然可用。
    /// </summary>
    private sealed class SingleOnlyProviderManager : IProviderManager
    {
        public int SingleCalls;

        public ModelInfo ResolveActive(WorkspaceConfig? workspace = null) => throw new NotSupportedException();
        public IReadOnlyList<ModelInfo> BuildCandidates(WorkspaceConfig? workspace = null) => throw new NotSupportedException();
        public ModelInfo? ResolveModel(string reference) => null;
        public IAsyncEnumerable<LlmStreamEvent> StreamAsync(
            Func<ModelInfo, LlmRequest> requestFactory, WorkspaceConfig? workspace, CancellationToken ct,
            Func<ModelInfo, bool>? candidateFilter = null) => throw new NotSupportedException();
        public Task<(bool Success, string Detail, double LatencyMs)> TestModelAsync(ModelInfo model, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> FetchModelsAsync(
            ProviderConfig provider, string? url = null, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<EmbeddingResult?> EmbedAsync(string input, string? modelRef = null, CancellationToken ct = default)
        {
            SingleCalls++;
            return Task.FromResult<EmbeddingResult?>(new EmbeddingResult([new float[] { 1, 0 }], "stub/embed"));
        }
    }

    /// <summary>整批一次调用替身（生产里由 ProviderManager 覆盖默认成员达成同样效果）。</summary>
    private sealed class BatchRecordingProviderManager : IProviderManager
    {
        public int BatchCalls;
        public int SingleCalls;
        public List<string> LastInputs = [];

        public ModelInfo ResolveActive(WorkspaceConfig? workspace = null) => throw new NotSupportedException();
        public IReadOnlyList<ModelInfo> BuildCandidates(WorkspaceConfig? workspace = null) => throw new NotSupportedException();
        public ModelInfo? ResolveModel(string reference) => null;
        public IAsyncEnumerable<LlmStreamEvent> StreamAsync(
            Func<ModelInfo, LlmRequest> requestFactory, WorkspaceConfig? workspace, CancellationToken ct,
            Func<ModelInfo, bool>? candidateFilter = null) => throw new NotSupportedException();
        public Task<(bool Success, string Detail, double LatencyMs)> TestModelAsync(ModelInfo model, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<IReadOnlyList<string>> FetchModelsAsync(
            ProviderConfig provider, string? url = null, CancellationToken ct = default) => throw new NotSupportedException();

        public Task<EmbeddingResult?> EmbedAsync(string input, string? modelRef = null, CancellationToken ct = default)
        {
            SingleCalls++;
            throw new NotSupportedException("委派客户端必须走整批入口，单条入口不该被用到。");
        }

        public Task<EmbeddingResult?> EmbedBatchAsync(
            IReadOnlyList<string> inputs, string? modelRef = null, CancellationToken ct = default)
        {
            BatchCalls++;
            LastInputs = [.. inputs];
            return Task.FromResult<EmbeddingResult?>(new EmbeddingResult(
                [.. inputs.Select((_, index) => new float[] { index, 1 })], "stub/embed"));
        }
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
