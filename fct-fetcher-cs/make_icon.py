"""生成 FCT-Fetcher 应用图标
设计: 深色圆角背景 + 漏斗(筛选) + 上方三色文件块(xml/csv/tdms) + 下方压缩包 + 警示红点(fail)
风格与 fct-fail-ranker / fct-distributor 保持一致(暗色系、冷色调、4x 超采样)
"""
from PIL import Image, ImageDraw

SIZE = 256
SS = 4                      # 4x 超采样, 缩小后边缘平滑
W = SIZE * SS

img = Image.new("RGBA", (W, W), (0, 0, 0, 0))
d = ImageDraw.Draw(img)

# ---- 暗色圆角背景 ----
radius = int(W * 0.22)
d.rounded_rectangle([0, 0, W, W], radius=radius, fill=(24, 27, 34, 255))
grad = Image.new("RGBA", (W, W), (0, 0, 0, 0))
gd = ImageDraw.Draw(grad)
gd.rounded_rectangle([0, 0, W, int(W * 0.6)], radius=radius, fill=(38, 44, 58, 120))
img = Image.alpha_composite(img, grad)
d = ImageDraw.Draw(img)
d.rounded_rectangle([int(SS * 1.5), int(SS * 1.5), W - int(SS * 1.5), W - int(SS * 1.5)],
                    radius=radius, outline=(70, 80, 100, 200), width=SS * 2)

# ---- 上方三色文件块: xml / csv / tdms ----
blk_w = int(W * 0.115)
blk_h = int(W * 0.085)
blk_r = int(blk_w * 0.22)
blk_y = int(W * 0.175)
gap = int(W * 0.045)
total_w = blk_w * 3 + gap * 2
bx = (W - total_w) // 2
COLORS = [
    (86, 204, 242),    # xml  亮青
    (120, 214, 150),   # csv  绿
    (245, 166, 90),    # tdms 橙
]
for col in COLORS:
    d.rounded_rectangle([bx, blk_y, bx + blk_w, blk_y + blk_h],
                        radius=blk_r, fill=col + (255,))
    # 顶部高光, 增加体积感
    d.rounded_rectangle([bx + int(blk_w * 0.12), blk_y + int(blk_h * 0.14),
                         bx + blk_w - int(blk_w * 0.12), blk_y + int(blk_h * 0.36)],
                        radius=int(blk_r * 0.6),
                        fill=(255, 255, 255, 70))
    bx += blk_w + gap

# ---- 漏斗(筛选) ----
fx0 = int(W * 0.235)
fx1 = int(W * 0.765)
fy0 = int(W * 0.335)
neck_y = int(W * 0.575)
neck_half = int(W * 0.058)
cx = W // 2

funnel = [
    (fx0, fy0),
    (fx1, fy0),
    (cx + neck_half, neck_y),
    (cx + neck_half, int(W * 0.655)),
    (cx - neck_half, int(W * 0.655)),
    (cx - neck_half, neck_y),
]
d.polygon(funnel, fill=(79, 172, 254, 255))
# 漏斗左侧提亮, 模拟受光
d.polygon([(fx0, fy0), (cx, fy0), (cx, neck_y), (cx - neck_half, neck_y)],
          fill=(255, 255, 255, 46))
# 漏斗口沿
d.rounded_rectangle([fx0 - int(SS * 2), fy0 - int(W * 0.022),
                     fx1 + int(SS * 2), fy0 + int(W * 0.012)],
                    radius=int(W * 0.017), fill=(150, 215, 255, 255))

# ---- 下方压缩包 ----
box_w = int(W * 0.30)
box_h = int(W * 0.145)
box_x = cx - box_w // 2
box_y = int(W * 0.675)
d.rounded_rectangle([box_x, box_y, box_x + box_w, box_y + box_h],
                    radius=int(W * 0.028), fill=(226, 232, 240, 255))
# 包体下半部稍暗, 形成厚度
d.rounded_rectangle([box_x, box_y + int(box_h * 0.55), box_x + box_w, box_y + box_h],
                    radius=int(W * 0.028), fill=(186, 196, 212, 255))
# 拉链(竖向虚线)
zx = cx
zn = 4
for i in range(zn):
    zy = box_y + int(box_h * (0.16 + 0.21 * i))
    d.rounded_rectangle([zx - int(SS * 2.2), zy, zx + int(SS * 2.2), zy + int(SS * 5)],
                        radius=int(SS * 1.6), fill=(90, 102, 122, 255))

# ---- 警示红点(fail) ----
dot_r = int(W * 0.055)
dcx = int(W * 0.775)
dcy = int(W * 0.255)
glow = Image.new("RGBA", (W, W), (0, 0, 0, 0))
gld = ImageDraw.Draw(glow)
gld.ellipse([dcx - dot_r * 2, dcy - dot_r * 2, dcx + dot_r * 2, dcy + dot_r * 2],
            fill=(255, 80, 90, 62))
img = Image.alpha_composite(img, glow)
d = ImageDraw.Draw(img)
d.ellipse([dcx - dot_r, dcy - dot_r, dcx + dot_r, dcy + dot_r], fill=(255, 76, 87, 255))
hl = int(dot_r * 0.34)
d.ellipse([dcx - dot_r * 0.4 - hl, dcy - dot_r * 0.4 - hl,
           dcx - dot_r * 0.4 + hl, dcy - dot_r * 0.4 + hl],
          fill=(255, 180, 185, 205))

# ---- 输出 ----
img = img.resize((SIZE, SIZE), Image.LANCZOS)
sizes = [16, 24, 32, 48, 64, 128, 256]
img.save("app_icon.ico", format="ICO", sizes=[(s, s) for s in sizes])
img.save("app_icon_preview.png")
print("已生成 app_icon.ico (含 16/24/32/48/64/128/256) 和 app_icon_preview.png")
