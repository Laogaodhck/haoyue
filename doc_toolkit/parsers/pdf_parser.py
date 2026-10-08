"""PDF 解析器：基于 pypdf 的文本层提取。

限制说明：仅提取文本层，扫描件（纯图片 PDF）无法提取，会抛
EmptyDocumentError；加密 PDF 抛 CorruptedDocumentError。
标题/列表结构信息在 PDF 中不可靠，统一降级为段落（每页内按换行合并）。
"""

from __future__ import annotations

from pathlib import Path

from ..core.base import BaseParser
from ..core.errors import CorruptedDocumentError, EmptyDocumentError
from ..core.models import BlockType, Document, DocumentBlock


class PdfParser(BaseParser):
    name = "pdf"
    extensions = frozenset({"pdf"})

    def read(self, path: Path) -> Document:
        # 延迟导入：未安装 pypdf 时给出可操作的错误提示。
        try:
            from pypdf import PdfReader
        except ImportError as exc:  # pragma: no cover
            raise CorruptedDocumentError(
                "PDF 解析依赖 pypdf 未安装，请执行：pip install pypdf", detail=str(exc)
            ) from exc

        try:
            reader = PdfReader(str(path))
        except Exception as exc:  # pypdf 对损坏文件抛多种异常，统一兜底
            raise CorruptedDocumentError(
                f"{path.name} 不是有效的 PDF 文件或已损坏。", detail=str(exc)
            ) from exc

        if reader.is_encrypted:
            # 空密码尝试解密（部分 PDF 仅加权限锁），失败则视为受保护文档。
            try:
                reader.decrypt("")
            except Exception as exc:
                raise CorruptedDocumentError(
                    f"{path.name} 已加密，请先解密后再转换。", detail=str(exc)
                ) from exc

        blocks: list[DocumentBlock] = []
        for index, page in enumerate(reader.pages, start=1):
            try:
                page_text = page.extract_text() or ""
            except Exception as exc:
                raise CorruptedDocumentError(
                    f"提取 {path.name} 第 {index} 页文本失败，页面内容可能已损坏。",
                    detail=str(exc),
                ) from exc
            # 按空行把页内文本切成段落，避免整页糊成一块。
            for para in page_text.replace("\r\n", "\n").split("\n\n"):
                cleaned = para.strip()
                if cleaned:
                    blocks.append(DocumentBlock(BlockType.PARAGRAPH, cleaned))

        if not blocks:
            raise EmptyDocumentError(
                f"{path.name} 未提取到文本内容：可能是扫描件（纯图片 PDF）或空文档，"
                "请先进行 OCR。"
            )
        return Document(blocks=blocks, source_path=str(path), source_format=self.name)
