# Providers, Models, and Smart Routing

Haoyue treats service endpoints, credentials, and model capabilities as editable configuration. The seed configuration includes common service templates, and you can add any compatible service through Desktop, CLI, or `~/.haoyue/config.json`.

## Wire protocols and built-in templates

The Runtime currently implements two wire protocols:

- `anthropic`: Anthropic Messages API;
- `openai`: OpenAI Chat Completions API, also used for OpenAI-compatible services.

The initial templates include:

| Provider ID | Wire protocol | Default endpoint | Default state |
| --- | --- | --- | --- |
| `anthropic` | Anthropic | `https://api.anthropic.com` | enabled |
| `openai` | OpenAI | `https://api.openai.com/v1` | enabled |
| `google` | OpenAI compatible | `https://generativelanguage.googleapis.com/v1beta/openai` | enabled |
| `mimo` | OpenAI compatible | `https://token-plan-cn.xiaomimimo.com/v1` | enabled |
| `openrouter` | OpenAI compatible | `https://openrouter.ai/api/v1` | disabled by default |
| `ollama` | OpenAI compatible | `http://localhost:11434/v1` | disabled by default |
| `lmstudio` | OpenAI compatible | `http://localhost:1234/v1` | disabled by default |

DeepSeek, Azure OpenAI, enterprise gateways, and other compatible services can be added with `kind: "openai"`. Model IDs, context windows, output limits, capability flags, and pricing remain user-managed data rather than hard-coded Runtime constants.

## Configure Providers in Desktop

Open “Settings → Models & Providers” to manage Providers, Profiles, and the model catalog:

![Desktop model and Provider management](/screenshots/desktop/providers-and-models.png)

1. Select “+ Provider” or edit an existing entry.
2. Choose the `openai` or `anthropic` protocol and enter the ID, name, Base URL, and model IDs.
3. Enter the API key directly; it is stored in the configuration file as an encrypted `secret:` reference.
4. Configure proxy, timeout, priority, prompt caching, and enabled state as needed.
5. Save and select “Test,” then activate the Provider or model.

The `apiKey` value is stored in the configuration as an encrypted `secret:` reference (Windows DPAPI / Linux keyring) and is resolved, displayed, and edited by Desktop. Runtime does not read API keys from environment variables.

On request failure, the Runtime preserves the Provider name, HTTP status, and response body. Protocol mismatches, missing tool results, invalid model IDs, and other 400 responses are shown in full so the server's concrete guidance remains available.

## Configuration shape

Providers are stored as an array, with models nested under their Provider:

```json
{
  "activeProfile": "default",
  "profiles": {
    "default": { "provider": "deepseek", "model": "deepseek-chat", "strategy": "balanced" }
  },
  "providers": [
    {
      "id": "deepseek",
      "name": "DeepSeek",
      "kind": "openai",
      "baseUrl": "https://api.deepseek.com/v1",
      "apiKey": "your-api-key",
      "promptCaching": true,
      "enabled": true,
      "priority": 0,
      "timeoutSeconds": 120,
      "models": [
        {
          "id": "deepseek-chat",
          "contextWindow": 128000,
          "maxOutput": 8192,
          "capabilities": { "streaming": true, "toolCalling": true }
        }
      ]
    }
  ]
}
```

A plaintext `apiKey` written by hand into the configuration migrates automatically to an encrypted reference at Runtime startup; either way, never commit a personal global configuration containing credentials.

`promptCaching` is enabled by default. OpenAI-compatible services keep using their automatic prefix caches; the Anthropic protocol additionally places `cache_control` checkpoints after the stable system prompt and tool definitions. Disable it in Desktop or set `"promptCaching": false` when an older Anthropic-compatible gateway rejects that field. The default prompts contain no dynamic timestamp, and tool definitions are sorted by name so the cached prefix remains byte-stable across steps.

## Local models (in-process GGUF inference)

Beyond Ollama / LM Studio (via OpenAI-compatible endpoints), Haoyue can run GGUF models directly in-process with no server required:

```json
{
  "id": "local",
  "name": "Local GGUF",
  "kind": "local",
  "modelsDirectory": "~/.haoyue/models",
  "gpuLayers": 0,
  "threads": 8,
  "localPrefixReuse": true,
  "flashAttention": false,
  "kvCacheQuantization": "none",
  "mmprojPath": ""
}
```

