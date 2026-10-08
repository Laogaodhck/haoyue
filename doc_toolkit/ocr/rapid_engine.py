"""RapidOCR 引擎实现（ONNX Runtime CPU 推理，离线可用，中文效果佳）。

性能设计：
- 模型全局惰性加载一次（首次约 1-2 秒），后续进程内复用；
- onnxruntime 默认多线程推理，无需额外配置。
"""

from __future__ import annotations

import time
from pathlib import Path

from .base import BaseOcrEngine, OcrLine, OcrResult

#: 引擎单例缓存（模块级，跨实例复用，避免重复加载模型）。
_engine_instance = None


class OcrError(Exception):
    """OCR 引擎级致命错误（初始化失败、推理崩溃等）。"""


class RapidOcrEngine(BaseOcrEngine):
    name = "rapidocr"

    def __init__(self) -> None:
        try:
            from rapidocr_onnxruntime import RapidOCR
        except ImportError as exc:
            raise OcrError(
                "OCR 依赖 rapidocr-onnxruntime 未安装，"
                "请执行：pip install rapidocr-onnxruntime",
                ) from exc
        self._engine = RapidOCR()

    def recognize_image(self, image_path: str | Path) -> OcrResult:
        started = time.perf_counter()
        try:
            raw, _elapse = self._engine(str(image_path))
        except Exception as exc:
            raise OcrError(f"识别 {Path(image_path).name} 时引擎异常：{exc}") from exc

        result = OcrResult(source_path=str(image_path))
        # raw 形如 [[box, text, score], ...]；无可识别内容时为 None。
        if raw:
            for box, text, score in raw:
                clean = (text or "").strip()
                if clean:
                    result.lines.append(OcrLine(
                        text=clean,
                        confidence=float(score),
                        box=[[int(x), int(y)] for x, y in box],
                    ))
        if result.is_empty:
            result.warning = "未识别到文字内容。"
        elif result.low_confidence:
            result.warning = (
                f"图片质量较低（平均置信度 {result.mean_confidence:.0%}），"
                "识别结果可能不准确，建议使用更清晰的图片。"
            )
        result.elapsed_ms = int((time.perf_counter() - started) * 1000)
        return result


def get_shared_engine() -> RapidOcrEngine:
    """获取进程级共享引擎实例（首次调用时加载模型）。"""
    global _engine_instance
    if _engine_instance is None:
        try:
            _engine_instance = RapidOcrEngine()
        except OcrError:
            raise
    return _engine_instance
