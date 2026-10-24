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

## Layout and the right-hand work dock

The main window is a fixed three-column layout:

- The **left rail** is always present and carries the tasks / projects / timeline views plus the search box. The “New task” entry states whether it creates a **project task** or a **global task**, and hovering shows the landing spot. The project section stays available in the timeline view (collapsed by default).
- The **center column** is the conversation stream.
- The **right-hand work dock** switches between tabs: **Changes** (this turn's file diff), **History** (Git commits), **Tools** (calls in this session), **Browser** (built-in browser), and **Background Tasks**. It is resizable by dragging and becomes an overlay in narrow windows.

Settings, the Knowledge Center, Extensions (connectors / skills / experts), Scheduled Tasks, and Archive are sibling views inside the right pane rather than full-page takeovers — switching them keeps the left rail and top navigation in place. The back / forward buttons in the title bar walk a browser-style history stack.

## Project and global tasks

Desktop supports two task scopes:

- A **project task** is bound to a local directory and can use file, terminal, Git changes, and Git history features.
- A **global task** has no project directory and is intended for general conversation; it still has local file, terminal, and Git tools, it simply does not show the project-level entry points.

Creating a task does not immediately create a Runtime Session. Desktop creates and persists the session when the first message is sent. The “Global tasks” entry expands the global task list instead of forcing a switch to a particular task.

Titles are derived from the first prompt. Tasks can be archived, restored, or deleted, including bulk operations within a project or the global scope. Archived tasks are read-only. Deleting a project permanently deletes every Session under that project, including archived Sessions, but does not delete local project files.

Project properties gain an **Agent tab**: model, provider, temperature, agent mode, language, personality, verify commands (multi-line, fail-fast), and the system prompt can be set as that project's own defaults, written to `<project root>/.haoyue/config.json`. Only changed fields are submitted, so nothing you wrote by hand or through the CLI gets overwritten.

## Start a conversation

1. Select “New task,” then choose a project or use the directory-free global scope.
2. Select an Agent mode and `provider/model` in the composer. The Agent mode is **global** (the dropdown carries a “global” marker) and writes to the current workspace's `.haoyue/config.json`; to pin a mode per project, set it as a project default in project properties → Agent tab.
3. Enter a request and send it, or click a starter-prompt card. A card fills the composer but **does not send automatically**.
4. Follow streaming text, reasoning status, tool activity, and complete error details.

Vision-capable models expose an image button in the composer for selecting multiple PNG, JPEG, WebP, or GIF files. After taking a Windows screenshot, press `Ctrl+V` in the composer to attach the clipboard image directly. Images can be previewed or removed before sending and are persisted with the Session. As each image enters the model request, the assistant message shows a “Viewed” row with its file name and thumbnail; click it to preview the image again. The button is disabled when the selected model does not declare the `vision` capability.

Typing `@` references a file in the current project. Candidates come from the `workspace.files` RPC, ranked by file-name prefix, then name containment, then path containment, capped at 30. They are workspace-relative paths only — no file content is read — with `.git`, `node_modules`, `bin`, `obj`, `dist`, and `.haoyue` skipped and credential paths such as `.ssh`, `.aws`, and `.env*` never listed. `↑`/`↓` selects, `Enter` or `Tab` inserts, `Esc` dismisses. The result reads `@src/App.cs`, which the Agent resolves against the project root; directory-free tasks produce no candidates.

`Enter` sends and `Shift+Enter` breaks a line. `Esc` does one thing at a time: close the topmost dialog, decline a high-risk action awaiting confirmation, or stop the running turn (same as `Ctrl+.`). `Ctrl+K` focuses the sidebar search, `Ctrl+F` searches within the current Session.

Each task keeps its own draft — text, unsent images, and file attachments are restored when you return to it and discarded once the message is sent or the composer is cleared.

You can keep typing while the Agent is streaming. Pressing “Send” adds the message to a queue; queued messages are sent in order after the current turn finishes, and can be stacked or removed. Pressing “Steer” sends a queued message as additional guidance to the active turn without cancelling its in-flight request.

The network (globe) toggle in the composer is gated twice: by the current task's network switch and by the global one under Settings → Agent → Web search. When the global switch is off the icon is dimmed and its tooltip says where to turn it on — never a silent no-op.

Project tasks show the complete workspace path and shortcuts for opening the directory, terminal, Git changes, Git history, and task settings. The task settings dialog separates scopes: **reasoning depth** and **network** apply to this task only, while **model** and **agent mode** are global and are labeled as such. Global tasks omit the path and project-only tools.

## Follow the execution

The status line under the composer keeps the books for the turn: `N rounds · N steps | cache hits X% | N tok/s | input … tok · output … tok | context Y%`. Context occupancy is normalized to the model window, and the Agent compacts the history as it fills.

Each tool call is a collapsed row (tool, summary, duration); expand it for arguments and result, with failures marked “Error”. Write-side calls carry a diff, grouped under an “N files edited” card.

When a built-in guard intercepts a high-risk call, Desktop asks for confirmation and names the tool, an argument summary, and the reason. “Allow once” permits that call only; “Deny” or no answer within 120 seconds skips it. Both decisions land in `.haoyue/audit/tools.jsonl`.

A stopped turn reads “Stopped by you; the reply may be incomplete” and writes a `>>> [turn interrupted]` marker into the Session. After a reconnect Desktop replays turns that finished while it was offline, and a turn left incomplete by an abnormal Daemon exit is classified as interrupted rather than staying “Running”.

## Background tasks and scheduled tasks

The **Background Tasks** tab in the right dock exposes the daemon's whole background-task chain: dispatch a full agent turn for background execution (concurrency cap 4), watch live status and progress, cancel it, and jump back to the owning session. Closing the window or reconnecting loses nothing — progress replays from the event ledger.

**Scheduled Tasks** is a right-pane sibling view of Settings and Archive for cron-driven turns: each row shows the last run's status, time, error, and an output excerpt; “View output” expands the full text and “Open this session” jumps to the session that run produced. Failures raise a desktop notification, and when you need an external callback, the page points to Settings → Agent → Expert parameters for the Webhook URL.

## History and search

A Session opens with its most recent 80 messages; “Load N earlier messages” pages back, and image bytes are fetched by id only when you open a thumbnail.

The sidebar search runs a cross-Session full-text query from two characters, including archived tasks, and shows title, match count, and a snippet. Each task group lists up to 40 tasks by last activity with the active one always kept; the rest hide behind “Show all (N more)”.

## Models, Providers, and API keys

Open “Settings → Models & Providers” to:

- create, edit, test, enable, or remove Providers;
- view and edit API keys stored in the configuration (resolved for display, encrypted at rest);
- store and manage the API key in the configuration;
- manage models, Base URL, proxy, timeout, and priority;
- configure advanced provider fields: organization and project, concurrency threads (local inference), KV prefix reuse (local inference), custom request headers (secret header values are never echoed — leave blank to keep), and reasoning-effort maps;
- set a fallback model and input / output pricing per model (used to price usage reports);
- pick the vision sub-agent model (automatic or a specific vision-capable model);
- **Routing & failover**: failover toggle, local→cloud fallback (red-boxed cost warning when enabled), fallback-chain ordering and add/remove, load-balancing mode, retries and circuit breaking (including the chain budget);
- create Profiles and switch the active model or routing strategy;
- local models (GGUF) gain a "Load Settings" panel — six sections (context & performance, generation, thinking, memory, speculative decoding, advanced) with view/edit/save, "Load & Verify", and load reports (see [Providers, Models, and Smart Routing](/en/doc/providers)).

![Haoyue Desktop model and Provider management](/screenshots/desktop/providers-and-models.png)

::: tip API key visibility
The `apiKey` value is stored in `~/.haoyue/config.json` as an encrypted `secret:` reference (Windows DPAPI / Linux keyring); no plaintext appears in the configuration file. Desktop resolves it for display via `provider.list` and re-encrypts on edit. Runtime does not read API keys from environment variables.
:::

Use “Test” immediately after saving. Failed model requests include the Provider, HTTP status, and complete server response instead of only a generic `LLM request failed` message.

## Extensions, rules & memory, evolution

The sidebar "Extensions" entry opens a Qoder-style hub with three categories:

- **Connectors (MCP)**: manage connected MCP servers as cards with direct enable/disable and a detail view per server; one-click presets in the "Popular MCP" catalog, custom servers with local command / remote URL connections and OAuth / access-token auth; tools reload after saving;
- **Skills**: toggle and import installed skills, or grab official skills from the built-in marketplace;
- **Experts**: a catalog of built-in domain experts — bind one in a click from the detail page ("Start a new task with this expert", badge shown above the composer, removable anytime), or copy the prompt to the start of a conversation.

The settings workbench also provides:

- **Agent**: behavior boundaries — web search, the explorer agent (delegate_task delegation toggle), auto-verify & repair, plus the full expert-parameter set (max steps, thinking budget, tool-output cap, Bash timeout, session retention, memory consolidation, extra readable directories, personality and system prompt);
- **Inference Acceleration**: per local model, the **effective parameters + provenance layer** (model-level / provider-level / adaptive tier) and measured throughput, with an explicit warning when model-level settings shadow provider-level ones; warm-up, unload, benchmark (TTFT / decode / prefill / cache hits), and VRAM governance (resident models, concurrency, idle unload). Parameters are written in exactly one place (“Models & Providers → Load Settings”); this page only edits provider-level defaults;
- **Rules & Memory**: manage hierarchical AGENTS.md rule files and the long-term MEMORY.md — the editor creates rule files (Ctrl+N), deletes them, reveals their folder, saves with Ctrl+S and guards unsaved edits;
- **Evolution**: defect signals at a glance, manual or scheduled reflection, adopt/reject for skill drafts — see the [Evolution Engine](/en/doc/evolution);
- **Knowledge Center**: browse and revise what the agent saves as notebooks → sources → entries — attribution badges, cross-notebook search, tag filtering, Markdown export and the synonym editor — see the [Knowledge Center](/en/doc/knowledge);
- **Diagnostics & Usage**: inspect workspace, configuration, and Provider health plus calls, tokens, latency, and cost over a 7/14/30-day window, with the Daemon log and diagnostics bundle entry;
- **Advanced**: extra readable-directory allowlist, Computer Use and network-operation switches, a JSON editor for the global configuration (redacted on read, submitting only changed fields), and a factory-reset danger zone at the bottom.

The settings page has a search box that jumps to a switch by section name or keyword. While the daemon is disconnected the whole page turns read-only with an explanation (an empty list then means “could not read”, not “nothing configured”), and notices dismiss themselves after four seconds.

![Configure an MCP Server in Desktop](/screenshots/desktop/mcp-servers.png)

![Desktop Runtime diagnostics and usage](/screenshots/desktop/diagnostics-and-usage.png)

## Daemon logs and diagnostics bundle

Desktop writes the Runtime's standard output and error line by line to `logs\daemon.log` under its user data directory (`%APPDATA%\haoyue-desktop\logs\daemon.log` for a release build), rolling to `daemon.prev.log` once the file passes 8 MB. Crashes, GPU out-of-memory, and “the first token never arrives” leave their traces here.

“Settings → Diagnostics & Usage” exposes four actions: **Open logs** reveals the log file (or the directory when none exists yet), **Copy path** copies the full `daemon.log` path, **Refresh tail** shows the last 200 lines and the file size, and **Export bundle** writes `haoyue-诊断包-<timestamp>.txt` through a save dialog. The bundle holds app and Daemon state, each health check result, the usage totals for the selected window, and the last 400 log lines. Values of keys such as `api_key`, `authorization`, and `secret`, plus `Bearer`/`Basic` headers and `sk-`-prefixed secrets, are rewritten as `<已脱敏>` while reading; the configuration body and plaintext API keys are never included, so the file is safe to forward.

::: tip Turn failures name their source
The Daemon is not running or the connection dropped (nothing was sent — press reconnect), the Daemon accepted the request but produced no first token (a local model loading weights or a short GPU can take minutes — read the log), or any other failure (retry and expand “Technical details” for the full response, which stays available after a reload).
:::

## Runtime connection behavior

Desktop connects automatically on startup and performs a bounded reconnect sequence when the connection is lost. If reconnection still fails, it displays the concrete error and offers another retry or exit.

Check the following first:

1. Verify that `resources\runtime\haoyue.exe` exists in the release folder.
2. Check that an incompatible or stale Daemon is not occupying `\\.\pipe\haoyue-<username>`.
3. Run the checks again from “Diagnostics & Usage.”
4. For a source checkout, run `build.cmd` to stage the Runtime required by Desktop.

See [Daemon and IPC Protocol](/en/doc/daemon) for the lower-level integration contract and [FAQ & Diagnostics](/en/doc/faq) for more troubleshooting help.
