"""Markdown 解析器：零依赖的行式解析，覆盖标题/列表/引用/代码块/分隔线。

设计取舍：不追求完整 CommonMark 语义，只提取「结构块」，行内标记
（**粗体**、`代码` 等）原样保留在 text 中，由渲染器决定是否还原。
"""

from __future__ import annotations

import re
from pathlib import Path

from ..core.base import BaseParser
from ..core.errors import CorruptedDocumentError
from ..core.models import BlockType, Document, DocumentBlock

_HEADING_RE = re.compile(r"^(#{1,6})\s+(.*?)\s*#*\s*$")
_UNORDERED_RE = re.compile(r"^(\s*)[-*+]\s+(.*)$")
_ORDERED_RE = re.compile(r"^(\s*)(\d{1,3})[.)]\s+(.*)$")
_SETEXT_EQ_RE = re.compile(r"^=+\s*$")
_DIVIDER_RE = re.compile(r"^\s{0,3}(?:-{3,}|\*{3,}|_{3,})\s*$")
_SEPARATOR_CELL_RE = re.compile(r"^:?-{2,}:?$")


def _split_cells(line: str) -> list[str]:
    """按 | 切分表格行，忽略首尾管道与单元格空白。"""
    trimmed = line.strip().strip("|")
    return [cell.strip() for cell in trimmed.split("|")]


def _is_table_row(line: str) -> bool:
    """表格数据行：至少含 2 个管道符（确保是真正的多列表格行）。"""
    return line.strip().count("|") >= 2


def _is_table_header(line: str) -> bool:
    """表头行：管道表格行且不含分隔单元格（---）。"""
    s = line.strip()
    return s.count("|") >= 2 and not all(
        _SEPARATOR_CELL_RE.match(cell) for cell in _split_cells(s)
    )


def _is_separator_line(line: str) -> bool:
    """GFM 表格分隔行：| --- | --- | 或 :---: 对齐标记。"""
    s = line.strip()
    if "|" not in s:
        return False
    cells = _split_cells(s)
    return bool(cells) and all(_SEPARATOR_CELL_RE.match(cell) for cell in cells)


class MarkdownParser(BaseParser):
    name = "markdown"
    extensions = frozenset({"md", "markdown", "mkd"})

    def read(self, path: Path) -> Document:
        try:
            raw = path.read_bytes()
        except OSError as exc:
            raise CorruptedDocumentError(f"无法读取文件：{path.name}", detail=str(exc)) from exc
        try:
            text = raw.decode("utf-8-sig")
        except UnicodeDecodeError as exc:
            raise CorruptedDocumentError(
                f"{path.name} 不是有效的 UTF-8 Markdown 文件。", detail=str(exc)
            ) from exc
        return Document(blocks=self._parse_lines(text.replace("\r\n", "\n").split("\n")),
                        source_path=str(path), source_format=self.name)

    def _parse_lines(self, lines: list[str]) -> list[DocumentBlock]:
        blocks: list[DocumentBlock] = []
        para: list[str] = []
        in_code = False
        code_lines: list[str] = []
        code_lang = ""

        def flush_para() -> None:
            if para:
                blocks.append(DocumentBlock(BlockType.PARAGRAPH, "\n".join(para).strip()))
                para.clear()

        i = 0
        n = len(lines)
        while i < n:
            line = lines[i]
            # ---- 围栏代码块状态机 ----
            fence = re.match(r"^\s{0,3}(```+|~~~+)\s*(\S*)\s*$", line)
            if fence and not in_code:
                flush_para()
                in_code, code_lang = True, fence.group(2)
                i += 1
                continue
            if in_code:
                if fence:  # 闭合围栏
                    blocks.append(DocumentBlock(BlockType.CODE, "\n".join(code_lines)))
                    code_lines, in_code = [], False
                else:
                    code_lines.append(line)
                i += 1
                continue

            if not line.strip():
                flush_para()
                i += 1
                continue
            if (match := _HEADING_RE.match(line)):
                flush_para()
                blocks.append(DocumentBlock(
                    BlockType.HEADING, match.group(2).strip(), level=len(match.group(1))))
                i += 1
                continue
            # Setext 标题（上一行为文字 + 当前行 ===/---）：--- 优先按分隔线处理。
            if _SETEXT_EQ_RE.match(line) and para:
                blocks.append(DocumentBlock(BlockType.HEADING, para.pop(), level=1))
                i += 1
                continue
            if _DIVIDER_RE.match(line) and not para:
                blocks.append(DocumentBlock(BlockType.DIVIDER))
                i += 1
                continue
            if (match := _UNORDERED_RE.match(line)):
                flush_para()
                indent = len(match.group(1)) // 2  # 每 2 空格一层嵌套
                blocks.append(DocumentBlock(
                    BlockType.LIST_ITEM, match.group(2).strip(), ordered=False, indent=indent))
                i += 1
                continue
            if (match := _ORDERED_RE.match(line)):
                flush_para()
                indent = len(match.group(1)) // 3  # 有序列表通常带 ". " 宽度
                blocks.append(DocumentBlock(
                    BlockType.LIST_ITEM, match.group(3).strip(), ordered=True, indent=indent))
                i += 1
                continue
            if line.lstrip().startswith(">"):  # 引用块：合并 > 前缀
                flush_para()
                blocks.append(DocumentBlock(BlockType.QUOTE, line.lstrip().lstrip(">").strip()))
                i += 1
                continue
            # ---- GFM 表格：表头行 + 紧随的分隔行触发，连续表格行归并为单个 TABLE 块 ----
            if _is_table_header(line) and i + 1 < n and _is_separator_line(lines[i + 1]):
                flush_para()
                header = _split_cells(line)
                rows: list[list[str]] = []
                j = i + 2
                while j < n and lines[j].strip() and _is_table_row(lines[j]):
                    rows.append(_split_cells(lines[j]))
                    j += 1
                blocks.append(DocumentBlock(BlockType.TABLE, cells=[header, *rows]))
                i = j
                continue
            para.append(line.rstrip())
            i += 1

        if in_code:  # 围栏未闭合：按原样保留剩余内容，不算损坏
            blocks.append(DocumentBlock(BlockType.CODE, "\n".join(code_lines)))
        flush_para()
        return blocks
