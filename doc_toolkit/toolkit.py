"""DocToolkit — 统一调用入口。

典型用法::

    from doc_toolkit import default_toolkit

    kit = default_toolkit

    # 1) 提取纯文本
    text = kit.extract_text("报告.docx")

    # 2) 解析为结构化 IR（blocks 可自行消费）
    doc = kit.parse("notes.md")
    for block in doc.blocks:
        print(block.type, block.text)

    # 3) 格式转换（默认输出到同目录、同名不同后缀）
    out = kit.convert("notes.md", target_format="docx")   # → notes.docx
    out = kit.convert("报告.docx", target_format="pdf")   # → 报告.pdf

    # 4) 扩展新格式：实现 BaseParser/BaseRenderer 后注册
    kit.register_parser(MyParser())
    kit.register_renderer(MyRenderer())
"""

from __future__ import annotations

from pathlib import Path

from .core.base import BaseParser, BaseRenderer
from .core.errors import (
    CorruptedDocumentError,
    DocumentError,
    DocumentParseError,
    EmptyDocumentError,
    UnsupportedFormatError,
)
from .core.models import Document
from .core.registry import FormatRegistry
from .ocr import OcrError, OcrResult, get_default_engine
from .ocr.pdf_fallback import ocr_pdf_document
from .parsers import DocxParser, ImageParser, MarkdownParser, PdfParser, TxtParser
from .renderers import DocxRenderer, MarkdownRenderer, PdfRenderer, TxtRenderer

__all__ = ["DocToolkit", "default_toolkit"]


class DocToolkit:
    """文档处理门面：组合解析器/渲染器注册表，对外暴露核心操作。

    实例化后自带内置格式（含图片 OCR 解析）；每个实例持有独立注册表，
    注册扩展互不影响。OCR 引擎为进程级惰性单例，首次调用图片相关
    功能时才加载模型。
    """

    def __init__(self, register_builtin: bool = True) -> None:
        self.registry = FormatRegistry()
        if register_builtin:
            for parser in (
                TxtParser(), MarkdownParser(), PdfParser(), DocxParser(), ImageParser(),
            ):
                self.registry.register_parser(parser)
            for renderer in (TxtRenderer(), MarkdownRenderer(), DocxRenderer(), PdfRenderer()):
                self.registry.register_renderer(renderer)

    # ---- 扩展接口 --------------------------------------------------------
    def register_parser(self, parser: BaseParser) -> None:
        """注册自定义解析器（覆盖同名格式）。"""
        self.registry.register_parser(parser)

    def register_renderer(self, renderer: BaseRenderer) -> None:
        """注册自定义渲染器（覆盖同名格式）。"""
        self.registry.register_renderer(renderer)

    @property
    def supported_formats(self) -> dict[str, list[str]]:
        """当前能力清单：{"parse": [...], "render": [...]}。"""
        return {
            "parse": self.registry.supported_parse_formats,
            "render": self.registry.supported_render_formats,
        }

    # ---- 核心操作 --------------------------------------------------------
    def parse(self, path: str | Path, *, ocr_fallback: bool = False) -> Document:
        """把任意受支持格式的文件解析为统一 IR。

        Args:
            path: 源文件路径。
            ocr_fallback: PDF 提取不到文本层（扫描件）时，是否回退为逐页
                OCR 识别。开启后扫描件也可正常解析与转换，但速度明显变慢。
        """
        source = Path(path)
        if not source.is_file():
            raise DocumentError(f"文件不存在：{source}")
        parser = self.registry.parser_for_path(source)
        try:
            document = parser.read(source)
        except EmptyDocumentError:
            # 目前仅 PDF 存在「可打开但无文本层」的情况。
            if not (ocr_fallback and isinstance(parser, PdfParser)):
                raise
            try:
                document = ocr_pdf_document(source)
            except RuntimeError as exc:
                raise CorruptedDocumentError(
                    f"{source.name} 既无文本层也无法进行 OCR 识别。", detail=str(exc)
                ) from exc
        except OcrError as exc:
            raise CorruptedDocumentError(str(exc)) from exc
        document.source_path = str(source)
        document.source_format = parser.name
        return document

    def extract_text(self, path: str | Path, separator: str = "\n\n",
                     *, ocr_fallback: bool = False) -> str:
        """一步提取纯文本，不关心中间结构。"""
        document = self.parse(path, ocr_fallback=ocr_fallback)
        if document.is_empty:
            raise EmptyDocumentError(f"{Path(path).name} 未提取到文本内容。")
        return document.plain_text(separator)

    def recognize(self, path: str | Path, *, structured: bool = False) -> str | dict:
        """OCR 识别图片中的文字。

        Args:
            path: 图片路径（PNG/JPG/BMP/WEBP/TIFF）。
            structured: False 返回纯文本；True 返回结构化 dict
                （含每行文本、置信度、坐标、警告、耗时）。

        Raises:
            DocumentError: 文件不存在。
            CorruptedDocumentError: 图片损坏或引擎识别失败。
            EmptyDocumentError: 图片中没有任何文字。
        """
        source = Path(path)
        if not source.is_file():
            raise DocumentError(f"文件不存在：{source}")
        try:
            result: OcrResult = get_default_engine().recognize_image(source)
        except OcrError as exc:
            raise CorruptedDocumentError(str(exc)) from exc
        if result.is_empty:
            raise EmptyDocumentError(
                f"{source.name} 中未识别到文字内容（可能为空白或纯图形图片）。"
            )
        return result.structured() if structured else result.plain_text

    def convert(
        self,
        path: str | Path,
        target_format: str,
        out_path: str | Path | None = None,
        *,
        overwrite: bool = True,
    ) -> Path:
        """格式转换：源文件 → IR → 目标格式。

        Args:
            path: 源文件路径。
            target_format: 目标格式名（如 "docx" / "pdf" / "markdown" / "txt"）。
            out_path: 输出路径；缺省时输出到源目录、同名换后缀。
            overwrite: 输出文件已存在时是否覆盖，默认覆盖。

        Returns:
            实际写入的输出路径。
        """
        source = Path(path)
        renderer = self.registry.renderer_by_name(target_format)

        if out_path is None:
            out_path = source.with_suffix(renderer.default_suffix)
        out_path = Path(out_path)
        if not overwrite and out_path.exists():
            raise DocumentError(f"输出文件已存在：{out_path}（设置 overwrite=True 覆盖）")
        out_path.parent.mkdir(parents=True, exist_ok=True)

        document = self.parse(source)
        if document.is_empty:
            raise EmptyDocumentError(
                f"{source.name} 没有可转换的文本内容，已跳过。"
            )
        return renderer.write(document, out_path)


#: 模块级默认实例：多数场景直接 `from doc_toolkit import default_toolkit` 使用。
default_toolkit = DocToolkit()
