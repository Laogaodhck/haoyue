# 贡献指南 —— 技能 / 连接器

浩玥技能市场采用 **GitHub 单仓库 + 社区 PR** 模式：本仓库既是站点内容源，也是分发渠道。
合入后用户即可通过 `haoyue skill import` 导入你的技能，或直接参考仓库文件手动安装。

## 一、提交技能

### 1. 选择格式

| 格式 | 适用 | 结构 |
|------|------|------|
| 裸 Markdown（`.md`） | 纯提示词技能，无需配置 | 一个文件，正文即提示词 |
| ZIP 包 | 含清单、参数声明、工具边界的正式技能 | 压缩包根部一个技能目录 |

### 2. 技能目录规范

一个技能目录必须包含：

```
<slug>/
├── skill.yaml      # 清单（必需）
└── prompt.txt      # 提示词（必需，文件名可在清单中自定义）
```

`skill.yaml` 字段：

| 字段 | 必需 | 说明 |
|------|------|------|
| `name` | ✅ | 技能显示名（中文，与目录内唯一） |
| `description` | 建议 | 一句话说明用途 |
| `version` | 建议 | 语义化版本，如 `1.0.0` |
| `triggers` | 可选 | 关键词门控：仅当用户消息包含其中之一时注入；缺省常驻 |
| `allowed-tools` | 可选 | 工具权限边界：注入期间仅允许列出的工具；缺省全量工具集 |
| `parameters` | 可选 | 声明式参数：模型在应用技能前需从对话中收集 |
| `prompt` | 可选 | 提示词文件相对路径，默认 `prompt.txt` |

最小示例：

```yaml
name: "周报摘要助手"
description: "把零散工作记录整理成结构化周报"
version: "1.0.0"
triggers: ["周报", "weekly"]
```

### 3. 放置位置

- 官方目录技能安装到 `~/.haoyue/skills/<slug>/`（由运行时写入，勿手工提交）。
- **社区技能提交到 `marketplace/community/<slug>/`**，保留你的原始分发文件（`.md` 或 `.zip`），
  并附一段 README 说明用途与示例对话。

### 4. 用户如何安装

```bash
# 裸 markdown
haoyue skill import weekly-report.md

# zip 包
haoyue skill import weekly-report.zip
```

桌面端：设置 → 扩展中心 → 技能市场 → 「导入技能」选择下载的文件。

## 二、提交连接器

1. 遵循 `connectors/schema/connector.schema.json` 编写 `connectors/<id>/connector.json`；
2. `server` 段指向实际 MCP 服务器（stdio command 或 url）；
3. 涉及凭据的配置项标记 `"secret": true`（运行时经 SecretResolver 加密落盘）；
4. 运行 `haoyue connectors install <id>` 实测安装 → 启停 → 卸载全流程。

## 三、自查清单（PR 前逐项确认）

- [ ] 不含任何密钥、令牌、个人路径或隐私数据
- [ ] 提示词可独立执行，不依赖特定对话上下文
- [ ] `skill.yaml` 字段符合上表；`name` 与既有技能不重名
- [ ] 已在本地实测：技能可触发、输出符合描述
- [ ] 连接器已通过 schema 校验并实测安装/卸载
- [ ] PR 描述含用途说明与至少一个示例

## 四、审核标准

维护者按以下标准合入：安全性（无注入风险、无越权工具声明）、通用性（不绑定个人环境）、
质量（提示词工程清晰、示例可复现）。合入后站点数据将在下一次运行时导出时自动收录
（社区区由本目录直接渲染）。

## 五、许可

贡献即同意以仓库当前 LICENSE 分发。
