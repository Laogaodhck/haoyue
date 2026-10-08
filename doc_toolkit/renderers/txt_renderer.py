"""TXT 渲染器：直接复用 IR 的 plain_text 输出。"""

from __future__ import annotations

from pathlib import Path

from ..core.base import BaseRenderer
from ..core.models import Document


class TxtRenderer(BaseRenderer):
    name = "txt"
    extensions = frozenset({"txt"})
    default_suffix = ".txt"

    def write(self, document: Document, out_path: Path) -> Path:
        out_path.write_text(document.plain_text() + "\n", encoding="utf-8")
        return out_path
