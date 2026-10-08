using System.Buffers.Text;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Haoyue.Runtime.Mcp;

/// <summary>
/// A remote MCP server answered 401 — the trigger for the local OAuth
/// authorization assistant. The resource URL drives RFC 9728 metadata discovery;
/// the header value is diagnostics only and is never logged.
/// </summary>
public sealed class McpUnauthorizedException(string resourceUrl, string? wwwAuthenticate)
    : Exception($"MCP server '{resourceUrl}' requires authorization (HTTP 401).")
{
    public string ResourceUrl { get; } = resourceUrl;

    public string? WwwAuthenticate { get; } = wwwAuthenticate;
}

public sealed record OAuthTokens(string AccessToken, string? RefreshToken, int? ExpiresInSeconds);

/// <summary>Result of an interactive authorization: tokens plus the registered client identity
/// that must be persisted alongside them for later silent refreshes.</summary>
public sealed record OAuthGrant(OAuthTokens Tokens, string? ClientId, string? ClientSecret);

/// <summary>The subset of RFC 8414 authorization-server metadata haoyue consumes.</summary>
public sealed record OAuthServerMetadata(
    string AuthorizationEndpoint,
    string TokenEndpoint,
    string? RegistrationEndpoint,
    IReadOnlyList<string> ScopesSupported,
    bool PreferS256Challenge);

/// <summary>Registered OAuth client identity (RFC 7591 dynamic registration result).</summary>
public sealed record OAuthClient(string Id, string? Secret);

/// <summary>
/// Local OAuth 2.0 authorization-code assistant for remote MCP servers that
/// declare authorization metadata (MCP 2025-03-26 authorization spec): discovers
/// the authorization server, registers a public PKCE client, opens the system
/// browser, captures the loopback callback, exchanges the code and refreshes
/// tokens on later connections — "click, browser, connected".
/// </summary>
public static class McpOAuthFlow
{
    /// <summary>Upper bound on waiting for the user to finish the browser round-trip.</summary>
    public static TimeSpan CallbackTimeout { get; set; } = TimeSpan.FromSeconds(120);

    /// <summary>Test seam: overrides opening the system browser.</summary>
    internal static Func<string, Task>? OpenBrowserOverride { get; set; }

    private static readonly HttpClient Http = CreateHttpClient();

    /// <summary>
    /// Runs the full interactive authorization flow. Opens the default browser
    /// and blocks until the loopback callback arrives or <see cref="CallbackTimeout"/>
    /// elapses, so callers must pass a cancellation token tied to the connect cycle.
    /// </summary>
    public static async Task<OAuthGrant> AuthorizeAsync(string resourceUrl, CancellationToken ct)
    {
        var asBase = await DiscoverAuthorizationServerAsync(resourceUrl, ct).ConfigureAwait(false);
        var metadata = await GetAuthorizationServerMetadataAsync(asBase, ct).ConfigureAwait(false);

        using var listener = new HttpListener();
        var redirectUri = StartLoopbackListener(listener);

        var (client, challengeMethod) = await PrepareClientAsync(metadata, redirectUri, ct).ConfigureAwait(false);
        var (verifier, challenge) = CreatePkce(challengeMethod);
        var state = NewToken();
        var scope = string.Join(' ', metadata.ScopesSupported);

        var authUrl = BuildAuthorizationUrl(
            metadata.AuthorizationEndpoint, client.Id, redirectUri, scope, state, challenge, challengeMethod);
        await OpenBrowserAsync(authUrl).ConfigureAwait(false);

        var code = await WaitForCallbackCodeAsync(listener, state, ct).ConfigureAwait(false);
        var tokens = await ExchangeCodeAsync(
            metadata.TokenEndpoint, client.Id, client.Secret, code, redirectUri, verifier, ct).ConfigureAwait(false);
        return new OAuthGrant(tokens, client.Id, client.Secret);
    }

