using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Haoyue.Runtime;
using Haoyue.Runtime.Agents;
using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Data;
using Haoyue.Runtime.Providers;
using Haoyue.Runtime.Workspaces;

namespace Haoyue.Tests;

public sealed class ContextCompactionTests
{
    private static string TempDir() => Path.Combine(
        Path.GetTempPath(), "haoyue-compaction-tests", Guid.NewGuid().ToString("N"));

    // ---------------------------------------------------------------- split logic

    [Fact]
    public void SplitForCompaction_SplitsOversizedHistoryIntoOldAndRecent()
    {
        var model = new ModelConfig { Id = "m", ContextWindow = 2_000, MaxOutput = 256 };
        var messages = new List<ChatMessage>();
        for (var i = 0; i < 40; i++)
            messages.Add(ChatMessage.User($"Message {i}: " + new string('x', 80)));

        var (old, recent) = ContextPlanner.SplitForCompaction(messages, model, "x");

        Assert.True(old.Count > 0, "expected an old part to summarize");
        Assert.True(recent.Count > 0, "expected a recent tail to keep");
        Assert.Equal(messages.Count, old.Count + recent.Count);
        Assert.Equal(messages, old.Concat(recent).ToList());
        Assert.NotEqual(ChatRole.Tool, recent[0].Role);
    }

    [Fact]
    public void SplitForCompaction_NeverOpensRecentTailWithAnOrphanedToolResult()
    {
        var model = new ModelConfig { Id = "m", ContextWindow = 2_000, MaxOutput = 256 };
        var messages = new List<ChatMessage>();
        for (var i = 0; i < 25; i++)
        {
            messages.Add(ChatMessage.User($"User {i}: " + new string('x', 60)));
            messages.Add(new ChatMessage
            {
                Role = ChatRole.Assistant,
                ToolCalls = [new ToolCallRequest($"c{i}", "read_file", "{}")],
            });
            messages.Add(ChatMessage.ToolResult($"c{i}", "read_file", new string('y', 60), true));
        }

        var (old, recent) = ContextPlanner.SplitForCompaction(messages, model, "x");

        Assert.Equal(messages.Count, old.Count + recent.Count);
        Assert.Equal(messages, old.Concat(recent).ToList());
        // The tail must never open with an orphaned tool result; leading results are
        // folded back into the summarized old part where their assistant turn lives.
        Assert.NotEqual(ChatRole.Tool, recent[0].Role);
    }

    [Fact]
    public void SplitForCompaction_TailRatio_ControlsRetainedShare()
    {
        var model = new ModelConfig { Id = "m", ContextWindow = 4_000, MaxOutput = 256 };
        var messages = new List<ChatMessage>();
        for (var i = 0; i < 40; i++)
            messages.Add(ChatMessage.User($"Message {i}: " + new string('x', 400)));

        var (oldSmall, smallTail) = ContextPlanner.SplitForCompaction(messages, model, "x", tailRatio: 0.2);
        var (oldLarge, largeTail) = ContextPlanner.SplitForCompaction(messages, model, "x", tailRatio: 0.8);

        Assert.True(largeTail.Count > smallTail.Count,
            $"expected the larger ratio to keep more verbatim history, got {largeTail.Count} vs {smallTail.Count}");
        Assert.True(oldLarge.Count < oldSmall.Count, "the larger tail must leave less to summarize");
    }

    [Fact]
    public void SplitForCompaction_TailRatio_IsClampedToSaneRange()
    {
        var model = new ModelConfig { Id = "m", ContextWindow = 4_000, MaxOutput = 256 };
        var messages = new List<ChatMessage>();
        for (var i = 0; i < 40; i++)
            messages.Add(ChatMessage.User($"Message {i}: " + new string('x', 400)));

        // 退化配置（0 / 1 / 负数 / NaN）不得把整段历史判成「全旧」或「全新」——
        // 任一端为空都会让压缩或保留失去意义。
        foreach (var ratio in new[] { 0.0, -1.0, 1.0, 5.0, double.NaN })
        {
            var (old, recent) = ContextPlanner.SplitForCompaction(messages, model, "x", tailRatio: ratio);
            Assert.True(old.Count > 0, $"ratio {ratio} produced no old part");
            Assert.True(recent.Count > 0, $"ratio {ratio} produced no recent tail");
            Assert.Equal(messages.Count, old.Count + recent.Count);
        }
    }

    [Fact]
    public void SummaryConverged_RequiresSummarySmallerThanMaskedHistory()
    {
        var masked = new List<ChatMessage>();
        for (var i = 0; i < 20; i++)
            masked.Add(ChatMessage.User($"Message {i}: " + new string('x', 200)));

        Assert.True(ContextPlanner.SummaryConverged("完成了 X，下一步做 Y。", masked));
        Assert.False(ContextPlanner.SummaryConverged(new string('x', 20_000), masked));
    }

