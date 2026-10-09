# Built-in Tools and Extensions

The Runtime currently registers ten built-in tools. File and command tools require a concrete workspace, while web tools remain available in directory-free global tasks.

## Built-in tools

| Tool | Purpose | Mutating | Requires workspace |
| --- | --- | --- | --- |
| `read_file` | Reads lines with optional `offset` and `limit` | no | yes |
| `write_file` | Creates or overwrites a complete file | yes | yes |
| `edit_file` | Replaces a unique `old_string` with `new_string` | yes | yes |
| `list_dir` | Lists a directory tree to a requested depth | no | yes |
| `glob` | Matches file paths and returns up to 200 recent entries | no | yes |
| `grep` | Searches file content with regex and optional Glob filtering | no | yes |
| `bash` | Runs a shell command in the workspace | yes | yes |
| `web_search` | Searches Google, Bing, or Baidu | no | no |
| `web_fetch` | Extracts text from an HTTP or HTTPS page | no | no |
| `update_plan` | Defines and updates task plan milestones; the result echoes plan progress | no | no |

Descriptions are loaded from `prompts/tool/<name>.txt`, while arguments are validated through JSON Schema. Output budgets adapt to the model context window and remain capped by `agent.maxToolOutputChars`.

## Network Ops plugin

Flip on the Network Ops toggle in Advanced Settings and Haoyue gains a complete network diagnostics and operations toolkit, exposed to the model as two tools:

- **`network_diagnose`**: a one-shot health check. Interfaces, routes, active connections, the ARP table and DNS configuration arrive in a single report — no need to run five commands to find out why the network is down;
- **`network_cmd`**: directory-based command execution. The model picks a logical command from the directory, adds a target and arguments, and the adapter smooths out every Windows/Linux difference.

The directory covers the full ops command set: `ping`, `traceroute` (tracert), `interfaces` (ipconfig / ip addr), `routes`, `connections` (netstat / ss), `arp`, `dns_lookup`, `dns_flush`, `getmac`, `ethtool`, plus the complete `netsh` subtree on Windows and `ip` / `nmcli` on Linux.

Safety is the foundation of this plugin:

- **Arguments never touch a shell** — they go through argv one item at a time, so pipes and command chaining are structurally impossible and injection has nothing to grab onto;
- **Read/write split** — `dns_flush` and any `netsh` / `ip` / `nmcli` call carrying verbs like `set`, `add` or `delete` counts as mutating; disable "allow mutating commands" and they are all rejected;
- **Mode interplay** — `plan` and `readonly` modes filter the whole plugin automatically;
- **Says what's missing** — traceroute not installed or insufficient privileges produce a readable hint, not a stack trace.

## `edit_file` arguments

The current tool uses text matching rather than line-number patches:

```json
{
  "path": "src/UserService.cs",
  "old_string": "public bool IsActive => false;",
  "new_string": "public bool IsActive => status.IsActive;",
  "replace_all": false
}
```

`old_string` must match and is required to be unique by default. Add context when it matches multiple places or set `replace_all: true` explicitly. A successful edit publishes a unified diff event for Desktop and CLI renderers.

## Modes and scopes

- Global tasks filter tools whose `RequiresWorkspace` value is `true`.
- `plan` and `readonly` filter tools whose `Mutating` value is `true`.
- `edit` and `auto` allow mutations, which can trigger build verification.
- A workspace can disable tools by name through `disabledTools`.

## Task planning and self-correction

Haoyue's agent does more than draw a plan on screen — the milestones declared through `update_plan` are written into the runtime, and tool results echo the current plan state and progress, so the model can always "see" where it stands. The runtime keeps comparing executed work against the declared plan and steps in when they drift: a finished plan that keeps calling tools earns a nudge to extend or wrap up; many tool calls without a status refresh earn a reminder to update the plan; a call that keeps failing gets a gentle "stop retrying blindly" at twice, and a straight "change your approach" at four; and plenty of tool calls with nothing to show for it — in edit or auto mode, with no plan in sight — triggers a direction check.

The step-budget wrap-up notice also grew teeth: it now carries an execution ledger stating how many steps ran, how they fared, and which tools kept failing, so an interrupted turn still leaves a readable account of itself. And calling a tool that does not exist no longer dead-ends — the error names the closest registered tools.

## Custom C# tools

Implement `ITool` and register the instance with `IToolRegistry`:

```csharp
public sealed class ProjectSummaryTool : ITool
{
    public string Name => "project_summary";
    public string Description => "Summarize the current project";
    public JsonObject ParameterSchema => ToolSchema.Object(
        ("depth", ToolSchema.Integer("Maximum directory depth"), false));
    public bool Mutating => false;
    public bool RequiresWorkspace => true;
    public string StatusLabel => "Inspecting project";

    public Task<ToolResult> ExecuteAsync(
        JsonObject arguments,
        ToolContext context,
        CancellationToken ct)
    {
        var summary = $"Workspace: {context.Workspace.Root}";
        return Task.FromResult(ToolResult.Ok(summary));
    }
}

using var registration = toolRegistry.Register(new ProjectSummaryTool());
```

The returned `IDisposable` unregisters the tool. MCP reload uses the same mechanism to clean up stale registrations.
