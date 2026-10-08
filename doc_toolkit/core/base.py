"""扩展接口：解析器与渲染器的抽象基类。

实现约束：
- 解析器：read(path) 返回 Document；内容损坏抛 CorruptedDocumentError。
- 渲染器：write(document, out_path) 落盘并返回 out_path。
"""

from __future__ import annotations

from abc import ABC, abstractmethod
from pathlib import Path

from .models import Document


class BaseParser(ABC):
    """格式解析器抽象基类 —— 新增「读」能力的扩展点。

    子类需声明 name 与 extensions（不含点号，如 {"md", "markdown"}）。
    """

    #: 格式唯一标识（如 "markdown"），注册表按此索引。
    name: str = ""
    #: 该解析器接管的文件扩展名（小写、不含点）。
    extensions: frozenset[str] = frozenset()

    @abstractmethod
    def read(self, path: Path) -> Document:
        """把源文件解析为统一 IR。失败时抛 DocumentParseError 体系异常。"""

    def can_parse(self, suffix: str) -> bool:
        return suffix.lower().lstrip(".") in self.extensions


class BaseRenderer(ABC):
    """格式渲染器抽象基类 —— 新增「写」能力的扩展点。"""

    name: str = ""
    #: 默认输出扩展名（不含点）。
    extensions: frozenset[str] = frozenset()
    #: 默认输出文件后缀（含点），未显式指定输出路径时使用。
    default_suffix: str = ""

    @abstractmethod
    def write(self, document: Document, out_path: Path) -> Path:
        """把 IR 渲染并写入 out_path，返回最终落盘路径。"""
