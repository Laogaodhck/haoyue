# 内置工具与扩展

Runtime 当前注册 9 个内置工具。文件和命令工具需要可用的工作目录；网络工具不依赖具体工作区。不绑定项目的任务使用运行时当前目录作为文件与命令工具的工作根目录。

## 内置工具

| 工具 | 作用 | 修改状态 | 需要工作区 |
| --- | --- | --- | --- |
| `read_file` | 按行读取文件，支持 `offset` / `limit` | 否 | 是 |
| `write_file` | 创建或覆盖完整文件 | 是 | 是 |
| `edit_file` | 用唯一 `old_string` 替换为 `new_string` | 是 | 是 |
| `list_dir` | 按指定深度列出目录树 | 否 | 是 |
| `glob` | 匹配文件路径，最多返回最近的 200 项 | 否 | 是 |
| `grep` | 正则搜索文件内容，可按 Glob 过滤 | 否 | 是 |
| `bash` | 在工作区运行 Shell 命令 | 是 | 是 |
| `web_search` | 使用 Google、Bing 或百度搜索 | 否 | 否 |
| `web_fetch` | 提取 HTTP / HTTPS 页面正文 | 否 | 否 |

工具描述从 `prompts/tool/<name>.txt` 加载，可以随 Prompt 更新；参数由 JSON Schema 校验。工具输出预算会根据模型上下文窗口调整，并受 `agent.maxToolOutputChars` 上限保护。

## 网络运维插件 (Network Ops)

在 高级设置 中开启「网络运维插件」后，Runtime 会注册两个模型可调用的网络工具（`network_ops.enabled` 配置项控制，默认关闭）：

| 工具 | 作用 | 修改状态 |
| --- | --- | --- |
| `network_diagnose` | 一键诊断：接口 / 路由 / 活动连接 / ARP / DNS 五个部分聚合报告，可用 `section` 单选 | 否 |
| `network_cmd` | 运行命令目录中的单个运维命令，支持 `target` 与逐项 `args` | 是（按命令分级） |

命令目录跨平台映射，模型只需选择逻辑命令，不必拼写平台命令：

| 逻辑命令 | Windows | Linux | 变更类 |
| --- | --- | --- | --- |
| `ping` | `ping`（默认 `-n 4`） | `ping`（默认 `-c 4`） | 否 |
| `traceroute` | `tracert` | `traceroute` | 否 |
| `interfaces` | `ipconfig /all` | `ip addr show` | 否 |
| `routes` | `route print` | `ip route show` | 否 |
| `connections` | `netstat -ano` | `ss -tulpn`（回退 `netstat`） | 否 |
| `arp` | `arp -a` | `ip neigh show` | 否 |
| `dns_lookup` | `nslookup` | `nslookup` | 否 |
| `dns_flush` | `ipconfig /flushdns` | `resolvectl flush-caches` | 是 |
| `getmac` | `getmac /fo list /v` | — | 否 |
| `netsh` | `netsh <args>` | — | 按动词判定 |
| `ip` | — | `ip <args>` | 按动词判定 |
| `nmcli` | — | `nmcli <args>` | 按动词判定 |
| `ethtool` | — | `ethtool <接口>` | 否 |

安全模型：

- **不经 Shell**：`args` 逐项通过 argv 传给子进程，管道、重定向与命令链在结构上不可行，杜绝注入；
- **变更分级**：`dns_flush` 与 `netsh` / `ip` / `nmcli` 中含 `set`、`add`、`delete`、`flush` 等动词的调用视为变更类，`networkOps.allowMutating=false` 时拒绝执行；
- **模式联动**：`network_cmd` 标记为 Mutating，`plan` / `readonly` 模式自动屏蔽整个工具；
- **降级友好**：命令缺失（如 traceroute 未安装）或权限不足时返回可读提示而非抛错。

## `edit_file` 参数

当前 `edit_file` 使用文本匹配，而不是行号 Patch：

```json
{
  "path": "src/UserService.cs",
  "old_string": "public bool IsActive => false;",
  "new_string": "public bool IsActive => status.IsActive;",
  "replace_all": false
}
```

`old_string` 必须匹配；默认要求只出现一次。多处匹配时，应增加上下文使其唯一，或明确设置 `replace_all: true`。修改完成后会发布统一 Diff 事件，供 Desktop 与 CLI 展示。

## 模式与作用域

- 不绑定项目的任务不会过滤文件或命令工具，但会跳过项目工作区 Prompt 与自动构建验证。
- `plan` 和 `readonly` 模式过滤所有 `Mutating: true` 工具。
- `edit` 与 `auto` 模式允许修改工具；修改成功后可触发构建验证。
- 工作区 `disabledTools` 可以按名称禁用工具。

## 自定义 C# Tool

自定义工具实现 `ITool` 并注册到 `IToolRegistry`：

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

注册返回的 `IDisposable` 用于注销工具；MCP 重载也依赖这一机制清理旧注册。
