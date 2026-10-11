"""XLSX 渲染器：基于 openpyxl，把块序列映射为工作表行。

映射规则（与 IR 契约一致，未知类型降级为单列行）：
- HEADING        → 分区标题行（加粗，占整行）
- PARAGRAPH / LIST_ITEM
  - 行内含 ≥2 个「|」→ 识别为表格行：按 | 切分单元格；
    全为 -/: 的分隔行（|---|---|）跳过；连续表格行之间不空行
  - 否则每行占一行（LIST_ITEM 带序号/圆点与缩进前缀）
- QUOTE          → 注释行（前缀「# 」）
- CODE           → 每行一格，等宽字体
- DIVIDER        → 空行

该映射让 Markdown 表格（模型最常见的结构化数据形态）直接落为
真实的电子表格行/列，而不仅是文本堆叠。
"""

from __future__ import annotations

import re
from pathlib import Path

from ..core.base import BaseRenderer
from ..core.errors import DocumentError
from ..core.models import BlockType, Document

_SPLIT_RE = re.compile(r"\s*\|\s*")
_SEPARATOR_CELLS_RE = re.compile(r"^:?-{2,}:?$")


class XlsxRenderer(BaseRenderer):
    name = "xlsx"
    extensions = frozenset({"xlsx"})
    default_suffix = ".xlsx"

    def write(self, document: Document, out_path: Path) -> Path:
        try:
            from openpyxl import Workbook
            from openpyxl.styles import Alignment, Font
        except ImportError as exc:  # pragma: no cover
            raise DocumentError(
                "XLSX 输出依赖 openpyxl 未安装，请执行：pip install openpyxl",
                detail=str(exc),
            ) from exc

        wb = Workbook()
        sheet = wb.active
        sheet.title = "Sheet1"

        bold = Font(bold=True)
        mono = Font(name="Consolas")
        wrap = Alignment(wrap_text=True, vertical="top")

        row_index = 1

        def next_row() -> int:
            nonlocal row_index
            row = row_index
            row_index += 1
            return row

        def put(row: int, values: list[str], *, font: Font | None = None) -> None:
            for col, value in enumerate(values, start=1):
                cell = sheet.cell(row=row, column=col, value=value)
                cell.alignment = wrap
                if font is not None:
                    cell.font = font

        for block in document.blocks:
            match block.type:
                case BlockType.HEADING:
                    put(next_row(), [block.text], font=bold)
                case BlockType.QUOTE:
                    put(next_row(), [f"# {block.text}"])
                case BlockType.CODE:
                    for line in block.text.split("\n"):
                        put(next_row(), [line], font=mono)
                case BlockType.DIVIDER:
                    next_row()
                case _:  # PARAGRAPH / LIST_ITEM / 未知类型
                    prefix = ""
                    if block.type is BlockType.LIST_ITEM:
                        prefix = f"{block.indent * '  '}"
                        if block.ordered:
                            prefix = ""
                    for line in block.text.split("\n"):
                        cells = _split_table_row(line)
                        if cells is None:
                            put(next_row(), [prefix + line])
                        elif cells:  # 分隔行（|---|）返回空列表，直接跳过不占行
                            put(next_row(), cells)

        wb.save(str(out_path))
        return out_path


def _split_table_row(line: str) -> list[str] | None:
    """识别 Markdown 表格行：至少 2 个 |，切出非空单元格序列。

    返回 None 表示不是表格行（按普通文本处理）；分隔行（|---|---|）返回 []。
    """
    stripped = line.strip()
    if stripped.count("|") < 2:
        return None
    trimmed = stripped.strip("|")
    cells = [cell.strip() for cell in _SPLIT_RE.split(trimmed)]
    if cells and all(_SEPARATOR_CELLS_RE.match(cell) for cell in cells):
        return []
    return cells if any(cells) else None
