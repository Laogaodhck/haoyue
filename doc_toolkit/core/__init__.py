"""core 包：中间模型、错误体系、扩展接口与注册表。"""

from .base import BaseParser, BaseRenderer
from .errors import (
    CorruptedDocumentError,
    DocumentError,
    DocumentParseError,
    EmptyDocumentError,
    UnsupportedFormatError,
)
from .models import BlockType, Document, DocumentBlock
from .registry import FormatRegistry

__all__ = [
    "BaseParser",
    "BaseRenderer",
    "BlockType",
    "CorruptedDocumentError",
    "Document",
    "DocumentBlock",
    "DocumentError",
    "DocumentParseError",
    "EmptyDocumentError",
    "FormatRegistry",
    "UnsupportedFormatError",
]
