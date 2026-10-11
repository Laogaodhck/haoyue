"""命令行入口：供宿主程序（如 haoyue 的 doc_render 工具）以子进程方式调用。

用法::

    python -m doc_toolkit convert 报告.md --to docx --out 输出/报告.docx
    python -m doc_toolkit formats            # 列出支持的解析/渲染格式
    python -m doc_toolkit text 报告.docx      # 提取纯文本到 stdout

设计约定：
- 输出路径目录不存在时自动创建；默认输出到源目录、同名换后缀。
- 所有错误统一以中文消息打到 stderr，退出码 1；成功时把输出文件绝对路径
  打到 stdout（唯一一行），便于宿主程序解析。
"""

from __future__ import annotations

import argparse
import sys
from pathlib import Path

from .core.errors import DocumentError
from .toolkit import default_toolkit


def _build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="doc_toolkit",
        description="多格式文档提取、互转与 OCR（离线、纯 Python）。",
    )
    sub = parser.add_subparsers(dest="command", required=True)

    convert = sub.add_parser("convert", help="格式转换：源文件 → IR → 目标格式")
    convert.add_argument("source", help="源文件路径")
    convert.add_argument("--to", required=True, dest="target",
                         help="目标格式名（docx / xlsx / pptx / pdf / markdown / txt）")
    convert.add_argument("--out", default="", help="输出路径；缺省时输出到源目录、同名换后缀")

    sub.add_parser("formats", help="列出支持的解析与渲染格式")

    text = sub.add_parser("text", help="提取纯文本到 stdout")
    text.add_argument("source", help="源文件路径")
    text.add_argument("--ocr", action="store_true",
                      help="PDF 无文本层（扫描件）时回退逐页 OCR")

    return parser


def main(argv: list[str] | None = None) -> int:
    args = _build_parser().parse_args(argv)

    try:
        if args.command == "formats":
            capability = default_toolkit.supported_formats
            print(f"parse: {', '.join(capability['parse'])}")
            print(f"render: {', '.join(capability['render'])}")
            return 0

        if args.command == "text":
            print(default_toolkit.extract_text(args.source, ocr_fallback=args.ocr))
            return 0

        # convert
        out = default_toolkit.convert(args.source, args.target,
                                      out_path=args.out or None)
        print(str(Path(out).resolve()))
        return 0
    except DocumentError as exc:
        print(str(exc), file=sys.stderr)
        return 1
    except OSError as exc:
        print(f"文件读写失败：{exc}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
