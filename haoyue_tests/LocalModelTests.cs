using System.Text;
using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Providers;

namespace Haoyue.Tests;

public sealed class LocalModelTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "haoyue-local-tests", Guid.NewGuid().ToString("N"));

    public LocalModelTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch (IOException) { }
    }

    [Fact]
    public void ResolveDirectory_PrefersConfiguredValueThenEnvironment()
    {
        Assert.Equal(Path.GetFullPath(_dir), LocalModels.ResolveDirectory(_dir));

        var fromEnv = Path.Combine(_dir, "from-env");
        Environment.SetEnvironmentVariable(LocalModels.DirectoryEnvVar, fromEnv);
        try
        {
            Assert.Equal(Path.GetFullPath(fromEnv), LocalModels.ResolveDirectory(null));
        }
        finally
        {
            Environment.SetEnvironmentVariable(LocalModels.DirectoryEnvVar, null);
        }
    }

    [Fact]
    public void ResolveModelPath_UsesExplicitPathThenModelsDirectory()
    {
        var outside = WriteGguf("outside.gguf");
        var provider = new ProviderConfig { Id = "local", Kind = "local", ModelsDirectory = _dir };

        Assert.Equal(outside, LocalModels.ResolveModelPath(provider,
            new ModelConfig { Id = "ignored", LocalPath = outside }));
        // A file name registered without its extension still resolves inside the directory.
        File.WriteAllBytes(Path.Combine(_dir, "qwen.gguf"), new byte[] { 1 });
        Assert.Equal(Path.Combine(_dir, "qwen.gguf"),
            LocalModels.ResolveModelPath(provider, new ModelConfig { Id = "qwen" }));
    }

    [Fact]
    public void Scan_ReadsNamesAndContextFromTheGgufHeader()
    {
        WriteGguf("plain.gguf");
        var models = LocalModelProbe.Scan(_dir);

        var match = Assert.Single(models);
        Assert.Equal("plain.gguf", match.ModelId);
        Assert.Equal("Tiny Test Model", match.DisplayName);
        Assert.Equal("qwen3", match.Architecture);
        Assert.Equal(131_072L, match.TrainedContext);
        // The registered window stays below the trained one so a CPU run does not have to
        // allocate a KV cache for the full 128k.
        Assert.Equal(LocalModelProbe.DefaultContextWindow, match.ContextWindow);
    }

    [Fact]
    public void Scan_FallsBackToFileStemWhenHeaderIsUnreadable()
    {
        File.WriteAllText(Path.Combine(_dir, "broken.gguf"), "not a gguf file at all");

        var model = Assert.Single(LocalModelProbe.Scan(_dir));
        Assert.Equal("broken", model.DisplayName);
        Assert.Null(model.Architecture);
        Assert.Null(model.TrainedContext);
        Assert.Equal(8192, model.ContextWindow);
    }

    [Fact]
    public void Scan_SkipsMultimodalProjectorFiles()
    {
        WriteGguf("plain.gguf");
        WriteGguf("model-mmproj-F16.gguf");

        var models = LocalModelProbe.Scan(_dir);

        // 投影器文件不能作为聊天模型加载，扫描注册不得把它列入模型。
        var model = Assert.Single(models);
        Assert.Equal("plain.gguf", model.ModelId);
    }

    [Fact]
    public void ScanIds_SkipsProjectorFiles()
    {
        WriteGguf("chat-model.gguf");
        WriteGguf("chat-model-mmproj-F16.gguf");

        Assert.Equal(["chat-model.gguf"], LocalModels.ScanIds(_dir));
    }

    [Fact]
    public void Registry_HidesLocalProjectorEntriesFromCatalogAndResolve()
    {
        var store = new ConfigStore(
            Path.Combine(_dir, "registry-config.json"), Path.Combine(_dir, "registry-state.json"));
        store.Config.Providers.Clear();
        store.Config.Providers.Add(new ProviderConfig
        {
            Id = "local", Kind = "local", ModelsDirectory = _dir,
            Models =
            [
                new ModelConfig { Id = "chat.gguf", LocalPath = Path.Combine(_dir, "chat.gguf") },
                new ModelConfig { Id = "mmproj.gguf", LocalPath = Path.Combine(_dir, "mmproj.gguf") },
            ],
        });
        var registry = new ModelRegistry(store);

        // 遗留配置里误注册的投影器条目不再进入模型目录，也不可被解析切换。
        Assert.DoesNotContain(registry.All(), m => m.Model.Id == "mmproj.gguf");
        Assert.Contains(registry.All(), m => m.Model.Id == "chat.gguf");
        Assert.Null(registry.Resolve("local/mmproj.gguf"));
    }

    [Fact]
    public async Task StreamAsync_RejectsProjectorFileWithClearError()
    {
        var projector = WriteGguf("vision-mmproj.gguf");
        var provider = new ProviderConfig { Id = "local", Kind = "local", ModelsDirectory = _dir };
        var client = new LocalLlmClient(new LocalModelCache());

        var error = await Assert.ThrowsAsync<LlmException>(() => DrainAsync(client, provider,
            new ModelConfig { Id = "vision-mmproj.gguf", LocalPath = projector }));
        Assert.Contains("投影器", error.Message);
    }

    [Fact]
    public void ToConfig_AdvertisesLocalModelsWithoutToolCalling()
    {
        WriteGguf("registered.gguf");
        var config = LocalModelProbe.ToConfig(LocalModelProbe.Scan(_dir).First());

        Assert.EndsWith(".gguf", config.Id);
        Assert.True(config.Capabilities.Streaming);
        Assert.False(config.Capabilities.ToolCalling);
        Assert.True(File.Exists(LocalModels.ResolveModelPath(
            new ProviderConfig { Id = "local", Kind = "local" }, config)));
    }

    [Theory]
    // The R1 template injects the opening tag, so generation starts inside the reasoning.
    [InlineData(true, "思考", "</think>\n\n回答", "思考", "回答")]
    // The template's generation prefix puts the parser inside the reasoning, so a close tag
    // that arrives split across two chunks must not lose its tail.
    [InlineData(true, "思考\n</thi", "nk>\n\n回答", "思考\n", "回答")]
    // No tags at all: everything stays in the mode the template implied.
    [InlineData(true, "只有推理", "", "只有推理", "")]
    [InlineData(false, "只有回答", "", "", "只有回答")]
    public void ThinkTagParser_SplitsReasoningFromAnswer(
        bool startsInThinking, string first, string second, string expectedThinking, string expectedAnswer)
    {
        var parser = new ThinkTagParser(startsInThinking);
        var pieces = parser.Process(first).Concat(parser.Process(second)).Concat(parser.Flush()).ToList();

        Assert.Equal(expectedThinking, string.Concat(pieces.Where(p => p.IsThinking).Select(p => p.Text)));
        Assert.Equal(expectedAnswer, string.Concat(pieces.Where(p => !p.IsThinking).Select(p => p.Text)));
    }

    [Fact]
    public async Task StreamAsync_ReportsMissingFileInsteadOfLoading()
    {
        var provider = new ProviderConfig { Id = "local", Kind = "local", ModelsDirectory = _dir };
        var client = new LocalLlmClient(new LocalModelCache());

        var error = await Assert.ThrowsAsync<LlmException>(() => DrainAsync(client, provider,
            new ModelConfig { Id = "absent.gguf" }));
        Assert.Contains("absent.gguf", error.Message);
    }

    /// <summary>
    /// Runs one real generation on a local GGUF file. It is opt-in because loading a
    /// multi-gigabyte model takes minutes and CI has no model files.
    /// Set HAOYUE_LOCAL_MODEL to the GGUF path (otherwise the first file of the default
    /// models directory is used).
    /// </summary>
    [Fact]
    public async Task StreamAsync_GeneratesTextFromAGgufModel()
    {
        if (Environment.GetEnvironmentVariable("HAOYUE_LOCAL_MODEL_SMOKE") != "1")
            return;

        var configured = Environment.GetEnvironmentVariable("HAOYUE_LOCAL_MODEL");
        var path = !string.IsNullOrWhiteSpace(configured)
            ? Path.GetFullPath(configured)
            : LocalModelProbe.Scan().FirstOrDefault()?.Path;
        if (path is null || !File.Exists(path))
            return;

        var provider = new ProviderConfig
        {
            Id = "local",
            Kind = "local",
            ModelsDirectory = Path.GetDirectoryName(path),
        };
        // Set HAOYUE_LOCAL_MODEL_GPU_LAYERS (e.g. 99) to offload layers to the GPU
        // (requires the CUDA backend build: dotnet build -p:LlamaBackend=Cuda12).
        if (int.TryParse(Environment.GetEnvironmentVariable("HAOYUE_LOCAL_MODEL_GPU_LAYERS"), out var gpuLayers) && gpuLayers > 0)
            provider.GpuLayers = gpuLayers;
        var model = new ModelConfig
        {
            Id = Path.GetFileName(path),
            LocalPath = path,
            ContextWindow = 8192,
            MaxOutput = 64,
        };
        var client = new LocalLlmClient(new LocalModelCache());
        var request = new LlmRequest
        {
            Provider = provider,
            Model = model,
            Messages = [ChatMessage.User("Reply with exactly one short greeting in Chinese.")],
            MaxTokens = 24,
        };

        var text = new StringBuilder();
        var thinking = new StringBuilder();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await foreach (var evt in client.StreamAsync(request, CancellationToken.None))
        {
            // A small token budget is spent entirely inside R1-style reasoning, so any
            // streamed token (thinking or answer) proves the native pipeline works.
            if (evt is LlmTextDelta delta)
                text.Append(delta.Text);
            if (evt is LlmThinkingDelta think)
                thinking.Append(think.Text);
            if (evt is LlmCompleted done)
                Assert.True(done.Completion.Usage.InputTokens > 0);
        }
        watch.Stop();
        Console.WriteLine($"[local-smoke] first generation: {watch.ElapsedMilliseconds} ms, text={text} thinking={thinking}");

        Assert.True(
            text.Length > 0 || thinking.Length > 0,
            "Local inference produced no streamed tokens (neither thinking nor answer).");
        if (text.Length > 0)
            Assert.NotEmpty(text.ToString().Trim());

        // Second generation extends the first conversation — the exact agent-loop shape.
        // With KV prefix reuse this must decode only the new suffix and still produce text.
        var firstAnswer = text.ToString();
        var followUp = new LlmRequest
        {
            Provider = provider,
            Model = model,
            Messages =
            [
                .. request.Messages,
                ChatMessage.Assistant(firstAnswer),
                ChatMessage.User("Now reply with exactly one short farewell in Chinese."),
            ],
            MaxTokens = 24,
        };

        var followUpText = new StringBuilder();
        var followUpThinking = new StringBuilder();
        watch.Restart();
        await foreach (var evt in client.StreamAsync(followUp, CancellationToken.None))
        {
            if (evt is LlmTextDelta delta)
                followUpText.Append(delta.Text);
            if (evt is LlmThinkingDelta think)
                followUpThinking.Append(think.Text);
        }
        watch.Stop();
        Console.WriteLine($"[local-smoke] second generation: {watch.ElapsedMilliseconds} ms, text={followUpText}");

        Assert.True(
            followUpText.Length > 0 || followUpThinking.Length > 0,
            "The follow-up generation (KV prefix reuse path) produced no streamed tokens.");
    }

    [Theory]
    // 模型把全部预算耗在推理里、从未输出结束标记：推理内容整体提升为回答，下游不会收到空回复。
    [InlineData("", "  \n逐步分析……仍未完成", "逐步分析……仍未完成", "")]
    // 正常回答：原样保留，仅去掉尾部空白，避免污染会话历史里下一轮的模板渲染。
    [InlineData("回答正文\n\n", "推理", "回答正文", "推理")]
    // 回答与推理都为空（模型立即输出 EOS）：不产生兜底文本。
    [InlineData("", "", "", "")]
    public void FinalizeOutput_PromotesUnfinishedThinkingAndTrimsTail(
        string answer, string thinking, string expectedAnswer, string expectedThinking)
    {
        var (finalAnswer, finalThinking) = LocalLlmClient.FinalizeOutput(answer, thinking);

        Assert.Equal(expectedAnswer, finalAnswer);
        Assert.Equal(expectedThinking, finalThinking);
    }

    private static async Task DrainAsync(LocalLlmClient client, ProviderConfig provider, ModelConfig model)
    {
        await foreach (var _ in client.StreamAsync(new LlmRequest
        {
            Provider = provider,
            Model = model,
            Messages = [ChatMessage.User("hi")],
        }, CancellationToken.None))
        {
        }
    }

    private string WriteGguf(string fileName)
    {
        var bytes = GgufFixture.Create(
            ("general.architecture", "qwen3"),
            ("general.name", "Tiny Test Model"),
            ("qwen3.context_length", 131_072u));
        var path = Path.Combine(_dir, fileName);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    /// <summary>Builds the smallest GGUF header a local model scan has to understand.</summary>
    private static class GgufFixture
    {
        public static byte[] Create(params (string Key, object Value)[] entries)
        {
            using var stream = new MemoryStream();
            using var writer = new BinaryWriter(stream, Encoding.UTF8);
            writer.Write(0x46554747u); // "GGUF"
            writer.Write(3u);           // version
            writer.Write(0UL);          // tensor count
            writer.Write((ulong)entries.Length);
            foreach (var (key, value) in entries)
            {
                WriteString(writer, key);
                switch (value)
                {
                    case string text:
                        writer.Write(8u);
                        WriteString(writer, text);
                        break;
                    case uint number:
                        writer.Write(4u);
                        writer.Write(number);
                        break;
                }
            }
            writer.Flush();
            return stream.ToArray();
        }

        private static void WriteString(BinaryWriter writer, string value)
        {
            var bytes = Encoding.UTF8.GetBytes(value);
            writer.Write((ulong)bytes.Length);
            writer.Write(bytes);
        }
    }

    /// <summary>
    /// Gemma 4 的官方聊天模板使用 llama.cpp 内置 Jinja 尚不支持的语法，apply 失败后
    /// 兜底渲染器必须产出与 canonical 格式逐字符一致的提示词。
    /// </summary>
    public sealed class GemmaTurnPromptTests
    {
        [Fact]
        public void RendersSystemUserAndModelTurns()
        {
            var prompt = LocalLlmClient.RenderGemmaTurnPrompt(
            [
                ChatMessage.User("第一问"),
                ChatMessage.Assistant("第一答"),
                ChatMessage.User("第二问"),
            ], system: "你是助手", enableThinking: false);

            Assert.Equal(
                "<|turn>system\n你是助手<turn|>\n" +
                "<|turn>user\n第一问<turn|>\n" +
                "<|turn>model\n第一答<turn|>\n" +
                "<|turn>user\n第二问<turn|>\n" +
                "<|turn>model\n",
                prompt);
        }

        [Fact]
        public void OmitsSystemTurnWhenNoSystemAndThinkingDisabled()
        {
            var prompt = LocalLlmClient.RenderGemmaTurnPrompt(
                [ChatMessage.User("你好")], system: null, enableThinking: false);

            Assert.Equal("<|turn>user\n你好<turn|>\n<|turn>model\n", prompt);
        }

        [Fact]
        public void ThinkingTokenGoesInsideTheSystemTurn()
        {
            var prompt = LocalLlmClient.RenderGemmaTurnPrompt(
                [ChatMessage.User("你好")], system: "规则", enableThinking: true);

            Assert.Equal("<|turn>system\n<|think|>\n规则<turn|>\n<|turn>user\n你好<turn|>\n<|turn>model\n", prompt);
        }

        [Fact]
        public void ToolResultsAreReplayedAsUserTurns()
        {
            var prompt = LocalLlmClient.RenderGemmaTurnPrompt(
                [ChatMessage.ToolResult("t1", "grep", "结果文本", success: true)], system: null, enableThinking: false);

            Assert.Equal("<|turn>user\n[grep result]\n结果文本<turn|>\n<|turn>model\n", prompt);
        }
    }
}
