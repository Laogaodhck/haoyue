# ADR-20261003-desktop-electron

- 状态：已接受
- 日期：2026-10-03
- 关联代码：`haoyue_desktop/`（electron-vite + Vue3 + TypeScript）

## 背景

需要一个图形界面的桌面客户端，要求快速迭代 UI、流式渲染 agent 输出、跨平台分发。

## 决策

**Electron + Vue 3 + TypeScript**（electron-vite 脚手架）；主进程通过 `daemon-client.ts` 连接 daemon，渲染进程只与主进程 IPC，绝不直接接触 daemon 协议。

## 备选方案与取舍

- **Tauri（Rust 壳 + WebView）**：包体小、内存低，但团队无 Rust 栈，且 Named Pipe 客户端生态在 Rust 侧要自己铺；迭代速度优先级高于包体。
- **.NET MAUI / Avalonia**：与运行时同栈，类型全通；但桌面 UI 生态与热重载体验明显弱于 Web 技术栈，复杂会话流式界面开发效率低。
- **选定（Electron）**：UI 迭代最快（Vue3 + vite HMR）；主进程是 Node，对接 JSONL 协议轻而易举。

## 后果

- 正面：UI 开发效率高；渲染层与协议层严格隔离（main 进程单点收发），TS 侧类型漂移后来由契约生成链根治。
- 负面：包体与内存占用高于 Tauri；GPU/进程崩溃类问题在无头 CI 上不可验证，需依赖 vitest 主进程单测（见仓库测试纪律）。
