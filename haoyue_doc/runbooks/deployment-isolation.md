# Runbook：部署级网络隔离与本地模型（GGUF）配套

> 适用版本：2026-10-10 之后（`agent.bashSandbox` / `knowledge.semanticEnabled` / `imageGen` 配置节已落地）。
> 本手册覆盖 Agent 能力评估报告中两项**部署侧**剩余边界：网络级隔离与模型生态。两者均为环境配置问题，不是代码缺口。

---

## 1. 边界与威胁模型

| 层 | 能力 | 不能做的 |
|---|---|---|
| `ToolExecutionPolicy`（G1） | 参数正则 allow/deny + 15 类 bash 高危守卫，执行前强制拦截 | 无法感知命令的**实际**网络行为（如间接解释执行的请求） |
| `BashSandbox`（G6） | Windows Job Object：内存/进程数/UI 限制、kill-on-close 清树 | **Job Object 内核能力不包含网络过滤**——沙箱内进程仍可发起任意出站连接 |

结论：进程级围栏已闭环，**网络面必须由部署环境收口**。以下按投入从低到高给出三个方案，可叠加。

---

## 2. 网络级隔离（部署侧）

### 方案 A：出站白名单代理（推荐，改动最小）

原理：强制 haoyue 与其 bash 子进程经唯一代理出网，防火墙只放行代理进程，代理侧配置域名白名单。

1. 为 daemon 进程环境固定代理变量（bash 子进程会继承）：

```jsonc
// ~/.haoyue/config.json（providers 同级不收 env，走启动环境）
// Windows 服务化时写入服务环境；桌面端在「设置 → 高级」注入
HTTPS_PROXY=http://127.0.0.1:3128
HTTP_PROXY=http://127.0.0.1:3128
NO_PROXY=localhost,127.0.0.1
```

2. 代理侧白名单（squid / clash / 企业网关均可）只放行 LLM 端点，例如：
   `api.openai.com`、`api.anthropic.com`、`api.deepseek.com`、`openrouter.ai`、`dashscope.aliyuncs.com`、本地 `127.0.0.1`（Ollama/LM Studio）。
3. Windows 防火墙兜底：仅放行代理进程出站，阻断其他一切出站 443/80：

```powershell
New-NetFirewallRule -DisplayName "haoyue-egress-proxy-only" -Direction Outbound -Action Allow -Program "C:\path\to\squid.exe" -Protocol TCP -RemotePort 443,80
New-NetFirewallRule -DisplayName "haoyue-deny-default-egress" -Direction Outbound -Action Block -Protocol TCP -RemotePort 443,80
```

> 注意：第二条规则阻断**全系统**出站，仅适合专机/专用部署；共享开发机请用方案 C 的按程序阻断。

### 方案 B：Windows 防火墙按程序阻断

bash 子进程的可执行文件集合是有限集（bash/cmd/pwsh/git 等），逐个阻断出站：

```powershell
foreach ($exe in @("bash.exe","cmd.exe","pwsh.exe","powershell.exe","git.exe","curl.exe","node.exe")) {
  $p = (Get-Command $exe -ErrorAction SilentlyContinue).Source
  if ($p) { New-NetFirewallRule -DisplayName "haoyue-block-$exe" -Direction Outbound -Action Block -Program $p -Protocol TCP }
}
```

局限：按程序名匹配，`copy` 一个改名后的 exe 即绕过；适合作为纵深防御，不作为唯一手段。

### 方案 C：容器化部署（最强隔离）

haoyue 是 .NET 8 自包含应用，daemon + CLI 可整体进容器。要点：

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:8.0
RUN apt-get update && apt-get install -y git bash curl && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY . .
# 模型目录以只读卷挂载，工作区以读写卷挂载
```

```yaml
# compose 片段：默认网络无出站，仅放行模型 API 专属网络
services:
  haoyue:
    build: .
    volumes:
      - ./models:/opt/haoyue/models:ro        # GGUF 只读
      - workspace:/workspace                  # 工作区读写
    networks: [egress-models]
networks:
  egress-models:
    driver: bridge
    # 配合宿主 iptables/防火墙仅放行该网段到 LLM API 的 443
