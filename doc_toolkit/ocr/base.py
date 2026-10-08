"""OCR 结果模型与引擎抽象。

设计要点：
- OcrResult 同时承载纯文本（plain_text）与结构化（lines：文本+置信度+坐标）两种输出；
- 模糊/低质量图片不硬失败，通过 low_confidence 标记 + warning 提示上层自行决策；
- 引擎统一走 BaseOcrEngine 抽象，后续可替换 PaddleOCR / 云端 OCR 而不动调用方。
"""

from __future__ import annotations

from abc import ABC, abstractmethod
from dataclasses import dataclass, field
from pathlib import Path

#: 平均置信度低于该阈值视为「图片质量差，结果可能不准」。
LOW_CONFIDENCE_THRESHOLD = 0.55


@dataclass
class OcrLine:
    """单行识别结果。box 为四角坐标 [[x,y]×4]，坐标为原图像素值。"""

    text: str
    confidence: float
    box: list[list[int]] = field(default_factory=list)

    def to_dict(self) -> dict:
        return {"text": self.text, "confidence": round(self.confidence, 4), "box": self.box}


@dataclass
class OcrResult:
    """一次识别的完整结果。

    Attributes:
        lines: 按阅读顺序排列的文本行。
        source_path: 源图片路径（PDF 回退时为临时页图，可为空）。
        warning: 非致命提示（如「图片可能模糊」），致命失败直接抛异常。
        elapsed_ms: 识别耗时，供性能观测。
    """

    lines: list[OcrLine] = field(default_factory=list)
    source_path: str = ""
    warning: str = ""
    elapsed_ms: int = 0

    @property
    def plain_text(self) -> str:
        """纯文本输出：按行拼接。"""
        return "\n".join(line.text for line in self.lines)

    @property
    def mean_confidence(self) -> float:
        if not self.lines:
            return 0.0
        return sum(line.confidence for line in self.lines) / len(self.lines)

    @property
    def is_empty(self) -> bool:
        return not any(line.text.strip() for line in self.lines)

    @property
    def low_confidence(self) -> bool:
        return bool(self.lines) and self.mean_confidence < LOW_CONFIDENCE_THRESHOLD

    def structured(self) -> dict:
        """结构化输出：可直接 JSON 序列化。"""
        return {
            "text": self.plain_text,
            "line_count": len(self.lines),
            "mean_confidence": round(self.mean_confidence, 4),
            "low_confidence": self.low_confidence,
            "warning": self.warning,
            "elapsed_ms": self.elapsed_ms,
            "lines": [line.to_dict() for line in self.lines],
        }


class BaseOcrEngine(ABC):
    """OCR 引擎抽象 —— 替换/新增引擎的扩展点。"""

    #: 引擎标识（如 "rapidocr"）。
    name: str = ""

    @abstractmethod
    def recognize_image(self, image_path: str | Path) -> OcrResult:
        """识别单张图片，返回 OcrResult。致命失败抛 OcrError。"""
