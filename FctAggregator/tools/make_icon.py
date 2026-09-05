"""把 all-icons 里的 6 个 PNG 打成一个多分辨率 .ico。

为什么不用 PIL 的 save(format='ICO')：它会把所有帧重新缩放/编码，且对 256 尺寸
的处理依赖版本。这里直接按 ICO 容器格式写：每个尺寸原样塞入 PNG 数据
（Vista+ 支持 PNG 压缩的 ICO 条目），保证与设计稿逐像素一致。
"""
import io, os, struct
from PIL import Image

SRC = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), 'assets', 'icon')
OUT = os.path.join(os.path.dirname(os.path.dirname(os.path.abspath(__file__))), 'app_icon.ico')

sizes = [16, 32, 48, 64, 128, 256]
frames = []
for s in sizes:
    p = os.path.join(SRC, f'icon-{s}x{s}.png')
    raw = io.open(p, 'rb').read()
    im = Image.open(io.BytesIO(raw))
    print(f'  {s:>3}x{s:<3} {im.size[0]}x{im.size[1]} {im.mode:<5} {len(raw)/1024:6.1f} KB')
    if im.size != (s, s):
        raise SystemExit(f'{p} 实际尺寸 {im.size} 与文件名不符')
    frames.append((s, raw))

out = bytearray()
out += struct.pack('<HHH', 0, 1, len(frames))
offset = 6 + 16 * len(frames)
for s, raw in frames:
    w = 0 if s >= 256 else s
    out += struct.pack('<BBBBHHII',
                       w, w,
                       0,
                       0,
                       1,
                       32,
                       len(raw),
                       offset)
    offset += len(raw)
for _, raw in frames:
    out += raw

old = os.path.getsize(OUT) if os.path.exists(OUT) else 0
io.open(OUT, 'wb').write(bytes(out))
print(f'\n写出 {OUT}')
print(f'  {old} 字节 -> {len(out)} 字节，{len(frames)} 个尺寸')

d = io.open(OUT, 'rb').read()
res, typ, cnt = struct.unpack('<HHH', d[:6])
assert res == 0 and typ == 1, '不是合法 ICO 头'
print(f'  校验：type={typ} count={cnt}')
for i in range(cnt):
    w, h, _, _, planes, bpp, nbytes, off = struct.unpack('<BBBBHHII', d[6 + 16 * i: 22 + 16 * i])
    sig = d[off:off + 8]
    kind = 'PNG' if sig.startswith(b'\x89PNG') else 'BMP'
    print(f'    #{i}: {w or 256}x{h or 256} {bpp}bpp {kind} {nbytes} 字节 @ {off}')
