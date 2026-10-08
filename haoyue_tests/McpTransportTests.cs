using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Haoyue.Runtime.Mcp;
using Xunit;

namespace Haoyue.Tests;

public class McpTransportTests
{
    [Fact]
    public async Task StdioTransport_ResolvesExeShimsOnPath()
    {
        if (!OperatingSystem.IsWindows()) return;

        var resolved = StdioMcpTransport.ResolveOnPath("dotnet");
        Assert.NotNull(resolved);
        Assert.EndsWith(".exe", resolved, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StdioTransport_StartsBatchShimsLikeNpxOnWindows()
    {
        if (!OperatingSystem.IsWindows()) return;

        // `npx` ships as npx.cmd on Windows; CreateProcess cannot start batch
        // shims directly, so the transport must wrap them with cmd.exe.
        var resolved = StdioMcpTransport.ResolveOnPath("npx");
        Assert.NotNull(resolved);

        await using var transport = new StdioMcpTransport("npx", ["--version"], null);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await transport.StartAsync(cts.Token);
    }

    [Fact]
    public async Task StdioTransport_UnknownBareCommandFailsWithFriendlyHint()
    {
        if (!OperatingSystem.IsWindows()) return;

        var transport = new StdioMcpTransport("haoyue-no-such-tool-xyz", [], null);
        var ex = await Assert.ThrowsAsync<McpException>(
            () => transport.StartAsync(CancellationToken.None));
        Assert.Contains("haoyue-no-such-tool-xyz", ex.Message);
        Assert.Contains("PATH", ex.Message);
    }

    [Fact]
    public async Task StdioTransport_RunsBatchShimFromPathWithSpaces()
    {
        if (!OperatingSystem.IsWindows()) return;

        // A .cmd shim under a spaced directory (the common "C:\Program Files" case)
        // must survive the cmd.exe /s /c wrapper and keep the JSON-RPC pipe working.
        var dir = Path.Combine(Path.GetTempPath(), $"haoyue mcp {Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var script = Path.Combine(dir, "fake-mcp.cmd");
        var lines = string.Join("\r\n",
            "@echo off",
            ":loop",
            "set \"REQ=\"",
            "set /p REQ=",
            "if not defined REQ exit /b 0",
            @"echo {""jsonrpc"":""2.0"",""id"":1,""result"":{""protocolVersion"":""2024-11-05"",""capabilities"":{},""serverInfo"":{""name"":""fake-mcp"",""version"":""1.0""}}}",
            "goto loop",
            "");
        File.WriteAllText(script, lines);

        try
        {
            await using var transport = new StdioMcpTransport(script, [], null);
            await using var client = new McpClient("fake-mcp", transport);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            await client.InitializeAsync(cts.Token);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public async Task StdioTransport_UnexpectedChildExitSurfacesStderrDetail()
    {
        if (!OperatingSystem.IsWindows()) return;

        var script = Path.Combine(Path.GetTempPath(), $"haoyue-mcp-exit-{Guid.NewGuid():N}.cmd");
        var lines = string.Join("\r\n",
            "@echo off",
            "ping -n 2 127.0.0.1 >nul",
            "echo boom 1>&2",
            "exit /b 7",
            "");
        File.WriteAllText(script, lines);

        try
        {
            await using var transport = new StdioMcpTransport(script, [], null);
            await using var client = new McpClient("exit-probe", transport);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var ex = await Assert.ThrowsAsync<McpException>(() => client.InitializeAsync(cts.Token));
            Assert.Contains("exit code 7", ex.Message);
            Assert.Contains("boom", ex.Message);
        }
        finally
        {
            try { File.Delete(script); } catch { }
        }
    }

    [Fact]
    public async Task StdioTransport_SendsBomlessUtf8ToChild()
    {
        // Encoding.UTF8 as StandardInputEncoding makes StreamWriter emit a BOM
        // before the first line; Node-based servers fail JSON.parse on
        // "\uFEFF{...}" and silently drop every request. The child below exits
        // with code 3 exactly when the first line is not parseable JSON.
        if (OperatingSystem.IsWindows() && StdioMcpTransport.ResolveOnPath("node") is null) return;

        const string script =
            "const rl=require('readline').createInterface({input:process.stdin});" +
            "rl.once('line',l=>{try{const m=JSON.parse(l);" +
            "process.stdout.write(JSON.stringify({jsonrpc:'2.0',id:m.id,result:{protocolVersion:'2024-11-05',capabilities:{},serverInfo:{name:'bom-test',version:'1'}}})+'\\n')}" +
            "catch(e){process.exit(3)}});";

        await using var transport = new StdioMcpTransport("node", ["-e", script], null);
        await using var client = new McpClient("bom-test", transport);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await client.InitializeAsync(cts.Token);
    }

    private sealed class DelegatingMockHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(responder(request));
        }
    }

    [Fact]
    public async Task HttpMcpTransport_StreamableHttp_HandlesSseResponseOnPost()
    {
        // Simulates StarLife: GET returns JSON welcome and closes, POST returns SSE with event: message
        var handler = new DelegatingMockHandler(req =>
        {
            if (req.Method == HttpMethod.Get)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        """{"status":"online","server":"starlife_webAPI","transport":"Streamable HTTP","endpoint":"POST /mcp"}""",
                        Encoding.UTF8, "application/json")
                };
            }

            var sseContent = "event: message\ndata: {\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"protocolVersion\":\"2024-11-05\",\"capabilities\":{},\"serverInfo\":{\"name\":\"starlife_webAPI\",\"version\":\"1.0\"}}}\n\n";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(sseContent, Encoding.UTF8, "text/event-stream")
            };
        });

