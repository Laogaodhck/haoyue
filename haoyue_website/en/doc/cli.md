# CLI Command Reference

`haoyue_cli` is Haoyue's terminal client and also provides the `haoyue.exe daemon` entry point used by the packaged Runtime. Desktop users normally do not run these commands manually, but CLI and Desktop use the same global configuration and session format.

## Installation

Install the published `haoyue-cli` package from npm:

```powershell
npm install -g haoyue-cli
haoyue --version
haoyue doctor
```

The package is a self-contained .NET binary. On Windows x64 no separate .NET SDK is required; the prerequisites are Node.js 18 or later and Git for workspace detection.

After installation, use the `haoyue` command directly. For a source checkout, replace `haoyue` in the examples with `dotnet run --project haoyue_cli --`.

## Conversation

```bash
# Interactive mode (equivalent forms)
haoyue
haoyue chat

# One-shot task
haoyue "Analyze this project and fix the tests"

# Continue the newest Session in this workspace
haoyue --continue
haoyue chat --continue

# Resume a specific Session
haoyue --resume <session-id>

# Override the model for this run without changing the stored Profile
haoyue --model "anthropic/claude-sonnet-5" "Review the authentication code"
```

During interactive use, the first `Ctrl+C` cancels the active turn. Use it again while idle to exit.

## Sessions

```bash
# List the newest 30 Sessions in the active workspace
haoyue sessions

# Resume using an ID from the list
haoyue chat --resume <session-id>
```

Archiving, archive restoration, and deletion are currently exposed through Desktop or the Daemon IPC `session.*` administration methods.

## Providers

```bash
haoyue provider list

# Start the interactive add workflow
haoyue provider add

# Add non-interactively
haoyue provider add --id deepseek --kind openai \
  --base-url "https://api.deepseek.com/v1" \
  --api-key "your-api-key" \
  --model "deepseek-chat"

haoyue provider edit deepseek --timeout 120 --priority 1
haoyue provider test deepseek
haoyue provider use deepseek
haoyue provider remove deepseek
```

Omit the ID from `provider test` to probe all enabled Providers.

## Models and Profiles

```bash
haoyue model list
haoyue model info "anthropic/claude-opus-5"
haoyue model search quality
haoyue model test "openai/gpt-5.5"
haoyue model use "openai/gpt-5.5"
haoyue model stats

haoyue profile list
haoyue profile create work --provider openai --model gpt-5.5 --strategy quality --temperature 0.2
haoyue profile use work
haoyue profile delete work

# Interactively select Provider, model, and route
haoyue switch
```

## Usage and diagnostics

```bash
haoyue usage
haoyue usage --days 7
haoyue doctor
```

`usage` aggregates calls, success rate, input and output tokens, cost, and average latency by Provider and model. `doctor` checks configuration, workspace, prompts, Providers, and the active model.

## Workspace, Skills, and MCP

```bash
# Initialize .haoyue directories and .gitignore entries
haoyue init

haoyue skill list
haoyue skill enable code-review
haoyue skill disable code-review

haoyue mcp list
haoyue mcp test
```

`mcp test` connects to every enabled server and reports the number of discovered tools.

## Knowledge Base

```bash
haoyue knowledge list
haoyue knowledge search build
haoyue knowledge add "Build command" "pnpm build" --tags build,frontend
haoyue knowledge import notes.md minutes.docx
haoyue knowledge export backup.md
```

`add` takes piped content directly (`cat notes.md | haoyue knowledge add "Title"`), `import` chunks automatically and re-importing upserts, and `export` prints to stdout when no file name is given. Entries can also be inspected with `show` and removed with `delete` — see the [Knowledge Base](/en/doc/knowledge).

## Evolution

```bash
haoyue evolve defects   # health score + defect details
haoyue evolve pending   # drafts awaiting review
haoyue evolve history   # reflection run history
haoyue evolve stats     # adoption rate and skill usage
haoyue evolve decide abc123 adopt --prompt "The rewritten skill prompt"
```

`decide` fingerprints accept a unique prefix and the verdict is `adopt | reject | defer`. Starting a reflection turn is handled by Desktop or auto reflection, while the CLI covers inspection and the human gate — see the [Evolution Engine](/en/doc/evolution).

## Daemon

```bash
haoyue daemon
```

The Windows endpoint is fixed at `\\.\pipe\haoyue`; Linux and macOS use `~/.haoyue/daemon.sock`. The Daemon has no custom `--pipe` option. Desktop starts and stops its managed Daemon automatically, so manual execution is mainly useful for protocol development and debugging.

See [Daemon and IPC 2.1](/en/doc/daemon) for the method contract.
