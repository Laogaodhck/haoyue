"""OCR 模块：图片文字识别。

对外入口：
- get_default_engine()：获取默认引擎（惰性初始化，进程内共享）；
- OcrResult / OcrLine：识别结果模型（纯文本 plain_text + 结构化 structured()）；
- OcrError：引擎级致命错误。
"""

from .base import LOW_CONFIDENCE_THRESHOLD, BaseOcrEngine, OcrLine, OcrResult
from .rapid_engine import OcrError, RapidOcrEngine, get_shared_engine

__all__ = [
    "LOW_CONFIDENCE_THRESHOLD",
    "BaseOcrEngine",
    "OcrError",
    "OcrLine",
    "OcrResult",
    "RapidOcrEngine",
    "get_default_engine",
]


def get_default_engine() -> BaseOcrEngine:
    """返回默认 OCR 引擎（当前为 RapidOCR，进程级单例）。"""
    return get_shared_engine()
