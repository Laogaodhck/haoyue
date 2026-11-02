/**
 * 「新增模型提供商 → 接口协议」的预设。
 *
 * 国内云厂商都提供 OpenAI 兼容端点，这里单独保留 kind 只是为了预填地址、给出模型
 * 示例并展示厂商标签；Runtime 侧统一按 OpenAI 兼容客户端处理（见 ProviderKinds）。
 */
export type ProviderKind =
  | 'openai'
  | 'anthropic'
  | 'aliyun'
  | 'volcengine'
  | 'tencent'
  | 'baidu'
  | 'zhipu'
  | 'deepseek'
  | 'local'

export interface ProviderProtocolOption {
  value: ProviderKind
  label: string
  description: string
  /** 选中协议时预填的 API 根地址；用户已自定义 Base URL 时不会覆盖。 */
  baseUrl?: string
  /** 模型 ID 输入框示例。 */
  modelHint?: string
  /** API Key 输入框占位。 */
  keyHint?: string
}

export const PROVIDER_PROTOCOLS: ProviderProtocolOption[] = [
  {
    value: 'openai',
    label: 'OpenAI 兼容',
    description: '兼容 OpenAI Chat Completions 接口（OpenAI / Azure / 网关）',
    modelHint: 'gpt-5 / deepseek-chat',
    keyHint: 'sk-…'
  },
  {
    value: 'anthropic',
    label: 'Anthropic',
    description: '使用 Anthropic Messages API',
    modelHint: 'claude-sonnet-4-5',
    keyHint: 'sk-ant-…'
  },
  {
    value: 'aliyun',
    label: '阿里云百炼',
    description: 'DashScope OpenAI 兼容模式 · 通义千问 / Qwen',
    baseUrl: 'https://dashscope.aliyuncs.com/compatible-mode/v1',
    modelHint: 'qwen-plus / qwen-max',
    keyHint: 'sk-…'
  },
  {
    value: 'volcengine',
    label: '火山方舟',
    description: '火山引擎 Ark OpenAI 兼容接口 · 豆包等',
    baseUrl: 'https://ark.cn-beijing.volces.com/api/v3',
    modelHint: 'doubao-seed-1-6-250615 或推理接入点 ep-…',
    keyHint: 'sk-…'
  },
  {
    value: 'tencent',
    label: '腾讯云混元',
    description: '腾讯云 Hunyuan OpenAI 兼容接口',
    baseUrl: 'https://api.hunyuan.cloud.tencent.com/v1',
    modelHint: 'hunyuan-turbos-latest / hunyuan-t1-latest',
    keyHint: 'sk-…'
  },
  {
    value: 'baidu',
    label: '百度千帆',
    description: '千帆 v2 OpenAI 兼容接口 · ERNIE 等',
    baseUrl: 'https://qianfan.baidubce.com/v2',
    modelHint: 'ernie-4.5-8k-preview / deepseek-v3',
    keyHint: 'bce-v3/ALTAK-…'
  },
  {
    value: 'zhipu',
    label: '智谱 GLM',
    description: '智谱开放平台 OpenAI 兼容接口',
    baseUrl: 'https://open.bigmodel.cn/api/paas/v4',
    modelHint: 'glm-4.6 / glm-4.5-air',
    keyHint: 'API Key 或 id.secret'
  },
  {
    value: 'deepseek',
    label: 'DeepSeek',
    description: 'DeepSeek 官方 OpenAI 兼容接口',
    baseUrl: 'https://api.deepseek.com/v1',
    modelHint: 'deepseek-chat / deepseek-reasoner',
    keyHint: 'sk-…'
  },
  {
    value: 'local',
    label: '本地 GGUF',
    description: '模型在本进程内直接运行，无需网络与 API Key'
  }
]

const PROTOCOL_BY_KIND = new Map<string, ProviderProtocolOption>(
  PROVIDER_PROTOCOLS.map((option) => [option.value, option])
)

export function providerProtocol(kind: string): ProviderProtocolOption | undefined {
  return PROTOCOL_BY_KIND.get(kind.toLowerCase())
}

/** 提供商列表里的协议标签；未知 kind 原样展示，避免配置手写错误被吞掉。 */
export function providerKindLabel(kind: string): string {
  return providerProtocol(kind)?.label ?? kind
}
