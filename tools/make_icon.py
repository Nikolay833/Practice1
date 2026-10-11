"""Generate src/Dropshelf/Assets/app.ico: blue water drop on a dark rounded square.

Usage: python3 tools/make_icon.py   (requires Pillow)
"""
import math
from pathlib import Path
from PIL import Image, ImageDraw, ImageFilter

S = 1024  # supersampled canvas
out = Path(__file__).resolve().parent.parent / "src" / "Dropshelf" / "Assets" / "app.ico"

img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
d = ImageDraw.Draw(img)

# Dark rounded square with a faint inner border.
d.rounded_rectangle((16, 16, S - 16, S - 16), radius=220, fill=(15, 18, 26, 255))
d.rounded_rectangle((16, 16, S - 16, S - 16), radius=220, outline=(42, 48, 64, 255), width=10)

# Water drop: circle + tangent triangle to a tip.
cx, cy, r = 512, 610, 235
tip = (512, 175)
dist = cy - tip[1]
alpha = math.asin(r / dist)
length = math.sqrt(dist * dist - r * r)
left = (tip[0] - length * math.sin(alpha), tip[1] + length * math.cos(alpha))
right = (tip[0] + length * math.sin(alpha), tip[1] + length * math.cos(alpha))

drop = Image.new("RGBA", (S, S), (0, 0, 0, 0))
dd = ImageDraw.Draw(drop)
blue = (59, 130, 246, 255)
dd.polygon([tip, left, right], fill=blue)
dd.ellipse((cx - r, cy - r, cx + r, cy + r), fill=blue)

# Soft vertical gradient: lighter top, deeper bottom.
grad = Image.new("RGBA", (S, S), (0, 0, 0, 0))
gp = grad.load()
for y in range(S):
    t = min(max((y - tip[1]) / (cy + r - tip[1]), 0), 1)
    c = (int(96 + (37 - 96) * t), int(165 + (99 - 165) * t), int(250 + (235 - 250) * t), 255)
    for x in range(S):
        gp[x, y] = c
mask = drop.split()[3]
img.paste(grad, (0, 0), mask)

# Highlight.
hl = Image.new("RGBA", (S, S), (0, 0, 0, 0))
hd = ImageDraw.Draw(hl)
hd.ellipse((cx - 150, cy - 40, cx - 70, cy + 120), fill=(255, 255, 255, 120))
hl = hl.filter(ImageFilter.GaussianBlur(10))
img = Image.alpha_composite(img, hl)

sizes = [(16, 16), (32, 32), (48, 48), (256, 256)]
img.resize((256, 256), Image.LANCZOS).save(out, format="ICO", sizes=sizes)
print("wrote", out)
