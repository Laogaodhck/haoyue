# Model Context Protocol (MCP)

Haoyue acts as an MCP Client, connects to external servers, and registers discovered tools and prompts with the Runtime. The current client protocol version is `2024-11-05`.

## Supported surface

- **stdio** starts a local process and exchanges JSON-RPC 2.0 over stdin and stdout.
- **HTTP SSE** receives messages over a Server-Sent Events stream and sends requests to the POST endpoint announced by the server.
- **Streamable HTTP** posts directly to `/mcp`; the response may be a JSON body or a streaming SSE body.
- The manager calls `tools/list` and registers tools with `IToolRegistry`.
- It calls `prompts/list`, with `prompts/get` used for prompt content when supported.
- The client can list resources, but `McpManager` does not currently inject them into Agent context automatically.
- `websocket` is reserved but not implemented.

## Configure MCP in Desktop

Open “Settings → MCP” to add a global or workspace server, pick a connection method (stdio / HTTP SSE / Streamable HTTP), fill in the command or URL, arguments, environment, and enabled state, then select “Save and reload.”

The connection field recognizes what you paste: a bare package name (`@modelcontextprotocol/server-github` expands to `npx -y …`, `mcp-server-fetch` to `uvx …`), a full command line, or a remote URL — the transport is inferred from the content. Pasting a Claude / Cursor style JSON config fills every field automatically.

One-click presets cover the common cases: GitHub, filesystem, web fetch, memory graph, sequential thinking, Git, browser automation (Playwright), web search (Brave Search), framework docs (context7), PostgreSQL, Slack, and time/timezone. Presets prefill the command and credential key names; credential values stay user-provided.

![Desktop MCP server configuration](/screenshots/desktop/mcp-servers.png)

Queries return MCP environment-variable names but not their sensitive values. When editing an existing server, enter again any environment values that should be persisted.

## OAuth authorization for remote servers

Remote servers that follow the MCP authorization spec answer 401 and Haoyue starts the local OAuth assistant automatically (RFC 9728 / RFC 8414 metadata discovery → RFC 7591 dynamic client registration → PKCE authorization-code flow): the system browser opens, you sign in, the local loopback port receives the callback, and the token exchange triggers an automatic reconnect — no manual header configuration needed.

- Tokens (including silently refreshed ones) are stored encrypted with a `secret:` prefix, kept on this machine only, never echoed back or logged.
- On reconnect with an expired token, a silent refresh (refresh_token) is attempted before reopening the browser.
- If the authorization server does not support dynamic client registration — or you prefer an existing token — fall back to an `Authorization` header.
- The “disable OAuth browser authorization” advanced option turns the behavior off entirely.

## Advanced options (expert mode)

The “Advanced” section of the server editor offers the following tuning knobs; all default to empty / off, preserving runtime defaults:

| Option | Description |
| --- | --- |
| Transport | Explicitly override stdio / HTTP SSE / Streamable HTTP (usually inferred from the connection string) |
| Connect timeout | Remote connect timeout in seconds, 1–120, default 10 |
| Always-mutating tools | Tool names (one per line) that mutate the world despite carrying no mutating keyword (e.g. `send_email`, `deploy`) |
| Always read-only tools | Tool names forced to read-only; takes precedence over the previous option |
| Trust read-only tools | Relax the conservative default: tools with unknown names are treated as read-only in readonly / plan mode |
| Disable OAuth | Stop opening the browser on 401; the server simply reports a connect failure |

## JSON configuration

Global servers can be stored under `mcp.servers` in `~/.haoyue/config.json`. A workspace can use `.haoyue/mcp/servers.json` or the `mcp` field in `.haoyue/config.json`; a workspace entry with the same name overrides the global one.

```json
{
  "servers": {
    "filesystem": {
      "transport": "stdio",
      "command": "npx",
      "args": ["-y", "@modelcontextprotocol/server-filesystem", "E:\\Project"],
      "env": { "TOKEN": "..." },
      "enabled": true
    },
    "remote-tools": {
      "transport": "sse",
      "url": "https://mcp.example.com/sse",
      "enabled": true
    }
  }
}
```

Remote servers support custom HTTP `headers` (e.g. `Authorization`); values are stored encrypted with a `secret:` prefix (Windows DPAPI; Linux prefers the Secret Service and falls back to an AES-GCM-encrypted `~/.haoyue/secrets.json` keyed by the machine identifier — weaker than DPAPI), never echoed back or logged. Connections carrying headers must use https:// (127.0.0.1 debugging exempt).

JSON configuration accepts the expert fields too: `connectTimeoutSeconds`, `mutatingTools`, `readOnlyTools`, `trustReadOnly`, and `oauthDisabled`, with the same meaning as the table above.

## Connection and reload behavior

The Daemon begins accepting IPC connections before MCP initialization proceeds serially in the background. A workspace switch, configuration save, or `mcp.reload` unregisters old tools and prompts and closes old clients before loading the new configuration, preventing stale duplicate registrations.

A server that fails to connect is reported as disconnected with its reason and no longer blocks the remaining servers; remote connections default to a 10 second connect timeout (adjustable in advanced options) and initialization waits at most 30 seconds. A 401 from a remote server automatically enters the OAuth flow described above. Saving a server or flipping its enabled state returns immediately and connects in the background: affected servers are marked as connecting first, and the Daemon broadcasts an `mcp.updated` event to every connected client once the attempt finishes.

Use the CLI to inspect connections:

```bash
haoyue mcp list
haoyue mcp test
```

`mcp test` connects to every enabled server and reports its status and tool count. Desktop provides equivalent visual status and reload controls.