    /// <summary>
    /// Silent token refresh; re-discovers metadata for the token endpoint. Returns
    /// null when refresh is impossible (no client id, server unreachable, server
    /// rejected the grant) so the caller falls back to the interactive flow.
    /// </summary>
    public static async Task<OAuthTokens?> RefreshAsync(
        string resourceUrl, string refreshToken, string? clientId, string? clientSecret, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(clientId)) return null;
        try
        {
            var asBase = await DiscoverAuthorizationServerAsync(resourceUrl, ct).ConfigureAwait(false);
            var metadata = await GetAuthorizationServerMetadataAsync(asBase, ct).ConfigureAwait(false);
            var form = new Dictionary<string, string?>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
                ["client_id"] = clientId,
            };
            return await PostTokenRequestAsync(metadata.TokenEndpoint, clientId, clientSecret, form, ct).ConfigureAwait(false);
        }
        catch (McpException)
        {
            return null;
        }
    }

    // ---- metadata discovery (RFC 9728 + RFC 8414) ----

    internal static async Task<string> DiscoverAuthorizationServerAsync(string resourceUrl, CancellationToken ct)
    {
        var uri = new Uri(resourceUrl);
        var origin = uri.GetLeftPart(UriPartial.Authority);
        foreach (var wellKnown in new[]
                 {
                     $"{origin}/.well-known/oauth-protected-resource{uri.AbsolutePath}",
                     $"{origin}/.well-known/oauth-protected-resource",
                 })
        {
            var json = await TryGetJsonAsync(wellKnown, ct).ConfigureAwait(false);
            if (json?["authorization_servers"] is JsonArray servers &&
                servers.OfType<JsonValue>().FirstOrDefault() is { } first &&
                first.TryGetValue(out string? baseUrl) && !string.IsNullOrWhiteSpace(baseUrl))
                return baseUrl!;
        }
        // No protected-resource metadata: the resource origin is the authorization server.
        return origin;
    }

    internal static async Task<OAuthServerMetadata> GetAuthorizationServerMetadataAsync(string asBaseUrl, CancellationToken ct)
    {
        var baseUri = new Uri(asBaseUrl);
        var origin = baseUri.GetLeftPart(UriPartial.Authority);
        foreach (var candidate in new[]
                 {
                     $"{origin}/.well-known/oauth-authorization-server{baseUri.AbsolutePath}",
                     $"{origin}/.well-known/oauth-authorization-server",
                     $"{origin}/.well-known/openid-configuration",
                 })
        {
            var json = await TryGetJsonAsync(candidate, ct).ConfigureAwait(false);
            if (json?["authorization_endpoint"]?.GetValue<string>() is not { Length: > 0 } authEndpoint ||
                json["token_endpoint"]?.GetValue<string>() is not { Length: > 0 } tokenEndpoint)
                continue;

            var scopes = new List<string>();
            if (json["scopes_supported"] is JsonArray scopeArray)
                foreach (var scope in scopeArray)
                    if (scope?.GetValue<string>() is { Length: > 0 } value)
                        scopes.Add(value);
            var s256 = json["code_challenge_methods"] is not JsonArray methods ||
                       methods.OfType<JsonValue>().Any(m => m.TryGetValue(out string? m2) && m2 == "S256");
            return new OAuthServerMetadata(authEndpoint, tokenEndpoint,
                json["registration_endpoint"]?.GetValue<string>(), scopes, s256);
        }
        throw new McpException($"授权服务器 '{asBaseUrl}' 未发布可用的 RFC 8414 元数据，无法发起 OAuth 授权。");
    }

    private static async Task<JsonObject?> TryGetJsonAsync(string url, CancellationToken ct)
    {
        try
        {
            using var response = await Http.GetAsync(url, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return JsonNode.Parse(body) as JsonObject;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    // ---- client registration (RFC 7591) ----

    private static async Task<(OAuthClient Client, bool S256)> PrepareClientAsync(
        OAuthServerMetadata metadata, string redirectUri, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(metadata.RegistrationEndpoint))
            throw new McpException(
                "该授权服务器不支持动态客户端注册（RFC 7591），无法自动授权；请在设置中改用 Authorization 请求头。");

        var payload = new JsonObject
        {
            ["client_name"] = "Haoyue",
            ["redirect_uris"] = new JsonArray(redirectUri),
            ["grant_types"] = new JsonArray("authorization_code", "refresh_token"),
            ["response_types"] = new JsonArray("code"),
            ["token_endpoint_auth_method"] = "none",
        };
        if (metadata.ScopesSupported.Count > 0)
            payload["scope"] = string.Join(' ', metadata.ScopesSupported);

        using var response = await Http.PostAsync(
            metadata.RegistrationEndpoint,
            new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json"),
            ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode ||
            JsonNode.Parse(body) is not JsonObject json ||
            json["client_id"]?.GetValue<string>() is not { Length: > 0 } clientId)
            throw new McpException($"动态客户端注册失败（{(int)response.StatusCode}）：{Truncate(body)}");

        var secret = json["client_secret"]?.GetValue<string>();
        return (new OAuthClient(clientId, string.IsNullOrEmpty(secret) ? null : secret), metadata.PreferS256Challenge);
    }

    // ---- PKCE + authorization URL (RFC 7636) ----

    internal static (string Verifier, string Challenge) CreatePkce(bool s256)
    {
        var verifier = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(48));
        var challenge = s256
            ? Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
            : verifier;
        return (verifier, challenge);
    }

    private static string NewToken() => Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));

    internal static string BuildAuthorizationUrl(
        string authorizationEndpoint, string clientId, string redirectUri,
        string scope, string state, string challenge, bool s256)
    {
        var endpoint = new Uri(authorizationEndpoint);
        var query =
            $"response_type=code&client_id={Uri.EscapeDataString(clientId)}" +
            $"&redirect_uri={Uri.EscapeDataString(redirectUri)}" +
            $"&state={Uri.EscapeDataString(state)}" +
            $"&code_challenge={Uri.EscapeDataString(challenge)}" +
            $"&code_challenge_method={(s256 ? "S256" : "plain")}";
        if (!string.IsNullOrWhiteSpace(scope)) query += $"&scope={Uri.EscapeDataString(scope)}";
        var separator = string.IsNullOrEmpty(endpoint.Query) ? "?" : "&";
        return $"{endpoint}{separator}{query}";
    }

    // ---- loopback callback (RFC 8252 §7) ----

    internal static string StartLoopbackListener(HttpListener listener)
    {
        // The ephemeral port is dedicated to this flow, so a root prefix is safe and
        // sidesteps HttpListener's strict trailing-slash prefix matching. Probe-then-bind
        // races with the OS reassigning the probed port, so a failed claim retries on a
        // fresh port instead of surfacing as a spurious connect error.
        for (var attempt = 0; ; attempt++)
        {
            var port = GetFreePort();
            listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            try
            {
                listener.Start();
                return $"http://127.0.0.1:{port}/callback";
            }
            catch (HttpListenerException) when (attempt < 4)
            {
                listener.Prefixes.Clear();
            }
        }
    }

    private static int GetFreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        try { return ((IPEndPoint)probe.LocalEndpoint).Port; }
        finally { probe.Stop(); }
    }

    /// <summary>
    /// Waits for the browser callback and returns the authorization code. Throws a
    /// user-actionable McpException when the user denies the grant, sends an empty
    /// callback, or the wait times out; mismatched states are answered and skipped.
    /// </summary>
    internal static async Task<string> WaitForCallbackCodeAsync(HttpListener listener, string expectedState, CancellationToken ct)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(CallbackTimeout);

        while (true)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().WaitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new McpException("等待浏览器授权超时，请点击重连再次发起授权。");
            }

            var path = context.Request.Url?.AbsolutePath ?? "/";
            if (!path.StartsWith("/callback", StringComparison.Ordinal))
            {
                Respond(context, 404, "Not found");
                continue;
            }

            var query = ParseQuery(context.Request.Url?.Query);
            if (query.TryGetValue("state", out var state) && state != expectedState)
            {
                Respond(context, 400, "state mismatch");
                continue;
            }
            if (query.TryGetValue("error", out var error))
            {
                Respond(context, 200, HtmlPage($"授权被拒绝：{error}", success: false));
                throw new McpException($"浏览器授权被拒绝：{error}");
            }
            if (!query.TryGetValue("code", out var code) || string.IsNullOrWhiteSpace(code))
            {
                Respond(context, 200, HtmlPage("授权失败：回调缺少授权码，请重试。", success: false));
                throw new McpException("授权回调未携带授权码，请重试。");
            }

            Respond(context, 200, HtmlPage("授权成功！", success: true));
            return code;
        }
    }

    private static Dictionary<string, string> ParseQuery(string? query)
    {
        var parsed = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(query)) return parsed;
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=');
            if (eq < 0) continue;
            parsed[Uri.UnescapeDataString(part[..eq])] =
                Uri.UnescapeDataString(part[(eq + 1)..].Replace('+', ' '));
        }
        return parsed;
    }

    private static void Respond(HttpListenerContext context, int status, string body)
    {
        try
        {
            context.Response.StatusCode = status;
            context.Response.ContentType = "text/html; charset=utf-8";
            var bytes = Encoding.UTF8.GetBytes(body);
            context.Response.ContentLength64 = bytes.Length;
            context.Response.OutputStream.Write(bytes, 0, bytes.Length);
        }
        catch
        {
            // Browser closed the tab before reading the response — nothing to do.
        }
        finally
        {
            try { context.Response.Close(); } catch { }
        }
    }

    private static string HtmlPage(string message, bool success) => $$"""

        <!doctype html>
        <html lang="zh">
        <head><meta charset="utf-8"><title>Haoyue 授权</title></head>
        <body style="font-family:system-ui,sans-serif;display:grid;place-items:center;height:100vh;margin:0;background:#f7f7f8">
          <div style="text-align:center">
            <h1 style="font-size:20px;color:#{{(success ? "16a34a" : "dc2626")}}">{{message}}</h1>
            <p style="color:#666">此窗口可以关闭，请返回 Haoyue 继续。</p>
          </div>
        </body>
        </html>
        """;

    // ---- token exchange (RFC 6749 §4.1.3 / §6) ----

    internal static async Task<OAuthTokens> ExchangeCodeAsync(
        string tokenEndpoint, string clientId, string? clientSecret,
        string code, string redirectUri, string verifier, CancellationToken ct)
    {
        var form = new Dictionary<string, string?>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["client_id"] = clientId,
            ["code_verifier"] = verifier,
        };
        return await PostTokenRequestAsync(tokenEndpoint, clientId, clientSecret, form, ct).ConfigureAwait(false);
    }

    private static async Task<OAuthTokens> PostTokenRequestAsync(
        string tokenEndpoint, string clientId, string? clientSecret,
        Dictionary<string, string?> form, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint)
        {
            Content = new FormUrlEncodedContent(form.Where(pair => pair.Value is not null)
                .Select(pair => new KeyValuePair<string, string>(pair.Key, pair.Value!))),
        };
        if (!string.IsNullOrEmpty(clientSecret))
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes($"{clientId}:{clientSecret}")));

        using var response = await Http.SendAsync(request, ct).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new McpException($"OAuth 令牌交换失败（{(int)response.StatusCode}）：{Truncate(body)}");
        if (JsonNode.Parse(body) is not JsonObject json ||
            json["access_token"]?.GetValue<string>() is not { Length: > 0 } accessToken)
            throw new McpException($"OAuth 令牌响应缺少 access_token：{Truncate(body)}");

        var refresh = json["refresh_token"]?.GetValue<string>();
        int? expiresIn = json["expires_in"] is JsonValue exp && exp.TryGetValue(out int expSeconds) ? expSeconds : null;
        return new OAuthTokens(accessToken, string.IsNullOrEmpty(refresh) ? null : refresh, expiresIn);
    }

    // ---- browser + misc ----

    private static Task OpenBrowserAsync(string url) =>
        OpenBrowserOverride?.Invoke(url) ?? DefaultOpenBrowserAsync(url);

    private static Task DefaultOpenBrowserAsync(string url)
    {
        Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        return Task.CompletedTask;
    }

    private static HttpClient CreateHttpClient() =>
        new(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };

    private static string Truncate(string text) => text.Length <= 200 ? text : text[..200] + "…";
}
