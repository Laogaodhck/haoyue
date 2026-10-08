using System.Buffers.Text;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Haoyue.Runtime.Mcp;
using Xunit;

namespace Haoyue.Tests;

public class McpOAuthTests
{
    private static readonly TimeSpan OriginalCallbackTimeout = McpOAuthFlow.CallbackTimeout;
    private static readonly HttpClient BrowserHttp = new();

    // ------------------------------------------------------------------
    // PKCE + authorization URL
    // ------------------------------------------------------------------

    [Fact]
    public void CreatePkce_S256ChallengeIsHashOfVerifier()
    {
        var (verifier, challenge) = McpOAuthFlow.CreatePkce(s256: true);

        Assert.False(string.IsNullOrEmpty(verifier));
        Assert.NotEqual(verifier, challenge);
        var expected = Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        Assert.Equal(expected, challenge);
    }

    [Fact]
    public void CreatePkce_PlainChallengeEqualsVerifier()
    {
        var (verifier, challenge) = McpOAuthFlow.CreatePkce(s256: false);
        Assert.Equal(verifier, challenge);
    }

    [Fact]
    public void BuildAuthorizationUrl_CarriesAllRequiredParameters()
    {
        var url = McpOAuthFlow.BuildAuthorizationUrl(
            "https://auth.example.com/authorize", "client-1", "http://127.0.0.1:5/callback",
            "read write", "state-1", "challenge-1", s256: true);

        var query = ParseUrlQuery(url);
        Assert.Equal("code", query["response_type"]);
        Assert.Equal("client-1", query["client_id"]);
        Assert.Equal("http://127.0.0.1:5/callback", query["redirect_uri"]);
        Assert.Equal("state-1", query["state"]);
        Assert.Equal("challenge-1", query["code_challenge"]);
        Assert.Equal("S256", query["code_challenge_method"]);
        Assert.Equal("read write", query["scope"]);
    }

    [Fact]
    public void BuildAuthorizationUrl_OmitsScopeWhenEmptyAndUsesPlainMethodWithoutS256()
    {
        var url = McpOAuthFlow.BuildAuthorizationUrl(
            "https://auth.example.com/authorize", "c", "http://127.0.0.1/cb", "", "s", "ch", s256: false);

        var query = ParseUrlQuery(url);
        Assert.Equal("plain", query["code_challenge_method"]);
        Assert.False(query.ContainsKey("scope"));
    }

    [Fact]
    public void BuildAuthorizationUrl_AppendsWithAmpersandWhenEndpointHasQuery()
    {
        var url = McpOAuthFlow.BuildAuthorizationUrl(
            "https://auth.example.com/authorize?tenant=x", "c", "http://127.0.0.1/cb", "", "s", "ch", s256: true);

        Assert.StartsWith("https://auth.example.com/authorize?tenant=x&response_type=code", url);
    }

    // ------------------------------------------------------------------
    // Loopback callback listener
    // ------------------------------------------------------------------

    [Fact]
    public void StartLoopbackListener_BindsAnEphemeralPort()
    {
        using var listener = new HttpListener();
        var redirectUri = McpOAuthFlow.StartLoopbackListener(listener);

        Assert.True(listener.IsListening);
        var uri = new Uri(redirectUri);
        Assert.Equal("127.0.0.1", uri.Host);
        Assert.NotEqual(0, uri.Port);
        Assert.Equal("/callback", uri.AbsolutePath);
    }

    [Fact]
    public async Task WaitForCallbackCodeAsync_ReturnsCodeAndAnswersSuccessPage()
    {
        using var listener = new HttpListener();
        var redirectUri = McpOAuthFlow.StartLoopbackListener(listener);
        var waitTask = McpOAuthFlow.WaitForCallbackCodeAsync(listener, "state-1", CancellationToken.None);

        using var http = new HttpClient();
        var response = await http.GetAsync($"{redirectUri}?code=the-code&state=state-1");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal("the-code", await waitTask);
        Assert.True(response.IsSuccessStatusCode);
        Assert.Contains("授权成功", html);
    }

