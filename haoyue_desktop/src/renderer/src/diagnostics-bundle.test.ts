import { describe, expect, it } from 'vitest'
import { buildDiagnosticsBundle, diagnosticsFileName } from './diagnostics-bundle'

const base = {
  generatedAt: new Date('2026-10-15T08:30:00.000Z'),
  appVersion: '1.3.27',
  platform: 'win32',
  logDir: 'C:/Users/me/AppData/Roaming/haoyue-desktop/logs',
  daemonLogFile: 'C:/Users/me/AppData/Roaming/haoyue-desktop/logs/daemon.log',
  daemonConnected: false,
  daemonEndpoint: '\\\\.\\pipe\\haoyue-runtime',
  usageDays: 14,
  checks: [{ name: '后台进程', ok: false, detail: '未检测到监听端点' }],
  usage: [{ provider: 'openai', model: 'gpt', calls: 12, failures: 2, inputTokens: 900, outputTokens: 300, avgLatencyMs: 800, successRate: 0.833 }],
  totals: { calls: 12, failures: 2, successRate: 0.833, tokens: 1200, cachedTokens: 400, cacheEfficiency: 33, avgLatencyMs: 800 },
  logTail: 'llama_kv_cache: out of memory',
  logBytes: 20480,
  skippedLogBytes: 0
}

describe('buildDiagnosticsBundle', () => {
  it('汇总环境、体检、用量与日志尾部', () => {
    const text = buildDiagnosticsBundle(base)
    expect(text).toContain('应用版本：1.3.27')
    expect(text).toContain('后台连接：未连接')
    expect(text).toContain('后台进程：未通过｜未检测到监听端点')
    expect(text).toContain('调用 12 次，失败 2 次，成功率 83%')
    expect(text).toContain('openai / gpt')
    expect(text).toContain('```text\nllama_kv_cache: out of memory\n```')
  })

  it('省略窗口外日志时写清楚，避免误读成完整日志', () => {
    expect(buildDiagnosticsBundle({ ...base, skippedLogBytes: 600 * 1024 })).toContain('前 600 KB 已省略')
    expect(buildDiagnosticsBundle(base)).not.toContain('已省略')
  })

  it('空数据留占位而不是生成残缺文件', () => {
    const text = buildDiagnosticsBundle({ ...base, checks: [], usage: [], logTail: '', logBytes: 0 })
    expect(text).toContain('（尚未运行体检）')
    expect(text).toContain('（暂无记录）')
    expect(text).toContain('日志为空')
  })

  it('文件名带可排序的时间戳且不含非法字符', () => {
    expect(diagnosticsFileName(base.generatedAt)).toBe('haoyue-诊断包-2026-10-15-08-30-00.txt')
  })
})
