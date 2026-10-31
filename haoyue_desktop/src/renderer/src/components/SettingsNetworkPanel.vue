<script setup lang="ts">
import { Copy, Eye, EyeOff, LoaderCircle, Plug, RefreshCw, Wifi } from '@lucide/vue'
import { computed, onMounted, ref, watch } from 'vue'
import type { ConnectionMode } from '../../../shared/ipc'
import { requestJson } from '../daemon-json'
import SelectMenu from './SelectMenu.vue'

/**
 * 设置 → 局域网连接。
 *
 * 两个方向：
 * - **这台桌面连哪台 Runtime**：本机（管道 / socket）或局域网内另一台电脑（TCP）。
 *   局域网握手用一次性 nonce + HMAC 证明，令牌本身不上线。
 * - **本机 Runtime 是否对局域网开放**：写 daemon.lan.*，默认关闭；开启后同网段的
 *   桌面填写本机地址、端口与令牌即可接入。
 *
 * 远程模式下会话、模型与工具执行都在远端机器，本机的「选择工作区 / 附件文件 /
 * 打开终端」仍作用于本机文件系统——面板底部对此有明确提示。
 */
const props = defineProps<{
  daemonConnected: boolean
  daemonEndpoint: string
}>()

const emit = defineEmits<{
  notice: [message: string]
  error: [message: string]
}>()

const DEFAULT_LAN_PORT = 47_615

interface LanStatus {
  enabled: boolean
  running: boolean
  bindAddress: string
  port: number
  addresses: string[]
  error?: string
}

const loading = ref(false)
const action = ref('')
const mode = ref<ConnectionMode>('local')
const host = ref('')
const port = ref(DEFAULT_LAN_PORT)
const token = ref('')
const hasToken = ref(false)
const revealedToken = ref('')
const testResult = ref<{ ok: boolean; text: string } | null>(null)

const lan = ref<LanStatus | null>(null)
const lanBind = ref('0.0.0.0')
const lanPort = ref(DEFAULT_LAN_PORT)

/** 命名管道 / unix socket 说明当前连接的确实是本机 Runtime。 */
const isLocalEndpoint = computed(() =>
  /^\\\\\.\\pipe\\/i.test(props.daemonEndpoint) || props.daemonEndpoint.endsWith('daemon.sock'))
const showLocalService = computed(() => mode.value === 'local' || isLocalEndpoint.value)

const bindOptions = computed(() => {
  const addresses = lan.value?.addresses ?? []
  return [
    { value: '0.0.0.0', label: '0.0.0.0', description: '所有网卡（推荐）' },
    { value: '127.0.0.1', label: '127.0.0.1', description: '仅本机（不对局域网开放）' },
    ...addresses.map((address) => ({ value: address, label: address, description: '本机网卡地址' }))
  ]
})

const pairingAddresses = computed(() =>
  (lan.value?.addresses ?? []).map((address) => `${address}:${lan.value?.port ?? lanPort.value}`))

const lanDirty = computed(() =>
  lan.value !== null && (lanBind.value !== lan.value.bindAddress || lanPort.value !== lan.value.port))

function beginAction(name: string): void {
  action.value = name
  emit('error', '')
}

function fail(reason: unknown): void {
  emit('error', reason instanceof Error ? reason.message : String(reason))
}

function endAction(): void {
  action.value = ''
}

async function loadConnection(): Promise<void> {
  loading.value = true
  try {
    const view = await window.haoyue.connection.get()
    mode.value = view.mode
    host.value = view.host
    port.value = view.port
    hasToken.value = view.hasToken
    token.value = ''
    revealedToken.value = ''
  } catch (reason) {
    fail(reason)
  } finally {
    loading.value = false
  }
}

async function loadLan(): Promise<void> {
  if (!props.daemonConnected) {
    lan.value = null
    return
  }
  try {
    applyLan(await requestJson<LanStatus>('daemon.lan.get'))
  } catch {
    lan.value = null // 旧版 daemon 没有该 RPC：整块服务端卡片隐藏
  }
}

function applyLan(status: LanStatus): void {
  lan.value = status
  lanBind.value = status.bindAddress || '0.0.0.0'
  lanPort.value = status.port || DEFAULT_LAN_PORT
}

/** token 省略表示「保留已保存的令牌」，null 表示清除。 */
function connectionUpdate(): { mode: ConnectionMode; host: string; port: number; token?: string | null } {
  const trimmed = token.value.trim()
  return {
    mode: mode.value,
    host: host.value.trim(),
    port: Number(port.value),
    token: trimmed ? trimmed : undefined
  }
}

