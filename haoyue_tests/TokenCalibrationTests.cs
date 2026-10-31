using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Haoyue.Runtime;
using Haoyue.Runtime.Agents;
using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Data;
using Haoyue.Runtime.Providers;
using Haoyue.Runtime.Sessions;
using Haoyue.Runtime.Tools;
using Haoyue.Runtime.Workspaces;

namespace Haoyue.Tests;

/// <summary>
/// H-E：token 估算实测校准闭环。真实 prompt tokens 每次都有，此前没有任何代码把
/// actual/estimated 偏差反哺估算系数；工具 schema 与回传的 thinking 也不进预算。
/// </summary>
public sealed class TokenCalibrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "haoyue-token-calib-" + Guid.NewGuid().ToString("N"));

    public TokenCalibrationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private ModelTokenStats NewStats() => new(new HaoyueDatabase(Path.Combine(_dir, $"stats-{Guid.NewGuid():N}.db")));

    [Fact]
    public void FirstRecord_SetsRatioToSample()
    {
        var stats = NewStats();
        stats.Record("p", "m", actualInputTokens: 2000, estimatedPromptTokens: 1000);
        Assert.Equal(2.0, stats.GetRatio("p", "m"));
    }

    [Fact]
    public void SecondRecord_AppliesEwma25Percent()
    {
        var stats = NewStats();
        stats.Record("p", "m", 2000, 1000);          // ratio 2.0
        stats.Record("p", "m", 1000, 1000);          // sample 1.0 → EWMA: 2.0 + 0.25*(1.0-2.0) = 1.75
        Assert.Equal(1.75, stats.GetRatio("p", "m"), precision: 6);
    }

    [Fact]
    public void ExtremeSample_IsClamped()
    {
        var stats = NewStats();
        stats.Record("p", "m", 100_000, 1000);       // 100x → 钳制到 2.5
        Assert.Equal(2.5, stats.GetRatio("p", "m"));
        stats.Record("p2", "m", 10, 1000);           // 0.01x → 钳制到 0.5
        Assert.Equal(0.5, stats.GetRatio("p2", "m"));
    }

    [Fact]
    public void NonPositiveSamples_AreIgnored()
    {
        var stats = NewStats();
        stats.Record("p", "m", 0, 1000);             // 本地模型不上报 usage
        stats.Record("p", "m", 1000, 0);             // 空估算
        Assert.Equal(1.0, stats.GetRatio("p", "m")); // 无观测 → 纯启发式
    }

    [Fact]
    public void EstimateTokens_AppliesCalibration()
    {
        var text = new string('a', 400); // Latin: 400/4 = 100 tokens
        Assert.Equal(100, ContextPlanner.EstimateTokens(text));
        Assert.Equal(150, ContextPlanner.EstimateTokens(text, calibration: 1.5));
    }

    [Fact]
    public void EstimateTokens_Message_CountsThinking()
    {
        var message = new ChatMessage
        {
            Role = ChatRole.Assistant,
            Text = new string('a', 40),    // 10 tokens
            Thinking = new string('a', 400), // 100 tokens——此前不计入
        };
        Assert.Equal(118, ContextPlanner.EstimateTokens(message)); // 10 + 100 + 8 overhead
    }

    [Fact]
    public void EstimateRequestTokens_IncludesTools()
    {
        var request = new LlmRequest
        {
            Provider = new ProviderConfig { Id = "p" },
            Model = new ModelConfig { Id = "m" },
            Messages = [ChatMessage.User(new string('a', 40))], // 10 + 8
            System = new string('a', 40),                                 // 10
            Tools = [new ToolDefinition("bash", "run a command",
                ToolSchema.Object(("command", ToolSchema.String("cmd"), true)))],
        };
        var total = ContextPlanner.EstimateRequestTokens(request);
        var withoutTools = ContextPlanner.EstimateRequestTokens(request with { Tools = [] });
        Assert.True(total > withoutTools + 10, $"tools should add schema tokens: {total} vs {withoutTools}");
    }

    [Fact]
    public void WindowBudget_SubtractsToolSchema_AndDividesByCalibration()
    {
        var model = new ModelConfig { Id = "m", ContextWindow = 10_000, MaxOutput = 1_000 };
        var system = new string('a', 4000); // 1000 tokens
        var baseline = ContextPlanner.WindowBudget(model, system); // 10000-1000-1000-1500 = 6500
        Assert.Equal(6500, baseline);

        var withTools = ContextPlanner.WindowBudget(model, system, toolSchemaTokens: 500);
        Assert.Equal(6000, withTools);

        // 低估（ratio 2.0）→ 窗口折算成原始单位后减半：(10000-1000)/2 - 1000 - 500 - 1500 = 1500
        var calibrated = ContextPlanner.WindowBudget(model, system, calibration: 2.0, toolSchemaTokens: 500);
        Assert.Equal(1500, calibrated);
    }

    [Fact]
    public void FitToWindow_ComparesRawEstimatesAgainstRawBudget()
    {
        var model = new ModelConfig { ContextWindow = 12_000, MaxOutput = 2_000 };
        // 原始预算 = (12000-2000)/2 - 1 - 1500 = 3499；每条约 108 原始 token。
        var messages = new List<ChatMessage>();
        for (var i = 0; i < 20; i++)
            messages.Add(i % 2 == 0 ? ChatMessage.User(new string('x', 400)) : ChatMessage.Assistant(new string('x', 400)));

        var fitted = ContextPlanner.FitToWindow(messages, model, "sys", calibration: 2.0);

        // 原始合计 2160 < 3499，必须原样保留；若总量误乘 calibration（4320）会误裁到 6 条。
        Assert.Equal(messages.Count, fitted.Count);
    }

    [Fact]
    public void SplitForCompaction_SizesTailInRawUnitsToo()
    {
        var model = new ModelConfig { ContextWindow = 12_000, MaxOutput = 2_000 };
        var messages = new List<ChatMessage>();
        for (var i = 0; i < 20; i++)
            messages.Add(i % 2 == 0 ? ChatMessage.User(new string('x', 400)) : ChatMessage.Assistant(new string('x', 400)));

        var (old, recent) = ContextPlanner.SplitForCompaction(
            messages, model, "sys", calibration: 2.0, tailRatio: 0.5);

        // 尾部预算 1749 原始 token → 16 条（16×108 = 1728）；按校准值只能放 8 条。
        Assert.Equal(16, recent.Count);
        Assert.Equal(4, old.Count);
    }

    [Fact]
    public async Task ClosedLoop_ActualUsageFeedsBackIntoStats()
    {
        // 端到端：脚本化客户端回传真实 usage → ProviderManager 记录 → ModelTokenStats
        // 沉淀系数。这是"数据链路已存在、只差反馈环"的闭环验证。
        var dbFile = Path.Combine(_dir, "loop.db");
        var store = new ConfigStore(Path.Combine(_dir, "config.json"), Path.Combine(_dir, "state.json"));
        store.Config.Providers.Clear();
        store.Config.Providers.Add(new ProviderConfig
        {
            Id = "openai",
            Kind = "openai",
            BaseUrl = "https://test.local/v1",
            Models = [new ModelConfig { Id = "gpt-test", ContextWindow = 128_000 }],
        });
        var factory = new UsageReportingClientFactory(new TokenUsage(50_000, 100));
        var workspace = new WorkspaceInfo { Root = Path.Combine(_dir, "ws"), ProjectKinds = [] };
        Directory.CreateDirectory(workspace.Root);
        await using var runtime = HaoyueRuntime.CreateIsolated(workspace, configureServices: services =>
        {
            services.AddSingleton<IConfigStore>(store);
            services.AddSingleton(new HaoyueDatabase(dbFile));
            services.AddSingleton<ILlmHttpFactory>(new LlmHttpFactory());
            services.AddSingleton<ILlmClientFactory>(factory);
            services.AddSingleton(new CircuitBreaker(store.Config.Routing.Retry));
        });
        var session = runtime.Sessions.Create(workspace, networkEnabled: false);
        var result = await runtime.Agent.RunTurnAsync(session, workspace, "你好", CancellationToken.None);

        Assert.Null(result.Error);
        var stats = new ModelTokenStats(new HaoyueDatabase(dbFile));
        var ratio = stats.GetRatio("openai", "gpt-test");
        Assert.True(ratio > 1.5, $"usage 50000 vs a tiny prompt should pin the ratio near the 2.5 clamp, got {ratio}");
        Assert.True(ratio <= 2.5);
    }

    private sealed class UsageReportingClientFactory(TokenUsage usage) : ILlmClientFactory
    {
        public ILlmClient GetClient(string kind) => new Client(usage);

        private sealed class Client(TokenUsage usage) : ILlmClient
        {
            public string Kind => "openai";

            public Task<EmbeddingResult?> EmbedAsync(
                ProviderConfig provider, IReadOnlyList<string> inputs, string? model = null, CancellationToken ct = default)
                => Task.FromResult<EmbeddingResult?>(null);

            public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
                LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
            {
                await Task.Yield();
                yield return new LlmCompleted(new LlmCompletion { Text = "done", Usage = usage });
            }
        }
    }
}
