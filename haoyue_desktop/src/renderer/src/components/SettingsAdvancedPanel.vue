<script setup lang="ts">
import { LoaderCircle, RotateCcw, Sparkles } from '@lucide/vue'
import { onMounted, ref } from 'vue'
import { confirmAction } from '../confirmation'
import { requestJson } from '../daemon-json'
import AllowedSitesDialog from './AllowedSitesDialog.vue'

/**
 * 设置 → 高级。全局网络开关的写入口在「智能体 → 网页搜索」，这里只读回显，
 * 避免同一开关两处可写、互相覆盖对方的本地副本。
 */
const props = defineProps<{
  daemonConnected: boolean
  networkEnabled: boolean
}>()

const emit = defineEmits<{
  notice: [message: string]
  error: [message: string]
  navigateAgent: []
  runtimeChanged: []
  close: []
}>()

const computerUseEnabled = ref(false)
const computerUseDriver = ref('auto')
const networkOpsEnabled = ref(false)
const networkOpsAllowMutating = ref(true)
const allowedSitesOpen = ref(false)
const allowedSitesCount = ref<number | null>(null)
const action = ref('')

function beginAction(name: string): void {
  action.value = name
  emit('error', '')
}

function fail(reason: unknown): void {
  emit('error', reason instanceof Error ? reason.message : String(reason))
}

async function loadAdvanced(): Promise<void> {
  const config = await requestJson<{
    networkEnabled: boolean
    computerUseEnabled?: boolean
    computerUseDriver?: string
    networkOpsEnabled?: boolean
    networkOpsAllowMutating?: boolean
  }>('advanced.get')
  computerUseEnabled.value = config.computerUseEnabled ?? false
  computerUseDriver.value = config.computerUseDriver ?? 'auto'
  networkOpsEnabled.value = config.networkOpsEnabled ?? false
  networkOpsAllowMutating.value = config.networkOpsAllowMutating ?? true
  try {
    const web = await requestJson<{ allowedSites: string[] }>('web.allowedGet')
    allowedSitesCount.value = web.allowedSites?.length ?? 0
  } catch {
    allowedSitesCount.value = null // daemon 版本较旧时静默隐藏计数
  }
}

async function toggleComputerUse(): Promise<void> {
  beginAction('advanced.set:computerUse')
  try {
    const config = await requestJson<{
      networkEnabled: boolean
      computerUseEnabled: boolean
      computerUseDriver: string
    }>('advanced.set', {
      networkEnabled: props.networkEnabled,
      computerUseEnabled: computerUseEnabled.value,
      computerUseDriver: computerUseDriver.value
    })
    computerUseEnabled.value = config.computerUseEnabled
    emit('notice', computerUseEnabled.value
      ? '已启用电脑操作智能体 (Computer Use)，Agent 获得直接操作桌面能力'
      : '已禁用电脑操作智能体，所有相关驱动与工具已注销并保持零开销')
  } catch (reason) {
    fail(reason)
    try {
      const config = await requestJson<{ computerUseEnabled: boolean }>('advanced.get')
      computerUseEnabled.value = config.computerUseEnabled ?? false
    } catch { /* keep last known state */ }
  } finally {
    action.value = ''
  }
}

async function toggleNetworkOps(): Promise<void> {
  beginAction('advanced.set:networkOps')
  try {
    const config = await requestJson<{ networkOpsEnabled: boolean }>('advanced.set', {
      networkOpsEnabled: networkOpsEnabled.value
    })
    networkOpsEnabled.value = config.networkOpsEnabled
    emit('notice', networkOpsEnabled.value
      ? '已启用网络运维插件，Agent 获得网络诊断与运维命令能力'
      : '已禁用网络运维插件，相关工具已注销')
  } catch (reason) {
    fail(reason)
    try {
      const config = await requestJson<{ networkOpsEnabled: boolean }>('advanced.get')
      networkOpsEnabled.value = config.networkOpsEnabled ?? false
    } catch { /* keep last known state */ }
  } finally {
    action.value = ''
  }
}

async function toggleNetworkOpsMutating(): Promise<void> {
  const previous = networkOpsAllowMutating.value
  networkOpsAllowMutating.value = !previous
  beginAction('advanced.set:networkOpsMutating')
  try {
    const config = await requestJson<{ networkOpsAllowMutating: boolean }>('advanced.set', {
      networkOpsAllowMutating: networkOpsAllowMutating.value
    })
    networkOpsAllowMutating.value = config.networkOpsAllowMutating
    emit('notice', networkOpsAllowMutating.value
      ? '网络运维已允许变更类命令（route、netsh set、DNS 缓存刷新等）'
      : '网络运维已限制为只读查询，变更类命令将被拒绝')
  } catch (reason) {
    networkOpsAllowMutating.value = previous
    fail(reason)
  } finally {
    action.value = ''
  }
}

