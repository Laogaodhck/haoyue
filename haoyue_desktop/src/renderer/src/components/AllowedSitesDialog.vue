<script setup lang="ts">
import { Plus, Trash2, X } from '@lucide/vue'
import { ref, watch } from 'vue'

const props = defineProps<{
  open: boolean
}>()

const emit = defineEmits<{
  close: []
}>()

interface WebAccessPayload {
  allowedSites: string[]
  mode: 'unrestricted' | 'allowlist'
  localhostAlwaysAllowed: boolean
}

const sites = ref<string[]>([])
const mode = ref<'unrestricted' | 'allowlist'>('unrestricted')
const draft = ref('')
const loading = ref(false)
const saving = ref(false)
const error = ref('')

async function request<T>(method: string, params: Record<string, unknown> = {}): Promise<T> {
  const response = await window.haoyue.daemon.request(method, params)
  return JSON.parse(response.data) as T
}

async function load(): Promise<void> {
  loading.value = true
  error.value = ''
  try {
    const payload = await request<WebAccessPayload>('web.allowedGet')
    sites.value = payload.allowedSites ?? []
    mode.value = payload.mode
  } catch (reason) {
    error.value = reason instanceof Error ? reason.message : String(reason)
  } finally {
    loading.value = false
  }
}

watch(() => props.open, (open) => {
  if (open) void load()
})

async function persist(next: string[]): Promise<void> {
  saving.value = true
  error.value = ''
  try {
    const payload = await request<WebAccessPayload>('web.allowedSet', { allowedSites: next })
    sites.value = payload.allowedSites ?? []
    mode.value = payload.mode
  } catch (reason) {
    error.value = reason instanceof Error ? reason.message : String(reason)
  } finally {
    saving.value = false
  }
}

function addSite(): void {
  const entry = draft.value.trim()
  if (!entry || saving.value) return
  if (sites.value.some((s) => s.toLowerCase() === entry.toLowerCase())) {
    draft.value = ''
    return
  }
  const next = [...sites.value, entry]
  draft.value = ''
  void persist(next)
}

function removeSite(entry: string): void {
  if (saving.value) return
  void persist(sites.value.filter((s) => s !== entry))
}
</script>

<template>
  <Transition name="modal-fade">
    <div v-if="open" class="modal-backdrop allowed-sites-backdrop" @mousedown.self="emit('close')">
      <section class="allowed-sites-dialog" role="dialog" aria-modal="true" aria-labelledby="allowed-sites-title">
        <header class="dialog-header">
          <h2 id="allowed-sites-title">允许的网站</h2>
          <button class="icon-button" title="关闭" aria-label="关闭" @click="emit('close')">
            <X :size="18" />
          </button>
        </header>

        <div class="dialog-body">
          <p class="dialog-desc">
            Agent 的网页抓取 (web_fetch) 只能访问列表中的网站。使用 https://*.example.com
            允许访问子域名，并单独添加 https://example.com。localhost / 127.x / ::1
            始终可以访问。列表为空时不限制外部访问。
          </p>

          <form class="allowed-sites-input-row" @submit.prevent="addSite">
            <input v-model="draft" type="text" class="allowed-sites-input" placeholder="https://example.com"
              :disabled="saving" spellcheck="false" />
            <button class="secondary-button" type="submit" :disabled="!draft.trim() || saving">
              <Plus :size="14" /> 添加
            </button>
          </form>

          <p v-if="error" class="allowed-sites-error">{{ error }}</p>

          <ul v-if="sites.length > 0" class="allowed-sites-list">
            <li v-for="site in sites" :key="site" class="allowed-site-item">
              <span class="allowed-site-name">{{ site }}</span>
              <button class="icon-button" :disabled="saving" title="移除" aria-label="移除"
                @click="removeSite(site)">
                <Trash2 :size="14" />
              </button>
            </li>
          </ul>
          <p v-else-if="!loading" class="allowed-sites-empty">
            列表为空：当前不限制外部网站访问。添加条目后，仅列表中的站点与本地主机可用。
          </p>

          <p class="allowed-sites-note">
            变更立即生效：web_fetch 每次执行时都会读取当前白名单，无需重启。已有会话与配置保留。
          </p>
        </div>

        <footer class="dialog-footer">
          <button class="secondary-button primary-action" type="button" @click="emit('close')">完成</button>
        </footer>
      </section>
    </div>
  </Transition>
</template>

<style scoped>
.allowed-sites-backdrop {
  z-index: 250;
  display: flex;
  align-items: center;
  justify-content: center;
}

.allowed-sites-dialog {
  width: min(560px, 92vw);
  max-height: 85vh;
  display: flex;
  flex-direction: column;
  overflow: hidden;
  background: var(--surface-raised);
  border: 1px solid var(--border);
  border-radius: 12px;
  box-shadow: var(--shadow);
}

.dialog-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 16px 20px 12px;
}

.dialog-header h2 {
  margin: 0;
  font-size: 15px;
  font-weight: 600;
  color: var(--text);
}

.dialog-body {
  padding: 0 20px 18px;
  overflow-y: auto;
  font-size: 13px;
  line-height: 1.55;
  color: var(--text-secondary);
}

.dialog-desc {
  margin: 0 0 12px;
}

.allowed-sites-input-row {
  display: flex;
  gap: 8px;
}

.allowed-sites-input {
  flex: 1;
  padding: 7px 10px;
  font-size: 13px;
  color: var(--text);
  background: var(--surface);
  border: 1px solid var(--border);
  border-radius: 8px;
  outline: none;
}

.allowed-sites-input:focus {
  border-color: var(--accent);
}

.allowed-sites-error {
  margin: 10px 0 0;
  color: var(--danger, #e5534b);
  font-size: 12px;
}

.allowed-sites-list {
  margin: 12px 0 0;
  padding: 4px 0;
  list-style: none;
  border: 1px solid var(--border);
  border-radius: 8px;
  background: var(--surface);
}

.allowed-site-item {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 8px;
  padding: 7px 12px;
  font-size: 13px;
  color: var(--text);
}

.allowed-site-item:hover {
  background: var(--surface-hover);
}

.allowed-site-name {
  font-family: ui-monospace, SFMono-Regular, Consolas, monospace;
  font-size: 12px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.allowed-sites-empty {
  margin: 12px 0 0;
  padding: 12px;
  border: 1px dashed var(--border);
  border-radius: 8px;
  font-size: 12px;
  text-align: center;
}

.allowed-sites-note {
  margin: 12px 0 0;
  font-size: 12px;
  color: var(--text-muted);
}

.dialog-footer {
  display: flex;
  align-items: center;
  justify-content: flex-end;
  gap: 8px;
  padding: 12px 20px;
  background: var(--sidebar);
  border-top: 1px solid var(--border);
}
</style>
