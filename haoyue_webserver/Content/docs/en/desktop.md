# Desktop Client

Haoyue Desktop is an Electron and Vue client for the same .NET Runtime used by the CLI. It connects through the local Daemon IPC protocol and provides a graphical workspace for projects, tasks, models, tools, and runtime health. Desktop and CLI share Provider, Profile, MCP, Skill, and session data.

![Haoyue Desktop AI chat and project management](/screenshots/desktop/chat-and-projects.png)

## Install and launch

### Use a packaged release

<LatestRelease />

The current Desktop release target is **Windows x64**. Keep the complete `Haoyue-win-x64` directory and run `Haoyue.exe`; copying the EXE by itself is not supported.

The package contains a self-contained Runtime:

```text
Haoyue-win-x64/
├── Haoyue.exe
└── resources/
    └── runtime/
        └── haoyue.exe
```

End users do not need to install .NET. On launch, Desktop first tries to connect to an existing Daemon and starts the bundled Runtime when none is available. On exit it stops only the Runtime instance it started, leaving independently started Daemons alone.

### Build a release from source

Building requires Windows x64, the .NET 10 SDK, Node.js, pnpm, and Python 3. After cloning the repository, double-click `build.cmd` in the repository root:

```text
build.cmd
```

The command wrapper launches the cross-platform `build.py` entry point. It installs dependencies, runs .NET and Desktop tests, publishes the self-contained Runtime, builds Electron, and assembles the final folder at:

```text
publish\Haoyue-win-x64\Haoyue.exe
```

Optional terminal flags include:

```powershell
build.cmd --skip-tests
build.cmd --skip-install
```

Distribute the whole `publish\Haoyue-win-x64` folder. Packaging downloads Electron binaries; the script configures download mirrors and retries transient packaging failures.

## Project and global tasks

Desktop supports two task scopes:

- A **project task** is bound to a local directory and can use file, terminal, Git changes, and Git history features.
- A **global task** has no project directory and is intended for general conversation; local file, terminal, and Git tools are unavailable.

Creating a task does not immediately create a Runtime Session. Desktop creates and persists the session when the first message is sent. The “Global tasks” entry expands the global task list instead of forcing a switch to a particular task.

Titles are derived from the first prompt. Tasks can be archived, restored, or deleted, including bulk operations within a project or the global scope. Archived tasks are read-only. Deleting a project permanently deletes every Session under that project, including archived Sessions, but does not delete local project files.

## Start a conversation

1. Select “New task,” then choose a project or use the directory-free global scope.
2. Select an Agent mode and `provider/model` in the composer.
3. Enter a request and send it, or click a starter-prompt card. A card fills the composer but **does not send automatically**.
4. Follow streaming text, reasoning status, tool activity, and complete error details.

Vision-capable models expose an image button in the composer for selecting multiple PNG, JPEG, WebP, or GIF files. After taking a Windows screenshot, press `Ctrl+V` in the composer to attach the clipboard image directly. Images can be previewed or removed before sending and are persisted with the Session. As each image enters the model request, the assistant message shows a “Viewed” row with its file name and thumbnail; click it to preview the image again. The button is disabled when the selected model does not declare the `vision` capability.

Typing `@` references a file in the current project. The candidate list opens as soon as you type it and ranks matches by file-name prefix, then file-name containment, then path containment, capped at 30 entries. Candidates come from the `workspace.files` RPC and are workspace-relative paths only — no file content is read — with `.git`, `node_modules`, `bin`, `obj`, `dist`, and `.haoyue` skipped along with anything ignored by `.gitignore` or `.haoyueignore`; credential locations such as `.ssh`, `.aws`, and `.env*` never appear. Use `↑`/`↓` to select, `Enter` or `Tab` to insert, `Esc` to dismiss. The result is a path like `@src/App.cs` that the Agent resolves against the project root, so you never have to paste absolute paths. Directory-free tasks produce no candidates.

`Enter` sends and `Shift+Enter` inserts a newline. `Esc` does exactly one thing depending on context: it closes the topmost dialog (stacked dialogs close one at a time), declines a high-risk action awaiting confirmation, or stops the running turn, the same as `Ctrl+.`. `Ctrl+K` focuses the sidebar search and `Ctrl+F` searches within the current Session.

