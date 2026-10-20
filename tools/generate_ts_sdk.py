#!/usr/bin/env python3
"""haoyue TypeScript SDK 生成器（S9）。

单源：contracts/daemon-contract.json。
产物：sdks/typescript/ 下的类型化客户端——
  src/contract.ts   契约常量与方法元数据（纯生成）
  src/client.ts     DaemonClient 运行时 + 按类别生成的类型化 RPC 方法（100% 覆盖契约）
  src/index.ts      导出入口
  package.json / tsconfig.json / README.md / examples/hello.ts

运行：python tools/generate_ts_sdk.py [--check]
  --check  校验模式：不写文件，验证现有产物与契约一致（CI 护栏）。

设计约定：
- 不另造协议：JSONL over named pipe / unix socket，握手 {method:"handshake",params:{token,protocolVersion}}。
- params 为 JSON 对象；响应信封 {id, event, data, sessionId?, details?}。
- terminalEvents 中除流式事件外都作为 call() 的终态（result/error 之外的方法特定终态也覆盖）。
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
CONTRACT_PATH = REPO_ROOT / "contracts" / "daemon-contract.json"
SDK_DIR = REPO_ROOT / "sdks" / "typescript"

STREAM_EVENTS = {"delta", "step", "tool_call", "tool_result", "usage", "plan"}


def to_pascal(value: str) -> str:
    return "".join(part.capitalize() for part in re.split(r"[^a-zA-Z0-9]+", value) if part)


def to_camel(value: str) -> str:
    pascal = to_pascal(value)
    return pascal[0].lower() + pascal[1:] if pascal else value


def load_contract() -> dict:
    return json.loads(CONTRACT_PATH.read_text(encoding="utf-8"))


# --------------------------------------------------------------------------- #
# contract.ts（纯生成）
# --------------------------------------------------------------------------- #

def render_contract_ts(contract: dict) -> str:
    methods = contract["methods"]
    lines = [
        "/* eslint-disable */",
        "/**",
        f" * 由 tools/generate_ts_sdk.py 生成——单源 contracts/daemon-contract.json"
        f"（contractVersion {contract['contractVersion']}，协议 {contract['protocolVersion']}）。",
        " * 不要手改；重新生成：python tools/generate_ts_sdk.py",
        " */",
        "",
        f"export const CONTRACT_VERSION = {json.dumps(contract['contractVersion'])};",
        f"export const PROTOCOL_VERSION = {json.dumps(contract['protocolVersion'])};",
        "",
        "/** 契约声明的全部 RPC 方法名（114 个）。 */",
        "export type MethodName =",
    ]
    names = [m["name"] for m in methods]
    for i in range(0, len(names), 4):
        chunk = " | ".join(json.dumps(n) for n in names[i : i + 4])
        lines.append(f"  | {chunk}" if i > 0 else f"  {chunk}")
    lines += [
        "",
        "/** 方法元数据：参数 schema、类别、终态事件。 */",
        "export interface MethodInfo {",
        "  name: MethodName;",
        "  category: string;",
        "  description: string;",
        "  paramsSchema: Record<string, unknown>;",
        "  terminalEvents: string[];",
        "}",
        "",
        "export const METHODS: readonly MethodInfo[] = [",
    ]
    for m in methods:
        lines += [
            "  {",
            f"    name: {json.dumps(m['name'])},",
            f"    category: {json.dumps(m['category'])},",
            f"    description: {json.dumps(m['description'])},",
            f"    paramsSchema: {json.dumps(m['params'], ensure_ascii=False, sort_keys=True)},",
            f"    terminalEvents: {json.dumps(m['terminalEvents'])},",
            "  },",
        ]
    lines += ["];", ""]
    return "\n".join(lines)


# --------------------------------------------------------------------------- #
# client.ts（运行时模板 + 生成的方法桩）
# --------------------------------------------------------------------------- #

CLIENT_RUNTIME = r"""/* eslint-disable */
/**
 * haoyue Daemon TypeScript 客户端（运行时部分）。
 * 与 src/contract.ts 一样由 tools/generate_ts_sdk.py 维护；方法桩按契约类别生成，
 * 也可直接使用 call(method, params) 调用任意契约方法。
 *
 * 传输：JSONL over Windows named pipe / Unix domain socket（与桌面端同一端点）。
 * 握手：首条消息 {id, method:"handshake", params:{token, protocolVersion}}；
 *       token 缺省读取 ~/.haoyue/daemon.token（与 daemon、CLI 同源）。
 */

