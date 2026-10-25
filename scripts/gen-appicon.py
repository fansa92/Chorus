"""生成 WinUI 风格的应用图标（需 Pillow）：圆角方形渐变底 + 三条斜向圆角竖条（呼应 Bettbox 的速度条）。

输出 1024 主图（appicon.png）+ 多尺寸 appicon.ico（16~256）。
小尺寸（<=32）把图形整体放大一点，避免 16px 下三条杆糊成一团。
"""
from PIL import Image, ImageDraw, ImageFilter
import io
import math
import os
import struct

SS = 4                     # 超采样倍数
BASE = 1024
C_TOP = (0x5C, 0xB8, 0xFF)  # WinUI 蓝：亮
C_BOTTOM = (0x0A, 0x5E, 0xCC)  # WinUI 蓝：深


def lerp(a, b, t):
    return tuple(round(a[i] + (b[i] - a[i]) * t) for i in range(3))


def gradient(size, top, bottom, angle_deg=118):
    """线性渐变：先在 96px 上逐像素算，再放大（线性渐变放大不会失真，快几百倍）。"""
    small = 96
    img = Image.new("RGB", (small, small))
    px = img.load()
    rad = math.radians(angle_deg)
    dx, dy = math.cos(rad), math.sin(rad)
    lo = min(0.0, small * dx + small * dy)
    hi = max(0.0, small * dx + small * dy)
    span = (hi - lo) or 1
    for y in range(small):
        for x in range(small):
            t = ((x * dx + y * dy) - lo) / span
            px[x, y] = lerp(top, bottom, max(0.0, min(1.0, t)))
    return img.resize((size, size), Image.BICUBIC)


def rounded_mask(size, radius):
    m = Image.new("L", (size, size), 0)
    ImageDraw.Draw(m).rounded_rectangle([0, 0, size - 1, size - 1], radius=radius, fill=255)
    return m


def bar_layer(size, bars, angle_deg):
    layer = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    d = ImageDraw.Draw(layer)
    for (cx, cy, w, h) in bars:
        d.rounded_rectangle([cx - w / 2, cy - h / 2, cx + w / 2, cy + h / 2],
                            radius=w / 2, fill=(255, 255, 255, 247))
    return layer.rotate(angle_deg, resample=Image.BICUBIC, center=(size / 2, size / 2))


def build(glyph_scale=1.0):
    W = BASE * SS
    mask = rounded_mask(W, round(W * 0.222))   # Windows 11 圆角比例

    bg = gradient(W, C_TOP, C_BOTTOM).convert("RGBA")
    bg.putalpha(mask)

    # 顶部柔和高光，避免纯渐变的塑料感
    glow = Image.new("L", (W, W), 0)
    ImageDraw.Draw(glow).ellipse([-W * 0.35, -W * 0.78, W * 1.35, W * 0.40], fill=52)
    glow = glow.filter(ImageFilter.GaussianBlur(W * 0.09))
    glow = Image.composite(glow, Image.new("L", (W, W), 0), mask)
    bg = Image.composite(Image.new("RGBA", (W, W), (255, 255, 255, 255)), bg, glow)

    # 三条斜条：底边对齐、左高右矮，整体右倾 13°（坐标按 1024 设计稿，再整体缩放）
    bottom = 772
    bars1024 = [
        (512 - 162, bottom - 468 / 2, 118, 468),
        (512, bottom - 378 / 2, 118, 378),
        (512 + 162, bottom - 298 / 2, 118, 298),
    ]
    k = W / 1024
    bars = []
    for (x, y, w, h) in bars1024:
        bars.append((
            (512 + (x - 512) * glyph_scale) * k,
            (512 + (y - 512) * glyph_scale) * k,
            w * glyph_scale * k,
            h * glyph_scale * k,
        ))

    out = Image.alpha_composite(bg, bar_layer(W, bars, angle_deg=-13))
    return out.resize((BASE, BASE), Image.LANCZOS)


def _bmp_frame(im):
    """按 ICO 的 BMP 帧格式打包：BITMAPINFOHEADER + 自下而上的 BGRA + AND 掩码。"""
    w, h = im.size
    px = im.convert("RGBA").load()
    body = bytearray()
    for y in range(h - 1, -1, -1):
        for x in range(w):
            r, g, b, a = px[x, y]
            body += bytes((b, g, r, a))

    mask_row = ((w + 31) // 32) * 4
    mask = bytearray()
    for y in range(h - 1, -1, -1):
        row = bytearray(mask_row)
        for x in range(w):
            if px[x, y][3] == 0:
                row[x // 8] |= 0x80 >> (x % 8)
        mask += row

    header = struct.pack("<IiiHHIIiiII", 40, w, h * 2, 1, 32, 0, len(body) + len(mask), 0, 0, 0, 0)
    return bytes(header) + bytes(body) + bytes(mask)


def write_ico(frames, path):
    """自拼 ICO：<=96 用 BMP 帧（兼容性最好），128/256 用 PNG 帧（体积小）。"""
    frames = sorted(frames, key=lambda im: -im.width)
    blobs = []
    for im in frames:
        if im.width <= 96:
            blobs.append((im.width, _bmp_frame(im)))
        else:
            buf = io.BytesIO()
            im.save(buf, format="PNG", optimize=True)
            blobs.append((im.width, buf.getvalue()))

    offset = 6 + 16 * len(blobs)
    entries = b""
    for size, data in blobs:
        dim = 0 if size >= 256 else size
        entries += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
    with open(path, "wb") as f:
        f.write(struct.pack("<HHH", 0, 1, len(blobs)) + entries)
        for _, data in blobs:
            f.write(data)


def main():
    root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
    assets = os.path.join(root, "src", "Chorus.App", "Assets")
    master = build(1.0)
    master.save(os.path.join(assets, "appicon.png"))

    cache = {1.0: master}
    frames = []
    for size in (16, 20, 24, 32, 40, 48, 64, 96, 128, 256):
        scale = 1.22 if size <= 24 else (1.10 if size <= 32 else 1.0)
        if scale not in cache:
            cache[scale] = build(scale)
        src = cache[scale]
        frames.append(src.resize((size, size), Image.LANCZOS))
    write_ico(frames, os.path.join(assets, "appicon.ico"))
    print("ok", [f.width for f in frames])


if __name__ == "__main__":
    main()