Each task keeps its own draft. Text you have typed plus images and file attachments that were never sent are stored per task and restored when you return to it; the draft is discarded once a message is sent successfully or the composer is cleared.

You can keep typing while the Agent is streaming. Pressing “Send” adds the message to a queue; queued messages are sent in order after the current turn finishes, and can be stacked or removed. Pressing “Steer” sends a queued message as additional guidance to the active turn without cancelling its in-flight request.

Project tasks show the complete workspace path and shortcuts for opening the directory, terminal, Git changes, Git history, and task settings. Global tasks omit the path and project-only tools.

## Follow the execution

The status line under the composer reports the accounting for the current turn: `N rounds · N steps | cache hits X% | N tok/s | input … tok · output … tok | context Y%`. Context occupancy is normalized to the selected model's window because raw token counts mean little there; as the window fills, the Agent compacts the history before continuing.

Each tool call collapses into one row showing the tool, a summary, and its duration. Expand it for the arguments and the result, with failed results marked “Error”. Write-side calls carry a diff, collected under an “N files edited” card where each file name opens the added and removed lines.

When a built-in guard intercepts a high-risk call, Desktop shows “High-risk action needs your approval” with the tool, an argument summary, and the reason it was blocked. “Allow once” permits that single call only — it is not a standing whitelist; “Deny”, or no answer within 120 seconds, skips execution. Both decisions are appended to `.haoyue/audit/tools.jsonl` alongside the other tool records.

A manually stopped turn labels the assistant message “Stopped by you; the reply may be incomplete” and writes a `>>> [turn interrupted]` marker into the Session. After Desktop reconnects, it replays finished activity from the event ledger for the period it was offline; a turn left incomplete by an abnormal Daemon exit is classified as interrupted instead of staying “Running” forever.

## History and search

A Session loads its most recent 80 messages, with “Load N earlier messages” above the transcript to page further back. Image bytes are not delivered with the messages — each thumbnail is fetched by image id only when you actually open it.

Typing two or more characters in the sidebar search starts a cross-Session full-text search that includes archived tasks. Results show the title, the match count, and a snippet around the first match, and open that Session when clicked.

Each task group lists at most 40 tasks ordered by last activity, and the active task always stays in the list; the rest sit behind “Show all (N more)”.

## Models, Providers, and API keys

Open “Settings → Models & Providers” to:

- create, edit, test, enable, or remove Providers;
- view and edit API keys stored in the configuration (resolved for display, encrypted at rest);
- store and manage the API key in the configuration;
- manage models, Base URL, proxy, timeout, and priority;
- create Profiles and switch the active model or routing strategy.

![Haoyue Desktop model and Provider management](/screenshots/desktop/providers-and-models.png)

::: tip API key visibility
The `apiKey` value is stored in `~/.haoyue/config.json` as an encrypted `secret:` reference (Windows DPAPI / Linux keyring); no plaintext appears in the configuration file. Desktop resolves it for display via `provider.list` and re-encrypts on edit. Runtime does not read API keys from environment variables.
:::

Use “Test” immediately after saving. Failed model requests include the Provider, HTTP status, and complete server response instead of only a generic `LLM request failed` message.

## Extensions, rules & memory, evolution

The sidebar "Extensions" entry opens a Qoder-style extensions hub with three categories:

- **Connectors (MCP)**: manage connected MCP servers as cards — status and tool count at a glance, with direct enable/disable and a detail view per server; the "Popular MCP" catalog offers one-click setup (GitHub, filesystem, web fetch, Playwright…), or add a custom server manually. The detail page covers local command / remote URL connections, automatic OAuth or access-token authentication, and advanced options (scope, timeout, tool trust) in a collapsible section; tools reload automatically after saving; the project properties dialog links straight here via "Manage connectors";
- **Skills**: the "Installed" view inspects, imports (.md / .zip) and toggles discovered skills; the "Marketplace" view installs or disables official skills in one click;
- **Experts**: browse the built-in domain-expert catalog (filter by domain). On the detail page, click "Start a new task with this expert" to bind it in one click — an expert badge appears above the composer and can be removed anytime; you can still copy the expert prompt into a new task manually.

