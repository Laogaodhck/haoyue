import { resolve } from 'node:path'
import { defineConfig, externalizeDepsPlugin } from 'electron-vite'
import vue from '@vitejs/plugin-vue'

export default defineConfig({
  main: {
    plugins: [externalizeDepsPlugin()]
  },
  preload: {
    plugins: [externalizeDepsPlugin()],
    build: {
      rollupOptions: {
        output: {
          format: 'cjs',
          entryFileNames: '[name].cjs'
        }
      }
    }
  },
  renderer: {
    resolve: {
      alias: {
        '@renderer': resolve('src/renderer/src'),
        '@shared': resolve('src/shared')
      }
    },
    plugins: [
      // <webview> 是 Electron 的宿主自定义元素，不是 Vue 组件——不声明为自定义元素时
      // 模板编译会把它当未解析组件处理（运行期告警且属性/事件绑定语义不同）。
      vue({ template: { compilerOptions: { isCustomElement: (tag) => tag === 'webview' } } })
    ]
  }
})
