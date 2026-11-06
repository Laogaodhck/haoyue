import { describe, expect, it } from 'vitest'
import { describeGhCli } from './gh-cli-status'

describe('describeGhCli', () => {
  it('已安装且已登录：展示版本与账号，可用', () => {
    const view = describeGhCli({
      installed: true,
      version: '2.40.1',
      authenticated: true,
      account: 'octocat',
      available: true
    })
    expect(view.installedLine).toBe('已安装 v2.40.1')
    expect(view.loginLine).toBe('已登录 octocat')
    expect(view.availabilityLine).toBe('可用')
    expect(view.needsInstall).toBe(false)
    expect(view.needsLogin).toBe(false)
    expect(view.error).toBe('')
  })

  it('未安装：给出安装引导，不提示登录', () => {
    const view = describeGhCli({
      installed: false,
      authenticated: false,
      available: false,
      error: '未检测到 GitHub CLI（gh）。'
    })
    expect(view.installedLine).toBe('未安装')
    expect(view.loginLine).toBe('未登录')
    expect(view.availabilityLine).toBe('不可用')
    expect(view.needsInstall).toBe(true)
    expect(view.needsLogin).toBe(false)
    expect(view.error).toContain('未检测到')
  })

  it('已安装未登录：给出登录引导并展示原因', () => {
    const view = describeGhCli({
      installed: true,
      authenticated: false,
      available: false,
      error: 'GitHub CLI 未登录或凭据失效：请运行 gh auth login 后重试。'
    })
    expect(view.needsInstall).toBe(false)
    expect(view.needsLogin).toBe(true)
    expect(view.error).toContain('gh auth login')
  })

  it('缺少状态：按未知处理，不误判为未安装', () => {
    const view = describeGhCli(null)
    expect(view.installedLine).toBe('状态未知')
    expect(view.needsInstall).toBe(false)
    expect(view.available).toBe(false)
  })
})
