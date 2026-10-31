import { createApp } from 'vue'
import App from './App.vue'
import './styles.css'
// 视觉层叠加在基础样式之后：只改外观（扁平分区、等宽元信息、IDE 状态栏），不改交互。
import './ide-theme.css'

createApp(App).mount('#app')

