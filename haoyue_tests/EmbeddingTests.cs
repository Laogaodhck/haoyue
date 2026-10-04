using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Events;
using Haoyue.Runtime.Providers;
using Xunit;

namespace Haoyue.Tests;

public class EmbeddingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "haoyue-embed-tests", Guid.NewGuid().ToString("N"));

    public EmbeddingTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static ProviderConfig OpenAiProvider(params ModelConfig[] models) => new()
    {
        Id = "openai", Kind = "openai", BaseUrl = "https://api.test/v1",
        Models = models.ToList(),
    };

    /// <summary>Records the request (URL + body) so routing and payload can be asserted.</summary>
    private sealed class CapturingHandler(string body) : HttpMessageHandler
    {
        public string? RequestUrl { get; private set; }
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUrl = request.RequestUri?.ToString();
            RequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class CapturingHttpFactory(CapturingHandler handler) : ILlmHttpFactory
    {
        public HttpClient GetClient(ProviderConfig provider) => new(handler);
    }

    // ---------------------------------------------------------------- client layer

    [Fact]
    public async Task OpenAiEmbedAsync_ParsesVectorsRestoringInputOrderByIndex()
    {
        var handler = new CapturingHandler(
            """{"model":"text-embedding-3-small","data":[{"index":1,"embedding":[0.5,0.25]},{"index":0,"embedding":[1.0,2.0]}]}""");
        var client = new OpenAiCompatibleClient(new CapturingHttpFactory(handler));
        var provider = OpenAiProvider();

        var result = await client.EmbedAsync(provider, ["first", "second"], "text-embedding-3-small");

        Assert.NotNull(result);
        Assert.Equal("text-embedding-3-small", result!.Model);
        Assert.Equal(2, result.Vectors.Count);
        Assert.Equal([1.0f, 2.0f], result.Vectors[0]); // index 0 first
        Assert.Equal([0.5f, 0.25f], result.Vectors[1]);
        Assert.Equal("https://api.test/v1/embeddings", handler.RequestUrl);
        var sent = JsonNode.Parse(handler.RequestBody!)!;
        Assert.Equal("text-embedding-3-small", sent["model"]!.GetValue<string>());
        Assert.Equal(["first", "second"], sent["input"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray());
    }

    [Fact]
    public async Task OpenAiEmbedAsync_WithoutModelName_Throws()
    {
        var client = new OpenAiCompatibleClient(new CapturingHttpFactory(new CapturingHandler("{}")));

        var ex = await Assert.ThrowsAsync<LlmException>(
            () => client.EmbedAsync(OpenAiProvider(), ["hello"], model: null));

        Assert.Contains("embedding model", ex.Message);
    }

    [Fact]
    public async Task OpenAiEmbedAsync_ApiError_ThrowsLlmException()
    {
        var handler = new ThrowingStatusHandler(HttpStatusCode.Unauthorized, """{"error":{"message":"bad key"}}""");
        var client = new OpenAiCompatibleClient(new StubHttpFactoryOnly(handler));

        var ex = await Assert.ThrowsAsync<LlmException>(
            () => client.EmbedAsync(OpenAiProvider(), ["hello"], "text-embedding-3-small"));

        Assert.Contains("401", ex.Message);
    }

    [Fact]
    public async Task OpenAiEmbedAsync_EmptyInputs_SkipsHttp()
    {
        var handler = new CapturingHandler("{}");
        var client = new OpenAiCompatibleClient(new CapturingHttpFactory(handler));

        var result = await client.EmbedAsync(OpenAiProvider(), [], "text-embedding-3-small");

        Assert.NotNull(result);
        Assert.Empty(result!.Vectors);
        Assert.Null(handler.RequestUrl); // never touched the network
    }

    [Fact]
    public async Task AnthropicClient_EmbedAsync_DegradesToNull()
    {
        var client = new AnthropicClient(new CapturingHttpFactory(new CapturingHandler("{}")));

        var result = await client.EmbedAsync(OpenAiProvider(), ["hello"], "whatever");

        Assert.Null(result);
    }

    [Fact]
    public async Task LocalClient_EmbedAsync_DegradesToNull()
    {
        var client = new LocalLlmClient(new LocalModelCache());

        var result = await client.EmbedAsync(OpenAiProvider(), ["hello"], "gguf-model");

        Assert.Null(result);
    }

    // ---------------------------------------------------------------- routing layer

    [Fact]
    public async Task ProviderManager_EmbedAsync_RoutesToEmbeddingCapableModel()
    {
        var store = new ConfigStore(
            Path.Combine(_dir, "cfg.json"), Path.Combine(_dir, "state.json"));
        store.Config.Providers.Add(OpenAiProvider(
            new ModelConfig { Id = "gpt-5.5" },
            new ModelConfig { Id = "text-embedding-3-small", Capabilities = new ModelCapabilities { Embedding = true } }));
        var handler = new CapturingHandler(
            """{"model":"text-embedding-3-small","data":[{"index":0,"embedding":[0.1,0.2]}]}""");
        var manager = NewManager(store, handler);

        var result = await manager.EmbedAsync("部署流程是什么");

        Assert.NotNull(result);
        Assert.Equal([0.1f, 0.2f], result!.Vectors.Single());
        // The chat model is NOT reused: the request carries the embedding model id.
        var sent = JsonNode.Parse(handler.RequestBody!)!;
        Assert.Equal("text-embedding-3-small", sent["model"]!.GetValue<string>());
        Assert.Equal(["部署流程是什么"], sent["input"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray());
    }

    [Fact]
    public async Task ProviderManager_EmbedAsync_WithoutEmbeddingModel_ReturnsNull()
    {
        var store = new ConfigStore(
            Path.Combine(_dir, "cfg2.json"), Path.Combine(_dir, "state2.json"));
        store.Config.Providers.Add(OpenAiProvider(new ModelConfig { Id = "gpt-5.5" }));
        var manager = NewManager(store, new CapturingHandler("{}"));

        Assert.Null(await manager.EmbedAsync("hello"));
    }

    private ProviderManager NewManager(ConfigStore store, CapturingHandler handler) => new(
        store,
        new ModelRegistry(store),
        new LlmClientFactory([new OpenAiCompatibleClient(new CapturingHttpFactory(handler))]),
        new CapturingHttpFactory(handler),
        new UsageTracker(new EventBus(), Path.Combine(_dir, "usage.jsonl")),
        new EventBus());

    private sealed class ThrowingStatusHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
    }

    private sealed class StubHttpFactoryOnly(HttpMessageHandler handler) : ILlmHttpFactory
    {
        public HttpClient GetClient(ProviderConfig provider) => new(handler);
    }
}
