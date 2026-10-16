import { onBeforeUnmount, watch } from 'vue'

/**
 * Esc 关闭弹窗（P1-2.3 可访问性）。监听绑在 document 上并 stopPropagation，
 * App.vue 的全局快捷键绑在 window，因此弹窗打开时 Esc 归弹窗，不会再顺手掐掉
 * 后台正在跑的回合。多个弹窗可以叠着打开（扩展页里再开连接器编辑器），栈顶独享
 * 这一次按键，Esc 一层一层退，不会一键清空。
 */
const escapeOwners: symbol[] = []

export function useEscapeClose(isOpen: () => boolean, close: () => void): void {
  const owner = Symbol('escape-owner')
  function onKeydown(event: KeyboardEvent): void {
    if (event.key !== 'Escape' || escapeOwners[escapeOwners.length - 1] !== owner) return
    event.stopPropagation()
    close()
  }
  function release(): void {
    const index = escapeOwners.indexOf(owner)
    if (index >= 0) escapeOwners.splice(index, 1)
    document.removeEventListener('keydown', onKeydown)
  }
  watch(isOpen, (open) => {
    release()
    if (!open) return
    escapeOwners.push(owner)
    document.addEventListener('keydown', onKeydown)
  }, { immediate: true })
  onBeforeUnmount(release)
}
