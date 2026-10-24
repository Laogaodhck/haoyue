<script setup lang="ts">
import { LoaderCircle, Save } from '@lucide/vue'
import { computed, reactive, ref, watch } from 'vue'
import { requestJson } from '../daemon-json'
import { diffPatch, loadConfigDocument, saveConfigPatch } from '../config-patch'
import FieldLabel from './FieldLabel.vue'
import SelectMenu from './SelectMenu.vue'

/**
 * 模型与提供商 → 外部能力。
 *
 * 三个此前完全没有入口、只能手改 `~/.haoyue/config.json` 的配置：
 * - `ImageGen.Model/Size`：图像生成工具用的模型与尺寸。未配置可用图像模型时
 *   `image_generate` 工具**自动从工具清单里消失**，用户完全不知道原因；
 * - `DocRender.Enabled/ToolkitPath/PythonPath`：Python 文档渲染工具。Python 缺失时
 *   同样静默隐藏；
 * - `Knowledge.EmbeddingModel`：语义检索用的嵌入模型。总开关有 UI，模型选择没有，
 *   于是「语义检索开着但一直退回纯词法」也无从排查。
 */
const props = defineProps<{ connected: boolean }>()

interface CatalogItem {
  ref: string
  provider: string
  providerEnabled: boolean
  capabilities?: Record<string, boolean | string>
}

const imageGen = reactive({ model: '', size: '1024x1024' })
const docRender = reactive({ enabled: true, toolkitPath: '', pythonPath: '' })
const embeddingModel = ref('')
const catalog = ref<CatalogItem[]>([])
const docRenderAvailable = ref(false)
const loading = ref(false)
const saving = ref(false)
const note = ref('')
const error = ref('')

const sizeOptions = [
  { value: '1024x1024', label: '1024×1024（方图）' },
  { value: '1024x1792', label: '1024×1792（竖图）' },
  { value: '1792x1024', label: '1792×1024（横图）' },
  { value: '512x512', label: '512×512（小图）' }
]

const imageModelOptions = computed(() => [
  { value: '', label: '自动', description: '使用第一个声明了图像能力的模型；找不到时 image_generate 工具会被隐藏' },
  ...catalog.value
    .filter((item) => item.capabilities?.image === true && item.providerEnabled)
    .map((item) => ({ value: item.ref, label: item.ref, description: item.provider }))
])

const embeddingOptions = computed(() => [
  { value: '', label: '自动', description: '使用第一个支持嵌入的 HTTP 提供商模型；找不到时检索退回纯词法匹配' },
  ...catalog.value
    .filter((item) => item.capabilities?.embedding === true && item.providerEnabled)
    .map((item) => ({ value: item.ref, label: item.ref, description: item.provider }))
])

async function load(): Promise<void> {
  if (!props.connected) return
  loading.value = true
  error.value = ''
  try {
    const [document, models] = await Promise.all([
      loadConfigDocument(),
      requestJson<CatalogItem[]>('model.catalog')
    ])
    catalog.value = models
    const config = document.config as {
      ImageGen?: { Model?: string | null; Size?: string }
      DocRender?: { Enabled?: boolean; ToolkitPath?: string; PythonPath?: string }
      Knowledge?: { EmbeddingModel?: string | null }
    }
    imageGen.model = config.ImageGen?.Model ?? ''
    imageGen.size = config.ImageGen?.Size ?? '1024x1024'
    docRender.enabled = config.DocRender?.Enabled ?? true
    docRender.toolkitPath = config.DocRender?.ToolkitPath ?? ''
    docRender.pythonPath = config.DocRender?.PythonPath ?? ''
    embeddingModel.value = config.Knowledge?.EmbeddingModel ?? ''
    docRenderAvailable.value = true
  } catch (reason) {
    error.value = reason instanceof Error ? reason.message : String(reason)
  } finally {
    loading.value = false
  }
}

async function save(): Promise<void> {
  if (!props.connected) return
  saving.value = true
  error.value = ''
  note.value = ''
  try {
    const current = {
      ImageGen: { Model: imageGen.model, Size: imageGen.size },
      DocRender: { Enabled: docRender.enabled, ToolkitPath: docRender.toolkitPath, PythonPath: docRender.pythonPath },
      Knowledge: { EmbeddingModel: embeddingModel.value }
    }
    const config = (await loadConfigDocument()).config as {
      ImageGen?: { Model?: string | null; Size?: string }
      DocRender?: { Enabled?: boolean; ToolkitPath?: string; PythonPath?: string }
      Knowledge?: { EmbeddingModel?: string | null }
    }
    const baseline = {
      ImageGen: { Model: config.ImageGen?.Model ?? '', Size: config.ImageGen?.Size ?? '1024x1024' },
      DocRender: {
        Enabled: config.DocRender?.Enabled ?? true,
        ToolkitPath: config.DocRender?.ToolkitPath ?? '',
        PythonPath: config.DocRender?.PythonPath ?? ''
      },
      Knowledge: { EmbeddingModel: config.Knowledge?.EmbeddingModel ?? '' }
    }
    const patch = diffPatch(baseline, current)
    if (Object.keys(patch).length === 0) {
      note.value = '没有改动'
      return
    }
    await saveConfigPatch(patch)
    note.value = '外部能力配置已保存，下一个回合生效'
  } catch (reason) {
    error.value = reason instanceof Error ? reason.message : String(reason)
  } finally {
    saving.value = false
  }
}

watch(() => props.connected, (connected) => { if (connected) void load() }, { immediate: true })
</script>

