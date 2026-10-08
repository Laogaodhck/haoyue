"""TXT 解析器：按空行切段落，结构信息丢失（TXT 本身无结构语义）。"""

from __future__ import annotations

from pathlib import Path

from ..core.base import BaseParser
from ..core.errors import CorruptedDocumentError
from ..core.models import Document


class TxtParser(BaseParser):
    name = "txt"
    extensions = frozenset({"txt", "log", "text"})

    def read(self, path: Path) -> Document:
        try:
            raw = path.read_bytes()
        except OSError as exc:
            raise CorruptedDocumentError(f"无法读取文件：{path.name}", detail=str(exc)) from exc

        # 编码探测：优先 UTF-8，其次 GBK（中文环境常见），均失败再试 UTF-16。
        text: str | None = None
        for encoding in ("utf-8-sig", "gb18030", "utf-16"):
            try:
                text = raw.decode(encoding)
                break
            except UnicodeDecodeError:
                continue
        if text is None:
            raise CorruptedDocumentError(
                f"无法识别 {path.name} 的文本编码，文件可能已损坏或为二进制内容。"
            )
        return Document.from_plain_text(text, source_format=self.name)
