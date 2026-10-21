# ADR-20261021-SQLCipher 数据级静态加密：步骤①验证过关，生产集成按触发条件延后

## 状态

已采纳（2026-10-21，执行卡 5.4 步骤①完成）。**落地状态：验证过关、集成延后**——
步骤②（口令 DPAPI 包封）与步骤③（冷启动不回退）已具备全部技术前提，生产集成
等「Team 版立项」触发条件满足时执行（执行卡原文：「Team 版立项时优先评估」）。

## 背景

执行卡 5.4 要求 haoyue.db 接 SQLCipher（口令 DPAPI 包封本地存放，用户无感，
防「文件被拷走」），红线：SQLCipher + WAL + busy_timeout 兼容验证不过关即整体
退级不做。现状凭据级保护：provider key 已 DPAPI 化（ConfigStore），但 haoyue.db
（usage/会话/知识库）本身是明文。

## 决策

1. **步骤①过关**（可复跑验证工具：`tools/sqlcipher-spike/`，不进 slnx/CI/分发）：
   - SQLCipher 4.5.2 community 与 Microsoft.Data.Sqlite 10 兼容；**必须在任何连接
     打开前显式 `SQLitePCL.raw.SetProvider(new SQLite3Provider_e_sqlcipher())`**
     ——bundle_e_sqlite3 与 bundle_e_sqlcipher 的 battery 初始化顺序未定义
   - FTS5 + trigram + CJK、WAL、busy_timeout 在 SQLCipher 构建内全部正常
   - 无口令打开被拒（`file is not a database`）；明文库同进程共存正常（超集语义：
     换 bundle 后既有明文库无需迁移，不带 Password 即可读）
   - 冷启动（open+首查）中位 ~0.1ms，验收 <2s 富余三个数量级
   - 39.2MB 库全表聚合：加密 325ms vs 明文 45ms（≈7×，全表解密扫描场景；
     kdf_iter=256000 确认密钥派生真实生效）
2. **生产集成路径**（触发后执行）：runtime 换 `SQLitePCLRaw.bundle_e_sqlcipher`
   （注意 2.1.12 在 nuget.org 缺失，钉 2.1.11）→ HaoyueDatabase 静态 SetProvider →
   配置节 `data.encryption`（enabled 默认 false + DPAPI 包封密钥，沿用 ConfigStore
   的 DPAPI 模式）→ 迁移路径 = 明文库改名后以加密库重建导回（或 SQLCipher 的
   `sqlcipher_export`）。集成即引入 ~2MB/RID 原生依赖 + packages.lock RID 快照
   变化 + CI 锁文件守卫联动——这是延后而非立即做的主因。
3. **性能结论对长期路线的影响**：全库加密对「全表扫描型」查询有 ~7× 开销；
   执行卡提到的「knowledge/messages 表选择性加密」仍是正确方向——SQLCipher 是
   文件级加密，选择性加密需要拆库（独立加密库文件），触发后一并评估。

## 后果

- 好处：加密可行性从「未验证的假设」变成「有复跑工具与实测数字的结论」；
  Team 版立项时无需再做一轮探索。
- 代价：~100 行 spike 工具与 README 进仓库（tools/，CI 不构建）。
- 风险：bundle_e_sqlcipher 是社区构建（SQLCipher 4.5.2 community, zlib 许可）；
  立项集成时需复核许可证与版本更新。
