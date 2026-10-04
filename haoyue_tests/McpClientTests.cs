using System.Text.Json.Nodes;
using System.Threading.Channels;
using Haoyue.Runtime.Mcp;
using Xunit;

namespace Haoyue.Tests;

/// <summary>
/// In-memory MCP transport: answers every JSON-RPC request through a
/// responder delegate keyed by method. Shared by McpClient and McpManager tests.
/// </summary>
internal sealed class FakeMcpTransport : IMcpTransport
{
    private readonly Channel<JsonObject> _incoming = Channel.CreateUnbounded<JsonObject>();

    public Func<JsonObject, JsonObject> Responder { get; set; } = _ => new JsonObject();

    public ChannelReader<JsonObject> Incoming => _incoming.Reader;

    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    public async Task SendAsync(JsonObject message, CancellationToken ct)
    {
        if (!message.ContainsKey("id")) return; // notifications — nothing to answer

        var body = Responder(message);
        var response = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = message["id"]!.DeepClone(),
        };
        if (body.ContainsKey("error"))
            response["error"] = body["error"]!.DeepClone();
        else
            // DeepClone: responders may hand back shared/static JsonObject
            // literals; attaching the same node to two responses throws
            // "The node already has a parent".
            response["result"] = body.DeepClone();

        await _incoming.Writer.WriteAsync(response, ct).ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        _incoming.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}

public class McpClientResourceTests
{
    private static readonly JsonObject MethodNotFound = new()
    {
        ["error"] = new JsonObject { ["code"] = -32601, ["message"] = "method not found" },
    };

    private static readonly JsonObject InitializeResult = new()
    {
        ["protocolVersion"] = "2024-11-05",
        ["capabilities"] = new JsonObject(),
    };

    private static McpClient CreateClient(Func<JsonObject, JsonObject> responder) =>
        new("demo", new FakeMcpTransport { Responder = responder });

    [Fact]
    public async Task GetResourceAsync_ConcatenatesTextContents()
    {
        var client = CreateClient(_ => new JsonObject
        {
            ["contents"] = new JsonArray(
                new JsonObject { ["uri"] = "demo://a", ["mimeType"] = "text/plain", ["text"] = "first part" },
                new JsonObject { ["uri"] = "demo://a", ["mimeType"] = "text/plain", ["text"] = "second part" }),
        });
        await using var _ = client;

        var text = await client.InitializeThen(x => x.GetResourceAsync("demo://a", CancellationToken.None));

        Assert.Equal("first part\nsecond part", text);
    }

    [Fact]
    public async Task GetResourceAsync_SkipsBlobContents()
    {
        var client = CreateClient(_ => new JsonObject
        {
            ["contents"] = new JsonArray(
                new JsonObject { ["uri"] = "demo://bin", ["mimeType"] = "text/plain", ["text"] = "readme text" },
                new JsonObject { ["uri"] = "demo://bin", ["mimeType"] = "image/png", ["blob"] = "aGVsbG8=" }),
        });
        await using var _ = client;

        var text = await client.InitializeThen(x => x.GetResourceAsync("demo://bin", CancellationToken.None));

        Assert.Equal("readme text", text);
    }

    [Fact]
    public async Task GetResourceAsync_ServerError_DegradesToNull()
    {
        // initialize must succeed; only the resources/read call is rejected.
        var client = CreateClient(message => message["method"]?.GetValue<string>() == "initialize"
            ? InitializeResult
            : MethodNotFound);
        await using var _ = client;

        var text = await client.InitializeThen(x => x.GetResourceAsync("demo://missing", CancellationToken.None));

        Assert.Null(text);
    }

    [Fact]
    public async Task GetResourceAsync_WithoutTextContent_ReturnsNull()
    {
        var client = CreateClient(_ => new JsonObject
        {
            ["contents"] = new JsonArray(
                new JsonObject { ["uri"] = "demo://blob-only", ["blob"] = "aGVsbG8=" }),
        });
        await using var _ = client;

        var text = await client.InitializeThen(x => x.GetResourceAsync("demo://blob-only", CancellationToken.None));

        Assert.Null(text);
    }
}

internal static class McpClientTestExtensions
{
    /// <summary>Initializes the client, runs the action, then returns the result.</summary>
    public static async Task<T> InitializeThen<T>(this McpClient client, Func<McpClient, Task<T>> action)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await client.InitializeAsync(cts.Token).ConfigureAwait(false);
        return await action(client).ConfigureAwait(false);
    }
}