        using var client = new HttpClient(handler);
        await using var transport = new HttpMcpTransport("http://localhost:5070/mcp", client);
        await using var mcpClient = new McpClient("StarLife", transport);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await mcpClient.InitializeAsync(cts.Token);
    }

    [Fact]
    public async Task HttpMcpTransport_DirectHttp_HandlesJsonResponseOnPost()
    {
        var handler = new DelegatingMockHandler(req =>
        {
            if (req.Method == HttpMethod.Get)
            {
                return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
            }

            var jsonContent = """{"jsonrpc":"2.0","id":1,"result":{"protocolVersion":"2024-11-05","capabilities":{},"serverInfo":{"name":"json-mcp","version":"1.0"}}}""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(jsonContent, Encoding.UTF8, "application/json")
            };
        });

        using var client = new HttpClient(handler);
        await using var transport = new HttpMcpTransport("http://localhost:8080/mcp", client);
        await using var mcpClient = new McpClient("JsonMcp", transport);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await mcpClient.InitializeAsync(cts.Token);
    }

    [Fact]
    public async Task HttpMcpTransport_PreservesSessionIdAcrossRequests()
    {
        string? capturedSessionId = null;
        var handler = new DelegatingMockHandler(req =>
        {
            if (req.Headers.TryGetValues("Mcp-Session-Id", out var values))
            {
                capturedSessionId = values.FirstOrDefault();
            }

            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"jsonrpc":"2.0","id":1,"result":{"protocolVersion":"2024-11-05","capabilities":{},"serverInfo":{"name":"session-server"}}}""", Encoding.UTF8, "application/json")
            };
            resp.Headers.Add("Mcp-Session-Id", "session-xyz-789");
            return resp;
        });

        using var client = new HttpClient(handler);
        await using var transport = new HttpMcpTransport("http://localhost:9000/mcp", client);

        await transport.StartAsync(CancellationToken.None);
        await transport.SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "initialize" }, CancellationToken.None);

        // Second request should send the session ID captured from the first
        await transport.SendAsync(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 2, ["method"] = "tools/list" }, CancellationToken.None);

        Assert.Equal("session-xyz-789", capturedSessionId);
    }

    [Fact]
    public async Task McpClient_HandlesStringIdsInResponse()
    {
        var handler = new DelegatingMockHandler(req =>
        {
            // Server returns string "id": "1" instead of numeric 1
            var jsonContent = """{"jsonrpc":"2.0","id":"1","result":{"protocolVersion":"2024-11-05","capabilities":{},"serverInfo":{"name":"string-id-server"}}}""";
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(jsonContent, Encoding.UTF8, "application/json")
            };
        });

        using var client = new HttpClient(handler);
        await using var transport = new HttpMcpTransport("http://localhost:8080/mcp", client);
        await using var mcpClient = new McpClient("StringIdMcp", transport);

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await mcpClient.InitializeAsync(cts.Token);
    }
}
