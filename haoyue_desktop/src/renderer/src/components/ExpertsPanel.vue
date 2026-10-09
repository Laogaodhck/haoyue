<script setup lang="ts">
import { ArrowLeft, Check, Copy, LoaderCircle, RefreshCw, Search, Sparkles, Users } from '@lucide/vue'
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'

/** 专家条目，由 daemon 的 expert.list 接口返回（runtime 内置专家目录）。 */
interface Expert {
  id: string
  name: string
  title: string
  avatar: string
  bio: string
  domains: string[]
  skills: string[]
  prompt: string
}

const emit = defineEmits<{ start: [expert: Expert] }>()

const experts = ref<Expert[]>([])
const loading = ref(false)
const error = ref('')
const query = ref('')
const activeDomain = ref('')
const selected = ref<Expert | null>(null)
const copied = ref(false)

const domainOptions = computed(() => {
  const domains = new Set<string>()
  for (const expert of experts.value) {
    for (const domain of expert.domains) domains.add(domain)
  }
  return [...domains]
})

const filtered = computed(() => {
  const normalized = query.value.trim().toLocaleLowerCase()
  return experts.value
    .filter((expert) => !activeDomain.value || expert.domains.includes(activeDomain.value))
    .filter((expert) => {
      if (!normalized) return true
      const haystack = [expert.name, expert.title, expert.bio, ...expert.domains, ...expert.skills]
      return haystack.some((field) => field.toLocaleLowerCase().includes(normalized))
    })
})

async function requestJson<T>(method: string, params: Record<string, unknown> = {}): Promise<T> {
  const response = await window.haoyue.daemon.request(method, params)
  return JSON.parse(response.data) as T
}

async function loadExperts(): Promise<void> {
  loading.value = true
  error.value = ''
  try {
    experts.value = await requestJson<Expert[]>('expert.list')
  } catch (reason) {
    error.value = reason instanceof Error ? reason.message : String(reason)
  } finally {
    loading.value = false
  }
}

function selectExpert(expert: Expert): void {
  selected.value = expert
  copied.value = false
}

function backToList(): void {
  selected.value = null
  copied.value = false
}

async function copyPrompt(): Promise<void> {
  if (!selected.value) return
  try {
    await navigator.clipboard.writeText(selected.value.prompt)
    copied.value = true
    window.setTimeout(() => { copied.value = false }, 1600)
  } catch {
    copied.value = false
  }
}

function startWithExpert(expert: Expert): void {
  emit('start', expert)
}

function closeOnEscape(event: KeyboardEvent): void {
  // 详情态先返回列表；列表态不拦 Esc，交给宿主页面处理。
  if (event.key === 'Escape' && selected.value) backToList()
}

onMounted(() => {
  document.addEventListener('keydown', closeOnEscape)
  void loadExperts()
})
onBeforeUnmount(() => document.removeEventListener('keydown', closeOnEscape))
</script>

