"""文档中间模型（IR）：所有解析器的输出、所有渲染器的输入。

解析器把任意格式「拍平」成 Document（块序列），渲染器再从块序列生成目标格式。
新增格式只需对接 IR，解析器之间、渲染器之间互不感知。
"""

from __future__ import annotations

import json
from dataclasses import dataclass, field
from enum import Enum


class BlockType(str, Enum):
    """支持的块类型。新增块类型时渲染器可自行决定忽略或降级为段落。"""

    HEADING = "heading"        # 标题，level 1-6
    PARAGRAPH = "paragraph"    # 普通段落
    LIST_ITEM = "list_item"    # 列表项，ordered 标记有序/无序
    QUOTE = "quote"            # 引用块
    CODE = "code"              # 代码块
    DIVIDER = "divider"        # 分隔线（无文本内容）


@dataclass
class DocumentBlock:
    """单个内容块。

    Attributes:
        type: 块类型。
        text: 块文本内容（DIVIDER 可为空串）。
        level: 标题级别（1-6），非标题块忽略。
        ordered: 是否有序列表项，仅 LIST_ITEM 使用。
        indent: 列表嵌套层级，0 为顶层。
    """

    type: BlockType
    text: str = ""
    level: int = 1
    ordered: bool = False
    indent: int = 0

    def to_dict(self) -> dict:
        return {
            "type": self.type.value,
            "text": self.text,
            "level": self.level,
            "ordered": self.ordered,
            "indent": self.indent,
        }

    @classmethod
    def from_dict(cls, data: dict) -> "DocumentBlock":
        return cls(
            type=BlockType(data["type"]),
            text=data.get("text", ""),
            level=int(data.get("level", 1)),
            ordered=bool(data.get("ordered", False)),
            indent=int(data.get("indent", 0)),
        )


@dataclass
class Document:
    """解析后的统一文档模型：块序列 + 少量元信息。"""

    blocks: list[DocumentBlock] = field(default_factory=list)
    source_path: str = ""
    source_format: str = ""

    # ---- 便捷构造 -------------------------------------------------------
    @classmethod
    def from_plain_text(cls, text: str, source_format: str = "txt") -> "Document":
        """把纯文本按空行切段落，供 TXT 解析与各处兜底使用。"""
        blocks: list[DocumentBlock] = []
        for para in text.replace("\r\n", "\n").split("\n\n"):
            stripped = para.strip()
            if stripped:
                blocks.append(DocumentBlock(BlockType.PARAGRAPH, stripped))
        return cls(blocks=blocks, source_format=source_format)

    # ---- 常用查询 -------------------------------------------------------
    @property
    def is_empty(self) -> bool:
        return not any(block.text.strip() for block in self.blocks)

    def plain_text(self, separator: str = "\n\n") -> str:
        """提取为纯文本（列表项合并为连续行），供 extract_text 使用。"""
        lines: list[str] = []
        for block in self.blocks:
            if block.type is BlockType.DIVIDER:
                lines.append("-" * 8)
            elif block.type is BlockType.LIST_ITEM:
                bullet = f"{block.indent * '  '}- " if not block.ordered else ""
                lines.append(f"{bullet}{block.text}")
            else:
                lines.append(block.text)
        return separator.join(lines)

    def to_json(self) -> str:
        """序列化为 JSON，便于上层系统直接消费 IR。"""
        return json.dumps(
            {"source_format": self.source_format, "blocks": [b.to_dict() for b in self.blocks]},
            ensure_ascii=False,
            indent=2,
        )
