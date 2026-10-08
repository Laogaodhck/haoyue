"""图片解析器：通过 OCR 把图片内容解析为统一 IR。

效果：图片可作为一等文档参与全部流程 —— extract_text / convert("图片.png", "docx")。
每行 OCR 结果作为一个段落块；置信度过低时在文档首部附提示块。
"""

from __future__ import annotations

from pathlib import Path

from ..core.base import BaseParser
from ..core.errors import CorruptedDocumentError
from ..core.models import BlockType, Document, DocumentBlock
from ..ocr import OcrError, get_default_engine

#: 支持的图片扩展名（PIL 可读 + OCR 常用）。
IMAGE_EXTENSIONS = frozenset({"png", "jpg", "jpeg", "bmp", "webp", "tif", "tiff"})


class ImageParser(BaseParser):
    name = "image"
    extensions = IMAGE_EXTENSIONS

    def read(self, path: Path) -> Document:
        # 先做一次可读性校验，把「损坏/非图片」与「识别失败」区分开。
        try:
            from PIL import Image
            Image.open(path).verify()
        except ImportError as exc:  # pragma: no cover
            raise CorruptedDocumentError(
                "图片解析依赖 pillow 未安装，请执行：pip install pillow", detail=str(exc)
            ) from exc
        except Exception as exc:
            raise CorruptedDocumentError(
                f"{path.name} 不是有效的图片文件或已损坏。", detail=str(exc)
            ) from exc

        try:
            result = get_default_engine().recognize_image(path)
        except OcrError as exc:
            raise CorruptedDocumentError(str(exc)) from exc

        blocks: list[DocumentBlock] = [
            DocumentBlock(BlockType.PARAGRAPH, line.text) for line in result.lines
        ]
        if result.warning:
            blocks.insert(0, DocumentBlock(BlockType.QUOTE, f"[提示] {result.warning}"))
        if not blocks:
            raise CorruptedDocumentError(
                f"{path.name} 中未识别到文字内容（可能为空白或纯图形图片）。"
            )
        return Document(blocks=blocks, source_path=str(path), source_format=self.name)
