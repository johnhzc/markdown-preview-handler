"""Generate a demo PNG (gradient + droplet lens motif) for the sample markdown."""
import struct
import zlib
import os

W, H = 480, 320


def png_chunk(tag, data):
    c = struct.pack(">I", len(data)) + tag + data
    return c + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF)


def pixel(x, y):
    # 背景竖向渐变（深蓝 -> 浅蓝）
    t = y / H
    r = int(20 + 60 * t)
    g = int(40 + 110 * t)
    b = int(90 + 130 * t)
    # 液滴透镜：圆心 (240,150)，半径 80
    dx, dy = x - 240, y - 150
    d = (dx * dx + dy * dy) ** 0.5
    if d < 80:
        # 透镜体内：高光与折射条纹
        frac = (x - 160) / 160.0
        band = int((y / 14)) % 2
        r = int(90 + 60 * frac)
        g = int(160 + 40 * frac)
        b = int(220 - 30 * band)
        # 高光
        if (dx + 30) ** 2 + (dy + 30) ** 2 < 300:
            r, g, b = 255, 255, 255
    elif abs(d - 80) < 1.5:
        r, g, b = 255, 255, 255  # 轮廓
    # 主光轴虚线
    if y == 150 and (x % 18) < 8:
        r, g, b = 255, 210, 90
    return (r, g, b)


def main():
    raw = bytearray()
    for y in range(H):
        raw.append(0)  # filter none
        for x in range(W):
            raw.extend(pixel(x, y))
    ihdr = struct.pack(">IIBBBBB", W, H, 8, 2, 0, 0, 0)
    png = (b"\x89PNG\r\n\x1a\n"
           + png_chunk(b"IHDR", ihdr)
           + png_chunk(b"IDAT", zlib.compress(bytes(raw), 9))
           + png_chunk(b"IEND", b""))
    out = os.path.join(os.path.dirname(os.path.abspath(__file__)), "assets", "sample-image.png")
    os.makedirs(os.path.dirname(out), exist_ok=True)
    with open(out, "wb") as f:
        f.write(png)
    print("[OK] " + out)


if __name__ == "__main__":
    main()
