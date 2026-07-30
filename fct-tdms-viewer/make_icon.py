"""生成 FCT-TdmsViewer 应用图标
设计: 深色圆角背景 + 网格 + 阶梯状波形(呼应真实 TDMS 数据的平台结构) + 判定点标记
风格与 fct-fail-ranker / fct-distributor 保持一致(暗色系、冷色调、4x 超采样)
"""
from PIL import Image, ImageDraw

SIZE = 256
SS = 4
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

# ---- 绘图区 ----
pl, pr = int(W * 0.145), int(W * 0.875)
pt, pb = int(W * 0.215), int(W * 0.795)

# 网格(极淡, 避免在 16x16 下变成噪点)
for i in range(1, 3):
    y = pt + (pb - pt) * i // 3
    d.line([pl, y, pr, y], fill=(48, 55, 70, 255), width=int(SS * 0.8))
for i in range(1, 3):
    x = pl + (pr - pl) * i // 3
    d.line([x, pt, x, pb], fill=(48, 55, 70, 255), width=int(SS * 0.8))

# 坐标轴
d.line([pl, pb, pr, pb], fill=(130, 142, 162, 255), width=int(SS * 2.0))
d.line([pl, pt, pl, pb], fill=(130, 142, 162, 255), width=int(SS * 2.0))

# ---- 阶梯状波形: 直接对应实测 RES_v_ResAng 的平台形态 ----
# 只用4段, 小尺寸下仍能辚认出阶梯轮廓
levels = [0.90, 0.16, 0.45, 0.74]
seg = (pr - pl) / len(levels)
pts = []
for i, lv in enumerate(levels):
    y = pb - (pb - pt) * lv
    x0 = pl + seg * i
    x1 = pl + seg * (i + 1)
    pts.append((x0, y))
    pts.append((x1, y))

# 波形发光(底层加粗半透明)
glow = Image.new("RGBA", (W, W), (0, 0, 0, 0))
gld = ImageDraw.Draw(glow)
gld.line(pts, fill=(86, 204, 242, 80), width=int(SS * 15), joint="curve")
img = Image.alpha_composite(img, glow)
d = ImageDraw.Draw(img)

# 波形主线(加粗, 保证 16x16 可辨识)
d.line(pts, fill=(86, 204, 242, 255), width=int(SS * 7.0), joint="curve")

# ---- 上限线(虚线, 体现"判定限值") ----
lim_y = pb - (pb - pt) * 0.82
dash = int(W * 0.038)
x = pl
while x < pr:
    d.line([x, lim_y, min(x + dash, pr), lim_y],
           fill=(255, 120, 128, 205), width=int(SS * 2.2))
    x += dash * 2

# ---- 细边框 ----
d.rounded_rectangle([int(SS * 1.5), int(SS * 1.5), W - int(SS * 1.5), W - int(SS * 1.5)],
                    radius=radius, outline=(70, 80, 100, 200), width=SS * 2)

# ---- 输出 ----
img = img.resize((SIZE, SIZE), Image.LANCZOS)
sizes = [16, 24, 32, 48, 64, 128, 256]
img.save("app_icon.ico", format="ICO", sizes=[(s, s) for s in sizes])
img.save("app_icon_preview.png")
print("已生成 app_icon.ico (含 16/24/32/48/64/128/256) 和 app_icon_preview.png")
