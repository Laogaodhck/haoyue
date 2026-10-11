# marketplace/

浩玥技能与连接器市场站点（纯静态，可直接部署到 GitHub Pages）。

```
marketplace/
├── index.html            # 站点本体（零依赖，原生 HTML/CSS/JS）
├── data/
│   ├── skills.json       # 官方技能快照（单源：haoyue_runtime OfficialSkillCatalog）
│   └── connectors.json   # 连接器快照（单源：connectors/*/connector.json）
├── community/            # 社区技能提交区（PR 合入后在此浏览）
└── CONTRIBUTING.md       # 贡献指南（技能/连接器格式与审核标准）
```

## 数据治理

`data/*.json` 由 **`MarketplaceDataTests` 漂移护栏** 强制与代码单源一致：

```bash
# 重新生成快照（运行时目录/技能目录变更后）
HAOYUE_EXPORT_MARKETPLACE=1 dotnet test haoyue_tests --filter MarketplaceDataTests
```

未重新生成时，护栏测试失败并提示重新导出——与 `contracts/daemon-contract.json` 同一套治理模式。

## 本地预览 / 部署

```bash
# 本地预览（fetch 需要 HTTP 服务，双击打开受限）
cd marketplace && python -m http.server 8080
# 打开 http://localhost:8080
```

GitHub Pages 部署：仓库设置 → Pages → 从 `main` 分支 `/marketplace` 目录发布即可，无需构建步骤。

## 安装渠道（站点只做展示，安装走运行时）

| 途径 | 操作 |
|------|------|
| 桌面端 | 设置 → 扩展中心 → 技能市场 / 连接器 → 一键安装 |
| CLI | `haoyue skill install <slug>` · `haoyue connectors install <id>` |
| 社区技能 | 下载 `community/` 内文件后 `haoyue skill import <文件>` |