The settings workbench also provides:

- **Agent**: centralize model selection and behavior boundaries — root model, vision sub-agent model (automatic or a specific vision-capable model), web search, the explorer agent (delegate_task delegation toggle), and auto-verify & repair;
- **Rules & Memory**: toggle rule injection and the memory management mode; the editor maintains hierarchical AGENTS.md rule files and MEMORY.md — create rule files (Ctrl+N), delete them, reveal their folder, and save quickly with Ctrl+S;
- **Evolution**: browse defect signals, run reflection turns manually or on an interval, and review the skill drafts it produces (see the [Evolution Engine](/en/doc/evolution));
- **Knowledge Center**: manage what the agent saves automatically as notebooks → sources → entries — notebook grouping, text/URL/file sources, attribution badges, cross-notebook search, exact tag filtering, Markdown export and a synonym-table editor (see the [Knowledge Center](/en/doc/knowledge));
- **Diagnostics & Usage**: inspect workspace, configuration, and Provider health plus calls, tokens, latency, and cost over a 7/14/30-day window, with the Daemon log and diagnostics bundle entry described below.

![Configure an MCP Server in Desktop](/screenshots/desktop/mcp-servers.png)

![Desktop Runtime diagnostics and usage](/screenshots/desktop/diagnostics-and-usage.png)

## Daemon logs and diagnostics bundle

Desktop appends the Runtime's standard output and standard error to `logs\daemon.log` under its user data directory. For a release build that directory is `%APPDATA%\haoyue-desktop`, so the file lives at `C:\Users\<username>\AppData\Roaming\haoyue-desktop\logs\daemon.log`. Once it exceeds 8 MB the file rolls to `daemon.prev.log` before the next start and one generation is kept. Crashes, GPU out-of-memory, and “the first token never arrives” all leave their evidence here.

The **Daemon logs and diagnostics bundle** block in “Settings → Diagnostics & Usage” offers four actions:

- **Open logs** reveals the log file in the system file manager, or the log directory when no log exists yet;
- **Copy path** copies the full `daemon.log` path so it can be pasted to a collaborator;
- **Refresh tail** reads and displays the last 200 lines in place, together with the file size;
- **Export bundle** writes `haoyue-诊断包-<timestamp>.txt` to a location you choose in the save dialog.

The bundle collects what triage needs: app version and platform, Daemon endpoint and connection state, log directory and file size, every health check result, the call and token totals for the selected usage window, and the last 400 log lines. While reading them the main process rewrites values of keys such as `api_key`, `authorization`, `access_token`, `refresh_token`, `client_secret`, `password`, and `secret` as `<已脱敏>`, and does the same for `Bearer`/`Basic` headers and `sk-`-prefixed secrets. No configuration body and no plaintext API key are included, so the file is safe to forward.

::: tip Turn failures name their source
- **Daemon not running or connection lost**: the message was never sent. Press reconnect, and if it keeps failing read the Daemon log in “Diagnostics & Usage.”
- **The Daemon took the request but produced no first token**: a local model loading weights, or a GPU that cannot hold it, can take several minutes. This is not a broken connection — check the Daemon log for progress instead.
- **Everything else**: retry, and expand “Technical details” for the full server response. Details are stored with the message and remain readable after a reload.
:::

## Runtime connection behavior

Desktop connects automatically on startup and performs a bounded reconnect sequence when the connection is lost. If reconnection still fails, it displays the concrete error and offers another retry or exit.

Check the following first:

1. Verify that `resources\runtime\haoyue.exe` exists in the release folder.
2. Check that an incompatible or stale Daemon is not occupying `\\.\pipe\haoyue-<username>`.
3. Run the checks again from “Diagnostics & Usage.”
4. For a source checkout, run `build.cmd` to stage the Runtime required by Desktop.

See [Daemon and IPC Protocol](/en/doc/daemon) for the lower-level integration contract and [FAQ & Diagnostics](/en/doc/faq) for more troubleshooting help.