async function factoryReset(): Promise<void> {
  if (!props.daemonConnected) return
  if (!await confirmAction({
    title: '恢复出厂设置',
    message: '将删除全局配置、会话、计划任务、项目记录、用量日志、日志、全局技能与提示词，并重建数据库。项目源码文件不会删除，此操作无法撤销。',
    confirmLabel: '继续',
    danger: true
  })) return
  if (!await confirmAction({
    title: '再次确认恢复出厂设置',
    message: '这是最后一次确认。确认后将清空 Haoyue 的全部用户数据，并恢复默认设置。',
    confirmLabel: '恢复出厂设置',
    danger: true
  })) return

  beginAction('factory.reset')
  try {
    await requestJson<{ ok: boolean; home: string }>('factory.reset')
    emit('runtimeChanged')
    emit('close')
  } catch (reason) {
    fail(reason)
  } finally {
    action.value = ''
  }
}

onMounted(loadAdvanced)

function closeAllowedSites(): void {
  allowedSitesOpen.value = false
  void loadAdvanced()
}
</script>

<template>
  <div class="settings-section-heading">
    <div>
      <h3>高级设置</h3>
      <p>网络与桌面权限、网络运维与出厂重置</p>
    </div>
  </div>

  <section class="settings-group">
    <!-- 2.7：这里原本是「全局网络访问」的第二个写入口。同一开关两处可改，
         且这一处保存时会连带把 failover/deepSeek 的本地副本一起回写，
         在别处改过那两个值后就会互相覆盖。现在只读回显 + 跳转唯一入口。 -->
    <div class="provider-enabled-row">
      <span>
        <strong>全局网络访问（联网搜索与抓取）</strong>
        <small v-if="networkEnabled">已开启：所有任务与项目都可用 web_search / web_fetch。开关只有一个写入口，在「智能体 → 网页搜索」。</small>
        <small v-else>已关闭：所有项目和任务强制离线，网络工具被剥离。要开启请到「智能体 → 网页搜索」。</small>
      </span>
      <button class="secondary-button" type="button" @click="emit('navigateAgent')">
        <Sparkles :size="15" /> 去「智能体」管理
      </button>
    </div>
    <div class="provider-enabled-row">
      <span>
        <strong>允许的网站（外部网站访问）</strong>
        <small v-if="allowedSitesCount && allowedSitesCount > 0">已允许 {{ allowedSitesCount }} 个网站。仍可浏览本地主机。web_fetch 与内置浏览器都按此白名单拦截列表外的站点。</small>
        <small v-else>未设置白名单：web_fetch 不限制外部访问，内置浏览器仅允许本地主机。添加条目后，两者都只放行列表中的站点与本地主机。</small>
      </span>
      <button class="secondary-button" type="button" @click="allowedSitesOpen = true">管理…</button>
    </div>
  </section>

  <section class="settings-group">
    <label class="provider-enabled-row">
      <span>
        <strong>电脑操作智能体 (Computer Use)</strong>
        <small>允许 Agent 获得跨平台桌面直接操作能力（点击、输入、移动、滚动、快捷键与原生窗口UI元素解析）。内置双层故障沙盒与熔断防护，安全解耦，零残留。</small>
      </span>
      <input v-model="computerUseEnabled" class="sr-only" type="checkbox"
        :disabled="action === 'advanced.set:computerUse'" @change="toggleComputerUse" />
      <span class="toggle-switch" aria-hidden="true"><span /></span>
    </label>
  </section>

  <section class="settings-group">
    <label class="provider-enabled-row">
      <span>
        <strong>网络运维插件 (Network Ops)</strong>
        <small>允许 Agent 运行网络诊断与运维命令：一键诊断（接口 / 路由 / 连接 / ARP / DNS）、ping、traceroute、netstat、nslookup、netsh、ip 等全命令集（Windows 与 Linux 自动映射）。命令经白名单目录执行，参数不经 Shell，杜绝注入。</small>
      </span>
      <input v-model="networkOpsEnabled" class="sr-only" type="checkbox"
        :disabled="action === 'advanced.set:networkOps'" @change="toggleNetworkOps" />
      <span class="toggle-switch" aria-hidden="true"><span /></span>
    </label>
    <label v-if="networkOpsEnabled" class="provider-enabled-row">
      <span>
        <strong>允许变更类网络命令</strong>
        <small>关闭后仅允许只读查询（ping、netstat、nslookup 等）；route add、netsh set、DNS 缓存刷新等变更命令将被拒绝。</small>
      </span>
      <input v-model="networkOpsAllowMutating" class="sr-only" type="checkbox"
        :disabled="action === 'advanced.set:networkOpsMutating'" @change="toggleNetworkOpsMutating" />
      <span class="toggle-switch" aria-hidden="true"><span /></span>
    </label>
  </section>

  <!-- 危险区永远放最后：恢复出厂是唯一会摧毁数据的按钮，不该待在「常规」首屏。 -->
  <section class="settings-group danger-zone">
    <div class="provider-enabled-row">
      <span>
        <strong>恢复出厂设置</strong>
        <small>清空全局配置、会话数据并重建数据库；不会删除项目源码文件。此操作无法撤销。</small>
      </span>
      <button class="danger-button" :disabled="action === 'factory.reset'" @click="factoryReset">
        <LoaderCircle v-if="action === 'factory.reset'" class="spin" :size="15" />
        <RotateCcw v-else :size="15" /> 恢复出厂设置
      </button>
    </div>
  </section>

  <AllowedSitesDialog :open="allowedSitesOpen" @close="closeAllowedSites" />
</template>
