import { defineConfig } from 'vitepress'

// PAGES_BASE lets CI deploy to a GitHub Pages project site (e.g. "/haoyue/");
// unset means serve from the domain root, which keeps local dev unchanged.
const base = process.env.PAGES_BASE || '/'

export default defineConfig({
  title: 'Haoyue',
  description: '本地优先、开源、可扩展的通用 AI Agent Runtime',
  base,
  cleanUrls: true,

  head: [
    ['link', { rel: 'icon', type: 'image/x-icon', href: `${base}favicon.ico` }],
    ['link', { rel: 'shortcut icon', href: `${base}favicon.ico` }],
    ['link', { rel: 'icon', type: 'image/png', href: `${base}logo.png` }],
    ['link', { rel: 'apple-touch-icon', href: `${base}logo.png` }]
  ],

  locales: {
    root: {
      label: '简体中文',
      lang: 'zh-CN',
      title: 'Haoyue',
      description: '本地优先、开源、可扩展的通用 AI Agent Runtime',
      themeConfig: {
        logo: '/logo.png',
        siteTitle: 'Haoyue',
        nav: [
          { text: '首页', link: '/' },
          { text: '文档中心', link: '/doc/' },
          { text: 'Desktop', link: '/doc/desktop' },
          { text: '架构指南', link: '/doc/architecture' },
          { text: 'CLI 命令', link: '/doc/cli' },
          {
            text: 'v1.0.0 (.NET 10.0)',
            items: [
              { text: 'GitHub 仓库', link: 'https://github.com/Laogaodhck/haoyue' },
              { text: 'npm: haoyue-cli', link: 'https://www.npmjs.com/package/haoyue-cli' },
              { text: '更新日志', link: '/doc/faq#release-notes' }
            ]
          }
        ],

        sidebar: {
          '/doc/': [
            {
              text: '起步与概览',
              items: [
                { text: '项目概览', link: '/doc/' },
                { text: '快速开始', link: '/doc/quickstart' },
                { text: 'Desktop 桌面端', link: '/doc/desktop' },
                { text: '架构设计 (Runtime First)', link: '/doc/architecture' }
              ]
            },
            {
              text: '核心功能模块',
              items: [
                { text: '多提供商与智能路由', link: '/doc/providers' },
                { text: 'CLI 命令行交互指南', link: '/doc/cli' },
                { text: '工具生态与内置工具', link: '/doc/tools' },
                { text: '技能体系 (Skills)', link: '/doc/skills' },
                { text: 'Model Context Protocol (MCP)', link: '/doc/mcp' },
                { text: '进化引擎（Evolution）', link: '/doc/evolution' },
                { text: '知识库（Knowledge Base）', link: '/doc/knowledge' }
              ]
            },
            {
              text: '运行时进阶机制',
              items: [
                { text: '工作区管理与 Memory 记忆体系', link: '/doc/workspace' },
                { text: '代码构建验证与自动自我修复', link: '/doc/verification' },
                { text: '配置参考指南', link: '/doc/configuration' },
                { text: 'Daemon 服务与 IPC 协议', link: '/doc/daemon' },
                { text: '常见问题与诊断 (Doctor)', link: '/doc/faq' }
              ]
            }
          ]
        },

        footer: {
          message: '<div class="footer-slogan">为 .NET 生态添砖加瓦</div><div class="footer-icp"><a href="https://beian.miit.gov.cn/" target="_blank" rel="noopener noreferrer" class="icp-link">陕ICP备2025084176号-1</a></div>',
          copyright: '© 2022 - 2026 花濑HoiLai. 保留所有权利。探索无限 TO EXPLORE INFINITY.'
        },

        docFooter: {
          prev: '上一篇',
          next: '下一篇'
        },

        outline: {
          level: [2, 3],
          label: '页面导航'
        },

        search: {
          provider: 'local'
        }
      }
    },

    en: {
      label: 'English',
      lang: 'en-US',
      link: '/en/',
      title: 'Haoyue',
      description: 'A local-first, open-source, extensible general-purpose AI Agent Runtime',
      themeConfig: {
        logo: '/logo.png',
        siteTitle: 'Haoyue',
        nav: [
          { text: 'Home', link: '/en/' },
          { text: 'Docs', link: '/en/doc/' },
          { text: 'Desktop', link: '/en/doc/desktop' },
          { text: 'Architecture', link: '/en/doc/architecture' },
          { text: 'CLI Reference', link: '/en/doc/cli' },
          {
            text: 'v1.0.0 (.NET 10.0)',
            items: [
              { text: 'GitHub Repository', link: 'https://github.com/Laogaodhck/haoyue' },
              { text: 'npm: haoyue-cli', link: 'https://www.npmjs.com/package/haoyue-cli' },
              { text: 'Release Notes', link: '/en/doc/faq#release-notes' }
            ]
          }
        ],

        sidebar: {
          '/en/doc/': [
            {
              text: 'Getting Started',
              items: [
                { text: 'Overview', link: '/en/doc/' },
                { text: 'Quick Start', link: '/en/doc/quickstart' },
                { text: 'Desktop Client', link: '/en/doc/desktop' },
                { text: 'Architecture (Runtime First)', link: '/en/doc/architecture' }
              ]
            },
            {
              text: 'Core Features',
              items: [
                { text: 'Providers & Smart Routing', link: '/en/doc/providers' },
                { text: 'CLI Command Reference', link: '/en/doc/cli' },
                { text: 'Tools & Built-in System', link: '/en/doc/tools' },
                { text: 'Skills System', link: '/en/doc/skills' },
                { text: 'Model Context Protocol (MCP)', link: '/en/doc/mcp' },
                { text: 'Evolution Engine', link: '/en/doc/evolution' },
                { text: 'Knowledge Base', link: '/en/doc/knowledge' }
              ]
            },
            {
              text: 'Advanced Runtime',
              items: [
                { text: 'Workspace & Memory System', link: '/en/doc/workspace' },
                { text: 'Build Verification & Self-Healing', link: '/en/doc/verification' },
                { text: 'Configuration Reference', link: '/en/doc/configuration' },
                { text: 'Daemon & IPC Protocol', link: '/en/doc/daemon' },
                { text: 'FAQ & Diagnostics', link: '/en/doc/faq' }
              ]
            }
          ]
        },

        footer: {
          message: '<div class="footer-slogan">Building the .NET Ecosystem Together</div><div class="footer-icp"><a href="https://beian.miit.gov.cn/" target="_blank" rel="noopener noreferrer" class="icp-link">陕ICP备2025084176号-1</a></div>',
          copyright: '© 2022 - 2026 HoiLai. All Rights Reserved. TO EXPLORE INFINITY.'
        },

        docFooter: {
          prev: 'Previous Page',
          next: 'Next Page'
        },

        outline: {
          level: [2, 3],
          label: 'On this page'
        },

        search: {
          provider: 'local'
        }
      }
    }
  },

  themeConfig: {
    socialLinks: [
      { icon: 'github', link: 'https://github.com/Laogaodhck/haoyue' }
    ]
  }
})
