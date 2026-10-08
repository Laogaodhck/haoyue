# ADR-20261007-contract-first

- 状态：已接受
- 日期：2026-10-07
- 关联代码：`haoyue_runtime/Daemon/Contract/DaemonContract.cs`、`contracts/daemon-contract.json`、`haoyue_desktop/scripts/generate-contract.mjs`
- 详细设计：`haoyue_doc/跨语言契约治理-2026-10-07.md`

## 背景

daemon JSONL 协议被 C#（服务端）与 TypeScript（桌面端）双端消费。方法清单、事件名、错误码全靠人肉双处维护，已发生真实漂移：4 个已实现方法（agent.undo、session.truncate、session.fork、config.save）从未对外登记；TS 手抄结构与 C# 字段不一致（RuntimeWorkspace 缺 projectKinds）。

## 决策

**haoyue_runtime 是跨语言契约的唯一事实源**：

1. `DaemonContract.cs` 集中登记方法目录（params/result JSON Schema）、错误码枚举、事件名；
2. `ProtocolInfoJson` 从契约派生，禁止第二份手工清单；
3. 契约快照 `contracts/daemon-contract.json` 由测试驱动生成（`HAOYUE_UPDATE_CONTRACT=1`），默认模式断言快照一致——漂移在 CI 即失败；
4. TS 类型 `daemon-contract.gen.ts` 由快照生成（`pnpm sync:contract`），禁止手写；
5. 所有 error 事件强制携带契约化 `details.code`，TS 按 code 分支、message 仅展示。

## 备选方案与取舍

- **protobuf/OpenAPI + 多语言 stub 管线**：类型安全最强，但要为 JSONL 行协议引入 IDL 与生成链运维成本，对本机单协议场景过重。
- **TS 侧手写类型 + 契约测试比对**：仍然是两处维护，测试只能事后报警不能防止新方法漏登记。
- **选定（C# 单源 + 快照生成）**：零新依赖；快照入库让 TS 侧的变更可 review、可 diff。

## 后果

- 正面：双清单、手抄结构、自由文本错误通道三类漂移被机制性消灭；新增 RPC 方法不登记 schema 直接测试失败。
- 负面：改契约需要三步（改 C# → 跑快照测试 → `pnpm sync:contract`），比直接改 TS 略繁琐——这是买防漂移的已知价格。