```

容器方案天然获得：文件系统围栏（卷挂载边界）、进程隔离、可重建环境。Windows 上经 WSL2 后端运行时，Job Object 沙箱不生效（`agent.bashSandbox` 自动回退直接执行），容器隔离即替代面。

### 叠加建议

| 场景 | 推荐组合 |
|---|---|
| 个人开发机 | `ToolExecutionPolicy` + Job Object 沙箱 + 方案 A（代理白名单） |
| 对外服务/生产 | 容器化（方案 C）+ `agent.bashSandbox.enabled=false`（容器内 Job Object 无意义）+ 网络出口仅 LLM API |
| 专机无人值守 | 方案 A 全系统兜底 + 方案 B 程序级阻断 + 审计（`.haoyue/audit/bash.jsonl`）定期巡检 |

---

## 3. 模型生态（用户自备 GGUF）

### 3.1 目录约定

GGUF 文件放入本地提供商的模型目录（二选一）：

- 仓库 `models/`（开发环境，已 gitignore）
- `~/.haoyue/models`（或 config 中 `providers.<id>.modelsDirectory` 显式指定）

### 3.2 嵌入模型（语义检索 G4）

语义检索需要 embedding 模型。两条通道任选：

**通道一：HTTP（零门槛）**。任何 OpenAI 兼容 `/embeddings` 端点均可（Ollama、LM Studio、云端）。自动选用第一个声明 embedding 能力的 HTTP 模型，或显式指定：

```jsonc
{ "knowledge": { "semanticEnabled": true, "embeddingModel": "ollama/nomic-embed-text" } }
```

**通道二：本地 GGUF 进程内推理（零网络依赖）**。放一个 llama.cpp 支持的嵌入 GGUF（如 `bge-m3-GGUF`、`nomic-embed-text-v1.5-GGUF`、`snowflake-arctic-embed` 系列量化版），在 local 提供商下注册并声明能力：

```jsonc
{
  "providers": {
    "local": {
      "kind": "local",
      "modelsDirectory": "~/.haoyue/models",
      "models": [
        {
          "id": "bge-m3-Q8_0",
          "contextWindow": 8192,
          "maxOutput": 8,
          "capabilities": { "embedding": true, "toolCalling": false }
        }
      ]
    }
  },
  "knowledge": { "semanticEnabled": true, "embeddingModel": "local/bge-m3-Q8_0" }
}
```

要点：
- 嵌入模型**不要**与对话主模型混用同一注册项；嵌入走专用小上下文（2048）顺序推理，主模型上下文/输出预算不受影响。
- `embeddingModel` 引用格式为 `"providerId/modelId"`；引用不存在的提供商会在检索时得到明确配置错误（而不是静默退回）。
- 向量缓存键为 `(工作区, 词条, 实际模型)`——更换嵌入模型后首次检索自动重建，无需手动清缓存。

### 3.3 对话主模型与视觉

现有约定不变：主模型 GGUF（如 `gemma-4-E4B-it-Q4_K_M.gguf`）+ 可选配套 mmproj 权重（自动发现同目录 `*mmproj*.gguf`）实现本地视觉。GPU 卸载、KV 量化等见 README「本地推理加速」一节。

### 3.4 图像生成（G7）

`image_generate` 走 OpenAI 兼容 `images/generations` 端点，**仅 HTTP 通道**（本地 GGUF 扩散模型不在 LLamaSharp 能力内）。本地化方案：部署 ComfyUI / SD WebUI 的 OpenAI 兼容网关后注册为 `kind: "openai"` 提供商，模型声明 `capabilities.image = true`；未配置时工具自动隐藏，不影响其他功能。

### 3.5 验证清单

```bash
haoyue provider list          # local 提供商已注册
haoyue model list             # 嵌入模型出现 "embed" 能力标记
# 会话中执行知识检索，观察日志确认走混合排序而非纯词法回退
```

---

## 4. 快速对照

| 边界 | 归属层 | 处置 |
|---|---|---|
| 高危命令拦截 | 代码内 `ToolExecutionPolicy`（已闭环） | 配置 `agent.toolPolicy` |
| 进程资源/孤儿进程 | 代码内 `BashSandbox`（已闭环） | 配置 `agent.bashSandbox.enabled=true` |
| **网络出站** | **部署侧** | 本手册第 2 节（方案 A/B/C） |
| **嵌入/视觉/图像模型文件** | **用户自备** | 本手册第 3 节 |
