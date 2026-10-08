<script setup lang="ts">
import { LoaderCircle, PackageOpen, RefreshCw, Search } from '@lucide/vue'
import { computed, onMounted, ref } from 'vue'

const emit = defineEmits<{
  /** 目录发生变化（安装/启停），宿主可刷新本地技能列表。 */
  changed: []
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
    emit('changed')
  } catch (reason) {
    error.value = reason instanceof Error ? reason.message : String(reason)
  } finally {
    pending.value = pending.value.filter((slug) => slug !== skill.slug)
  }
}

onMounted(() => { void loadCatalog() })
</script>

<template>
  <div class="official-skills-panel">
    <div class="official-skills-toolbar">
      <label class="official-skills-search">
        <Search :size="16" />
        <input v-model="query" placeholder="搜索技能" aria-label="搜索技能" />
      </label>
      <button class="icon-button" title="刷新" :disabled="loading" @click="loadCatalog">
        <RefreshCw :size="16" :class="{ spinning: loading }" />
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
  </div>
</template>

<style scoped>
.official-skills-panel {
  display: grid;
  min-height: 0;
  grid-template-rows: auto minmax(0, 1fr);
}

.official-skills-toolbar {
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 4px 0 12px;
}

.official-skills-search {
  display: flex;
  min-width: 0;
  height: 38px;
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
  margin: 0 0 12px;
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
  padding-bottom: 8px;
}

.official-skills-empty {
  display: grid;
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
  min-height: 68px;
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

.spinning {
  animation: official-skills-spin .8s linear infinite;
}

@keyframes official-skills-spin {
  to {
    transform: rotate(360deg);
  }
}
</style>
