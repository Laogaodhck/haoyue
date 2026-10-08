"""doc_toolkit OCR 功能端到端验证（一次性脚本）。"""
import sys
import tempfile
import traceback
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent.parent))
from doc_toolkit import (  # noqa: E402
    CorruptedDocumentError,
    EmptyDocumentError,
    default_toolkit,
)

WORK = Path(tempfile.mkdtemp(prefix="doc_toolkit_ocr_"))
kit = default_toolkit
passed, failed = 0, []


def check(name: str, cond: bool, extra: str = "") -> None:
    global passed
    if cond:
        passed += 1
        print(f"  PASS {name}")
    else:
        failed.append(name)
        print(f"  FAIL {name} {extra}")


# ---- 1. 生成测试图片 -------------------------------------------------
from PIL import Image, ImageDraw, ImageFont, ImageFilter  # noqa: E402

FONT = ImageFont.truetype("C:/Windows/Fonts/msyh.ttc", 40)
TEXT_LINES = ["OCR 识别测试", "第二行：浩玥文档工具"]


def make_image(path: Path, blur: bool = False, blank: bool = False) -> None:
    img = Image.new("RGB", (900, 400), "white")
    draw = ImageDraw.Draw(img)
    if not blank:
        for i, line in enumerate(TEXT_LINES):
            draw.text((60, 60 + i * 110), line, fill="black", font=FONT)
    if blur:
        img = img.filter(ImageFilter.GaussianBlur(4))
    img.save(path)


clear_png = WORK / "clear.png"
make_image(clear_png)
blank_png = WORK / "blank.png"
make_image(blank_png, blank=True)
blur_png = WORK / "blurry.png"
make_image(blur_png, blur=True)
corrupt_png = WORK / "corrupt.png"
corrupt_png.write_bytes(b"\x89PNG garbage not an image" * 16)

print("== 1. 纯文本输出 ==")
try:
    text = kit.recognize(clear_png)
    check("清晰图识别", "OCR" in text and "文档工具" in text, repr(text[:80]))
except Exception as exc:
    check("清晰图识别", False, repr(exc))

print("== 2. 结构化输出 ==")
try:
    data = kit.recognize(clear_png, structured=True)
    ok = (data["line_count"] >= 2 and data["lines"][0]["box"]
          and 0 < data["mean_confidence"] <= 1 and data["elapsed_ms"] > 0)
    check("结构化字段完整", ok, str(data)[:120])
except Exception as exc:
    check("结构化字段完整", False, repr(exc))

print("== 3. 边界情况 ==")
try:
    kit.recognize(blank_png)
    check("空白图报错", False, "未抛出异常")
except EmptyDocumentError as exc:
    check("空白图报错", "未识别到文字" in str(exc), str(exc))

try:
    text = kit.recognize(blur_png)
    check("模糊图不崩溃", True)  # 能出结果即算稳（内容可空、可带警告）
except Exception as exc:
    check("模糊图不崩溃", False, repr(exc))

try:
    kit.recognize(corrupt_png)
    check("损坏图片报错", False, "未抛出异常")
except CorruptedDocumentError as exc:
    check("损坏图片报错", bool(str(exc)), str(exc))

try:
    kit.recognize(WORK / "no_such.png")
    check("文件不存在报错", False)
except Exception as exc:
    check("文件不存在报错", "不存在" in str(exc), str(exc))

print("== 4. 图片 → DOCX 转换 ==")
try:
    out = kit.convert(clear_png, "docx", WORK / "clear_from_ocr.docx")
    check("图片转 docx", out.is_file() and out.stat().st_size > 1000)
except Exception as exc:
    check("图片转 docx", False, repr(exc))

print("== 5. 扫描件 PDF OCR 回退 ==")
from fpdf import FPDF  # noqa: E402

scanned_pdf = WORK / "scanned.pdf"
p = FPDF(orientation="L", unit="pt", format=(900, 400))
p.add_page()
p.image(str(clear_png), x=0, y=0, w=900, h=400)
p.output(str(scanned_pdf))

try:
    kit.parse(scanned_pdf)
    check("无文本层默认报错", False, "未抛出异常")
except EmptyDocumentError as exc:
    check("无文本层默认报错", "OCR" in str(exc) or "扫描" in str(exc) or "文本" in str(exc), str(exc))

try:
    doc = kit.parse(scanned_pdf, ocr_fallback=True)
    text = doc.plain_text()
    # OCR 对小字号存在精度损失，断言主干关键字而非全文逐字一致。
    check("扫描件 OCR 回退", "OCR" in text and "第二行" in text, repr(text[:80]))
except Exception as exc:
    check("扫描件 OCR 回退", False, traceback.format_exc()[-300:])

print(f"\n结果：{passed} 通过，{len(failed)} 失败 {failed if failed else ''}")
print(f"样例目录：{WORK}")
sys.exit(1 if failed else 0)