<template>
  <div class="experts-panel">
    <template v-if="!selected">
      <div class="experts-toolbar">
        <label class="experts-search">
          <Search :size="16" />
          <input v-model="query" placeholder="搜索专家或擅长领域" aria-label="搜索专家" />
        </label>
        <button class="icon-button" title="刷新" :disabled="loading" @click="loadExperts">
          <RefreshCw :size="16" :class="{ spinning: loading }" />
        </button>
      </div>

      <div v-if="domainOptions.length > 0" class="experts-domain-filter" role="tablist" aria-label="按领域筛选">
        <button class="experts-domain-chip" :class="{ active: activeDomain === '' }"
          @click="activeDomain = ''">全部</button>
        <button v-for="domain in domainOptions" :key="domain" class="experts-domain-chip"
          :class="{ active: activeDomain === domain }" @click="activeDomain = activeDomain === domain ? '' : domain">
          {{ domain }}
        </button>
      </div>

      <p v-if="error && experts.length > 0" class="experts-error">{{ error }}</p>

      <div class="experts-list">
        <div v-if="loading && experts.length === 0" class="experts-empty">
          <RefreshCw :size="30" class="spinning" />
          <strong>正在加载专家目录…</strong>
        </div>
        <div v-else-if="experts.length === 0" class="experts-empty">
          <Users :size="34" />
          <strong>{{ error ? '专家目录加载失败' : '暂无可用专家' }}</strong>
          <span>{{ error || '请点击右上角刷新重试。' }}</span>
        </div>
        <div v-else-if="filtered.length === 0" class="experts-empty">
          <Search :size="30" />
          <strong>没有匹配的专家</strong>
          <span>换个关键词或清除领域筛选试试。</span>
        </div>
        <div v-else class="experts-grid">
          <button v-for="expert in filtered" :key="expert.id" class="experts-card" type="button"
            @click="selectExpert(expert)">
            <span class="experts-avatar">{{ expert.avatar }}</span>
            <strong class="experts-card-name">{{ expert.name }}</strong>
            <span class="experts-card-title">{{ expert.title }}</span>
            <small class="experts-card-bio">{{ expert.bio }}</small>
            <span class="experts-card-domains">
              <span v-for="domain in expert.domains" :key="domain" class="inline-badge">{{ domain }}</span>
            </span>
          </button>
        </div>
      </div>
    </template>

    <div v-else class="experts-detail">
      <button class="page-back-button experts-detail-back" type="button" @click="backToList">
        <ArrowLeft :size="16" />
        <span>返回专家列表</span>
      </button>

      <div class="experts-detail-hero">
        <span class="experts-avatar experts-avatar-hero">{{ selected.avatar }}</span>
        <div class="experts-detail-hero-copy">
          <h3>{{ selected.name }}</h3>
          <p>{{ selected.title }}</p>
          <div class="experts-card-domains">
            <span v-for="domain in selected.domains" :key="domain" class="inline-badge">{{ domain }}</span>
          </div>
        </div>
        <button class="secondary-button primary-action experts-start-button" type="button" @click="startWithExpert(selected)">
          <Sparkles :size="15" />
          以此专家开始新任务
        </button>
      </div>

      <p v-if="error" class="experts-error">{{ error }}</p>

      <div class="experts-detail-body">
        <section class="experts-detail-section">
          <h4>简介</h4>
          <p>{{ selected.bio }}</p>
        </section>

        <section class="experts-detail-section">
          <h4>擅长技能</h4>
          <div class="experts-skill-tags">
            <span v-for="skill in selected.skills" :key="skill" class="experts-skill-tag">{{ skill }}</span>
          </div>
        </section>

        <section class="experts-detail-section">
          <div class="experts-prompt-heading">
            <h4>专家提示词</h4>
            <button class="secondary-button compact" @click="copyPrompt">
              <Check v-if="copied" :size="14" />
              <Copy v-else :size="14" />
              {{ copied ? '已复制' : '复制提示词' }}
            </button>
          </div>
          <pre class="experts-prompt-preview">{{ selected.prompt }}</pre>
          <p class="experts-prompt-hint">也可以点击上方「以此专家开始新任务」一键绑定，或将提示词粘贴到对话开头。</p>
        </section>
      </div>
    </div>
  </div>
</template>

<style scoped>
.experts-panel {
  display: grid;
  min-height: 0;
  min-width: 0;
  grid-template-rows: minmax(0, 1fr);
}

.experts-toolbar {
  display: flex;
  align-items: center;
  gap: 10px;
  padding: 4px 0 10px;
}

.experts-search {
  display: flex;
  min-width: 0;
  height: 38px;
  flex: 1;
  align-items: center;
  gap: 9px;
  padding: 0 12px;
  color: var(--text-muted);
  background: var(--surface);
  border: 1px solid var(--border);
  border-radius: 10px;
}

.experts-search:focus-within {
  border-color: color-mix(in srgb, var(--accent) 55%, var(--border));
}

.experts-search input {
  min-width: 0;
  flex: 1;
  color: var(--text);
  background: transparent;
  border: 0;
  outline: none;
}

.experts-search input::placeholder {
  color: var(--text-muted);
}

.experts-domain-filter {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
  padding-bottom: 10px;
}

.experts-domain-chip {
  padding: 4px 12px;
  font-size: 12px;
  color: var(--text-secondary);
  background: var(--surface);
  border: 1px solid var(--border);
  border-radius: 999px;
  cursor: pointer;
  transition: color 0.15s ease, border-color 0.15s ease;
}

.experts-domain-chip:hover {
  color: var(--text);
  border-color: var(--text-secondary);
}

