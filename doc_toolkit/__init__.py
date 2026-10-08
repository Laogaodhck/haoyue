"""doc_toolkit — 模块化文档处理工具包。

多格式文档的内容提取与格式转换，当前支持：
- 解析（提取）：TXT / Markdown / PDF / DOCX / 图片（PNG/JPG/BMP/WEBP/TIFF，走 OCR）
- 渲染（输出）：TXT / Markdown / DOCX / PDF
- OCR：recognize() 直接识别图片文字；parse(ocr_fallback=True) 支持扫描件 PDF

架构分层：
- core/       文档中间模型（IR）、错误体系、解析器与渲染器的抽象扩展接口和注册表
- parsers/    各格式的解析器，将文件解析为统一 IR
- renderers/  各格式的渲染器，将 IR 输出为目标格式
- ocr/        OCR 引擎封装（RapidOCR）与扫描件 PDF 回退
- toolkit.py  统一调用入口 DocToolkit

扩展新格式：继承 core.base.BaseParser 或 BaseRenderer，实现抽象方法后调用
DocToolkit.register_parser() / register_renderer() 即可，无需改动既有代码。
"""

from .core.errors import (
    CorruptedDocumentError,
    DocumentError,
    DocumentParseError,
    EmptyDocumentError,
    UnsupportedFormatError,
)
from .core.models import BlockType, Document, DocumentBlock
from .ocr import OcrError, OcrLine, OcrResult
from .toolkit import DocToolkit, default_toolkit

__all__ = [
    "BlockType",
    "CorruptedDocumentError",
    "Document",
    "DocumentBlock",
    "DocumentError",
    "DocumentParseError",
    "DocToolkit",
    "EmptyDocumentError",
    "OcrError",
    "OcrLine",
    "OcrResult",
    "UnsupportedFormatError",
    "default_toolkit",
]

__version__ = "1.0.0"
