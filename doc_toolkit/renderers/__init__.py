"""渲染器模块：每个文件实现一种目标格式的 BaseRenderer 子类。"""

from .docx_renderer import DocxRenderer
from .markdown_renderer import MarkdownRenderer
from .pdf_renderer import PdfRenderer
from .pptx_renderer import PptxRenderer
from .txt_renderer import TxtRenderer
from .xlsx_renderer import XlsxRenderer

__all__ = [
    "DocxRenderer",
    "MarkdownRenderer",
    "PdfRenderer",
    "PptxRenderer",
    "TxtRenderer",
    "XlsxRenderer",
]
