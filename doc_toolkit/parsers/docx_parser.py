"""DOCX 解析器：基于 python-docx，完整保留标题/段落/列表结构。"""

from __future__ import annotations

from pathlib import Path

from ..core.base import BaseParser
from ..core.errors import CorruptedDocumentError
from ..core.models import BlockType, Document, DocumentBlock


class DocxParser(BaseParser):
    name = "docx"
    extensions = frozenset({"docx"})

    def read(self, path: Path) -> Document:
        try:
            from docx import Document as DocxDocument
            from docx.document import Document as _Doc  # noqa: F401
        except ImportError as exc:  # pragma: no cover
            raise CorruptedDocumentError(
                "DOCX 解析依赖 python-docx 未安装，请执行：pip install python-docx",
                detail=str(exc),
            ) from exc

        try:
            docx = DocxDocument(str(path))
        except Exception as exc:
            raise CorruptedDocumentError(
                f"{path.name} 不是有效的 DOCX 文件或已损坏"
                "（旧版 .doc 请先另存为 .docx）。",
                detail=str(exc),
            ) from exc

        blocks: list[DocumentBlock] = []
        for para in docx.paragraphs:
            text = para.text.strip()
            if not text:
                continue
            style = (para.style.name or "").lower()
            if style.startswith("heading"):
                # "Heading 2" / "标题 2" → 级别 2；超范围钳制到 1-6。
                level = _heading_level(style, default=1)
                blocks.append(DocumentBlock(BlockType.HEADING, text, level=level))
            elif style.startswith("list"):
                blocks.append(DocumentBlock(BlockType.LIST_ITEM, text))
            else:
                blocks.append(DocumentBlock(BlockType.PARAGRAPH, text))

        if not blocks:
            raise CorruptedDocumentError(
                f"{path.name} 中没有可提取的文本内容（可能为空白文档）。"
            )
        return Document(blocks=blocks, source_path=str(path), source_format=self.name)


def _heading_level(style: str, default: int = 1) -> int:
    """从样式名中解析标题级别，解析失败回退 default。"""
    for token in style.split():
        if token.isdigit():
            return max(1, min(6, int(token)))
    return default