async function saveConnection(): Promise<void> {
  beginAction('connection.set')
  try {
    const result = await window.haoyue.connection.set(connectionUpdate())
    if (!result.ok) {
      emit('error', result.error ?? '保存失败')
      return
    }
    token.value = ''
    revealedToken.value = ''
    await loadConnection()
    emit('notice', mode.value === 'lan'
      ? '已切换到局域网 Runtime，正在连接…'
      : '已切换到本机 Runtime')
  } catch (reason) {
    fail(reason)
  } finally {
    endAction()
  }
}

async function testConnection(): Promise<void> {
  beginAction('connection.test')
  testResult.value = null
  try {
    const result = await window.haoyue.connection.test(connectionUpdate())
    testResult.value = result.ok
      ? { ok: true, text: `连接成功：${result.endpoint}${result.protocolVersion ? ` · 协议 ${result.protocolVersion}` : ''}` }
      : { ok: false, text: result.error ?? '连接失败' }
  } catch (reason) {
    testResult.value = { ok: false, text: reason instanceof Error ? reason.message : String(reason) }
  } finally {
    endAction()
  }
}

async function revealToken(): Promise<void> {
  if (revealedToken.value) {
    revealedToken.value = ''
    return
  }
  try {
    revealedToken.value = (await window.haoyue.connection.revealToken()) ?? ''
    if (!revealedToken.value) emit('notice', '尚未保存局域网令牌')
  } catch (reason) {
    fail(reason)
  }
}

async function clearToken(): Promise<void> {
  beginAction('connection.clearToken')
  // 局域网模式没有令牌就连不上，清除后一并切回本机 Runtime，避免留下不可用的连接偏好。
  const fallbackToLocal = mode.value === 'lan'
  try {
    const result = await window.haoyue.connection.set({
      mode: fallbackToLocal ? 'local' : mode.value,
      host: host.value.trim(),
      port: Number(port.value),
      token: null
    })
    if (!result.ok) {
      emit('error', result.error ?? '清除令牌失败')
      return
    }
    hasToken.value = false
    revealedToken.value = ''
    if (fallbackToLocal) {
      mode.value = 'local'
      emit('notice', '已清除保存的局域网令牌，并切回本机 Runtime')
    } else {
      emit('notice', '已清除保存的局域网令牌')
    }
  } catch (reason) {
    fail(reason)
  } finally {
    endAction()
  }
}

async function copyText(text: string, label: string): Promise<void> {
  try {
    await navigator.clipboard.writeText(text)
    emit('notice', `已复制${label}`)
  } catch {
    emit('error', '复制失败：剪贴板不可用')
  }
}

async function copyLocalToken(): Promise<void> {
  try {
    const local = await window.haoyue.connection.localToken()
    if (!local) {
      emit('error', '本机 Runtime 尚未生成握手令牌（先启动一次本机 Runtime）')
      return
    }
    await copyText(local, '本机令牌')
  } catch (reason) {
    fail(reason)
  }
}

async function toggleLan(): Promise<void> {
  if (!lan.value) return
  beginAction('daemon.lan.set')
  try {
    applyLan(await requestJson<LanStatus>('daemon.lan.set', {
      enabled: !lan.value.enabled,
      bindAddress: lanBind.value,
      port: Number(lanPort.value)
    }))
    emit('notice', lan.value.enabled
      ? '局域网访问已开启，同网段的桌面可用下方地址接入'
      : '局域网访问已关闭')
  } catch (reason) {
    fail(reason)
    await loadLan()
  } finally {
    endAction()
  }
}

async function saveLanBinding(): Promise<void> {
  beginAction('daemon.lan.set')
  try {
    applyLan(await requestJson<LanStatus>('daemon.lan.set', {
      bindAddress: lanBind.value,
      port: Number(lanPort.value)
    }))
    emit('notice', '监听地址与端口已更新')
  } catch (reason) {
    fail(reason)
    await loadLan()
  } finally {
    endAction()
  }
}

onMounted(async () => {
  await loadConnection()
  await loadLan()
})

watch(() => props.daemonConnected, () => { void loadLan() })
</script>