    [Fact]
    public async Task WaitForCallbackCodeAsync_SkipsMismatchedStateThenAcceptsTheRealCallback()
    {
        using var listener = new HttpListener();
        var redirectUri = McpOAuthFlow.StartLoopbackListener(listener);
        var waitTask = McpOAuthFlow.WaitForCallbackCodeAsync(listener, "expected", CancellationToken.None);

        using var http = new HttpClient();
        var rejected = await http.GetAsync($"{redirectUri}?code=x&state=attacker");
        var accepted = await http.GetAsync($"{redirectUri}?code=real-code&state=expected");

        Assert.Equal("real-code", await waitTask);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.True(accepted.IsSuccessStatusCode);
    }

    [Fact]
    public async Task WaitForCallbackCodeAsync_Answers404ForNonCallbackPaths()
    {
        using var listener = new HttpListener();
        var redirectUri = McpOAuthFlow.StartLoopbackListener(listener);
        var waitTask = McpOAuthFlow.WaitForCallbackCodeAsync(listener, "s", CancellationToken.None);

        using var http = new HttpClient();
        var wrongPath = await http.GetAsync($"http://127.0.0.1:{new Uri(redirectUri).Port}/favicon.ico");
        var accepted = await http.GetAsync($"{redirectUri}?code=c&state=s");

        Assert.Equal("c", await waitTask);
        Assert.Equal(HttpStatusCode.NotFound, wrongPath.StatusCode);
        Assert.True(accepted.IsSuccessStatusCode);
    }

    [Fact]
    public async Task WaitForCallbackCodeAsync_ThrowsWhenTheUserDeniesTheGrant()
    {
        using var listener = new HttpListener();
        var redirectUri = McpOAuthFlow.StartLoopbackListener(listener);
        var waitTask = McpOAuthFlow.WaitForCallbackCodeAsync(listener, "s", CancellationToken.None);

        using var http = new HttpClient();
        await http.GetAsync($"{redirectUri}?error=access_denied&state=s");

        var ex = await Assert.ThrowsAsync<McpException>(() => waitTask);
        Assert.Contains("access_denied", ex.Message);
    }

    [Fact]
    public async Task WaitForCallbackCodeAsync_ThrowsWhenTheCallbackLacksACode()
    {
        using var listener = new HttpListener();
        var redirectUri = McpOAuthFlow.StartLoopbackListener(listener);
        var waitTask = McpOAuthFlow.WaitForCallbackCodeAsync(listener, "s", CancellationToken.None);

        using var http = new HttpClient();
        await http.GetAsync($"{redirectUri}?state=s");

        await Assert.ThrowsAsync<McpException>(() => waitTask);
    }

    [Fact]
    public async Task WaitForCallbackCodeAsync_TimesOutIntoAnActionableError()
    {
        McpOAuthFlow.CallbackTimeout = TimeSpan.FromMilliseconds(400);
        try
        {
            using var listener = new HttpListener();
            McpOAuthFlow.StartLoopbackListener(listener);

            var ex = await Assert.ThrowsAsync<McpException>(
                () => McpOAuthFlow.WaitForCallbackCodeAsync(listener, "s", CancellationToken.None));
            Assert.Contains("超时", ex.Message);
        }
        finally
        {
            McpOAuthFlow.CallbackTimeout = OriginalCallbackTimeout;
        }
    }

    // ------------------------------------------------------------------
    // Metadata discovery (RFC 9728 / RFC 8414)
    // ------------------------------------------------------------------

    [Fact]
    public async Task DiscoverAuthorizationServerAsync_ReadsProtectedResourceMetadata()
    {
        await using var resource = FakeHttpServer.Start(async (self, context) =>
        {
            await RespondJsonAsync(context, 200,
                $$"""{"resource":"{{self.Origin}}/mcp","authorization_servers":["{{self.Origin}}/as"]}""");
        });

        var asBase = await McpOAuthFlow.DiscoverAuthorizationServerAsync($"{resource.Origin}/mcp", CancellationToken.None);
        Assert.Equal($"{resource.Origin}/as", asBase);
    }

