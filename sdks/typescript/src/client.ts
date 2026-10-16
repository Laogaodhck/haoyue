/* eslint-disable */
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

  /** chat 类别方法。 */
  readonly chat = {
    /**
     * 发起一次 Agent 回合（流式事件 + done 终态）
     * 终态事件：done, cancelled, error
     */
    chat: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("chat", params),
    /**
     * chat 的显式命名别名
     * 终态事件：done, cancelled, error
     */
    agentRunturn: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("agent.runTurn", params),
    /**
     * 向活动回合注入引导消息
     * 终态事件：result, error
     */
    agentSteer: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("agent.steer", params),
    /**
     * 响应「本次允许」审批请求（内置高危守卫挂起的工具调用）
     * 终态事件：result, error
     */
    agentRespondapproval: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("agent.respondApproval", params),
    /**
     * 取消活动回合；缺省取消本连接全部
     * 终态事件：result, error
     */
    agentCancel: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("agent.cancel", params),
    /**
     * 撤销上一回合的文件修改
     * 终态事件：result, error
     */
    agentUndo: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("agent.undo", params),
    /**
     * 查询 daemon 崩溃时被中断的会话 id
     * 终态事件：result, error
     */
    agentInterrupted: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("agent.interrupted", params),
    /**
     * 提交回合负反馈，进入进化信号
     * 终态事件：result, error
     */
    feedbackTurn: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("feedback.turn", params),
  };

  /** config 类别方法。 */
  readonly config = {
    /**
     * 配置字段级补丁（多端单写者收敛）
     * 终态事件：result, error
     */
    configSave: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("config.save", params),
    /**
     * 配置一致性诊断
     * 终态事件：result, error
     */
    configStatus: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("config.status", params),
    /**
     * 重建配置存储
     * 终态事件：result, error
     */
    configRebuild: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("config.rebuild", params),
    /**
     * 查询智能路由配置
     * 终态事件：result, error
     */
    routingGet: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("routing.get", params),
    /**
     * 更新智能路由配置
     * 终态事件：result, error
     */
    routingSet: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("routing.set", params),
    /**
     * 查询高级配置
     * 终态事件：result, error
     */
    advancedGet: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("advanced.get", params),
    /**
     * 更新高级配置
     * 终态事件：result, error
     */
    advancedSet: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("advanced.set", params),
    /**
     * 查询允许的网站白名单（外部网站访问）
     * 终态事件：result, error
     */
    webAllowedget: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("web.allowedGet", params),
    /**
     * 整体替换允许的网站白名单（无效条目整体拒绝），立即生效
     * 终态事件：result, error
     */
    webAllowedset: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("web.allowedSet", params),
    /**
     * 查询智能体设置
     * 终态事件：result, error
     */
    agentConfigGet: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("agent.config.get", params),
    /**
     * 部分更新智能体设置（未传字段保持原值）
     * 终态事件：result, error
     */
    agentConfigSet: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("agent.config.set", params),
    /**
     * 提示词优化（无状态改写）
     * 终态事件：result, error
     */
    promptOptimize: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("prompt.optimize", params),
  };

  /** connectors 类别方法。 */
  readonly connectors = {
    /**
     * 列出连接器目录与安装状态
     * 终态事件：result, error
     */
    connectorsList: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("connectors.list", params),
    /**
     * 一键安装连接器（占位符填充、secret 加密落盘）
     * 终态事件：result, error
     */
    connectorsInstall: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("connectors.install", params),
    /**
     * 卸载连接器
     * 终态事件：result, error
     */
    connectorsUninstall: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("connectors.uninstall", params),
  };

  /** core 类别方法。 */
  readonly core = {
    /**
     * 探活，返回 pong
     * 终态事件：pong, error
     */
    ping: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("ping", params),
    /**
     * 协议版本、能力与方法清单
     * 终态事件：result, error
     */
    protocolInfo: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("protocol.info", params),
    /**
     * 导出完整契约文档（JSON Schema）
     * 终态事件：result, error
     */
    contractExport: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("contract.export", params),
    /**
     * 查询事件溯源日志（重连补播：稳定事件名 + camelCase 载荷 + 会话归属）
     * 终态事件：result, error
     */
    eventsRecent: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("events.recent", params),
    /**
     * 请求 daemon 优雅退出
     * 终态事件：bye, error
     */
    shutdown: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("shutdown", params),
  };

  /** diagnostics 类别方法。 */
  readonly diagnostics = {
    /**
     * 健康检查（人类可读文本）
     * 终态事件：result, error
     */
    doctor: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("doctor", params),
    /**
     * 运行健康检查（结构化结果）
     * 终态事件：result, error
     */
    doctorRun: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("doctor.run", params),
    /**
     * 列出文件写锁
     * 终态事件：result, error
     */
    lockList: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("lock.list", params),
  };

  /** evolution 类别方法。 */
  readonly evolution = {
    /**
     * 聚合失败信号的缺陷报告（含健康分、趋势与分布）
     * 终态事件：result, error
     */
    evolutionInspect: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("evolution.inspect", params),
    /**
     * 发起反思回合产出技能草稿
     * 终态事件：result, error
     */
    evolutionReflect: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("evolution.reflect", params),
    /**
     * 待审技能草稿清单（含状态与草稿文件内容）
     * 终态事件：result, error
     */
    evolutionPendingList: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("evolution.pending-list", params),
    /**
     * 人工终审技能草稿（adopt 前可用 prompt 改写提示词）
     * 终态事件：result, error
     */
    evolutionDecide: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("evolution.decide", params),
    /**
     * 反思回合历史（触发方式与处理结果）
     * 终态事件：result, error
     */
    evolutionHistory: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("evolution.history", params),
    /**
     * 进化效果统计（采纳率、技能使用与缺陷复发）
     * 终态事件：result, error
     */
    evolutionStats: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("evolution.stats", params),
    /**
     * 读取自动反思配置
     * 终态事件：result, error
     */
    evolutionConfigGet: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("evolution.config.get", params),
    /**
     * 更新自动反思配置
     * 终态事件：result, error
     */
    evolutionConfigSet: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("evolution.config.set", params),
  };

  /** knowledge 类别方法。 */
  readonly knowledge = {
    /**
     * 列出知识条目（含笔记本/来源归属）
     * 终态事件：result, error
     */
    knowledgeList: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("knowledge.list", params),
    /**
     * 高容错知识检索（配了嵌入模型时走语义+词法混合排序）
     * 终态事件：result, error
     */
    knowledgeSearch: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("knowledge.search", params),
    /**
     * 保存知识条目（同题 upsert，或按 id 原地更新）
     * 终态事件：result, error
     */
    knowledgeSave: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("knowledge.save", params),
    /**
     * 删除知识条目
     * 终态事件：result, error
     */
    knowledgeDelete: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("knowledge.delete", params),
    /**
     * 批量导入文件到知识库
     * 终态事件：result, error
     */
    knowledgeImport: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("knowledge.import", params),
    /**
     * 聚合标签与出现次数
     * 终态事件：result, error
     */
    knowledgeTags: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("knowledge.tags", params),
    /**
     * 导出整个范围为 Markdown 文本
     * 终态事件：result, error
     */
    knowledgeExport: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("knowledge.export", params),
    /**
     * 读取检索同义词表
     * 终态事件：result, error
     */
    knowledgeSynonymsGet: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("knowledge.synonyms.get", params),
    /**
     * 写入检索同义词表（热重载）
     * 终态事件：result, error
     */
    knowledgeSynonymsSave: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("knowledge.synonyms.save", params),
    /**
     * 列出知识笔记本
     * 终态事件：result, error
     */
    knowledgeNotebookList: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("knowledge.notebook.list", params),
    /**
     * 创建或更新笔记本（同库内同名拒绝）
     * 终态事件：result, error
     */
    knowledgeNotebookSave: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("knowledge.notebook.save", params),
    /**
     * 删除笔记本（含其来源与条目；默认笔记本不可删）
     * 终态事件：result, error
     */
    knowledgeNotebookDelete: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("knowledge.notebook.delete", params),
    /**
     * 添加来源（text/url/file），长文自动分块入库
     * 终态事件：result, error
     */
    knowledgeSourceAdd: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("knowledge.source.add", params),
    /**
     * 列出笔记本内的来源
     * 终态事件：result, error
     */
    knowledgeSourceList: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("knowledge.source.list", params),
    /**
     * 窗口化读取来源全文
     * 终态事件：result, error
     */
    knowledgeSourceRead: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("knowledge.source.read", params),
    /**
     * 删除来源及其分块条目（手工条目保留）
     * 终态事件：result, error
     */
    knowledgeSourceDelete: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("knowledge.source.delete", params),
    /**
     * 重新抓取/读取来源并重建分块条目
     * 终态事件：result, error
     */
    knowledgeSourceRefresh: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("knowledge.source.refresh", params),
    /**
     * 跨笔记本带出处检索（配了嵌入模型时走语义+词法混合排序）
     * 终态事件：result, error
     */
    knowledgeRetrieve: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("knowledge.retrieve", params),
    /**
     * 语义索引状态（是否启用/模型/已嵌入条数/待建条数/不可用原因）
     * 终态事件：result, error
     */
    knowledgeSemanticStatus: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("knowledge.semantic.status", params),
    /**
     * 分批建立语义索引（一次嵌入 batch 条，返回剩余 pending）
     * 终态事件：result, error
     */
    knowledgeSemanticBuild: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("knowledge.semantic.build", params),
  };

  /** mcp 类别方法。 */
  readonly mcp = {
    /**
     * 列出 MCP 服务器
     * 终态事件：result, error
     */
    mcpList: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("mcp.list", params),
    /**
     * 创建或更新 MCP 服务器
     * 终态事件：result, error
     */
    mcpUpsert: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("mcp.upsert", params),
    /**
     * 删除 MCP 服务器
     * 终态事件：result, error
     */
    mcpRemove: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("mcp.remove", params),
    /**
     * 重载 MCP 服务器
     * 终态事件：result, error
     */
    mcpReload: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("mcp.reload", params),
  };

  /** memory 类别方法。 */
  readonly memory = {
    /**
     * 读取 MEMORY.md 长期记忆
     * 终态事件：result, error
     */
    memoryGet: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("memory.get", params),
    /**
     * 写入 MEMORY.md 长期记忆
     * 终态事件：result, error
     */
    memorySave: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("memory.save", params),
    /**
     * 列出 AGENTS.md 工作区规则
     * 终态事件：result, error
     */
    rulesList: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("rules.list", params),
    /**
     * 写入工作区规则
     * 终态事件：result, error
     */
    rulesSave: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("rules.save", params),
    /**
     * 删除工作区规则文件
     * 终态事件：result, error
     */
    rulesDelete: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("rules.delete", params),
  };

  /** model 类别方法。 */
  readonly model = {
    /**
     * 扫描本地 GGUF 模型
     * 终态事件：result, error
     */
    localModels: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("local.models", params),
    /**
     * 当前提供商的模型 id 清单
     * 终态事件：result, error
     */
    modelList: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("model.list", params),
    /**
     * 模型目录（含能力标签）
     * 终态事件：result, error
     */
    modelCatalog: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("model.catalog", params),
    /**
     * 切换活跃模型
     * 终态事件：result, error
     */
    modelSwitch: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("model.switch", params),
    /**
     * 单模型连通性测试
     * 终态事件：result, error
     */
    modelTest: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("model.test", params),
    /**
     * 本地模型预检状态
     * 终态事件：result, error
     */
    modelStatus: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("model.status", params),
    /**
     * 预热本地模型（权重装入驻留槽，不占推理槽；进度经 model.load.progress 广播）
     * 终态事件：result, error
     */
    modelPreload: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("model.preload", params),
    /**
     * 卸载全部常驻本地权重（归还内存/显存；活跃推理结束后释放）
     * 终态事件：result, error
     */
    modelUnload: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("model.unload", params),
    /**
     * 模型加载配置页全量数据（含加载设置 / mmproj / 加载报告）
     * 终态事件：result, error
     */
    modelConfigGet: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("model.config.get", params),
    /**
     * 更新模型元数据与加载配置
     * 终态事件：result, error
     */
    modelUpdate: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("model.update", params),
    /**
     * 本地模型基准测试（多轮 TTFT/decode/prefill 速率与 KV 复用命中率）
     * 终态事件：result, error
     */
    modelBenchmark: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("model.benchmark", params),
  };

  /** project 类别方法。 */
  readonly project = {
    /**
     * 列出注册项目
     * 终态事件：result, error
     */
    projectList: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("project.list", params),
    /**
     * 注册或更新项目
     * 终态事件：result, error
     */
    projectUpsert: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("project.upsert", params),
    /**
     * 移除项目
     * 终态事件：result, error
     */
    projectRemove: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("project.remove", params),
  };

  /** provider 类别方法。 */
  readonly provider = {
    /**
     * 列出全部提供商
     * 终态事件：result, error
     */
    providerList: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("provider.list", params),
    /**
     * 创建或更新提供商
     * 终态事件：result, error
     */
    providerUpsert: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("provider.upsert", params),
    /**
     * 切换活跃提供商
     * 终态事件：result, error
     */
    providerUse: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("provider.use", params),
    /**
     * 删除提供商
     * 终态事件：result, error
     */
    providerRemove: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("provider.remove", params),
    /**
     * 连通性探测
     * 终态事件：result, error
     */
    providerTest: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("provider.test", params),
    /**
     * 从远端拉取模型列表
     * 终态事件：result, error
     */
    providerModelsFetch: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("provider.models.fetch", params),
  };

  /** schedule 类别方法。 */
  readonly schedule = {
    /**
     * 列出定时任务
     * 终态事件：result, error
     */
    scheduleList: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("schedule.list", params),
    /**
     * 新建定时任务
     * 终态事件：result, error
     */
    scheduleCreate: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("schedule.create", params),
    /**
     * 更新定时任务
     * 终态事件：result, error
     */
    scheduleUpdate: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("schedule.update", params),
    /**
     * 启停定时任务
     * 终态事件：result, error
     */
    scheduleToggle: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("schedule.toggle", params),
    /**
     * 删除定时任务
     * 终态事件：result, error
     */
    scheduleDelete: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("schedule.delete", params),
    /**
     * 立即执行一次定时任务
     * 终态事件：result, error
     */
    scheduleRun: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("schedule.run", params),
  };

  /** session 类别方法。 */
  readonly session = {
    /**
     * 列出会话头
     * 终态事件：result, error
     */
    sessionList: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("session.list", params),
    /**
     * 全文检索会话
     * 终态事件：result, error
     */
    sessionSearch: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("session.search", params),
    /**
     * 分页读取会话（头字段 + 消息数组；图片字节不内联）
     * 终态事件：result, error
     */
    sessionGet: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("session.get", params),
    /**
     * 按需读取会话中单张图片的 base64 字节
     * 终态事件：result, error
     */
    sessionImage: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("session.image", params),
    /**
     * 截断会话消息
     * 终态事件：result, error
     */
    sessionTruncate: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("session.truncate", params),
    /**
     * 从既有会话分叉新会话
     * 终态事件：result, error
     */
    sessionFork: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("session.fork", params),
    /**
     * 更新会话元数据
     * 终态事件：result, error
     */
    sessionUpdate: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("session.update", params),
    /**
     * 归档会话
     * 终态事件：result, error
     */
    sessionArchive: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("session.archive", params),
    /**
     * 删除会话
     * 终态事件：result, error
     */
    sessionDelete: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("session.delete", params),
    /**
     * 恢复会话为连接的遗留会话
     * 终态事件：result, error
     */
    sessionResume: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("session.resume", params),
    /**
     * 新建会话
     * 终态事件：result, error
     */
    sessionNew: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("session.new", params),
  };

  /** skill 类别方法。 */
  readonly skill = {
    /**
     * 列出已装技能
     * 终态事件：result, error
     */
    skillList: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("skill.list", params),
    /**
     * 导入技能目录
     * 终态事件：result, error
     */
    skillImport: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("skill.import", params),
    /**
     * 启停技能
     * 终态事件：result, error
     */
    skillToggle: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("skill.toggle", params),
    /**
     * 列出官方技能库
     * 终态事件：result, error
     */
    skillOfficialList: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("skill.official.list", params),
    /**
     * 安装官方技能
     * 终态事件：result, error
     */
    skillOfficialInstall: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("skill.official.install", params),
    /**
     * 列出内置领域专家
     * 终态事件：result, error
     */
    expertList: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("expert.list", params),
  };

  /** system 类别方法。 */
  readonly system = {
    /**
     * 工厂重置（清空全部数据）
     * 终态事件：result, error
     */
    factoryReset: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("factory.reset", params),
  };

  /** task 类别方法。 */
  readonly task = {
    /**
     * 提交后台 agent 回合（立即返回任务快照）
     * 终态事件：result, error
     */
    taskStart: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("task.start", params),
    /**
     * 查询单个后台任务
     * 终态事件：result, error
     */
    taskGet: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("task.get", params),
    /**
     * 列出后台任务（新→旧）
     * 终态事件：result, error
     */
    taskList: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("task.list", params),
    /**
     * 取消运行中的后台任务
     * 终态事件：result, error
     */
    taskCancel: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("task.cancel", params),
  };

  /** usage 类别方法。 */
  readonly usage = {
    /**
     * 查询用量聚合
     * 终态事件：result, error
     */
    usageGet: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("usage.get", params),
    /**
     * 查询按日用量时间线
     * 终态事件：result, error
     */
    usageTimeline: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("usage.timeline", params),
  };

  /** workspace 类别方法。 */
  readonly workspace = {
    /**
     * 查询当前工作区与模式
     * 终态事件：result, error
     */
    workspaceGet: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("workspace.get", params),
    /**
     * 切换工作区
     * 终态事件：result, error
     */
    workspaceOpen: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("workspace.open", params),
    /**
     * 列举工作区内文件，供输入框 @ 引用挑选（已跳过构建产物与凭证文件，只给相对路径）
     * 终态事件：result, error
     */
    workspaceFiles: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("workspace.files", params),
    /**
     * 初始化工作区（AGENTS.md 与配置结构）
     * 终态事件：result, error
     */
    workspaceInit: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("workspace.init", params),
    /**
     * 查询当前 Agent 模式
     * 终态事件：result, error
     */
    agentModeGet: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("agent.mode.get", params),
    /**
     * 切换 Agent 模式
     * 终态事件：result, error
     */
    agentModeSwitch: (params: Record<string, unknown> = {}): Promise<DaemonEvent> =>
      this.call("agent.mode.switch", params),
  };

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
      const { code } = this.parseDetails(event);
      this.pending.delete(event.id);
      pending.reject(new DaemonError(code ?? "requestFailed", event.data));
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
