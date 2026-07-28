"""生成暗色系应用图标 - FCT 不良项排名工具
设计: 深色圆角背景 + 降序排行柱状图(体现"排名") + 顶部警示红点(体现"不良/FAIL")
"""
from PIL import Image, ImageDraw

SIZE = 256
# 高分辨率画布(4x超采样后缩小, 边缘更平滑)
SS = 4
W = SIZE * SS

img = Image.new("RGBA", (W, W), (0, 0, 0, 0))
d = ImageDraw.Draw(img)

# ---- 暗色圆角背景 (深蓝灰渐变感, 用两层叠加模拟) ----
radius = int(W * 0.22)
# 底色: 深炭黑
d.rounded_rectangle([0, 0, W, W], radius=radius, fill=(24, 27, 34, 255))
# 顶部微微提亮一层(渐变错觉): 半透明深蓝
grad = Image.new("RGBA", (W, W), (0, 0, 0, 0))
gd = ImageDraw.Draw(grad)
gd.rounded_rectangle([0, 0, W, int(W*0.6)], radius=radius, fill=(38, 44, 58, 120))
img = Image.alpha_composite(img, grad)
d = ImageDraw.Draw(img)

# 细边框(冷灰), 提升质感
d.rounded_rectangle([int(SS*1.5), int(SS*1.5), W-int(SS*1.5), W-int(SS*1.5)],
                    radius=radius, outline=(70, 80, 100, 200), width=SS*2)

# ---- 降序排行柱状图 (从高到低, 体现"排名") ----
# 柱子区域
margin_x = int(W * 0.20)
base_y = int(W * 0.76)      # 柱底
top_y = int(W * 0.30)       # 最高柱顶
bar_w = int(W * 0.115)
gap = int(W * 0.045)
bar_r = int(bar_w * 0.28)

# 冷色渐变(从亮青蓝 -> 暗蓝), 降序高度
heights = [1.0, 0.72, 0.5, 0.32]
colors = [
    (86, 204, 242),   # 亮青
    (79, 172, 254),   # 蓝
    (99, 140, 220),   # 中蓝
    (90, 110, 170),   # 暗蓝
]
x = margin_x
for h, col in zip(heights, colors):
    bar_h = int((base_y - top_y) * h)
    y0 = base_y - bar_h
    d.rounded_rectangle([x, y0, x + bar_w, base_y], radius=bar_r, fill=col + (255,))
    x += bar_w + gap

# ---- 基线 ----
d.rounded_rectangle([margin_x - gap, base_y, x - gap + int(gap*0.2), base_y + int(SS*3)],
                    radius=int(SS*1.5), fill=(120, 130, 150, 255))

# ---- 顶部警示红点 (体现 FAIL/不良) 放在最高柱顶上方 ----
dot_r = int(W * 0.052)
dot_cx = margin_x + bar_w // 2
dot_cy = top_y - int(W * 0.055)
# 外发光
glow = Image.new("RGBA", (W, W), (0, 0, 0, 0))
gld = ImageDraw.Draw(glow)
gld.ellipse([dot_cx - dot_r*2, dot_cy - dot_r*2, dot_cx + dot_r*2, dot_cy + dot_r*2],
            fill=(255, 80, 90, 60))
img = Image.alpha_composite(img, glow)
d = ImageDraw.Draw(img)
d.ellipse([dot_cx - dot_r, dot_cy - dot_r, dot_cx + dot_r, dot_cy + dot_r],
          fill=(255, 76, 87, 255))
# 高光
hl = int(dot_r * 0.35)
d.ellipse([dot_cx - dot_r*0.4 - hl, dot_cy - dot_r*0.4 - hl,
           dot_cx - dot_r*0.4 + hl, dot_cy - dot_r*0.4 + hl],
          fill=(255, 180, 185, 200))

# ---- 缩小回目标尺寸 ----
img = img.resize((SIZE, SIZE), Image.LANCZOS)

# 保存多尺寸 ico
sizes = [16, 24, 32, 48, 64, 128, 256]
img.save("app_icon.ico", format="ICO",
         sizes=[(s, s) for s in sizes])
# 附带一个 png 预览
img.save("app_icon_preview.png")
print("已生成 app_icon.ico 和 app_icon_preview.png")
