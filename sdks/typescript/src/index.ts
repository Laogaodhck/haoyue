/**
 * @haoyue/daemon-client — haoyue daemon 的类型化 TypeScript 客户端。
 * 由 tools/generate_ts_sdk.py 生成；不要手改。
 */
export { DaemonClient, DaemonError, type DaemonClientOptions, type DaemonEvent } from "./client.js";
export { METHODS, PROTOCOL_VERSION, CONTRACT_VERSION, type MethodName, type MethodInfo } from "./contract.js";
