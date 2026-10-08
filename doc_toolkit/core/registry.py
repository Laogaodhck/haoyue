"""解析器 / 渲染器注册表：格式能力的中心索引，支持运行时注册扩展。"""

from __future__ import annotations

from pathlib import Path

from .base import BaseParser, BaseRenderer
from .errors import DocumentError, UnsupportedFormatError


class FormatRegistry:
    """按「格式名 + 扩展名」双索引管理解析器与渲染器。

    重复注册同名格式视为替换（便于外部覆盖内置实现）；同扩展名冲突时后注册者优先。
    """

    def __init__(self) -> None:
        self._parsers: dict[str, BaseParser] = {}
        self._parsers_by_ext: dict[str, str] = {}
        self._renderers: dict[str, BaseRenderer] = {}

    # ---- 解析器 ---------------------------------------------------------
    def register_parser(self, parser: BaseParser) -> None:
        if not parser.name or not parser.extensions:
            raise DocumentError(f"解析器 {type(parser).__name__} 未声明 name 或 extensions")
        self._parsers[parser.name] = parser
        for ext in parser.extensions:
            self._parsers_by_ext[ext.lower().lstrip(".")] = parser.name

    def parser_for_path(self, path) -> BaseParser:
        suffix = Path(path).suffix.lower().lstrip(".")
        name = self._parsers_by_ext.get(suffix)
        if name is None:
            raise UnsupportedFormatError.for_parse(path, list(self._parsers_by_ext))
        return self._parsers[name]

    def parser_by_name(self, name: str) -> BaseParser:
        parser = self._parsers.get(name)
        if parser is None:
            raise UnsupportedFormatError(f"未注册名为 {name!r} 的解析器")
        return parser

    @property
    def supported_parse_extensions(self) -> list[str]:
        return sorted(self._parsers_by_ext)

    @property
    def supported_parse_formats(self) -> list[str]:
        return sorted(self._parsers)

    # ---- 渲染器 ---------------------------------------------------------
    def register_renderer(self, renderer: BaseRenderer) -> None:
        if not renderer.name:
            raise DocumentError(f"渲染器 {type(renderer).__name__} 未声明 name")
        self._renderers[renderer.name] = renderer

    def renderer_by_name(self, name: str) -> BaseRenderer:
        renderer = self._renderers.get(name)
        if renderer is None:
            raise UnsupportedFormatError.for_render(name, list(self._renderers))
        return renderer

    @property
    def supported_render_formats(self) -> list[str]:
        return sorted(self._renderers)

    def has_renderer(self, name: str) -> bool:
        return name in self._renderers
