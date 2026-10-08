"""Markdown 渲染器：把 IR 块还原为 Markdown 语法。"""

from __future__ import annotations

from pathlib import Path

from ..core.base import BaseRenderer
from ..core.models import BlockType, Document, DocumentBlock


class MarkdownRenderer(BaseRenderer):
    name = "markdown"
    extensions = frozenset({"md"})
    default_suffix = ".md"

    def write(self, document: Document, out_path: Path) -> Path:
        out_path.write_text(self.render_string(document) + "\n", encoding="utf-8")
        return out_path

    def render_string(self, document: Document) -> str:
        parts: list[str] = []
        for block in document.blocks:
            match block.type:
                case BlockType.HEADING:
                    parts.append(f"{'#' * block.level} {block.text}")
                case BlockType.PARAGRAPH:
                    parts.append(block.text)
                case BlockType.LIST_ITEM:
                    indent = "  " * block.indent
                    marker = f"{indent}- " if not block.ordered else f"{indent}1. "
                    parts.append(f"{marker}{block.text}")
                case BlockType.QUOTE:
                    parts.append(f"> {block.text}")
                case BlockType.CODE:
                    parts.append(f"```\n{block.text}\n```")
                case BlockType.DIVIDER:
                    parts.append("---")
        return "\n\n".join(parts)