import { EventEmitter } from "node:events";
import * as net from "node:net";
import * as fs from "node:fs";
import * as os from "node:os";
import * as path from "node:path";
import { METHODS, PROTOCOL_VERSION, type MethodName, type MethodInfo } from "./contract.js";

/** 响应信封（契约 envelope.response）。 */
export interface DaemonEvent {
  id: number;
  event: string;
  data: string;
  sessionId?: string;
  /** error 事件必含 code；其余事件可能携带结构化元数据（JSON 字符串）。 */
  details?: string;
}

export interface DaemonClientOptions {
  /** Windows 命名管道全名；缺省按当前用户推导 \\.\pipe\haoyue-<user>（与 daemon 端一致）。 */
  pipeName?: string;
  /** Unix domain socket 路径；缺省 ~/.haoyue/daemon.sock。 */
  socketPath?: string;
  /** 握手令牌；缺省读取 ~/.haoyue/daemon.token。 */
  token?: string;
  /** haoyue 主目录；缺省 ~/.haoyue。 */
  homeDir?: string;
  /** 握手超时毫秒；缺省 15000（与 daemon 端 HandshakeTimeoutSeconds 对齐）。 */
  handshakeTimeoutMs?: number;
}

/** daemon 端返回的 error 事件（details 必含 code）。 */
export class DaemonError extends Error {
  constructor(
    public readonly code: string,
    message: string,
    public readonly method?: string,
  ) {
    super(message);
    this.name = "DaemonError";
  }
}

function sanitizePipeUser(raw: string | undefined): string {
  if (!raw) return "local";
  return raw.replace(/[^a-zA-Z0-9._-]/g, "-");
}

function defaultDaemonHome(): string {
  return process.env.HAOYUE_HOME ?? path.join(os.homedir(), ".haoyue");
}

function defaultToken(homeDir: string): string {
  return fs.readFileSync(path.join(homeDir, "daemon.token"), "utf-8").trim();
}

function defaultEndpoint(): { pipeName?: string; socketPath?: string } {
  if (process.platform === "win32") {
    const pipe = `\\\\.\\pipe\\haoyue-${sanitizePipeUser(process.env.USERNAME)}`;
    return { pipeName: pipe };
  }
  return { socketPath: path.join(defaultDaemonHome(), "daemon.sock") };
}

/**
 * haoyue daemon 客户端。
 *
 * ```ts
 * const client = new DaemonClient();           // token/端点自动发现
 * await client.connect();                      // 完成 token 握手
 * const pong = await client.call("ping");      // → "pong"
 * const reply = await client.chat_send({ session: "s1", text: "你好" });
 * ```
 */
export class DaemonClient {
  private socket?: net.Socket;
  private nextId = 1;
  private readonly pending = new Map<
    number,
    { resolve: (event: DaemonEvent) => void; reject: (err: Error) => void; terminals: Set<string> }
  >();
  private readonly buffer = new Map<number, string>();
  private readonly emitter = new EventEmitter();
  private readonly options: Required<Pick<DaemonClientOptions, "handshakeTimeoutMs">> &
    DaemonClientOptions;
  private connected = false;

  constructor(options: DaemonClientOptions = {}) {
    const endpoint = options.pipeName || options.socketPath ? {} : defaultEndpoint();
    this.options = {
      ...endpoint,
      homeDir: options.homeDir ?? defaultDaemonHome(),
      token: options.token ?? defaultToken(options.homeDir ?? defaultDaemonHome()),
      handshakeTimeoutMs: options.handshakeTimeoutMs ?? 15_000,
      ...options,
    };
  }

  /** 已解析的握手令牌（诊断用；避免打印）。 */
  get token(): string {
    return this.options.token ?? "";
  }

