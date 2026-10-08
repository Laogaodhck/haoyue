# 知识库（Knowledge Base）

知识库是 Haoyue 的长期事实存储：Agent 在对话中通过 `knowledge_save` / `knowledge_search` / `knowledge_forget` 工具自动沉淀与检索，你也可以在 Desktop 或 CLI 中手动维护。条目按「当前工作区 / 全局」两个作用域隔离，SQLite 持久化，重启与升级都不丢失。

## 检索如何工作

知识检索刻意使用**子串匹配**而非全文索引：SQLite 的分词器不会切分中文，只有子串匹配能让中英混排的查询真正命中。查询在服务端经历完整预处理：

- **归一化**：Unicode FormKC 折叠 + 小写，全角输入与半角等效；
- **CJK 二元切分**：中文查询切成二字片段，无空格的长问句也能命中；
- **同义词扩展**：查询词先按同义词表展开（见下文）；
- **容错**：短词允许 1 处编辑距离、长词 2 处（作用于标题与标签）；
- **计分与排序**：标题 +3、内容 +2、标签 +2，覆盖率优先，其次得分，最后按更新时间。

## 标签：精确归组

标签用逗号分隔（全角 `，` `；` 也支持），匹配语义是**整标签相等**（忽略大小写）——标签 `build` 不会命中 `buildtool`，这是刻意的：标签是归组维度，不是搜索词。

- `knowledge.tags` 聚合当前作用域的所有标签与条目计数；
- `knowledge.list` 与 `knowledge.search` 都接受 `tag` 参数做精确筛选；
- Desktop 的标签栏一键筛选，再次点击取消。

## 导入与导出

**导入**（`knowledge.import` / `haoyue knowledge import`）把文件拆成知识条目：

- 支持 txt / md / csv / 各类代码文件（UTF-8，GBK 自动回退）、docx、xlsx；
- 单文件上限 10 MB；正文按 6000 字符分块，单文件最多 200 条；
- 标题采用 `文件名 · 第N/M部分` 稳定命名，**重复导入同一文件即原地更新**，不会产生副本。

**导出**（`knowledge.export` / `haoyue knowledge export`）把整个作用域渲染成一份 Markdown 文档（含范围、时间、条目数与每条的标签），用于备份或分享。Desktop 通过系统保存对话框落盘，CLI 可写入文件或直接输出到标准输出。

## 同义词表

`~/.haoyue/knowledge/synonyms.txt` 是留给你的检索调优入口，**修改即热加载，无需重启**：

```text
# 注释行
部署 = 发布, 上线        # 也支持冒号分隔
密钥: key
```

- 每行一个同义词组，查询命中其中任意一词都会扩展到全组；
- 内置一张高频技术同义词表（部署/发布、构建/编译、账号/帐号、报错/异常……），用户条目合并其上：同名键扩展内置条目，新键直接追加；
- 归一化规则与查询一致，文件里写全角字符没有问题。

## 在 Desktop 中使用

打开「知识库」页：

- **标签栏**：展示当前作用域的标签与计数，点击精确筛选；
- **导出**：工具栏下载按钮，选择保存位置即得到 Markdown 备份；
- **同义词表**：滑杆按钮打开编辑器，显示文件路径、支持定位所在文件夹，有未保存修改时关闭会先确认；
- **快捷键**：`Ctrl+N` 新增知识、`Ctrl+F` 聚焦搜索、`Esc` 逐层退出（同义词面板 → 编辑表单 → 关闭页面）；
- 编辑已有条目时按 id 原地保存，**改名也不会分叉出新条目**。

## CLI

```bash
haoyue knowledge list
haoyue knowledge search 构建命令
haoyue knowledge add "标题" "内容" --tags a,b
haoyue knowledge show 1
haoyue knowledge delete 1

haoyue knowledge import notes.md 会议纪要.docx
haoyue knowledge export backup.md     # 省略文件名则输出到标准输出
```

`import` 逐文件汇报结果，个别文件失败不影响其余文件，有失败时以非零码退出。

## 相关 IPC 方法

| 方法 | 说明 |
| --- | --- |
| `knowledge.list` | 列出条目，支持 `tag` 精确筛选 |
| `knowledge.search` | 语义检索，支持 `tag` 组合过滤 |
| `knowledge.save` | 新增或更新：同题 upsert，或按 `id` 原地更新（含改名） |
| `knowledge.delete` | 按 id 删除 |
| `knowledge.import` | 从文件批量导入（自动分块、同文件 upsert） |
| `knowledge.tags` | 标签聚合与计数 |
| `knowledge.export` | 渲染整个作用域的 Markdown |
| `knowledge.synonyms.get` / `knowledge.synonyms.save` | 读取或保存同义词表 |

::: tip
Agent 侧的 `knowledge_save` / `knowledge_search` / `knowledge_forget` 工具与这里的 IPC 共用同一存储——你在 Desktop 里改动的条目，Agent 下次检索即可看到；反之亦然。
:::
