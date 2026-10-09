# CLI 命令参考

`haoyue_cli` 是 Haoyue 的终端前端，也是打包 Runtime 中 `haoyue.exe daemon` 的入口。Desktop 用户通常不需要手动运行这些命令，但 CLI 与 Desktop 使用同一份全局配置和会话格式。

## 安装

通过 npm 安装已发布的 `haoyue-cli`：

```powershell
npm install -g haoyue-cli
haoyue --version
haoyue doctor
```

该包是自包含 .NET 二进制包，当前 Windows x64 平台无需单独安装 .NET SDK；前置要求为 Node.js 18 或更高版本，以及用于工作区检测的 Git。

安装后可直接使用 `haoyue` 命令。从源码运行时，在以下示例的 `haoyue` 前替换为 `dotnet run --project haoyue_cli --` 即可。

## 对话

```bash
# 交互模式（两种写法等价）
haoyue
haoyue chat

# 单次任务
haoyue "分析当前项目并修复测试"

# 继续当前工作区最近一次 Session
haoyue --continue
haoyue chat --continue

# 恢复指定 Session
haoyue --resume <session-id>

# 仅为本次运行覆盖模型，不修改保存的 Profile
haoyue --model "anthropic/claude-sonnet-5" "审查认证代码"
```

交互过程中，第一次 `Ctrl+C` 取消活动 turn；空闲时再次使用退出程序。

## Session

```bash
# 列出当前工作区最近 30 个 Session
haoyue sessions

# 使用列表中的 ID 恢复
haoyue chat --resume <session-id>
```

归档、恢复归档和删除目前由 Desktop 或 Daemon IPC 的 `session.*` 管理方法提供。

## Provider

```bash
haoyue provider list

# 不带参数时进入交互式添加
haoyue provider add

# 非交互式添加
haoyue provider add --id deepseek --kind openai \
  --base-url "https://api.deepseek.com/v1" \
  --api-key "your-api-key" \
  --model "deepseek-chat"

haoyue provider edit deepseek --timeout 120 --priority 1
haoyue provider test deepseek
haoyue provider use deepseek
haoyue provider remove deepseek
```

`provider test` 省略 ID 时测试所有启用的 Provider。

## Model 与 Profile

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

# 交互式选择 Provider、模型和路由策略
haoyue switch
```

## 用量与诊断

```bash
haoyue usage
haoyue usage --days 7
haoyue doctor
```

`usage` 按 Provider / 模型汇总调用、成功率、输入 / 输出 Token、成本和平均延迟。`doctor` 检查配置、工作区、Prompt、Provider 和活动模型。

## 工作区、Skills 与 MCP

```bash
# 初始化 .haoyue 目录和 .gitignore 条目
haoyue init

haoyue skill list
haoyue skill enable code-review
haoyue skill disable code-review

haoyue mcp list
haoyue mcp test
```

`mcp test` 会连接每个已启用的 Server 并报告发现的工具数量。

## 知识库

```bash
haoyue knowledge list
haoyue knowledge search 构建
haoyue knowledge add "构建命令" "pnpm build" --tags build,前端
haoyue knowledge import notes.md 纪要.docx
haoyue knowledge export backup.md
```

`add` 可以直接接管道内容（`cat notes.md | haoyue knowledge add "标题"`），`import` 自动分块、重复导入即更新，`export` 省略文件名时输出到标准输出。条目管理还有 `show` 与 `delete`，完整能力见[知识库](/doc/knowledge)。

## 进化

```bash
haoyue evolve defects   # 健康分 + 缺陷明细
haoyue evolve pending   # 待审草稿清单
haoyue evolve history   # 反思回合历史
haoyue evolve stats     # 采纳率与技能使用统计
haoyue evolve decide abc123 adopt --prompt "改写后的技能提示词"
```

`decide` 的指纹支持唯一前缀匹配，裁决取 `adopt | reject | defer`。发起反思回合由 Desktop 或自动反思负责，CLI 覆盖检视与人工闸门，详见[进化引擎](/doc/evolution)。

## Daemon

```bash
haoyue daemon
```

Windows 端点固定为 `\\.\pipe\haoyue`；Linux / macOS 使用 `~/.haoyue/daemon.sock`。Daemon 不接受自定义 `--pipe` 参数。Desktop 会自动启动和关闭自己管理的 Daemon，手动执行通常仅用于协议开发或调试。

协议方法见 [Daemon 与 IPC 2.1](/doc/daemon)。
