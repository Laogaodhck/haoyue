# SQLCipher 兼容性验证 spike（5.4 步骤①）

执行卡 5.4（数据级静态加密）的决策输入：卡片红线「先验证 SQLCipher + WAL +
busy_timeout 兼容（不过关即整体退级，不做）」。本目录是可复跑的一次性验证工具，
**不在 Haoyue.slnx 内**（CI 不构建、不进分发包、无锁文件口径问题）。

## 复跑

```bash
cd tools/sqlcipher-spike
dotnet run
```

首次 restore 需访问 nuget.org（SQLitePCLRaw.bundle_e_sqlcipher 2.1.11）。

## 2026-10-21 实测结论（Windows x64，SQLCipher 4.5.2 community）

| 检查项 | 结果 |
|---|---|
| 与 Microsoft.Data.Sqlite 10 兼容 | ✓ 需在任何连接打开前显式 `SQLitePCL.raw.SetProvider(new SQLite3Provider_e_sqlcipher())`——bundle_e_sqlite3 与 bundle_e_sqlcipher 的 battery 初始化顺序未定义，显式切换是唯一可靠做法 |
| FTS5 + trigram + CJK | ✓ SQLCipher 构建内正常命中中文子串 |
| WAL + busy_timeout | ✓ journal_mode=wal、busy_timeout 生效 |
| 口令往返 | ✓ |
| 无口令打开 | ✓ 被拒：`SQLite Error 26: 'file is not a database'` |
| 明文库共存 | ✓ 同进程同 provider 下不带 Password 的库正常读写（超集语义） |
| 冷启动（open+首查，小库） | ✓ 中位 ~0.1ms（验收 <2s 富余三个数量级） |
| 39.2MB 库全表聚合（10k 行） | 加密中位 323ms vs 明文 44.3ms（≈7×，全表解密扫描场景；kdf_iter=256000 确认在跑） |

## 决策（ADR-20261021-sqlcipher-static-encryption）

步骤①过关。②（口令 DPAPI 包封）③（冷启动不回退）已具备落地条件，但**生产集成
（换 bundle 原生依赖进分发包 ~2MB/RID + packages.lock RID 快照 + CI 守卫）按卡片
「阶段性」定位与「Team 版立项时优先评估」的触发条件延后**——个人版凭据级现状
（provider key 已 DPAPI 化）已覆盖当前威胁模型的主要面。