    [Fact]
    public async Task DiscoverAuthorizationServerAsync_FallsBackToTheResourceOrigin()
    {
        await using var resource = FakeHttpServer.Start(async (self, context) =>
        {
            await RespondJsonAsync(context, 404, "{}");
        });

        var asBase = await McpOAuthFlow.DiscoverAuthorizationServerAsync($"{resource.Origin}/mcp", CancellationToken.None);
        Assert.Equal(resource.Origin, asBase);
    }

    [Fact]
    public async Task GetAuthorizationServerMetadataAsync_ParsesEndpointsScopesAndS256Preference()
    {
        await using var server = FakeHttpServer.Start(async (self, context) =>
        {
            await RespondJsonAsync(context, 200, $$"""
                {
                  "issuer": "{{self.Origin}}",
                  "authorization_endpoint": "{{self.Origin}}/authorize",
                  "token_endpoint": "{{self.Origin}}/token",
                  "registration_endpoint": "{{self.Origin}}/register",
                  "scopes_supported": ["read", "write"],
                  "code_challenge_methods": ["S256", "plain"]
                }
                """);
        });

        var metadata = await McpOAuthFlow.GetAuthorizationServerMetadataAsync(server.Origin, CancellationToken.None);
        Assert.Equal($"{server.Origin}/authorize", metadata.AuthorizationEndpoint);
        Assert.Equal($"{server.Origin}/token", metadata.TokenEndpoint);
        Assert.Equal($"{server.Origin}/register", metadata.RegistrationEndpoint);
        Assert.Equal(new[] { "read", "write" }, metadata.ScopesSupported);
        Assert.True(metadata.PreferS256Challenge);
    }

    [Fact]
    public async Task GetAuthorizationServerMetadataAsync_DefaultsToPlainWhenS256IsAbsent()
    {
        await using var server = FakeHttpServer.Start(async (self, context) =>
        {
            await RespondJsonAsync(context, 200, $$"""
                {
                  "authorization_endpoint": "{{self.Origin}}/authorize",
                  "token_endpoint": "{{self.Origin}}/token",
                  "code_challenge_methods": ["plain"]
                }
                """);
        });

        var metadata = await McpOAuthFlow.GetAuthorizationServerMetadataAsync(server.Origin, CancellationToken.None);
        Assert.False(metadata.PreferS256Challenge);
        Assert.Null(metadata.RegistrationEndpoint);
        Assert.Empty(metadata.ScopesSupported);
    }

    [Fact]
    public async Task GetAuthorizationServerMetadataAsync_ThrowsWhenNoMetadataIsUsable()
    {
        await using var server = FakeHttpServer.Start(async (self, context) =>
        {
            await RespondJsonAsync(context, 404, "{}");
        });

        await Assert.ThrowsAsync<McpException>(
            () => McpOAuthFlow.GetAuthorizationServerMetadataAsync(server.Origin, CancellationToken.None));
    }

    // ------------------------------------------------------------------
    // Full interactive flow and silent refresh
    // ------------------------------------------------------------------

