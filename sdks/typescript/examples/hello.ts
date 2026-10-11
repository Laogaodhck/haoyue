/**
 * 从安装到首回合 ≤10 行代码的示例。
 * 前置：haoyue daemon 运行中（haoyue daemon），token 位于 ~/.haoyue/daemon.token。
 */
import { DaemonClient } from "../src/index.js";

const client = new DaemonClient();
await client.connect();
console.log(await client.call("ping")); // → { id: 1, event: "result", data: "pong", ... }
client.close();
