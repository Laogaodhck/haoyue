"""扫描件 PDF 的 OCR 回退：pypdfium2 渲染页面为位图后逐页识别。"""

from __future__ import annotations

import tempfile
from pathlib import Path

from ..core.models import BlockType, Document, DocumentBlock
from ..ocr import OcrError, get_default_engine

#: 页面渲染缩放倍数（1 = 72dpi；2.5 ≈ 180dpi，识别精度与耗时的平衡点）。
_RENDER_SCALE = 2.5


def ocr_pdf_document(pdf_path: str | Path, page_limit: int = 0) -> Document:
    """把（扫描件）PDF 逐页渲染成图片并 OCR，返回统一 IR。

    Args:
        pdf_path: PDF 文件路径。
        page_limit: 最多识别前 N 页；0 表示全部页。
    Raises:
        RuntimeError: 渲染失败（PDF 损坏）。
        OcrError: 识别引擎异常。
    """
    import pypdfium2 as pdfium

    pdf = pdfium.PdfDocument(str(pdf_path))
    try:
        page_count = len(pdf)
        if page_limit > 0:
            page_count = min(page_count, page_limit)

        blocks: list[DocumentBlock] = []
        with tempfile.TemporaryDirectory(prefix="haoyue_ocr_") as tmp:
            for index in range(page_count):
                # 渲染单页 → 临时 PNG → OCR。
                bitmap = pdf[index].render(scale=_RENDER_SCALE)
                page_png = Path(tmp) / f"page_{index + 1}.png"
                bitmap.to_pil().save(page_png)
                result = get_default_engine().recognize_image(page_png)
                if index > 0 and result.lines:
                    blocks.append(DocumentBlock(BlockType.DIVIDER))
                for line in result.lines:
                    blocks.append(DocumentBlock(BlockType.PARAGRAPH, line.text))
        return Document(blocks=blocks, source_path=str(pdf_path), source_format="pdf")
    except OcrError:
        raise
    except Exception as exc:
        raise RuntimeError(f"渲染 PDF 页面失败：{exc}") from exc
    finally:
        pdf.close()
