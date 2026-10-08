# doc_toolkit — 模块化文档处理工具包

多格式文档的内容提取、格式转换与 OCR 识别。纯 Python 实现，离线可用，无外部服务依赖。

## 功能总览

| 能力 | 支持 |
|---|---|
| 内容提取（解析） | TXT（含 GBK/UTF-16 自动探测）、Markdown、PDF、DOCX、图片（PNG/JPG/BMP/WEBP/TIFF，走 OCR） |
| 格式输出（渲染） | TXT、Markdown、DOCX、PDF |
| 格式互转 | 任意「可解析格式」→ 任意「可输出格式」，保留标题/段落/列表/引用/代码块结构 |
| OCR 识别 | RapidOCR（ONNX Runtime CPU 推理，模型内置、完全离线），中文效果佳 |
| 扫描件 PDF | `ocr_fallback=True` 时逐页渲染识别，无文本层的 PDF 也能提取 |

## 安装依赖

```bash
pip install pypdf python-docx fpdf2          # 基础格式支持
pip install rapidocr-onnxruntime pillow pypdfium2  # OCR / 图片支持（可选）
```

## 快速上手

```python
from doc_toolkit import default_toolkit as kit

# 1) 一步提取纯文本
text = kit.extract_text("报告.docx")

# 2) 解析为结构化 IR（块序列，可自行消费或 JSON 序列化）
doc = kit.parse("notes.md")
for block in doc.blocks:
    print(block.type, block.level, block.text)
print(doc.to_json())

# 3) 格式转换（默认输出到源目录、同名换后缀）
kit.convert("notes.md", "docx")                     # → notes.docx
kit.convert("报告.docx", "pdf", out_path="报告2.pdf")
kit.convert("报告.docx", "pdf", overwrite=False)    # 输出已存在时报错

# 4) OCR 识别图片文字
kit.recognize("截图.png")                            # 纯文本
data = kit.recognize("截图.png", structured=True)    # 结构化：每行文本/置信度/坐标/耗时

# 5) 扫描件 PDF（无文本层）走 OCR 回退
doc = kit.parse("扫描件.pdf", ocr_fallback=True)
kit.convert("扫描件.pdf", "docx")  # 注意：convert 内部不自动回退，请先 parse 或直接用 extract_text(ocr_fallback=True)
```

## 架构与模块职责

```
doc_toolkit/
├── core/                  # 核心层：所有格式共用的基础设施
│   ├── models.py          #   文档中间模型（IR）：Document + DocumentBlock
│   │                      #   （标题/段落/列表/引用/代码块/分隔线六种块类型）
│   ├── errors.py          #   错误体系：全部携带中文可读信息
│   ├── base.py            #   扩展接口：BaseParser / BaseRenderer 抽象基类
│   └── registry.py        #   注册表：格式名 + 扩展名双索引，运行时可注册/覆盖
├── parsers/               # 解析层：文件 → IR
│   ├── txt_parser.py      #   多编码探测（UTF-8 → GB18030 → UTF-16）
│   ├── markdown_parser.py #   零依赖行式解析（标题/嵌套列表/引用/围栏代码块/Setext）
│   ├── pdf_parser.py      #   pypdf 文本层提取，识别加密/损坏/纯图片 PDF
│   ├── docx_parser.py     #   python-docx，Heading/List 样式完整映射
│   └── image_parser.py    #   PIL 校验 + OCR，图片作为一等文档参与全部流程
├── renderers/             # 渲染层：IR → 目标格式
│   ├── txt_renderer.py
│   ├── markdown_renderer.py
│   ├── docx_renderer.py   #   块类型映射 Heading/List Bullet/List Number 样式
│   └── pdf_renderer.py    #   fpdf2 + 自动查找中文字体（msyh → simhei → simsun）
├── ocr/                   # OCR 层
│   ├── base.py            #   OcrResult/OcrLine 结果模型 + BaseOcrEngine 扩展点
│   ├── rapid_engine.py    #   RapidOCR 引擎（进程级惰性单例，模型仅加载一次）
│   └── pdf_fallback.py    #   扫描件 PDF：pypdfium2 逐页渲染 → OCR
└── toolkit.py             # 门面：DocToolkit 统一调用入口
```

**数据流**：`源文件 → BaseParser → Document(IR) → BaseRenderer → 目标文件`。
解析器之间、渲染器之间互不感知，均只对接 IR——新增格式零侵入。

## 扩展新格式

```python
from pathlib import Path
from doc_toolkit import default_toolkit as kit
from doc_toolkit.core.base import BaseParser, BaseRenderer
from doc_toolkit.core.models import Document, BlockType, DocumentBlock


class EpubParser(BaseParser):
    name = "epub"
    extensions = frozenset({"epub"})

    def read(self, path: Path) -> Document:
        # ① 读文件（损坏时抛 doc_toolkit.core.errors.CorruptedDocumentError）
        # ② 解析内容为 DocumentBlock 序列
        return Document(blocks=[DocumentBlock(BlockType.PARAGRAPH, "...")],
                        source_path=str(path), source_format=self.name)


class EpubRenderer(BaseRenderer):
    name = "epub"
    default_suffix = ".epub"

    def write(self, document, out_path: Path) -> Path:
        # 渲染 IR 并落盘，返回最终路径
        return out_path


kit.register_parser(EpubParser())
kit.register_renderer(EpubRenderer())
kit.convert("book.epub", "markdown")   # 新格式立刻接入全部互转链路
```

## 错误处理约定

| 异常 | 触发场景 |
|---|---|
| `DocumentError` | 文件不存在、输出路径冲突等通用错误 |
| `UnsupportedFormatError` | 扩展名/目标格式未注册 |
| `CorruptedDocumentError` | 文件损坏、加密、编码无法识别、OCR 失败 |
| `EmptyDocumentError` | 可打开但无内容（空白图片、纯图片 PDF、空白文档） |
| `doc_toolkit.ocr.OcrError` | OCR 引擎级致命错误（未安装依赖、推理崩溃） |

## 边界情况与已知限制

- **扫描件 PDF**：仅提取文本层；纯图片 PDF 需 `ocr_fallback=True`（逐页 OCR，速度明显变慢，可用页数限制参数控制）
- **PDF 结构**：PDF 无可靠的结构语义，标题/列表统一降级为段落
- **模糊图片**：不硬失败——平均置信度 < 55% 时在结果中标记 `low_confidence` 并附中文警告，由调用方决策
- **小字号 OCR 精度**：可用 `ocr/pdf_fallback.py` 的 `_RENDER_SCALE`（默认 2.5 ≈ 180dpi）换精度
- **旧版 .doc**：二进制格式不支持，报错提示先另存为 .docx
- **PDF 中文字体**：依赖系统字体（微软雅黑/黑体/宋体），全缺失时报错

## 验证

```bash
python doc_toolkit/verify_e2e.py    # 23 项：4 格式提取、9 组互转、结构回读、错误提示
python doc_toolkit/verify_ocr.py    # 9 项：OCR 纯文本/结构化、边界情况、扫描件回退
```

依赖版本基线：Python 3.13 · pypdf 6.x · python-docx 1.2 · fpdf2 2.8 · rapidocr-onnxruntime 1.2 · pillow 12 · pypdfium2 5。
