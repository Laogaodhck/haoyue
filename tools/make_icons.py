"""从 haoyue logo/haoyue ico.png 生成全仓库图标资产（可重复执行）。

源图是白底圆角方块：直接缩放会把白边带进任务栏/深色背景，所以先按形状抠图
（圆角外改成透明），再输出各尺寸。图标出现在桌面端（Windows .ico / Linux png）、
Web 管理端与官网三处，必须同源生成，避免手工导出漂移。

用法：py tools/make_icons.py [源图路径]
"""
from __future__ import annotations

import struct
import sys
from pathlib import Path

from PIL import Image, ImageDraw

REPO_ROOT = Path(__file__).resolve().parents[1]
DEFAULT_SOURCE = REPO_ROOT / "haoyue logo" / "haoyue ico.png"

# 圆角方块以外的像素是纯白背景；离白足够远即视为图标本体。
WHITE_THRESHOLD = 12
# 遮罩按 4 倍超采样再缩回，圆角边缘才有平滑抗锯齿。
MASK_SUPERSAMPLE = 4
ICO_SIZES = [16, 24, 32, 48, 64, 128, 256]

# 目标资产：桌面端 .ico 供 Windows 打包与运行时窗口图标，logo.png 供 Linux
# hicolor/512x512、侧栏与关于对话框、官网与 README。
TARGETS = [
    ("haoyue_desktop/resources/favicon.ico", "ico"),
    ("haoyue_desktop/resources/logo.png", 512),
    ("haoyue_webserver/wwwroot/favicon.ico", "ico"),
    ("haoyue_webserver/wwwroot/favicon.png", 32),
    ("haoyue_webserver/wwwroot/logo.png", 512),
    ("haoyue_website/public/favicon.ico", "ico"),
    ("haoyue_website/public/logo.png", 512),
]


def _is_shape(sample: object) -> bool:
    """采样点可能是单通道灰度值（L 模式），也可能是 RGB/RGBA 元组。"""
    pixel = sample if isinstance(sample, tuple) else (sample, sample, sample)
    return any(abs(channel - 255) > WHITE_THRESHOLD for channel in pixel[:3])


def crop_to_shape(source: Image.Image) -> tuple[Image.Image, int]:
    """裁出圆角方块本体，返回图像与圆角半径（像素）。"""
    gray = source.convert("L")
    width, height = gray.size
    xs = [x for x in range(width) if any(_is_shape(gray.getpixel((x, y))) for y in range(0, height, 3))]
    ys = [y for y in range(height) if any(_is_shape(gray.getpixel((x, y))) for x in range(0, width, 3))]
    if not xs or not ys:
        raise SystemExit("源图中找不到图标形状（全白？）")
    left, right, top, bottom = min(xs), max(xs), min(ys), max(ys)
    box = source.convert("RGB").crop((left, top, right + 1, bottom + 1))
    side = max(box.size)
    square = Image.new("RGB", (side, side), (255, 255, 255))
    square.paste(box, ((side - box.width) // 2, (side - box.height) // 2))

    # 圆角半径 = 左边缘第一行出现形状的位置（其上方都被圆角切掉，露出白底）。
    edge = square.convert("L")
    radius = next((y for y in range(side) if _is_shape(edge.getpixel((0, y)))), 0)
    return square, radius


def rounded_alpha(side: int, radius: int) -> Image.Image:
    mask = Image.new("L", (side * MASK_SUPERSAMPLE, side * MASK_SUPERSAMPLE), 0)
    draw = ImageDraw.Draw(mask)
    draw.rounded_rectangle(
        [0, 0, mask.width - 1, mask.height - 1], radius=radius * MASK_SUPERSAMPLE, fill=255)
    return mask.resize((side, side), Image.LANCZOS)


def build_master(source_path: Path) -> tuple[Image.Image, dict[str, object]]:
    source = Image.open(source_path)
    square, radius = crop_to_shape(source)
    master = square.convert("RGBA")
    master.putalpha(rounded_alpha(master.width, radius or master.width // 6))
    return master, {"source": source.size, "side": master.width, "radius": radius}


def _png_entry(image: Image.Image) -> bytes:
    from io import BytesIO

    buffer = BytesIO()
    image.save(buffer, format="PNG")
    return buffer.getvalue()


def _dib_entry(image: Image.Image) -> bytes:
    """32bpp BI_RGB 自底向上 BGRA + 全 0 AND 掩码，与 Windows Shell 期望的 DIB 条目一致。"""
    width, height = image.size
    rgba = image.convert("RGBA")
    top_down = rgba.tobytes("raw", "BGRA")
    row = width * 4
    pixels = b"".join(top_down[y * row:(y + 1) * row] for y in range(height - 1, -1, -1))
    mask = b"\x00" * (height * ((width + 31) // 32) * 4)
    header = struct.pack(
        "<IiiHHIIiiII", 40, width, height * 2, 1, 32, 0, len(pixels) + len(mask), 0, 0, 0, 0)
    return header + pixels + mask


def write_ico(master: Image.Image, sizes: list[int]) -> bytes:
    """混合编码：≤48 用 BMP/DIB，更大用 PNG。

    Windows 资源管理器/任务栏对 PNG 压缩的小尺寸 .ico 条目兼容性差（10-07 那次
    「打包版任务栏图标不显示」的根因之一），所以小尺寸保留未压缩 DIB；大尺寸用
    PNG 省体积。两种条目混在一个 .ico 里是 Shell 自己生成的 .ico 的常见形态。
    """
    entries = []
    for size in sizes:
        icon = master if master.width == size else master.resize((size, size), Image.LANCZOS)
        entries.append((size, _dib_entry(icon) if size <= 48 else _png_entry(icon)))
    header_size = 6 + 16 * len(entries)
    offset = header_size
    directory = bytearray()
    blobs = bytearray()
    for size, blob in entries:
        dimension = 0 if size >= 256 else size
        directory += struct.pack("<BBBBHHII", dimension, dimension, 0, 0, 1, 32, len(blob), offset)
        blobs += blob
        offset += len(blob)
    return struct.pack("<HHH", 0, 1, len(entries)) + bytes(directory) + bytes(blobs)


def write_target(master: Image.Image, relative: str, spec: object) -> str:
    path = REPO_ROOT / relative
    path.parent.mkdir(parents=True, exist_ok=True)
    if spec == "ico":
        path.write_bytes(write_ico(master, ICO_SIZES))
        return f"{relative}  ICO {len(ICO_SIZES)} 档（≤48 BMP/DIB，更大 PNG）"
    size = int(spec)
    icon = master if master.width == size else master.resize((size, size), Image.LANCZOS)
    icon.save(path, format="PNG", optimize=True)
    return f"{relative}  PNG {size}x{size}"


def main(argv: list[str]) -> int:
    source_path = Path(argv[1]) if len(argv) > 1 else DEFAULT_SOURCE
    if not source_path.is_absolute():
        source_path = REPO_ROOT / source_path
    if not source_path.exists():
        raise SystemExit(f"源图不存在：{source_path}")
    master, info = build_master(source_path)
    print(f"源图 {info['source']} → 图标 {info['side']}x{info['side']}，圆角半径 {info['radius']}")
    for relative, spec in TARGETS:
        print("  " + write_target(master, relative, spec))
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv))