<template>
  <div class="integrations-panel">
    <p v-if="error" class="integrations-note is-error">{{ error }}</p>
    <p v-else-if="note" class="integrations-note is-ok">{{ note }}</p>
    <p v-if="loading" class="integrations-note"><LoaderCircle class="spin" :size="14" /> 正在读取配置</p>

    <div class="integrations-group">
      <div class="integrations-head">
        <strong>图像生成</strong>
        <small>未配置可用图像模型时 `image_generate` 工具会从工具清单中隐藏。</small>
      </div>
      <div class="integrations-grid">
        <label>
          <FieldLabel en="Image Model" zh="图像模型"
            help="只列出把 capabilities.image 打开了的模型。留空 = 自动选用第一个具备图像能力的模型。" />
          <SelectMenu v-model="imageGen.model" class="settings-select" label="图像模型"
            :options="imageModelOptions" :menu-min-width="320" />
        </label>
        <label>
          <FieldLabel en="Image Size" zh="生成尺寸"
            help="传给 images/generations 的 size 参数；提供商不支持该尺寸时由端点自行裁定。" />
          <SelectMenu v-model="imageGen.size" class="settings-select" label="生成尺寸"
            :options="sizeOptions" :menu-min-width="220" />
        </label>
      </div>
      <p v-if="imageModelOptions.length === 1" class="integrations-hint">
        当前没有任何模型声明图像能力——`image_generate` 工具处于隐藏状态。
        请在「模型与提供商」中给图像模型勾上对应能力。
      </p>
    </div>

    <div class="integrations-group">
      <div class="integrations-head">
        <strong>文档渲染（doc_render）</strong>
        <small>调用本机 Python 的 doc_toolkit 生成 Word/Excel；Python 缺失或找不到工具包时工具自动隐藏。</small>
      </div>
      <label class="provider-enabled-row">
        <span>
          <strong>启用文档渲染工具</strong>
          <small>关闭后 doc_render 从模型工具视图中移除（不再占用工具位与提示词预算）。</small>
        </span>
        <input v-model="docRender.enabled" class="sr-only" type="checkbox" />
        <span class="toggle-switch" aria-hidden="true"><span /></span>
      </label>
      <div v-if="docRender.enabled" class="integrations-grid">
        <label>
          <FieldLabel en="Toolkit Path" zh="doc_toolkit 路径"
            help="doc_toolkit 包目录（含 __init__.py 的那一级）的绝对路径。留空按可执行文件相对目录、HAOYUE_DOC_TOOLKIT_PATH 环境变量与当前目录逐级探测。" />
          <input v-model="docRender.toolkitPath" placeholder="留空 = 自动探测" spellcheck="false" />
        </label>
        <label>
          <FieldLabel en="Python Path" zh="Python 解释器"
            help="显式指定 Python 可执行文件路径。留空复用系统探测链（python3 / python / py）。" />
          <input v-model="docRender.pythonPath" placeholder="留空 = 自动探测" spellcheck="false" />
        </label>
      </div>
      <p v-if="!docRenderAvailable" class="integrations-hint">当前运行时版本未暴露文档渲染配置。</p>
    </div>

    <div class="integrations-group">
      <div class="integrations-head">
        <strong>知识库语义检索</strong>
        <small>语义检索的总开关在「知识库」页；这里选它用哪个嵌入模型。不配置时按第一个支持嵌入的模型自动选择，找不到就只做词法匹配。</small>
      </div>
      <label>
        <FieldLabel en="Embedding Model" zh="嵌入模型"
          help="只列出把 capabilities.embedding 打开了的模型。换模型后已建的语义索引需要重建（知识库页「建立索引」）。" />
        <SelectMenu v-model="embeddingModel" class="settings-select" label="嵌入模型"
          :options="embeddingOptions" :menu-min-width="320" />
      </label>
      <p v-if="embeddingOptions.length === 1" class="integrations-hint">
        没有任何模型声明嵌入能力——语义检索会静默退回纯词法匹配。
      </p>
    </div>

    <button type="button" class="secondary-button primary-action" :disabled="saving || !connected" @click="save">
      <LoaderCircle v-if="saving" class="spin" :size="14" />
      <Save v-else :size="14" />
      {{ saving ? '保存中…' : '保存外部能力配置' }}
    </button>
  </div>
</template>

<style scoped>
.integrations-panel {
  display: flex;
  flex-direction: column;
  gap: 12px;
}

.integrations-note {
  display: flex;
  align-items: center;
  gap: 6px;
  margin: 0;
  color: var(--text-muted);
  font-size: 12px;
}

.integrations-note.is-ok { color: var(--accent); }
.integrations-note.is-error { color: var(--danger); }

.integrations-group {
  display: flex;
  flex-direction: column;
  gap: 8px;
  padding: 10px 12px;
  background: color-mix(in srgb, var(--surface-hover) 45%, transparent);
  border: 1px solid var(--border);
  border-radius: 10px;
}

.integrations-head {
  display: flex;
  flex-direction: column;
  gap: 3px;
}

.integrations-head small,
.integrations-hint {
  color: var(--text-muted);
  font-size: 11px;
  line-height: 1.55;
}

.integrations-hint { margin: 0; color: var(--danger); }

.integrations-grid {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(230px, 1fr));
  gap: 12px;
}

.integrations-grid label {
  display: flex;
  flex-direction: column;
  gap: 5px;
  min-width: 0;
}

.integrations-grid input {
  width: 100%;
  padding: 7px 9px;
  color: var(--text);
  font-family: var(--font-mono, monospace);
  font-size: 12px;
  background: var(--surface);
  border: 1px solid var(--border-strong);
  border-radius: 8px;
}
</style>