  /** 连接 daemon 并完成 token 握手。重复调用幂等（已连接直接返回）。 */
  async connect(): Promise<void> {
    if (this.connected) return;
    const socket = this.options.pipeName
      ? net.connect({ path: this.options.pipeName })
      : net.connect({ path: this.options.socketPath! });
    socket.setEncoding("utf-8");

    let lineBuf = "";
    socket.on("data", (chunk: string) => {
      lineBuf += chunk;
      let idx: number;
      while ((idx = lineBuf.indexOf("\n")) >= 0) {
        const line = lineBuf.slice(0, idx).trim();
        lineBuf = lineBuf.slice(idx + 1);
        if (line) this.handleLine(line);
      }
    });
    const socketError = new Promise<never>((_, reject) =>
      socket.on("error", (err) => reject(err)),
    );
    socket.on("close", () => this.handleClose());
    this.socket = socket;

    // 握手：token 不对/版本不匹配时 daemon 回 error 并断开。
    const handshake = await Promise.race([
      this.rawCall("handshake", { token: this.token, protocolVersion: PROTOCOL_VERSION }, new Set(["ready"])),
      socketError,
      new Promise<never>((_, reject) =>
        setTimeout(() => reject(new DaemonError("handshakeTimeout", "握手超时")), this.options.handshakeTimeoutMs),
      ),
    ]);
    if (handshake.event === "error") {
      const { code } = this.parseDetails(handshake);
      throw new DaemonError(code ?? "unauthorized", handshake.data, "handshake");
    }
    this.connected = true;
  }

  /** 是否已完成握手。 */
  get isConnected(): boolean {
    return this.connected;
  }

  /** 优雅关闭连接。 */
  close(): void {
    this.socket?.end();
    this.socket = undefined;
    this.connected = false;
  }

  /**
   * 调用任意契约方法，返回其终态事件。
   * 终态 = 契约 terminalEvents 中的事件（result/error 之外的特定终态同样覆盖）；
   * 流式事件（delta/step/…）通过 onStream 观察，不会提前 resolve。
   */
  async call(method: MethodName, params: Record<string, unknown> = {}): Promise<DaemonEvent> {
    const info = METHODS.find((m) => m.name === method);
    const terminals = new Set(info?.terminalEvents ?? ["result"]);
    terminals.delete("error"); // error 统一走 reject
    return this.rawCall(method, params, terminals);
  }

  /** 订阅流式事件（delta/step/tool_call/…）；返回取消订阅函数。 */
  onStream(listener: (event: DaemonEvent) => void): () => void {
    this.emitter.on("stream", listener);
    return () => this.emitter.off("stream", listener);
  }

  /** 订阅广播事件（不携带请求 id 的服务端推送）。 */
  onBroadcast(listener: (event: DaemonEvent) => void): () => void {
    this.emitter.on("broadcast", listener);
    return () => this.emitter.off("broadcast", listener);
  }

  // ---- 内部 --------------------------------------------------------------

  private async rawCall(
    method: string,
    params: Record<string, unknown>,
    terminals: Set<string>,
  ): Promise<DaemonEvent> {
    if (!this.socket) throw new DaemonError("notConnected", "未连接：先调用 connect()", method);
    const id = this.nextId++;
    const request = { id, method, params };
    const promise = new Promise<DaemonEvent>((resolve, reject) =>
      this.pending.set(id, { resolve, reject, terminals }),
    );
    this.socket.write(JSON.stringify(request) + "\n");
    return promise;
  }

  private handleLine(line: string): void {
    let event: DaemonEvent;
    try {
      event = JSON.parse(line) as DaemonEvent;
    } catch {
      return; // 非 JSON 行（不会发生）静默丢弃
    }
    if (event.id === 0 || event.id === undefined) {
      this.emitter.emit("broadcast", event);
      return;
    }
    const pending = this.pending.get(event.id);
    if (!pending) {
      this.emitter.emit("stream", event);
      return;
    }
    if (event.event === "error") {
      const { code, payload } = this.parseDetails(event);
      this.pending.delete(event.id);
      // internalError 的 details 携带 method（服务端实现故障）；unknownMethod 则没有，
      // 二者语义不同，调用方不应再对 message 做字符串匹配。
      const failedMethod = (payload as { method?: unknown } | undefined)?.method;
      pending.reject(
        new DaemonError(
          code ?? "requestFailed",
          event.data,
          typeof failedMethod === "string" ? failedMethod : undefined,
        ),
      );
      return;
    }
    // 已缓冲的流式事件先吐出（按到达顺序）。
    if (pending.terminals.has(event.event)) {
      this.pending.delete(event.id);
      pending.resolve(event);
      return;
    }
    this.emitter.emit("stream", event);
  }