<template>
  <div class="settings-section-heading">
    <div>
      <h3>局域网连接</h3>
      <p>让同一局域网内的桌面接入另一台电脑的 Haoyue Runtime，或把本机 Runtime 共享给同网段的桌面</p>
    </div>
  </div>

  <section class="settings-group">
    <div class="settings-group-title">
      <strong>这台桌面连接的 Runtime</strong>
      <small>切换后立即断开当前连接并按新目标重连；对话、模型与工具执行都在目标 Runtime 上完成。</small>
    </div>

    <div class="network-mode-grid" role="radiogroup" aria-label="Runtime 连接方式">
      <button type="button" class="network-mode-card" :class="{ active: mode === 'local' }"
        role="radio" :aria-checked="mode === 'local'" @click="mode = 'local'">
        <Plug :size="16" />
        <strong>本机 Runtime</strong>
        <small>命名管道 / unix socket，随桌面自动拉起</small>
      </button>
      <button type="button" class="network-mode-card" :class="{ active: mode === 'lan' }"
        role="radio" :aria-checked="mode === 'lan'" @click="mode = 'lan'">
        <Wifi :size="16" />
        <strong>局域网 Runtime</strong>
        <small>连接同网段另一台电脑上的 daemon</small>
      </button>
    </div>

    <div v-if="mode === 'lan'" class="network-form">
      <label>
        <span>主机地址</span>
        <input v-model.trim="host" type="text" placeholder="例如 192.168.1.20" autocomplete="off" />
      </label>
      <label class="port-field">
        <span>端口</span>
        <input v-model.number="port" type="number" min="1024" max="65535" />
      </label>
      <label class="token-field">
        <span>握手令牌</span>
        <input v-model="token" type="password" autocomplete="off"
          :placeholder="hasToken ? '已保存令牌，留空保持不变' : '粘贴对方的握手令牌'" />
      </label>
    </div>

    <div class="network-actions">
      <button class="primary-button" :disabled="loading || action === 'connection.set'" @click="saveConnection">
        <LoaderCircle v-if="action === 'connection.set'" class="spin" :size="15" />
        <Plug v-else :size="15" />
        {{ mode === 'lan' ? '保存并连接' : '使用本机 Runtime' }}
      </button>
      <button class="secondary-button" :disabled="action === 'connection.test'" @click="testConnection">
        <LoaderCircle v-if="action === 'connection.test'" class="spin" :size="15" />
        <RefreshCw v-else :size="15" /> 测试连接
      </button>
      <button v-if="mode === 'lan' && hasToken" class="secondary-button" @click="revealToken">
        <Eye v-if="!revealedToken" :size="15" />
        <EyeOff v-else :size="15" />
        {{ revealedToken ? '隐藏令牌' : '显示已保存令牌' }}
      </button>
      <button v-if="mode === 'lan' && hasToken" class="secondary-button"
        :disabled="action === 'connection.clearToken'" @click="clearToken">清除令牌</button>
    </div>

    <div v-if="revealedToken" class="token-value">
      <code>{{ revealedToken }}</code>
      <button class="secondary-button" type="button" @click="copyText(revealedToken, '令牌')">
        <Copy :size="14" /> 复制
      </button>
    </div>

    <p v-if="testResult" class="network-result" :class="{ error: !testResult.ok }">{{ testResult.text }}</p>

    <p class="network-state">
      当前状态：<strong>{{ daemonConnected ? '已连接' : '未连接' }}</strong>
      <code>{{ daemonEndpoint || '—' }}</code>
    </p>
  </section>

  <section v-if="showLocalService" class="settings-group">
    <div class="settings-group-title">
      <strong>本机 Runtime 的局域网服务</strong>
      <small>默认关闭。开启后 daemon 在指定端口监听局域网连接，握手令牌不上线（挑战应答证明）。</small>
    </div>

    <label class="provider-enabled-row">
      <span>
        <strong>允许局域网访问</strong>
        <small>只对同网段开放；同一时刻最多 16 个并发连接，认证失败的连接会被延迟断开。</small>
      </span>
      <input class="sr-only" type="checkbox" :checked="lan?.enabled ?? false"
        :disabled="!daemonConnected || !lan || action === 'daemon.lan.set'" @change="toggleLan" />
      <span class="toggle-switch" aria-hidden="true"><span /></span>
    </label>

    <div v-if="lan" class="network-form">
      <label class="bind-field">
        <span>监听地址</span>
        <SelectMenu v-model="lanBind" :options="bindOptions" label="监听地址" :menu-min-width="260" />
      </label>
      <label class="port-field">
        <span>监听端口</span>
        <input v-model.number="lanPort" type="number" min="1024" max="65535"
          :disabled="!daemonConnected" />
      </label>
      <button class="secondary-button" type="button" :disabled="!daemonConnected || !lanDirty"
        @click="saveLanBinding">应用地址与端口</button>
    </div>

    <p v-if="lan" class="network-state">
      监听状态：
      <strong>{{ lan.running ? '正在监听' : (lan.enabled ? '等待重启生效' : '未开启') }}</strong>
      <code>{{ lan.bindAddress }}:{{ lan.port }}</code>
    </p>
    <p v-if="lan?.error" class="network-result error">最近一次监听失败：{{ lan.error }}</p>

    <div v-if="lan?.running && pairingAddresses.length" class="pairing-list">
      <span>在另一台桌面填写：</span>
      <button v-for="address in pairingAddresses" :key="address" type="button" class="stat-chip"
        @click="copyText(address, '接入地址')">
        <Copy :size="12" /> {{ address }}
      </button>
      <button type="button" class="secondary-button" @click="copyLocalToken">
        <Copy :size="14" /> 复制本机令牌
      </button>
    </div>
    <p v-else-if="lan?.running" class="network-state">
      未发现可用的局域网 IPv4 地址：请确认网卡已连接，或手动填写本机地址。
    </p>
  </section>

  <section class="settings-group">
    <div class="settings-group-title">
      <strong>使用说明</strong>
      <small>远程模式下，选择工作区、添加本地附件、打开项目终端仍在本机执行；会话与工具运行在远端 Runtime 所在电脑上。</small>
    </div>
    <p class="network-state">
      配对步骤：在被连接的电脑上开启「允许局域网访问」并复制其令牌；在这台桌面选择「局域网 Runtime」，
      填入对方地址、端口与令牌后保存。
    </p>
    <p class="network-state">
      监听使用明文 TCP：令牌只参与挑战应答、不会上线，但对话与工具结果可被抓包。请只在可信局域网内开启；
      需要跨公网访问时请改用 SSH 隧道等加密通道封装。
    </p>
  </section>
