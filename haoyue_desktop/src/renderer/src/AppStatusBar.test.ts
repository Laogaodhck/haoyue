// @vitest-environment jsdom
import { mount } from '@vue/test-utils'
import { describe, expect, it } from 'vitest'
import AppStatusBar from './components/AppStatusBar.vue'
import type { ThreadItem } from './types'

const thread = (id: string, running: boolean) => ({ id, title: id, updatedAt: 0, messages: [], running } as ThreadItem)

describe('AppStatusBar (组件级)', () => {
  it('offers a reconnect action while the runtime is offline', async () => {
    const wrapper = mount(AppStatusBar, {
      props: { connected: false, endpoint: '\\\\.\\pipe\\haoyue-test', version: '1.3.29' }
    })

    expect(wrapper.classes()).toContain('offline')
    expect(wrapper.text()).toContain('Runtime 未连接')

    await wrapper.get('button.status-runtime').trigger('click')
    expect(wrapper.emitted('reconnect')).toHaveLength(1)
  })

  it('shows the model, phase and running task count while connected', () => {
    const wrapper = mount(AppStatusBar, {
      props: {
        connected: true,
        endpoint: '192.168.1.20:47615',
        model: 'deepseek/deepseek-chat',
        phase: '正在生成',
        elapsedSeconds: 12,
        threads: [thread('a', true), thread('b', true), thread('c', false)],
        version: '1.3.29'
      }
    })

    const text = wrapper.text()
    expect(wrapper.classes()).not.toContain('offline')
    expect(wrapper.classes()).toContain('is-accent')
    expect(text).toContain('Runtime')
    expect(text).toContain('deepseek/deepseek-chat')
    expect(text).toContain('正在生成')
    expect(text).toContain('12s')
    expect(wrapper.get('[title="2 个任务正在运行"]').text()).toContain('2')
    expect(text).toContain('v1.3.29')
  })

  it('hides the running badge when no task is active', () => {
    const wrapper = mount(AppStatusBar, {
      props: { connected: true, endpoint: 'local', threads: [thread('a', false)] }
    })

    expect(wrapper.text()).not.toContain('running')
    expect(wrapper.classes()).not.toContain('is-accent')
  })
})