  private parseDetails(event: DaemonEvent): { code?: string; payload?: unknown } {
    if (!event.details) return {};
    try {
      const parsed = JSON.parse(event.details) as { code?: string };
      return { code: parsed.code, payload: parsed };
    } catch {
      return { code: event.details };
    }
  }

  private handleClose(): void {
    this.connected = false;
    const err = new DaemonError("connectionClosed", "连接已关闭");
    for (const pending of this.pending.values()) pending.reject(err);
    this.pending.clear();
    this.emitter.emit("close");
  }
}
"""

def render_client_ts(contract: dict) -> str:
    """运行时模板 + 按类别生成的类型化方法桩。"""
    groups: dict[str, list[dict]] = {}
    for m in contract["methods"]:
        groups.setdefault(m["category"], []).append(m)

    blocks = []
    for category in sorted(groups):
        lines = [f"  /** {category} 类别方法。 */", f"  readonly {to_camel(category)} = {"{"}"]
        for m in groups[category]:
            jsdoc = m["description"].replace("*/", "* /")
            lines += [
                "    /**",
                f"     * {jsdoc}",
                f"     * 终态事件：{', '.join(m['terminalEvents'])}",
                "     */",
                f"    {to_camel(m['name'])}: (params: Record<string, unknown> = {{}}): Promise<DaemonEvent> =>",
                f"      this.call({json.dumps(m['name'])}, params),",
            ]
        lines.append("  };")
        blocks.append("\n".join(lines))

    # 注入生成块：放在类体最后一个私有段之前。
    runtime = CLIENT_RUNTIME
    anchor = "  // ---- 内部 --------------------------------------------------------------"
    generated = "\n\n".join(blocks) + "\n\n"
    assert anchor in runtime, "client 模板锚点丢失"
    return runtime.replace(anchor, generated + anchor, 1)


INDEX_TS = """/**
 * @haoyue/daemon-client — haoyue daemon 的类型化 TypeScript 客户端。
 * 由 tools/generate_ts_sdk.py 生成；不要手改。
 */
export { DaemonClient, DaemonError, type DaemonClientOptions, type DaemonEvent } from "./client.js";
export { METHODS, PROTOCOL_VERSION, CONTRACT_VERSION, type MethodName, type MethodInfo } from "./contract.js";
"""

PACKAGE_JSON = {
    "name": "@haoyue/daemon-client",
    "version": "1.0.0",
    "description": "haoyue daemon 的类型化 TypeScript 客户端（契约驱动生成，114 个 RPC 方法全覆盖）",
    "type": "module",
    "main": "dist/index.js",
    "types": "dist/index.d.ts",
    "scripts": {
        "build": "tsc -p tsconfig.json",
        "example": "node --experimental-strip-types examples/hello.ts",
    },
    "devDependencies": {
        "@types/node": "^22.0.0",
        "typescript": "^5.6.0",
    },
    "license": "MIT",
}

TSCONFIG_JSON = {
    "compilerOptions": {
        "target": "ES2022",
        "module": "Node16",
        "moduleResolution": "Node16",
        "declaration": True,
        "outDir": "dist",
        "strict": True,
        "skipLibCheck": True,
    },
    "include": ["src/**/*.ts"],
}

EXAMPLE_TS = """/**
 * 从安装到首回合 ≤10 行代码的示例。
 * 前置：haoyue daemon 运行中（haoyue daemon），token 位于 ~/.haoyue/daemon.token。
 */
import { DaemonClient } from "../src/index.js";

const client = new DaemonClient();
await client.connect();
console.log(await client.call("ping")); // → { id: 1, event: "result", data: "pong", ... }
client.close();
"""

README_MD = """# @haoyue/daemon-client

haoyue daemon 的类型化 TypeScript 客户端。**由 `tools/generate_ts_sdk.py` 从
`contracts/daemon-contract.json` 生成**——契约是唯一单源，手改会被覆盖。