</template>

<style scoped>
.network-mode-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(220px, 1fr));
  gap: 10px;
  margin-top: 14px;
}

.network-mode-card {
  display: flex;
  flex-direction: column;
  gap: 4px;
  padding: 12px 14px;
  color: var(--text-secondary);
  text-align: left;
  background: var(--surface);
  border: 1px solid var(--border-strong);
  border-radius: 9px;
  cursor: pointer;
  transition: border-color 140ms ease, background-color 140ms ease;
}

.network-mode-card strong {
  display: flex;
  align-items: center;
  gap: 7px;
  color: var(--text);
  font-size: 13px;
}

.network-mode-card small {
  color: var(--text-muted);
  font-size: 11.5px;
  line-height: 1.4;
}

.network-mode-card.active {
  background: color-mix(in srgb, var(--accent) 9%, var(--surface));
  border-color: var(--accent);
}

.network-form {
  display: flex;
  flex-wrap: wrap;
  align-items: flex-end;
  gap: 12px;
  margin-top: 14px;
}

.network-form label {
  display: flex;
  min-width: 150px;
  flex: 1 1 180px;
  flex-direction: column;
  gap: 5px;
  color: var(--text-muted);
  font-size: 11.5px;
}

.network-form label.port-field {
  flex: 0 0 120px;
}

.network-form label.token-field {
  flex: 1 1 260px;
}

.network-form input {
  min-height: 34px;
  padding: 6px 9px;
  color: var(--text);
  background: var(--surface);
  border: 1px solid var(--border-strong);
  border-radius: 6px;
}

.network-form label.bind-field {
  flex: 1 1 220px;
}

.network-actions {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
  margin-top: 14px;
}

.token-value {
  display: flex;
  align-items: center;
  gap: 10px;
  margin-top: 12px;
  padding: 8px 10px;
  background: var(--surface-hover);
  border-radius: 7px;
}

.token-value code {
  overflow-wrap: anywhere;
  color: var(--text);
  font-size: 11.5px;
}

.network-result {
  margin: 12px 0 0;
  color: var(--success, var(--accent));
  font-size: 12px;
}

.network-result.error {
  color: var(--danger, #e5484d);
}

.network-state {
  margin: 12px 0 0;
  color: var(--text-muted);
  font-size: 12px;
}

.network-state strong {
  color: var(--text-secondary);
}

.network-state code {
  margin-left: 6px;
  color: var(--text-secondary);
}

.pairing-list {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 8px;
  margin-top: 12px;
  color: var(--text-muted);
  font-size: 12px;
}

.pairing-list .stat-chip {
  cursor: pointer;
  border: 0;
}
</style>
