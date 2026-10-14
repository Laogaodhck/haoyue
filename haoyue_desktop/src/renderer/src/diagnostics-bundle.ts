/**
 * 诊断包组装。放在组件外是为了让「导出内容」可单测：用户把这份文件发给别人排障时，
 * 必须能事先确认真没有密钥混在里面。
 */
export interface BundleCheck {
  name: string
  ok: boolean
  detail: string
}

export interface BundleModelUsage {
  provider: string
  model: string
  calls: number
  failures: number
  inputTokens: number
  outputTokens: number
  avgLatencyMs: number
  successRate: number
}

export interface BundleTotals {
  calls: number
  failures: number
  successRate: number
  tokens: number
  cachedTokens: number
  cacheEfficiency: number
  avgLatencyMs: number
}

export interface DiagnosticsBundleInput {
  generatedAt: Date
  appVersion: string
  platform: string
  logDir: string
  daemonLogFile: string
  daemonConnected: boolean
  daemonEndpoint: string
  usageDays: number
  checks: BundleCheck[]
  usage: BundleModelUsage[]
  totals: BundleTotals
  /** 主进程读到的日志尾部，已脱敏。 */
  logTail: string
  logBytes: number
  skippedLogBytes: number
}

export function diagnosticsFileName(at: Date): string {
  const stamp = at.toISOString().slice(0, 19).replace(/[:T]/g, '-')
  return `haoyue-诊断包-${stamp}.txt`
}

export function buildDiagnosticsBundle(input: DiagnosticsBundleInput): string {
  const percent = (ratio: number): string => `${Math.round(ratio * 100)}%`
  const lines: string[] = [
    '# 浩玥桌面诊断包',
    '',
    '内容：应用与后台状态、健康体检结果、用量统计、后台日志尾部。',
    '日志尾部在读取时已按密钥样式脱敏；不含配置文件正文与 API 密钥。',
    '',
    '## 环境',
    `- 生成时间：${input.generatedAt.toISOString()}`,
    `- 应用版本：${input.appVersion || '未知'}`,
    `- 平台：${input.platform || '未知'}`,
    `- 后台端点：${input.daemonEndpoint || '未知'}`,
    `- 后台连接：${input.daemonConnected ? '已连接' : '未连接'}`,
    `- 日志目录：${input.logDir || '未知'}`,
    `- 日志文件：${input.daemonLogFile || '未知'}（${Math.round(input.logBytes / 1024)} KB）`,
    '',
    '## 系统健康体检',
    ...(input.checks.length
      ? input.checks.map((check) => `- ${check.name}：${check.ok ? '通过' : '未通过'}｜${check.detail}`)
      : ['- （尚未运行体检）']),
    '',
    `## 用量汇总（近 ${input.usageDays} 天）`,
    `- 调用 ${input.totals.calls} 次，失败 ${input.totals.failures} 次，成功率 ${percent(input.totals.successRate)}`,
    `- Tokens ${input.totals.tokens}（缓存命中 ${input.totals.cachedTokens}，命中率 ${input.totals.cacheEfficiency}%）`,
    `- 平均延迟 ${input.totals.avgLatencyMs} ms`,
    '',
    '## 模型明细',
    ...(input.usage.length
      ? input.usage.map((item) => `- ${item.provider} / ${item.model}：调用 ${item.calls}，失败 ${item.failures}，`
        + `输入 ${item.inputTokens}，输出 ${item.outputTokens}，平均 ${item.avgLatencyMs} ms，成功率 ${percent(item.successRate)}`)
      : ['- （暂无记录）']),
    '',
    `## 后台日志尾部${input.skippedLogBytes > 0 ? `（前 ${Math.round(input.skippedLogBytes / 1024)} KB 已省略）` : ''}`,
    '```text',
    input.logTail || '（日志为空：后台尚未输出任何内容，或文件不存在）',
    '```',
    ''
  ]
  return lines.join('\n')
}