    [Fact]
    public async Task AuthorizeAsync_RegistersClientOpensBrowserAndExchangesTheCode()
    {
        McpOAuthFlow.CallbackTimeout = TimeSpan.FromSeconds(10);
        try
        {
            string? registrationBody = null;
            string? authorizationUrl = null;
            Dictionary<string, string>? tokenForm = null;

            await using var server = FakeHttpServer.Start(async (self, context) =>
            {
                switch (context.Request.Url!.AbsolutePath)
                {
                    case "/.well-known/oauth-protected-resource/mcp":
                        await RespondJsonAsync(context, 200,
                            $$"""{"authorization_servers":["{{self.Origin}}"]}""");
                        return;
                    case "/.well-known/oauth-authorization-server":
                        await RespondJsonAsync(context, 200, $$"""
                            {
                              "authorization_endpoint": "{{self.Origin}}/authorize",
                              "token_endpoint": "{{self.Origin}}/token",
                              "registration_endpoint": "{{self.Origin}}/register",
                              "scopes_supported": ["mcp:read", "mcp:write"],
                              "code_challenge_methods": ["S256"]
                            }
                            """);
                        return;
                    case "/register":
                        registrationBody = await ReadBodyAsync(context);
                        await RespondJsonAsync(context, 200,
                            """{"client_id":"dyn-client-42","client_id_issued_at":1}""");
                        return;
                    case "/token":
                        tokenForm = ParseForm(await ReadBodyAsync(context));
                        await RespondJsonAsync(context, 200,
                            """{"access_token":"at-123","refresh_token":"rt-456","expires_in":3600,"token_type":"Bearer"}""");
                        return;
                    default:
                        await RespondJsonAsync(context, 404, "{}");
                        return;
                }
            });

            McpOAuthFlow.OpenBrowserOverride = url =>
            {
                authorizationUrl = url;
                // Simulates the browser: parse the authorization request and fire the
                // loopback redirect without waiting for its response — exactly like a
                // real browser, whose page load completes after this call returns.
                var query = ParseUrlQuery(url);
                var callback = BrowserHttp.GetAsync(
                    $"{query["redirect_uri"]}?code=fake-code&state={Uri.EscapeDataString(query["state"])}");
                _ = callback.ContinueWith(
                    static task => _ = task.Exception, TaskContinuationOptions.OnlyOnFaulted);
                return Task.CompletedTask;
            };
            try
            {
                var grant = await McpOAuthFlow.AuthorizeAsync($"{server.Origin}/mcp", CancellationToken.None);

                Assert.Equal("at-123", grant.Tokens.AccessToken);
                Assert.Equal("rt-456", grant.Tokens.RefreshToken);
                Assert.Equal(3600, grant.Tokens.ExpiresInSeconds);
                Assert.Equal("dyn-client-42", grant.ClientId);
                Assert.Null(grant.ClientSecret);

                Assert.NotNull(registrationBody);
                Assert.Contains("\"redirect_uris\"", registrationBody);
                Assert.Contains("/callback", registrationBody);
                Assert.Contains("\"token_endpoint_auth_method\":\"none\"", registrationBody);

                Assert.NotNull(authorizationUrl);
                var authQuery = ParseUrlQuery(authorizationUrl);
                Assert.Equal("code", authQuery["response_type"]);
                Assert.Equal("dyn-client-42", authQuery["client_id"]);
                Assert.Equal("mcp:read mcp:write", authQuery["scope"]);
                Assert.Equal("S256", authQuery["code_challenge_method"]);

                Assert.NotNull(tokenForm);
                Assert.Equal("authorization_code", tokenForm["grant_type"]);
                Assert.Equal("fake-code", tokenForm["code"]);
                Assert.Equal("dyn-client-42", tokenForm["client_id"]);
                Assert.NotEmpty(tokenForm["code_verifier"]);
                // PKCE: the challenge carried by the authorization URL must be the
                // hash of the verifier that arrived at the token endpoint.
                var expectedChallenge = Base64Url.EncodeToString(
                    SHA256.HashData(Encoding.ASCII.GetBytes(tokenForm["code_verifier"])));
                Assert.Equal(authQuery["code_challenge"], expectedChallenge);
            }
            finally
            {
                McpOAuthFlow.OpenBrowserOverride = null;
            }
        }
        finally
        {
            McpOAuthFlow.CallbackTimeout = OriginalCallbackTimeout;
        }
    }

    [Fact]
    public async Task RefreshAsync_ExchangesTheRefreshTokenForNewTokens()
    {
        Dictionary<string, string>? tokenForm = null;
        await using var server = FakeHttpServer.Start(async (self, context) =>
        {
            switch (context.Request.Url!.AbsolutePath)
            {
                // RefreshAsync re-discovers the metadata to learn the token endpoint.
                case "/.well-known/oauth-authorization-server":
                    await RespondJsonAsync(context, 200, $$"""
                        {
                          "authorization_endpoint": "{{self.Origin}}/authorize",
                          "token_endpoint": "{{self.Origin}}/token"
                        }
                        """);
                    return;
                case "/token":
                    tokenForm = ParseForm(await ReadBodyAsync(context));
                    await RespondJsonAsync(context, 200,
                        """{"access_token":"at-new","refresh_token":"rt-new","expires_in":1800}""");
                    return;
                default:
                    await RespondJsonAsync(context, 404, "{}");
                    return;
            }
        });

        var tokens = await McpOAuthFlow.RefreshAsync(
            $"{server.Origin}/mcp", "rt-old", "client-1", null, CancellationToken.None);

        Assert.NotNull(tokens);
        Assert.Equal("at-new", tokens.AccessToken);
        Assert.Equal("rt-new", tokens.RefreshToken);
        Assert.Equal(1800, tokens.ExpiresInSeconds);
        Assert.Equal("refresh_token", tokenForm!["grant_type"]);
        Assert.Equal("rt-old", tokenForm["refresh_token"]);
        Assert.Equal("client-1", tokenForm["client_id"]);
    }