- `gpuLayers`: GPU-offloaded layers, default `0` (pure CPU); after building the CUDA 12 backend (`dotnet build -p:LlamaBackend=Cuda12`), set `999` to offload everything — measured 7.6 → 84.9 tok/s
- `mmprojPath`: path to the multimodal projector weights (mmproj GGUF); when left empty, the single `*mmproj*.gguf` in the model directory is auto-discovered (must sit next to the main model and be the companion version)
- `localPrefixReuse`: reuse KV prefixes across requests — multi-step turns only decode new tokens (measured TTFT 20.5s → 0.61s)
- `flashAttention` / `kvCacheQuantization`: attention-kernel acceleration and KV cache quantization (`q8_0` / `q4_0`); quantization only takes effect with Flash Attention enabled

Loading has a full fault-tolerance chain: failures retry automatically, and GPU-offload failures fall back to CPU; every successful load runs a small-context inference self-check — corrupted weights or an unallocatable context surface at load time, not mid-conversation.

### Per-model load settings (`load`)

Each local model can override provider-level settings via the `load` field on the model entry:

```json
{
  "id": "gemma-4-E4B-it-Q4_K_M",
  "contextWindow": 32768,
  "load": {
    "autoOptimize": false,
    "contextLength": 32768,
    "gpuOffload": 999,
    "evaluationBatchSize": 512,
    "physicalBatchSize": 256,
    "tryMmap": true,
    "keepModelInMemory": false,
    "offloadKvCacheToGpu": true,
    "kCacheQuantType": "q8_0",
    "temperature": 0.6,
    "topP": 0.95,
    "stopStrings": ["<end_of_turn>"]
  }
}
```

- **Semantics**: omitting `load` keeps provider-level and built-in defaults; a null field inside the object means "auto", and cloud providers are never affected; out-of-range values are clamped at parse time
- **Context & performance**: `contextLength` (clamps only to the trained value — may exceed the default 32k practical cap), `gpuOffload` / `threads`, `evaluationBatchSize` / `physicalBatchSize` (llama.cpp n_batch / ubatch)
- **Memory**: `tryMmap` (memory-mapped weights — lower load peak and faster startup), `keepModelInMemory` (mlock pinning; off by default so larger models fit), `kCacheQuantType` / `vCacheQuantType` (independent K/V quantization), `unifiedKvCache`
- **Generation & thinking**: `temperature` / `topP` / `minP` / `topK` / `repeatPenalty` / `seed`, `stopStrings`, `enableThinking`, `limitResponseLength`
- **Effect mechanism**: saving immediately invalidates resident weights; the next request reloads with the new settings; only load-affecting fields trigger a reload — sampling fields apply instantly
- **Experimental fields**: `speculativeDecoding`, `contextCheckpoints`, `chatTemplate`, `llamaCppOverride` and similar are saved/display-only for now

All of these can be edited visually on the Desktop "Models & Providers" page's local model load-settings panel (RPC `model.update` to change, `model.config.get` to query, with a "Load & Verify" button).

## Profiles and routing

A Profile can pin a Provider and model or specify only a strategy and let the Runtime build a candidate chain. Seed strategies are:

- `fast`: prioritize low latency;
- `balanced`: balance quality, speed, and cost;
- `quality`: prioritize high-capability models;
- `cheap`: prioritize low cost;
- `offline`: prioritize Ollama or LM Studio.

The default load-balancing mode is `priority`. Candidate attempts support exponential backoff, a maximum attempt count, circuit-break thresholds, and cooldowns. Once the first streamed token arrives, the Runtime does not switch Providers midway through an answer, preventing output from two models from being combined.

## CLI administration

```bash
haoyue provider list

haoyue provider add --id deepseek --kind openai \
  --api-key "sk-..." \
  --base-url "https://api.deepseek.com/v1" \
  --model "deepseek-chat"

haoyue provider test deepseek
haoyue model list
haoyue usage
```

See [Configuration Reference](/en/doc/configuration) for all fields and [FAQ & Diagnostics](/en/doc/faq) for connection troubleshooting.
