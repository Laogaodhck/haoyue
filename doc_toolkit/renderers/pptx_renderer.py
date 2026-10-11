"""PPTX 渲染器：基于 python-pptx，把六种块类型映射为幻灯片结构。

映射规则（与 IR 契约一致，未知类型降级为正文段落）：
- HEADING level 1  → 新幻灯片的标题（版式 1：标题+内容）
- HEADING level 2+ → 正文中的加粗行
- PARAGRAPH        → 正文段落（多行拆为多段）
- LIST_ITEM        → 项目符号行；indent 决定缩进层级
- QUOTE            → 斜体正文，前缀「❝ 」
- CODE             → Consolas 等宽段落，逐行软换行
- DIVIDER          → 空行间隔（幻灯片场景无水平线语义）
"""

from __future__ import annotations

from pathlib import Path
from typing import Any

from ..core.base import BaseRenderer
from ..core.errors import DocumentError
from ..core.models import BlockType, Document


class PptxRenderer(BaseRenderer):
    name = "pptx"
    extensions = frozenset({"pptx"})
    default_suffix = ".pptx"

    def write(self, document: Document, out_path: Path) -> Path:
        try:
            from pptx import Presentation
            from pptx.util import Pt
        except ImportError as exc:  # pragma: no cover
            raise DocumentError(
                "PPTX 输出依赖 python-pptx 未安装，请执行：pip install python-pptx",
                detail=str(exc),
            ) from exc

        prs = Presentation()

        def new_slide(title: str) -> Any:  # noqa: ANN401 — python-pptx 无公开类型存根
            slide = prs.slides.add_slide(prs.slide_layouts[1])  # 标题+内容
            slide.shapes.title.text = title
            body = slide.placeholders[1].text_frame
            body.clear()
            return body

        def add_par(body: Any, text: str, *, bold: bool = False, italic: bool = False,
                    font: str = "", size: int | None = None, level: int = 0) -> None:
            par = body.paragraphs[0] if (len(body.paragraphs) == 1 and not body.paragraphs[0].runs) \
                else body.add_paragraph()
            par.level = min(4, max(0, level))
            run = par.add_run()
            run.text = text
            run.font.bold = bold or None
            run.font.italic = italic or None
            if font:
                run.font.name = font
            if size is not None:
                run.font.size = Pt(size)

        body = None
        for block in document.blocks:
            match block.type:
                case BlockType.HEADING if block.level <= 1:
                    body = new_slide(block.text)
                case BlockType.HEADING:
                    # 二级及以下标题：无正文容器时先开一页；否则作为加粗行。
                    body = body if body is not None else new_slide(block.text)
                    add_par(body, block.text, bold=True, size=16)
                case BlockType.LIST_ITEM:
                    body = body if body is not None else new_slide("")
                    add_par(body, block.text, level=block.indent)
                case BlockType.QUOTE:
                    body = body if body is not None else new_slide("")
                    add_par(body, f"❝ {block.text}", italic=True)
                case BlockType.CODE:
                    body = body if body is not None else new_slide("")
                    for line in block.text.split("\n"):
                        add_par(body, line, font="Consolas", size=12)
                case BlockType.DIVIDER:
                    continue  # 幻灯片按标题分页，分隔线无对应语义
                case _:  # PARAGRAPH 与未知类型 → 正文段落
                    body = body if body is not None else new_slide("")
                    for line in block.text.split("\n"):
                        add_par(body, line)

        # 全程无任何 HEADING1：把已有内容并入单一标题页，避免出现空首屏。
        if body is None:
            body = new_slide(document.plain_text("\n")[:80] or "未命名演示")
            for line in document.plain_text("\n").split("\n"):
                add_par(body, line)

        prs.save(str(out_path))
        return out_path
