# Security Architecture & Permission Isolation

As an industrial-grade AI Agent Runtime, Haoyue treats safety and defense-in-depth as core architecture pillars. Multi-layered boundaries govern tool execution, file system operations, process concurrency, and network egress.

---

## 1. Tool Approval Policies

Haoyue offers configurable approval policies to prevent unintended destructive actions:

| Policy | Behavior | Best For |
| :--- | :--- | :--- |
| `never` | Fully automated execution within safe workspace scope | Daily local development, CI/CD pipelines |
| `ask_destructive` | Auto-executes standard tools; prompts for approval before destructive operations | Production codebases, team repos (Recommended) |
| `always` | Prompts for manual user confirmation before every tool call | High-security environments, demonstrations |

---

## 2. Destructive Action Interception

The runtime enforces safety gates against high-risk commands:

- **Destructive Git Actions**: Intercepts commands like `git reset --hard`, `git clean -fdx`, and `git push --force` that could destroy uncommitted work without user intent.
- **System Directory Protection**: Prohibits file modifications to critical system paths (`/etc`, `C:\Windows`) and user credentials (`~/.ssh`, `~/.aws`).
- **Execution Timeouts**: Subprocesses spawned via `bash` or system tools carry hard timeouts to prevent runaway processes or CPU exhaustion.

---

## 3. Multi-Process FileLockCoordinator

When Desktop, CLI sessions, and background cron schedules run concurrently on the same machine, multiple agent turns might target the same files.

Haoyue integrates a built-in cross-process lock coordinator:
1. **Exclusive Write Locks**: Modifying tools (`edit_file`, `write_file`) acquire exclusive write leases before touching files.
2. **Safe Shared Reads**: `read_file` allows concurrent readers while blocking conflicting writers.
3. **Lease Self-Healing**: If an agent process crashes unexpectedly, locks release automatically upon lease expiration, preventing deadlocks.

---

## 4. Network Isolation & Air-gapped Environments

- **Session Network Toggles**: Disabling network access immediately strips web tools (`web_search`, `web_fetch`) from the prompt prefix and rejects network calls.
- **100% Air-Gapped Operation**: Paired with local Ollama or vLLM backends, Haoyue operates reliably in isolated enterprise networks with zero external data transmission.

---

## 5. Credential Encryption & Local IPC Isolation

- **Provider API keys encrypted at rest**: Provider `apiKey` values in `~/.haoyue/config.json` are stored as `secret:` encrypted references (Windows DPAPI; Linux prefers Secret Service and falls back to the AES-GCM-encrypted `~/.haoyue/secrets.json` when no keyring is available). Legacy plaintext keys migrate automatically at runtime startup, and keys entered through the CLI or Desktop are encrypted on write, so plaintext credentials never appear in the configuration file. `provider.list` resolves references back to plaintext for Desktop display and editing; unresolvable references are treated as "credential lost" and return null — ciphertext is never forwarded to a model service.
- **Per-user named pipe**: The Windows endpoint is `\\.\pipe\haoyue-<username>` with a pipe ACL granting read/write only to the current user and SYSTEM, so other local accounts cannot open the endpoint; the handshake token (`~/.haoyue/daemon.token`) remains the actual authentication on top.
