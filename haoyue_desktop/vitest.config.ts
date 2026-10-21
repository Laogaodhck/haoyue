import vue from '@vitejs/plugin-vue'
import { defineConfig } from 'vitest/config'

// vitest 独立配置：默认 node 环境跑纯逻辑用例；组件测试（.vue）用文件级
// `// @vitest-environment jsdom` 切换，避免 jsdom 全局开销拖慢既有 269 个用例。
export default defineConfig({
  plugins: [vue()],
  test: {
    include: ['src/**/*.test.ts', 'src/**/*.test.tsx'],
  },
})
