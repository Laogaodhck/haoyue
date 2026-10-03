<script setup lang="ts">
import { ArrowLeft, LoaderCircle, PackageOpen, RefreshCw, Search } from '@lucide/vue'
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'

const props = defineProps<{
  open: boolean
}>()

const emit = defineEmits<{
  close: []
}>()

/** 技能市场条目，由 daemon 的 skill.official.list 接口返回。 */
interface OfficialSkill {
  slug: string
  name: string
  description: string
  version?: string
  tags?: string[]
  installed: boolean
  enabled: boolean
}

const catalog = ref<OfficialSkill[]>([])
const loading = ref(false)
const error = ref('')
const query = ref('')
/** 正在安装或启停中的技能 slug，行内开关显示进度。 */
const pending = ref<string[]>([])

const filtered = computed(() => {
  const normalized = query.value.trim().toLocaleLowerCase()
  if (!normalized) return catalog.value
  return catalog.value.filter((skill) =>
    skill.name.toLocaleLowerCase().includes(normalized) ||
    skill.description.toLocaleLowerCase().includes(normalized))
})

function isPending(skill: OfficialSkill): boolean {
  return pending.value.includes(skill.slug)
}

async function requestJson<T>(method: string, params: Record<string, unknown> = {}): Promise<T> {
  const response = await window.haoyue.daemon.request(method, params)
  return JSON.parse(response.data) as T
}

async function loadCatalog(): Promise<void> {
  loading.value = true
  error.value = ''
  try {
    catalog.value = await requestJson<OfficialSkill[]>('skill.official.list')
  } catch (reason) {
    error.value = reason instanceof Error ? reason.message : String(reason)
  } finally {
    loading.value = false
  }
}

/**
 * 开启 = 安装并启用（接口幂等，已安装时仅重新启用）；关闭 = 仅禁用，
 * 本地技能文件保留，可随时再次开启。
 */
async function toggleSkill(skill: OfficialSkill): Promise<void> {
  if (isPending(skill)) return
  pending.value = [...pending.value, skill.slug]
  error.value = ''
  try {
    if (skill.enabled) {
      await requestJson('skill.toggle', { name: skill.name, enabled: false })
      await loadCatalog()
    } else {
      catalog.value = await requestJson<OfficialSkill[]>('skill.official.install', { slug: skill.slug })
    }
  } catch (reason) {
    error.value = reason instanceof Error ? reason.message : String(reason)
  } finally {
    pending.value = pending.value.filter((slug) => slug !== skill.slug)
  }
}

watch(() => props.open, (open) => {
  if (open) {
    query.value = ''
    void loadCatalog()
  }
})

function closeOnEscape(event: KeyboardEvent): void {
  if (props.open && event.key === 'Escape') emit('close')
}

onMounted(() => document.addEventListener('keydown', closeOnEscape))
onBeforeUnmount(() => document.removeEventListener('keydown', closeOnEscape))
</script>

<template>
  <section v-if="open" class="official-skills-dialog embedded-page" role="region"
    aria-labelledby="official-skills-title">
    <header class="official-skills-header">
      <div class="official-skills-heading">
        <button class="page-back-button" type="button" @click="emit('close')">
          <ArrowLeft :size="18" />
          <span>返回应用</span>
        </button>
        <div class="official-skills-title-copy">
          <h2 id="official-skills-title">技能市场</h2>
        </div>
      </div>
    </header>

    <div class="official-skills-toolbar">
      <label class="official-skills-search">
        <Search :size="17" />
        <input v-model="query" autofocus placeholder="搜索技能" aria-label="搜索技能" />
      </label>
      <button class="icon-button" title="刷新" :disabled="loading" @click="loadCatalog">
        <RefreshCw :size="17" :class="{ spinning: loading }" />
      </button>
    </div>

    <p v-if="error && catalog.length > 0" class="official-skills-error">{{ error }}</p>

    <div class="official-skills-list">
      <div v-if="loading && catalog.length === 0" class="official-skills-empty">
        <RefreshCw :size="30" class="spinning" />
        <strong>正在加载技能目录…</strong>
      </div>
      <div v-else-if="catalog.length === 0" class="official-skills-empty">
        <PackageOpen :size="34" />
        <strong>{{ error ? '技能目录加载失败' : '暂无可用技能' }}</strong>
        <span>{{ error || '请点击右上角刷新重试。' }}</span>
      </div>
      <div v-else-if="filtered.length === 0" class="official-skills-empty">
        <Search :size="30" />
        <strong>没有匹配的技能</strong>
        <span>试试其他搜索词。</span>
      </div>
      <div v-else class="official-skills-items">
        <div v-for="skill in filtered" :key="skill.slug" class="official-skill-row">
          <div class="official-skill-icon">
            <PackageOpen :size="18" />
          </div>
          <div class="list-main">
            <div>
              <strong>{{ skill.name }}</strong>
              <span v-if="skill.version" class="version-text">v{{ skill.version }}</span>
              <span v-if="!skill.installed" class="inline-badge">未安装</span>
              <span v-for="tag in skill.tags" :key="tag" class="inline-badge">{{ tag }}</span>
            </div>
            <small>{{ skill.description }}</small>
          </div>
          <button class="switch-control" :class="{ active: skill.enabled, pending: isPending(skill) }"
            :disabled="isPending(skill)"
            :aria-label="isPending(skill) ? '处理中' : (skill.enabled ? '禁用' : (skill.installed ? '启用' : '安装并启用'))"
            @click="toggleSkill(skill)">
            <LoaderCircle v-if="isPending(skill)" class="spin" :size="12" /><span v-else />
          </button>
        </div>
      </div>
    </div>

    <footer class="official-skills-footer">
      技能市场由 Haoyue 团队维护 · 本地技能请前往「设置 → 技能」管理
    </footer>
  </section>
