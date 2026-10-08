"""PDF 渲染器：基于 fpdf2，注册系统中文字体后按块类型排版。

字体查找顺序：微软雅黑 → 黑体 → 宋体（Windows 自带），全缺失时报错提示。
"""

from __future__ import annotations

import sys
from pathlib import Path

from ..core.base import BaseRenderer
from ..core.errors import DocumentError
from ..core.models import BlockType, Document, DocumentBlock

#: 候选中文字体（相对 %WINDIR%/Fonts），fpdf2 的 core 字体不含 CJK 字形。
_CJK_FONT_CANDIDATES = ("msyh.ttc", "msyh.ttf", "simhei.ttf", "simsun.ttc", "simfang.ttf")

# 标题字号（pt），下标 0 对应 level 1。
_HEADING_SIZES = (20, 17, 15, 13, 12, 11)


class PdfRenderer(BaseRenderer):
    name = "pdf"
    extensions = frozenset({"pdf"})
    default_suffix = ".pdf"

    def write(self, document: Document, out_path: Path) -> Path:
        try:
            from fpdf import FPDF
        except ImportError as exc:  # pragma: no cover
            raise DocumentError(
                "PDF 输出依赖 fpdf2 未安装，请执行：pip install fpdf2", detail=str(exc)
            ) from exc
        font_path = self._find_cjk_font()
        if font_path is None:
            raise DocumentError(
                "未找到可用的中文字体（已尝试：msyh/simhei/simsun），"
                "无法生成包含中文的 PDF。"
            )

        pdf = FPDF()
        pdf.set_auto_page_break(auto=True, margin=18)
        pdf.add_page()
        pdf.add_font("cjk", "", str(font_path))
        for block in document.blocks:
            self._render_block(pdf, block)
        try:
            pdf.output(str(out_path))
        except Exception as exc:
            raise DocumentError(
                f"写入 PDF 失败：{out_path.name}（磁盘空间或权限问题？）", detail=str(exc)
            ) from exc
        return out_path

    # ---- 内部实现 -------------------------------------------------------
    @staticmethod
    def _find_cjk_font() -> Path | None:
        # Path(...).anchor 是 str（如 "C:\\"），需拼回 Path 再 join。
        exe_anchor = Path(sys.executable).anchor
        candidates = [Path(exe_anchor) / "Windows" / "Fonts"]
        for base in candidates:
            for name in _CJK_FONT_CANDIDATES:
                path = base / name
                if path.is_file():
                    return path
        return None

    def _render_block(self, pdf, block: DocumentBlock) -> None:
        match block.type:
            case BlockType.HEADING:
                level = max(1, min(6, block.level))
                pdf.set_font("cjk", size=_HEADING_SIZES[level - 1])
                pdf.multi_cell(0, 8, block.text, new_x="LMARGIN", new_y="NEXT")
                pdf.ln(2)
            case BlockType.LIST_ITEM:
                pdf.set_font("cjk", size=11)
                indent = 8 + block.indent * 6
                bullet = f"{block.indent + 1}. " if block.ordered else "• "
                pdf.set_x(pdf.l_margin + indent)
                pdf.multi_cell(0, 6, bullet + block.text, new_x="LMARGIN", new_y="NEXT")
            case BlockType.QUOTE:
                pdf.set_font("cjk", size=11)
                pdf.set_x(pdf.l_margin + 6)
                pdf.multi_cell(0, 6, "▎" + block.text, new_x="LMARGIN", new_y="NEXT")
            case BlockType.CODE:
                pdf.set_font("cjk", size=9.5)
                pdf.multi_cell(0, 5, block.text, new_x="LMARGIN", new_y="NEXT")
                pdf.ln(1)
            case BlockType.DIVIDER:
                pdf.set_font("cjk", size=10)
                pdf.cell(0, 6, "────────", align="C", new_x="LMARGIN", new_y="NEXT")
            case _:  # PARAGRAPH 及未知类型统一按段落排版
                pdf.set_font("cjk", size=11)
                # multi_cell 对 \n 自动换行，段后加 2mm 间距
                pdf.multi_cell(0, 6, block.text, new_x="LMARGIN", new_y="NEXT")
                pdf.ln(2)