    [Fact]
    public void SummaryConverged_CountsReplacementMessageOverhead()
    {
        // 摘要本身比原文小，但加上压缩包装文案后净收益消失——这种「纸面上收敛」不该
        // 被当成有效压缩（否则下一回合仍然超限，只是换了个地方空烧 token）。
        var masked = new List<ChatMessage> { ChatMessage.User(new string('x', 400)) }; // ≈100 tok + 8
        var summary = new string('x', 360); // ≈90 tok

        Assert.True(ContextPlanner.SummaryConverged(summary, masked));
        Assert.False(ContextPlanner.SummaryConverged(summary, masked, replacementOverheadTokens: 32));
    }

    // ---------------------------------------------------------------- agent turn

    [Fact]
    public async Task AgentTurn_CompactsOversizedHistory_AndKeepsRunning()
    {
        var dir = TempDir();
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
                        Id = "small",
                        ContextWindow = 2_000,
                        MaxOutput = 256,
                        Capabilities = new ModelCapabilities { ToolCalling = true },
                    },
                ],
            });
            store.Config.Provider = "openai";
            store.Config.Model = "small";
            store.Config.Routing.Fallback = ["openai/small"];

            var capture = new RecordingClientFactory();
            var globalWorkspace = new WorkspaceManager().CreateGlobal(Path.Combine(dir, "global"));
            await using var runtime = HaoyueRuntime.CreateIsolated(globalWorkspace, configureServices: services =>
            {
                services.AddSingleton<IConfigStore>(store);
                services.AddSingleton(new HaoyueDatabase(Path.Combine(dir, "state.db")));
                services.AddSingleton<ILlmHttpFactory>(new LlmHttpFactory());
                services.AddSingleton<ILlmClientFactory>(capture);
                services.AddSingleton(new CircuitBreaker(store.Config.Routing.Retry));
            });

            var session = runtime.Sessions.Create(globalWorkspace);
            for (var i = 0; i < 30; i++)
                runtime.Sessions.Append(session, ChatMessage.User($"Message {i}: " + new string('x', 200)));

            var result = await runtime.Agent.RunTurnAsync(session, globalWorkspace, "continue", CancellationToken.None);

            Assert.Null(result.Error);

            // A compaction summary request ran before the real turn request.
            var summaryRequest = capture.Requests[0];
            var turnRequest = capture.Requests[1];
            // 前缀复用：压缩调用与主回合共用同一 system 提示词（提示词前缀逐 token 一致，
            // 可命中提供商缓存），摘要指令作为最后一条 user 消息追加而非独立 system。
            Assert.NotNull(summaryRequest.System);
            Assert.Equal(turnRequest.System, summaryRequest.System);
            Assert.Contains("memory compaction", summaryRequest.Messages[^1].Text);
            Assert.Empty(summaryRequest.Tools);

            // The session history was replaced by the summary plus a recent tail.
            Assert.Contains("[Context compaction]", session.Messages[0].Text);
            Assert.Equal("continue", session.Messages[^2].Text);
            Assert.True(session.Messages.Count < 32, $"expected compaction, got {session.Messages.Count} messages");

            // The persisted history matches the compacted in-memory view.
            var reloaded = runtime.Sessions.Load(globalWorkspace, session.Header.Id);
            Assert.NotNull(reloaded);
            Assert.Equal(session.Messages.Count, reloaded!.Messages.Count);
            Assert.Equal(session.Messages[0].Text, reloaded.Messages[0].Text);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task AgentTurn_CompactionFailure_FallsBackToTrim_AndStillFinishes()
    {
        var dir = TempDir();
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
                        Id = "small",
                        ContextWindow = 2_000,
                        MaxOutput = 256,
                        Capabilities = new ModelCapabilities { ToolCalling = true },
                    },
                ],
            });
            store.Config.Provider = "openai";
            store.Config.Model = "small";
            store.Config.Routing.Fallback = ["openai/small"];

            var capture = new FailingCompactionClientFactory();
            var globalWorkspace = new WorkspaceManager().CreateGlobal(Path.Combine(dir, "global"));
            await using var runtime = HaoyueRuntime.CreateIsolated(globalWorkspace, configureServices: services =>
            {
                services.AddSingleton<IConfigStore>(store);
                services.AddSingleton(new HaoyueDatabase(Path.Combine(dir, "state.db")));
                services.AddSingleton<ILlmHttpFactory>(new LlmHttpFactory());
                services.AddSingleton<ILlmClientFactory>(capture);
                services.AddSingleton(new CircuitBreaker(store.Config.Routing.Retry));
            });

            var session = runtime.Sessions.Create(globalWorkspace);
            for (var i = 0; i < 30; i++)
                runtime.Sessions.Append(session, ChatMessage.User($"Message {i}: " + new string('x', 200)));

            var result = await runtime.Agent.RunTurnAsync(session, globalWorkspace, "continue", CancellationToken.None);

            // The turn must complete even though the compaction summary call failed.
            Assert.Null(result.Error);
            Assert.Equal(2, capture.Requests.Count); // failed summary + main turn
            Assert.DoesNotContain("[Context compaction]", session.Messages[0].Text);
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task AgentTurn_TruncatedOutput_AutoContinuesWithoutUserPrompt()
    {
        var dir = TempDir();
        Directory.CreateDirectory(dir);
        try
        {
            var store = new ConfigStore(Path.Combine(dir, "config.json"), Path.Combine(dir, "state.json"));
            store.Config.Providers.Clear();
            store.Config.Providers.Add(new ProviderConfig
            {
                Id = "deepseek",
                Kind = "openai",
                BaseUrl = "https://api.deepseek.com",
                Models =
                [
                    new ModelConfig
                    {
                        Id = "reasoner",
                        ContextWindow = 2_000,
                        MaxOutput = 256,
                        Capabilities = new ModelCapabilities { ToolCalling = true },
                    },
                ],
            });
            store.Config.Provider = "deepseek";
            store.Config.Model = "reasoner";
            store.Config.Routing.Fallback = ["deepseek/reasoner"];

            var capture = new SequenceClientFactory(
                new LlmCompletion { Text = "", Thinking = "long thinking that exceeded max output", FinishReason = "length" },
                new LlmCompletion { Text = "finished answer", FinishReason = "stop" });
            var globalWorkspace = new WorkspaceManager().CreateGlobal(Path.Combine(dir, "global"));
            await using var runtime = HaoyueRuntime.CreateIsolated(globalWorkspace, configureServices: services =>
            {
                services.AddSingleton<IConfigStore>(store);
                services.AddSingleton(new HaoyueDatabase(Path.Combine(dir, "state.db")));
                services.AddSingleton<ILlmHttpFactory>(new LlmHttpFactory());
                services.AddSingleton<ILlmClientFactory>(capture);
                services.AddSingleton(new CircuitBreaker(store.Config.Routing.Retry));
            });

            var session = runtime.Sessions.Create(globalWorkspace);
            var result = await runtime.Agent.RunTurnAsync(session, globalWorkspace, "build the game", CancellationToken.None);

            // The truncated "thinking-only" completion must not end the turn; the agent
            // continues automatically and finishes once the model produces real output.
            Assert.Null(result.Error);
            Assert.Equal("finished answer", result.Text);
            Assert.Equal(2, capture.Requests.Count);
            Assert.Contains(session.Messages, message =>
                message.Role == ChatRole.User && message.Text.Contains("[output truncated]"));
        }
        finally
        {
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    // ---------------------------------------------------------------- helpers

    private sealed class SequenceClientFactory(params LlmCompletion[] completions) : ILlmClientFactory
    {
        public List<LlmRequest> Requests { get; } = [];
        private readonly Queue<LlmCompletion> _completions = new(completions);

        public ILlmClient GetClient(string kind) => new SequenceClient(this);

        private sealed class SequenceClient(SequenceClientFactory owner) : ILlmClient
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
                var next = owner._completions.Count > 0
                    ? owner._completions.Dequeue()
                    : new LlmCompletion { Text = "done", FinishReason = "stop" };
                yield return new LlmCompleted(next);
            }
        }
    }

    private sealed class RecordingClientFactory : ILlmClientFactory
    {
        public List<LlmRequest> Requests { get; } = [];

        public ILlmClient GetClient(string kind) => new RecordingClient(this);

        private sealed class RecordingClient(RecordingClientFactory owner) : ILlmClient
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
                yield return new LlmCompleted(new LlmCompletion { Text = "summary-or-answer" });
            }
        }
    }

    private sealed class FailingCompactionClientFactory : ILlmClientFactory
    {
        public List<LlmRequest> Requests { get; } = [];

        public ILlmClient GetClient(string kind) => new FailingClient(this);

        private sealed class FailingClient(FailingCompactionClientFactory owner) : ILlmClient
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
                // Fail only the compaction summary call (no tools); the turn call succeeds.
                if (request.Tools.Count == 0)
                    throw new LlmException("simulated compaction failure", retryable: false);
                yield return new LlmCompleted(new LlmCompletion { Text = "done" });
            }
        }
    }
}
