using System.Text.Json;
using System.Text.Json.Nodes;
using Haoyue.Runtime.Configuration;
using Haoyue.Runtime.Mcp;
using Haoyue.Runtime.Secrets;
using Xunit;

namespace Haoyue.Tests;

/// <summary>
/// Tests for the MCP credential layer: secret: prefix resolution, HTTP headers
/// on the transport, connect-timeout override, and backward compatibility of the
/// extended McpServerConfig schema with pre-existing configs.
/// </summary>
public class McpSecretsTests
{
    private sealed class DelegatingMockHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(responder(request));
    }

    // ---- schema compatibility ----

    [Fact]
    public void McpServerConfig_OldJsonWithoutNewFields_DeserializesUnchanged()
    {
        // A config written before Headers/ConnectTimeoutSeconds existed must keep working.
        const string json = """
        {
          "servers": {
            "legacy": {
              "transport": "stdio",
              "command": "npx",
              "args": ["-y", "@modelcontextprotocol/server-github"],
              "env": { "TOKEN": "plain-value" },
              "enabled": true
            }
          }
        }
        """;
        var config = JsonSerializer.Deserialize(json, HaoyueJsonContext.Default.McpConfig);

        var server = config!.Servers["legacy"];
        Assert.Equal("stdio", server.Transport);
        Assert.Equal("npx", server.Command);
        Assert.Null(server.Headers);
        Assert.Null(server.ConnectTimeoutSeconds);
        Assert.Equal("plain-value", server.Env!["TOKEN"]);
    }

    [Fact]
    public void McpServerConfig_HeadersAndTimeout_RoundTripThroughJson()
    {
        var config = new McpConfig();
        config.Servers["remote"] = new McpServerConfig
        {
            Transport = "http",
            Url = "https://example.com/mcp",
            Headers = new Dictionary<string, string> { ["Authorization"] = "secret:dpapi:AQAA" },
            ConnectTimeoutSeconds = 25,
        };

        var json = JsonSerializer.Serialize(config, HaoyueJsonContext.Default.McpConfig);
        var restored = JsonSerializer.Deserialize(json, HaoyueJsonContext.Default.McpConfig)!;

        var server = restored.Servers["remote"];
        Assert.Equal("secret:dpapi:AQAA", server.Headers!["Authorization"]);
        Assert.Equal(25, server.ConnectTimeoutSeconds);
    }

    // ---- secret resolution ----

    [Fact]
    public void Resolve_PlaintextValues_PassThroughUnchanged()
    {
        Assert.Null(SecretResolver.Resolve(null));
        Assert.Equal("", SecretResolver.Resolve(""));
        Assert.Equal("Bearer tok", SecretResolver.Resolve("Bearer tok"));
        Assert.False(SecretResolver.IsSecret("Bearer tok"));
    }

    [Fact]
    public void Resolve_UnknownSecretScheme_ReturnsNullInsteadOfLeakingCiphertext()
    {
        Assert.True(SecretResolver.IsSecret("secret:future-scheme:xyz"));
        Assert.Null(SecretResolver.Resolve("secret:future-scheme:xyz"));
    }

    [Fact]
    public void Encrypt_IsIdempotentForAlreadyStoredValues()
    {
        const string stored = "secret:dpapi:AQAA-existing";
        Assert.Equal(stored, SecretResolver.Encrypt("purpose", stored));
    }

    [Fact]
    public void EncryptThenResolve_RoundTripsOnThisPlatform()
    {
        const string plaintext = "ghp_secret-value-123";
        var stored = SecretResolver.Encrypt("test:mcp-secrets", plaintext);

        // On Windows the value is DPAPI-protected; on Linux with secret-tool it is
        // keyring-stored; otherwise it stays plaintext (documented limitation).
        if (OperatingSystem.IsWindows())
            Assert.StartsWith("secret:dpapi:", stored);
        Assert.Equal(plaintext, SecretResolver.Resolve(stored));
    }

    // ---- transport headers ----

    [Fact]
    public async Task HttpMcpTransport_AppliesConfiguredHeadersToEveryRequest()
    {
        var seenAuth = new List<(string Method, string? Auth)>();
        var handler = new DelegatingMockHandler(req =>
        {
            seenAuth.Add((req.Method.Method, req.Headers.TryGetValues("Authorization", out var v) ? v.FirstOrDefault() : null));
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"jsonrpc":"2.0","id":1,"result":{"protocolVersion":"2024-11-05","capabilities":{},"serverInfo":{"name":"auth-server"}}}""",
                    System.Text.Encoding.UTF8, "application/json")
            };
        });

        using var client = new HttpClient(handler);
        await using var transport = new HttpMcpTransport(
            "http://localhost:8080/mcp",
            client,
            headers: new Dictionary<string, string> { ["Authorization"] = "Bearer tok-1" });

        await transport.StartAsync(CancellationToken.None);
        await transport.SendAsync(
            new JsonObject { ["jsonrpc"] = "2.0", ["id"] = 1, ["method"] = "initialize" },
            CancellationToken.None);

        Assert.Equal(2, seenAuth.Count); // GET probe + POST
        Assert.All(seenAuth, item => Assert.Equal("Bearer tok-1", item.Auth));
    }

    [Fact]
    public async Task HttpMcpTransport_WithoutHeaders_SendsNoAuthorization()
    {
        var seenAuth = new List<string?>();
        var handler = new DelegatingMockHandler(req =>
        {
            seenAuth.Add(req.Headers.TryGetValues("Authorization", out var v) ? v.FirstOrDefault() : null);
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(
                    """{"jsonrpc":"2.0","id":1,"result":{"protocolVersion":"2024-11-05","capabilities":{},"serverInfo":{"name":"plain"}}}""",
                    System.Text.Encoding.UTF8, "application/json")
            };
        });

        using var client = new HttpClient(handler);
        await using var transport = new HttpMcpTransport("http://localhost:8080/mcp", client);

        await transport.StartAsync(CancellationToken.None);
        Assert.All(seenAuth, Assert.Null);
    }

    // ---- transport creation resolves secrets ----

    [Fact]
    public void McpManager_CreateTransport_UnresolvableSecret_FailsWithActionableError()
    {
        // A dpapi ciphertext cannot be resolved off-Windows; transport creation must
        // stop with a re-enter hint instead of sending the ciphertext upstream.
        var server = new McpServerConfig
        {
            Transport = "http",
            Url = "https://example.com/mcp",
            Headers = new Dictionary<string, string> { ["Authorization"] = "secret:dpapi:c2hvcWVsZC1mYWtlLWNpcGhlcnRleHQ=" },
        };

        var method = typeof(McpManager).GetMethod(
            "CreateTransport",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);

        // CreateTransport is static — no manager instance needed.
        Exception? failure = null;
        try
        {
            method!.Invoke(null, new object?[] { "test", server });
        }
        catch (System.Reflection.TargetInvocationException ex)
        {
            failure = ex.InnerException ?? ex;
        }

        // The fake ciphertext fails DPAPI/keyring decryption on every platform, so
        // transport creation must fail with the actionable re-enter message instead
        // of sending ciphertext upstream as if it were the credential.
        var mcp = Assert.IsType<McpException>(failure);
        Assert.Contains("Re-enter the value", mcp.Message);
        Assert.DoesNotContain("c2hvcWVsZC1mYWtl", mcp.Message); // ciphertext never echoed
    }
}