</template>

<style scoped>
.official-skills-backdrop {
  z-index: 130;
  padding: clamp(16px, 4vw, 54px);
  background: rgb(0 0 0 / 38%);
  backdrop-filter: blur(3px);
}

.official-skills-dialog.embedded-page {
  width: 100%;
  height: 100%;
  border: 0;
  border-radius: 0;
  box-shadow: none;
  background: var(--bg);
}

.official-skills-dialog {
  display: grid;
  width: min(760px, 100%);
  height: min(620px, calc(100vh - 44px));
  grid-template-rows: auto auto minmax(0, 1fr) auto;
  min-height: 0;
  overflow: hidden;
  background: var(--surface-raised);
  border: 1px solid var(--border);
  border-radius: 18px;
  box-shadow: 0 24px 70px rgb(0 0 0 / 24%);
}

.official-skills-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 20px;
  min-height: 62px;
  padding: 10px 22px;
  border-bottom: 1px solid var(--border);
}

.official-skills-heading {
  display: flex;
  min-width: 0;
  align-items: center;
  gap: 12px;
}

.official-skills-title-copy {
  min-width: 0;
}

.official-skills-eyebrow {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  color: var(--accent);
  font-size: 10px;
  font-weight: 700;
  letter-spacing: .1em;
}

.official-skills-header h2,
.official-skills-header p {
  margin: 0;
}

.official-skills-header h2 {
  display: flex;
  align-items: center;
  gap: 9px;
  margin: 0;
  font-size: 16px;
  font-weight: 650;
  letter-spacing: -.01em;
}

.official-skills-chip {
  padding: 3px 9px;
  color: var(--accent);
  font-size: 11px;
  font-weight: 600;
  letter-spacing: 0;
  background: var(--accent-soft);
  border-radius: 999px;
}

.official-skills-header p {
  margin-top: 2px;
  color: var(--text-secondary);
  font-size: 11px;
}

.official-skills-toolbar {
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 18px 22px 14px;
}

.official-skills-search {
  display: flex;
  min-width: 0;
  height: 40px;
  flex: 1;
  align-items: center;
  gap: 9px;
  padding: 0 13px;
  color: var(--text-muted);
  background: var(--surface);
  border: 1px solid var(--border);
  border-radius: 10px;
}

.official-skills-search:focus-within {
  border-color: color-mix(in srgb, var(--accent) 55%, var(--border));
}

.official-skills-search input {
  min-width: 0;
  flex: 1;
  color: var(--text);
  background: transparent;
  border: 0;
  outline: 0;
}

.official-skills-search input::placeholder {
  color: var(--text-muted);
}

.official-skills-error {
  margin: 0 22px 12px;
  padding: 9px 12px;
  color: var(--danger);
  font-size: 12px;
  background: color-mix(in srgb, var(--danger) 9%, transparent);
  border: 1px solid color-mix(in srgb, var(--danger) 25%, transparent);
  border-radius: 8px;
}

.official-skills-list {
  min-height: 0;
  overflow-y: auto;
  padding: 0 22px 22px;
}

.official-skills-empty {
  display: grid;
  height: 100%;
  min-height: 260px;
  place-content: center;
  justify-items: center;
  gap: 7px;
  color: var(--text-muted);
  text-align: center;
}

.official-skills-empty svg {
  margin-bottom: 6px;
  color: var(--text-muted);
  opacity: .7;
}

.official-skills-empty strong {
  color: var(--text);
  font-size: 15px;
  font-weight: 600;
}

.official-skills-empty span {
  font-size: 12px;
}

.official-skills-items {
  display: grid;
  gap: 8px;
}

.official-skill-row {
  display: flex;
  min-width: 0;
  min-height: 72px;
  align-items: center;
  gap: 12px;
  padding: 12px 14px;
  background: var(--surface);
  border: 1px solid var(--border);
  border-radius: 12px;
}

.official-skill-icon {
  display: grid;
  width: 40px;
  height: 40px;
  flex: none;
  place-items: center;
  color: var(--accent);
  background: var(--accent-soft);
  border-radius: 10px;
}

.official-skill-row .list-main {
  min-width: 0;
  flex: 1;
}

.official-skill-row .list-main>div {
  display: flex;
  align-items: center;
  gap: 8px;
}

.official-skill-row small {
  display: block;
  margin-top: 4px;
  overflow: hidden;
  color: var(--text-secondary);
  font-size: 12px;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.official-skills-footer {
  padding: 12px 22px;
  color: var(--text-muted);
  font-size: 11px;
  text-align: center;
  border-top: 1px solid var(--border);
}

.spinning {
  animation: official-skills-spin .8s linear infinite;
}

@keyframes official-skills-spin {
  to {
    transform: rotate(360deg);
  }
}
</style>
