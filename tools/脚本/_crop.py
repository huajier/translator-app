"""极简 PNG 裁剪放大工具（不依赖 Pillow）。

用法：
    python tools/脚本/_crop.py 源.png 目标.png x,y,w,h [倍数]

为什么要有：取证截图是整屏 1920x1080，vision 直接读会被压得太小，
小而关键的字（蓝色「翻译中」、灰色详情行）必须裁出来再放大。
"""
import struct
import sys
import zlib


def load_png(path):
    data = open(path, "rb").read()
    i, idat, ct = 8, b"", 6
    while i < len(data):
        ln = struct.unpack(">I", data[i:i + 4])[0]
        typ = data[i + 4:i + 8]
        chunk = data[i + 8:i + 8 + ln]
        if typ == b"IHDR":
            w, h, _bd, ct = struct.unpack(">IIBB", chunk[:10])
        elif typ == b"IDAT":
            idat += chunk
        i += 12 + ln
    raw = zlib.decompress(idat)
    bpp = 3 if ct == 2 else 4
    stride = w * bpp
    rows, prev, p = [], bytearray(stride), 0
    for _y in range(h):
        f = raw[p]
        p += 1
        line = bytearray(raw[p:p + stride])
        p += stride
        if f == 1:
            for x in range(bpp, stride):
                line[x] = (line[x] + line[x - bpp]) & 255
        elif f == 2:
            for x in range(stride):
                line[x] = (line[x] + prev[x]) & 255
        elif f == 3:
            for x in range(stride):
                a = line[x - bpp] if x >= bpp else 0
                line[x] = (line[x] + ((a + prev[x]) >> 1)) & 255
        elif f == 4:
            for x in range(stride):
                a = line[x - bpp] if x >= bpp else 0
                b = prev[x]
                c = prev[x - bpp] if x >= bpp else 0
                pp = a + b - c
                pa, pb, pc = abs(pp - a), abs(pp - b), abs(pp - c)
                pr = a if (pa <= pb and pa <= pc) else (b if pb <= pc else c)
                line[x] = (line[x] + pr) & 255
        rows.append(bytes(line))
        prev = line
    return w, h, bpp, rows


def save_png(path, w, h, rgb_rows):
    def chunk(typ, data):
        return (struct.pack(">I", len(data)) + typ + data
                + struct.pack(">I", zlib.crc32(typ + data) & 0xFFFFFFFF))
    raw = b"".join(b"\x00" + r for r in rgb_rows)
    png = b"\x89PNG\r\n\x1a\n"
    png += chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 2, 0, 0, 0))
    png += chunk(b"IDAT", zlib.compress(raw, 6))
    png += chunk(b"IEND", b"")
    open(path, "wb").write(png)


def crop_zoom(src, dst, box, zoom=1):
    x0, y0, cw, ch = box
    w, h, bpp, rows = load_png(src)
    x0, y0 = max(0, x0), max(0, y0)
    cw, ch = min(cw, w - x0), min(ch, h - y0)
    out = []
    for y in range(y0, y0 + ch):
        src_row = rows[y]
        px = [src_row[x * bpp:x * bpp + 3] for x in range(x0, x0 + cw)]
        line = b"".join(p * zoom for p in px)
        for _ in range(zoom):
            out.append(line)
    save_png(dst, cw * zoom, ch * zoom, out)
    return cw * zoom, ch * zoom


if __name__ == "__main__":
    src, dst = sys.argv[1], sys.argv[2]
    box = [int(v) for v in sys.argv[3].split(",")]
    zoom = int(sys.argv[4]) if len(sys.argv) > 4 else 1
    print(crop_zoom(src, dst, box, zoom))
