"""错误体系：对外的异常均携带中文可读信息，便于上层直接展示。"""

from __future__ import annotations

from pathlib import Path


class DocumentError(Exception):
    """文档处理错误基类。message 面向最终用户，detail 面向开发者排查。"""

    def __init__(self, message: str, detail: str = "") -> None:
        super().__init__(message)
        self.message = message
        self.detail = detail

    def __str__(self) -> str:  # 兼容日志输出
        return f"{self.message}" + (f"（{self.detail}）" if self.detail else "")


class UnsupportedFormatError(DocumentError):
    """文件格式不受支持，或未注册对应的解析器/渲染器。"""

    @classmethod
    def for_parse(cls, path: str | Path, supported: list[str]) -> "UnsupportedFormatError":
        exts = ", ".join(sorted(supported))
        return cls(f"不支持的文档格式：{Path(path).suffix or '未知扩展名'}，当前支持解析：{exts}")

    @classmethod
    def for_render(cls, target_format: str, supported: list[str]) -> "UnsupportedFormatError":
        return cls(
            f"不支持的目标格式：{target_format}，当前支持输出：{', '.join(sorted(supported))}"
        )


class DocumentParseError(DocumentError):
    """解析失败基类：文件存在但内容无法解析。"""


class CorruptedDocumentError(DocumentParseError):
    """文件已损坏、被加密或结构非法，无法提取内容。"""


class EmptyDocumentError(DocumentParseError):
    """文件可解析但未提取到任何文本内容（如纯图片 PDF）。"""


class FileNotFoundError_(DocumentError):
    """源文件不存在，与内置 FileNotFoundError 区分以统一错误处理入口。"""
