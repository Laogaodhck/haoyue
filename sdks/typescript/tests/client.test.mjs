/**
 * SDK 集成测试：起一个最小 fake daemon（JSONL over 命名管道 / Unix socket），
 * 验证握手终态事件、请求/响应对应、错误码透传与方法桩生成。不依赖真实 daemon。
 */
import assert from "node:assert/strict";
import fs from "node:fs";
import net from "node:net";
import os from "node:os";
import path from "node:path";
import { test } from "node:test";
import { DaemonClient, DaemonError } from "../dist/index.js";

async function startFakeDaemon(handler) {
  const isWindows = process.platform === "win32";
  const address = isWindows
    ? `\\\\.\\pipe\\haoyue-sdk-test-${process.pid}-${Math.random().toString(16).slice(2)}`
    : path.join(fs.mkdtempSync(path.join(os.tmpdir(), "haoyue-sdk-test-")), "daemon.sock");

  const sockets = new Set();
  const server = net.createServer((socket) => {
    sockets.add(socket);
    socket.on("close", () => sockets.delete(socket));
    let buffer = "";
    socket.setEncoding("utf-8");
    socket.on("data", (chunk) => {
      buffer += chunk;
      let index;
      while ((index = buffer.indexOf("\n")) >= 0) {
        const line = buffer.slice(0, index).trim();
        buffer = buffer.slice(index + 1);
        if (!line) continue;
        const reply = handler(JSON.parse(line));
        if (reply) socket.write(JSON.stringify(reply) + "\n");
      }
    });
  });
  await new Promise((resolve, reject) => {
    server.once("error", reject);
    server.listen(address, resolve);
  });

  return {
    address,
    options: (isWindows ? { pipeName: address } : { socketPath: address }),
    close: () => new Promise((resolve) => {
      for (const socket of sockets) socket.destroy();
      server.close(resolve);
    }),
  };
}

function result(request, data) {
  return { id: request.id, event: "result", data };
}

test("handshake resolves on the daemon's result event, then calls round-trip", async () => {
  const calls = [];
  const daemon = await startFakeDaemon((request) => {
    calls.push(request);
    if (request.method === "handshake") {
      return result(request, JSON.stringify({ version: "2.2", transport: "jsonl" }));
    }
    return result(request, JSON.stringify({ method: request.method, params: request.params }));
  });
  try {
    const client = new DaemonClient({ ...daemon.options, token: "test-token", handshakeTimeoutMs: 2_000 });
    await client.connect();
    assert.equal(client.isConnected, true);
    assert.equal(calls[0].method, "handshake");
    assert.equal(calls[0].params.token, "test-token");

    const response = await client.workspace.workspaceGet({});
    assert.equal(response.event, "result");
    assert.equal(calls.at(-1).method, "workspace.get");

    // 信任方法桩按契约生成，且带参数往返。
    const trust = await client.workspace.workspaceTrust({ path: "/tmp/demo" });
    assert.equal(trust.event, "result");
    assert.deepEqual(calls.at(-1).params, { path: "/tmp/demo" });

    client.close();
  } finally {
    await daemon.close();
  }
});

test("handshake failure rejects with the daemon error code", async () => {
  const daemon = await startFakeDaemon((request) => ({
    id: request.id,
    event: "error",
    data: "authentication failed: missing or invalid handshake token",
    details: JSON.stringify({ code: "unauthorized" }),
  }));
  try {
    const client = new DaemonClient({ ...daemon.options, token: "wrong", handshakeTimeoutMs: 2_000 });
    await assert.rejects(
      () => client.connect(),
      (error) => error instanceof DaemonError && error.code === "unauthorized",
    );
    assert.equal(client.isConnected, false);
    client.close();
  } finally {
    await daemon.close();
  }
});
