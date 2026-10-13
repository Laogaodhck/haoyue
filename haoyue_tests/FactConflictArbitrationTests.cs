using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Haoyue.Runtime;
using Haoyue.Runtime.Agents;
using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Data;
using Haoyue.Runtime.Events;
using Haoyue.Runtime.Prompts;
using Haoyue.Runtime.Providers;
using Haoyue.Runtime.Tools;
using Haoyue.Runtime.Tools.Builtin;
using Haoyue.Runtime.Workspaces;

namespace Haoyue.Tests;

/// <summary>
/// M-D 事实冲突仲裁：显式 supersedes 取代、同主题高重合自动仲裁、低置信不取代、
/// 注入侧残余冲突标注。写时解决矛盾，避免模型每回合靠猜。
/// </summary>
public sealed class FactConflictArbitrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "haoyue-tests", "fact-arbitration-" + Guid.NewGuid().ToString("N"));
    private readonly FactsStore _store = new();

    public FactConflictArbitrationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    // ---------------------------------------------------------------- store: 显式取代

    [Fact]
    public void ExplicitSupersedes_ByContent_RetiresOldFact_FromActiveAndSearch()
    {
        var old = _store.Add(_dir, "构建命令是 npm run build", "build", confidence: 0.9);
        var corrected = _store.Add(_dir, "构建命令是 pnpm build", "build", confidence: 0.9, supersedes: old.Content);

        var active = _store.ListActiveForRoot(_dir);
        Assert.Single(active);
        Assert.Equal(corrected.Id, active[0].Id);
        // 被取代的事实对检索同样不可见（旧错误事实不会继续误导检索）。
        Assert.Empty(_store.SearchForRoot(_dir, "npm"));

        // 审计留痕：旧行仍留在文件里，SupersededBy 指向新事实而非直接删除。
        // 注：HaoyueJsonContext 用 camelCase 命名策略。
        var raw = File.ReadAllText(FactsStore.FactsFileForRoot(_dir));
        Assert.Contains("npm run build", raw, StringComparison.OrdinalIgnoreCase);
        var rows = JsonNode.Parse(raw)!.AsArray();
        var retired = rows.Select(r => r!).First(r => r["content"]!.GetValue<string>() == "构建命令是 npm run build");
        Assert.Equal(corrected.Id, retired["supersededBy"]!.GetValue<string>());
    }

    [Fact]
    public void ExplicitSupersedes_ById_RetiresOldFact()
    {
        var old = _store.Add(_dir, "部署目标是自建机房", "deploy", confidence: 0.9);
        _store.Add(_dir, "部署目标是阿里云容器服务", "deploy", confidence: 0.9, supersedes: old.Id);

        var active = _store.ListActiveForRoot(_dir);
        Assert.Single(active);
        Assert.Equal("部署目标是阿里云容器服务", active[0].Content);
    }

    // ---------------------------------------------------------------- store: 自动仲裁

    [Fact]
    public void SameTopicHighOverlap_AutoArbitrates_KeepingNewerFact()
    {
        _store.Add(_dir, "构建使用 pnpm install 安装依赖", "build", confidence: 0.8);
        var corrected = _store.Add(_dir, "构建使用 npm install 安装依赖", "build", confidence: 0.8);

        // 同主题 + 词条重合 0.6 ≥ 0.45 + 置信度不低于旧事实 → 自动顶掉旧说法。
        var active = _store.ListActiveForRoot(_dir);
        Assert.Single(active);
        Assert.Equal(corrected.Id, active[0].Id);
        Assert.Contains("npm install", active[0].Content);
    }

    [Fact]
    public void LowerConfidenceNewFact_DoesNotSupersede_HigherConfidenceOldFact()
    {
        _store.Add(_dir, "构建使用 pnpm install 安装依赖", "build", confidence: 0.95);
        _store.Add(_dir, "构建使用 npm install 安装依赖", "build", confidence: 0.5);

        // 低置信新事实不能顶掉高置信旧事实——两条都存活，留给注入侧标注冲突。
        var active = _store.ListActiveForRoot(_dir);
        Assert.Equal(2, active.Count);
        Assert.All(active, f => Assert.Null(f.SupersededBy));
    }

    [Fact]
    public void DifferentTopic_HighOverlap_IsNotArbitrated()
    {
        _store.Add(_dir, "安装依赖使用 pnpm install", "build", confidence: 0.8);
        _store.Add(_dir, "安装依赖使用 npm install", "convention", confidence: 0.8);

        // 主题不同即视为两条独立事实（哪怕字面重合度达标），不互相取代。
        Assert.Equal(2, _store.ListActiveForRoot(_dir).Count);
    }

    // ---------------------------------------------------------------- tool: 参数透传

    [Fact]
    public async Task FactSaveTool_PassesSupersedesThrough()
    {
        var tool = new FactSaveTool(new FilePromptProvider());
        var context = new ToolContext
        {
            Workspace = new WorkspaceManager().CreateGlobal(_dir),
            Events = new EventBus(),
            Agent = new AgentConfig(),
        };

        var first = await tool.ExecuteAsync(
            new JsonObject { ["content"] = "构建命令是 npm run build", ["topic"] = "build" },
            context, CancellationToken.None);
        Assert.True(first.Success, first.Output);

        var second = await tool.ExecuteAsync(
            new JsonObject
            {
                ["content"] = "构建命令是 pnpm build",
                ["topic"] = "build",
                ["supersedes"] = "构建命令是 npm run build",
            },
            context, CancellationToken.None);
        Assert.True(second.Success, second.Output);

        var active = new FactsStore().ListActiveForRoot(_dir);
        Assert.Single(active);
        Assert.Equal("构建命令是 pnpm build", active[0].Content);
    }

    // ---------------------------------------------------------------- 注入侧: 残余冲突标注

    [Fact]
    public async Task SameTopicResidualConflict_InjectionFlagsLowerPriorityFact()
    {
        var dir = Path.Combine(_dir, "run");
        Directory.CreateDirectory(dir);
        try
        {
            var store = new ConfigStore(Path.Combine(dir, "config.json"), Path.Combine(dir, "state.json"));
            store.Config.Providers.Clear();
            store.Config.Providers.Add(new ProviderConfig
            {
                Id = "openai",
                Kind = "openai",
                BaseUrl = "https://test.local/v1",
                Models =
                [
                    new ModelConfig
                    {
                        Id = "m",
                        ContextWindow = 32_000,
                        MaxOutput = 4_096,
                        Capabilities = new ModelCapabilities { ToolCalling = true },
                    },
                ],
            });
            store.Config.Provider = "openai";
            store.Config.Model = "m";
            store.Config.Routing.Fallback = ["openai/m"];

            var capture = new FactRecordingClientFactory();
            var globalWorkspace = new WorkspaceManager().CreateGlobal(Path.Combine(dir, "global"));
            await using var runtime = HaoyueRuntime.CreateIsolated(globalWorkspace, configureServices: services =>
            {
                services.AddSingleton<IConfigStore>(store);
                services.AddSingleton(new HaoyueDatabase(Path.Combine(dir, "state.db")));
                services.AddSingleton<ILlmHttpFactory>(new LlmHttpFactory());
                services.AddSingleton<ILlmClientFactory>(capture);
                services.AddSingleton(new CircuitBreaker(store.Config.Routing.Retry));
            });

            // 同主题但词条重合低（自动仲裁不接管）→ 两条都存活；注入侧按置信度排序，
            // 低优先级那条显式标注冲突与采信顺序，模型不再靠猜。
            new FactsStore().Add(globalWorkspace.Root, "前端技术栈是 Vue 3 组合式 API", "stack", confidence: 0.9);
            new FactsStore().Add(globalWorkspace.Root, "后端技术栈是 Spring Boot 3 与 GraphQL", "stack", confidence: 0.6);

            var session = runtime.Sessions.Create(globalWorkspace);
            var result = await runtime.Agent.RunTurnAsync(session, globalWorkspace, "你好", CancellationToken.None);
            Assert.Null(result.Error);

            var system = capture.Requests.Select(r => r.System).Last(s => s is not null)!;
            Assert.Contains("前端技术栈是 Vue 3 组合式 API", system);
            Assert.Contains("后端技术栈是 Spring Boot 3 与 GraphQL", system);

            var flagged = system.Split('\n').Where(l => l.Contains("⚠ 与同主题更高优先级的事实冲突")).ToList();
            Assert.Single(flagged);
            // 冲突标注落在低优先级（低置信）那条上，且同时带「待确认」。
            Assert.Contains("后端技术栈", flagged[0]);
            Assert.Contains("待确认", flagged[0]);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    private sealed class FactRecordingClientFactory : ILlmClientFactory
    {
        public List<LlmRequest> Requests { get; } = [];

        public ILlmClient GetClient(string kind) => new RecordingClient(this);

        private sealed class RecordingClient(FactRecordingClientFactory owner) : ILlmClient
        {
            public string Kind => "openai";

            public Task<EmbeddingResult?> EmbedAsync(
                ProviderConfig provider, IReadOnlyList<string> inputs, string? model = null, CancellationToken ct = default)
                => Task.FromResult<EmbeddingResult?>(null);

            public async IAsyncEnumerable<LlmStreamEvent> StreamAsync(
                LlmRequest request, [EnumeratorCancellation] CancellationToken ct)
            {
                owner.Requests.Add(request);
                await Task.Yield();
                yield return new LlmCompleted(new LlmCompletion { Text = "ok" });
            }
        }
    }
}