## 安装即用（首回合 ≤10 行）

```ts
import { DaemonClient } from "@haoyue/daemon-client";

const client = new DaemonClient();
await client.connect();                      // 自动读 ~/.haoyue/daemon.token 完成握手
const pong = await client.call("ping");      // 任意契约方法
client.close();
```

## 类型化方法桩

契约中的 114 个方法按类别生成为实例属性（100% 覆盖，CI 有护栏校验）：

```ts
await client.knowledge_search({ query: "部署口径" });
await client.session_list({});
await client.model_list({});
```

方法桩与 `call(method, params)` 等价；参数 schema 见 `src/contract.ts` 的 `METHODS`。

## 传输与握手

- Windows：命名管道 `\\\\.\\pipe\\haoyue-<用户名净化后缀>`（与 daemon/桌面端同一推导规则）。
- Unix：`~/.haoyue/daemon.sock`。
- 握手：首条消息 `{"id":1,"method":"handshake","params":{"token":…,"protocolVersion":"2.2"}}`，
  token 缺省读 `~/.haoyue/daemon.token`；主版本不一致时 daemon 拒绝并回 error。

## 流式与广播

```ts
const off = client.onStream((event) => {
  if (event.event === "delta") process.stdout.write(event.data);
});
await client.chat_send({ session: sessionId, text: "你好" });
off();
```

## 重新生成

```bash
python tools/generate_ts_sdk.py          # 生成
python tools/generate_ts_sdk.py --check  # CI 校验：产物与契约一致
```
"""


def generate(contract: dict) -> dict[str, str]:
    return {
        "src/contract.ts": render_contract_ts(contract),
        "src/client.ts": render_client_ts(contract),
        "src/index.ts": INDEX_TS,
        "package.json": json.dumps(PACKAGE_JSON, ensure_ascii=False, indent=2) + "\n",
        "tsconfig.json": json.dumps(TSCONFIG_JSON, indent=2) + "\n",
        "examples/hello.ts": EXAMPLE_TS,
        "README.md": README_MD,
    }


def check(contract: dict) -> list[str]:
    """护栏：产物必须覆盖契约 100% 方法且指纹一致。"""
    problems: list[str] = []
    contract_ts = SDK_DIR / "src" / "contract.ts"
    client_ts = SDK_DIR / "src" / "client.ts"
    for rel in ("src/contract.ts", "src/client.ts", "src/index.ts"):
        if not (SDK_DIR / rel).exists():
            problems.append(f"缺少产物 {rel}")
    if problems:
        return problems

    generated = generate(contract)
    if contract_ts.read_text(encoding="utf-8") != generated["src/contract.ts"]:
        problems.append("src/contract.ts 与契约不一致（需重新生成）")
    if client_ts.read_text(encoding="utf-8") != generated["src/client.ts"]:
        problems.append("src/client.ts 与契约不一致（需重新生成）")

    methods = contract["methods"]
    emitted = contract_ts.read_text(encoding="utf-8")
    client_text = client_ts.read_text(encoding="utf-8")
    missing = [
        m["name"]
        for m in methods
        if json.dumps(m["name"]) not in emitted or f'call({json.dumps(m["name"])}' not in client_text
    ]
    if missing:
        problems.append(f"{len(missing)} 个方法未覆盖：{missing[:8]}…")
    return problems


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true", help="校验现有产物与契约一致（不写文件）")
    args = parser.parse_args()

    contract = load_contract()
    if args.check:
        problems = check(contract)
        if problems:
            for problem in problems:
                print(f"[FAIL] {problem}", file=sys.stderr)
            return 1
        print(f"[OK] SDK 与契约一致：{len(contract['methods'])} 个方法全覆盖。")
        return 0

    for rel, content in generate(contract).items():
        target = SDK_DIR / rel
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(content, encoding="utf-8", newline="\n")
        print(f"生成 {target.relative_to(REPO_ROOT)}")

    # 自检：生成即校验。
    problems = check(contract)
    if problems:
        for problem in problems:
            print(f"[FAIL] {problem}", file=sys.stderr)
        return 1
    print(f"[OK] 自检通过：{len(contract['methods'])} 个方法全覆盖。")
    return 0


if __name__ == "__main__":
    sys.exit(main())
