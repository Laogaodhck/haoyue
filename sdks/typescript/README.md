# @haoyue/daemon-client

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

契约中的 125 个方法按类别生成为实例属性（100% 覆盖，CI 有护栏校验）：

```ts
await client.knowledge_search({ query: "部署口径" });
await client.session_list({});
await client.model_list({});
```

方法桩与 `call(method, params)` 等价；参数 schema 见 `src/contract.ts` 的 `METHODS`。

## 传输与握手

- Windows：命名管道 `\\.\pipe\haoyue-<用户名净化后缀>`（与 daemon/桌面端同一推导规则）。
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