.experts-domain-chip.active {
  color: var(--accent-contrast, #fff);
  background: var(--accent);
  border-color: var(--accent);
}

.experts-error {
  margin: 0 0 10px;
  font-size: 13px;
  color: var(--danger, #e5484d);
}

.experts-list {
  min-height: 0;
  overflow-y: auto;
  padding-bottom: 10px;
}

.experts-empty {
  display: grid;
  justify-items: center;
  gap: 8px;
  padding: 60px 20px;
  color: var(--text-muted);
  text-align: center;
}

.experts-empty svg {
  opacity: .7;
}

.experts-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(250px, 1fr));
  gap: 14px;
}

.experts-card {
  display: grid;
  align-content: start;
  justify-items: start;
  gap: 6px;
  padding: 16px;
  text-align: left;
  background: var(--surface);
  border: 1px solid var(--border);
  border-radius: 14px;
  cursor: pointer;
  transition: border-color 0.15s ease, transform 0.15s ease, box-shadow 0.15s ease;
}

.experts-card:hover {
  border-color: var(--accent);
  box-shadow: 0 6px 20px rgb(0 0 0 / 10%);
  transform: translateY(-2px);
}

.experts-avatar {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 42px;
  height: 42px;
  font-size: 22px;
  background: var(--surface);
  border: 1px solid var(--border);
  border-radius: 50%;
}

.experts-avatar-hero {
  width: 64px;
  height: 64px;
  font-size: 32px;
  flex-shrink: 0;
}

.experts-card-name {
  font-size: 15px;
}

.experts-card-title {
  font-size: 12px;
  color: var(--accent);
}

.experts-card-bio {
  display: -webkit-box;
  overflow: hidden;
  font-size: 12px;
  line-height: 1.6;
  color: var(--text-secondary);
  -webkit-box-orient: vertical;
  -webkit-line-clamp: 3;
}

.experts-card-domains {
  display: flex;
  flex-wrap: wrap;
  gap: 6px;
  margin-top: 2px;
}

.experts-detail {
  min-height: 0;
  overflow-y: auto;
  padding-bottom: 10px;
}

.experts-detail-back {
  margin-bottom: 12px;
}

.experts-detail-hero {
  display: flex;
  align-items: center;
  gap: 16px;
  padding: 18px;
  background: var(--surface);
  border: 1px solid var(--border);
  border-radius: 16px;
}

.experts-start-button {
  display: inline-flex;
  flex-shrink: 0;
  align-items: center;
  gap: 7px;
  margin-left: auto;
}

.experts-detail-hero-copy h3 {
  margin: 0 0 4px;
  font-size: 18px;
}

.experts-detail-hero-copy p {
  margin: 0 0 8px;
  font-size: 13px;
  color: var(--text-secondary);
}

.experts-detail-body {
  display: grid;
  gap: 16px;
  margin-top: 16px;
}

.experts-detail-section {
  padding: 16px;
  background: var(--surface);
  border: 1px solid var(--border);
  border-radius: 14px;
}

.experts-detail-section h4 {
  margin: 0 0 8px;
  font-size: 13px;
  color: var(--text-secondary);
}

.experts-detail-section > p {
  margin: 0;
  font-size: 13px;
  line-height: 1.7;
}

.experts-skill-tags {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
}

.experts-skill-tag {
  padding: 4px 12px;
  font-size: 12px;
  background: var(--surface);
  border: 1px solid var(--border);
  border-radius: 8px;
}

.experts-prompt-heading {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 10px;
  margin-bottom: 8px;
}

.experts-prompt-heading h4 {
  margin: 0;
}

.experts-prompt-preview {
  max-height: 260px;
  padding: 12px;
  overflow-y: auto;
  font-family: var(--font-mono, monospace);
  font-size: 12px;
  line-height: 1.7;
  white-space: pre-wrap;
  word-break: break-word;
  color: var(--text-secondary);
  background: var(--surface);
  border: 1px solid var(--border);
  border-radius: 10px;
}

.experts-prompt-hint {
  margin: 8px 0 0;
  font-size: 12px;
  color: var(--text-secondary);
}

.spinning {
  animation: experts-panel-spin .8s linear infinite;
}

@keyframes experts-panel-spin {
  to {
    transform: rotate(360deg);
  }
}

@media (max-width: 640px) {
  .experts-grid {
    grid-template-columns: 1fr;
  }

  .experts-detail-hero {
    align-items: flex-start;
    flex-direction: column;
  }
}
</style>