    [Fact]
    public async Task RefreshAsync_ReturnsNullWhenTheServerRejectsTheGrant()
    {
        await using var server = FakeHttpServer.Start(async (self, context) =>
        {
            await RespondJsonAsync(context, 400, """{"error":"invalid_grant"}""");
        });

        var tokens = await McpOAuthFlow.RefreshAsync(
            $"{server.Origin}/mcp", "rt-expired", "client-1", null, CancellationToken.None);

        Assert.Null(tokens);
    }

    [Fact]
    public async Task RefreshAsync_ReturnsNullWithoutAClientId()
    {
        var tokens = await McpOAuthFlow.RefreshAsync(
            "https://example.com/mcp", "rt", clientId: null, null, CancellationToken.None);
        Assert.Null(tokens);
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static Dictionary<string, string> ParseUrlQuery(string url)
    {
        var queryPart = url[(url.IndexOf('?') + 1)..];
        return queryPart.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .ToDictionary(
                pair => Uri.UnescapeDataString(pair[0]),
                pair => pair.Length > 1 ? Uri.UnescapeDataString(pair[1]) : string.Empty);
    }

    private static Dictionary<string, string> ParseForm(string body)
    {
        return body.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .ToDictionary(
                pair => Uri.UnescapeDataString(pair[0]),
                pair => pair.Length > 1 ? Uri.UnescapeDataString(pair[1]) : string.Empty);
    }

    private static async Task<string> ReadBodyAsync(HttpListenerContext context)
    {
        using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
        return await reader.ReadToEndAsync();
    }

    private static async Task RespondJsonAsync(HttpListenerContext context, int status, string json)
    {
        try
        {
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/json";
            var bytes = Encoding.UTF8.GetBytes(json);
            context.Response.ContentLength64 = bytes.Length;
            await context.Response.OutputStream.WriteAsync(bytes);
        }
        finally
        {
            try { context.Response.Close(); } catch { }
        }
    }

    /// <summary>Loopback HTTP server with a per-request handler; each instance binds a fresh ephemeral port.</summary>
    private sealed class FakeHttpServer : IAsyncDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;

        private FakeHttpServer(Func<FakeHttpServer, HttpListenerContext, Task> responder)
        {
            var port = FreePort();
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            Origin = $"http://127.0.0.1:{port}";
            _listener.Start();
            _loop = Task.Run(() => LoopAsync(responder, _cts.Token));
        }

        public static FakeHttpServer Start(Func<FakeHttpServer, HttpListenerContext, Task> responder) => new(responder);

        public string Origin { get; }

        private async Task LoopAsync(Func<FakeHttpServer, HttpListenerContext, Task> responder, CancellationToken ct)
        {
            while (!ct.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync().WaitAsync(ct).ConfigureAwait(false);
                }
                catch (Exception) when (ct.IsCancellationRequested)
                {
                    return;
                }
                _ = Task.Run(async () =>
                {
                    try { await responder(this, context).ConfigureAwait(false); }
                    catch { try { context.Response.Close(); } catch { } }
                }, CancellationToken.None);
            }
        }

        public async ValueTask DisposeAsync()
        {
            _cts.Cancel();
            try { _listener.Stop(); } catch { }
            try { await _loop.ConfigureAwait(false); } catch { }
            _cts.Dispose();
            GC.SuppressFinalize(this);
        }
    }

    private static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        try { return ((IPEndPoint)probe.LocalEndpoint).Port; }
        finally { probe.Stop(); }
    }
}
