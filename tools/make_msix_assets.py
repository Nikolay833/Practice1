"""Generate the MSIX tile/logo PNGs in packaging/Assets from the same blue water drop as tools/make_icon.py.

Writes Square44x44Logo, Square150x150Logo, StoreLogo and Wide310x150Logo, each at scale 100 (no suffix) and scale 200.
The manifest refers to the unsuffixed names; Windows picks the best match.

Usage: python3 tools/make_msix_assets.py   (requires Pillow)
"""
import math
from pathlib import Path
from PIL import Image, ImageDraw, ImageFilter

S = 1024  # supersampled canvas
OUT = Path(__file__).resolve().parent.parent / "packaging" / "Assets"

DARK = (15, 18, 26, 255)
EDGE = (42, 48, 64, 255)


def draw_drop(img):
    """The drop from make_icon.py (circle + tangent triangle, gradient, highlight) on a 1024 canvas."""
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

    grad = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    gp = grad.load()
    for y in range(S):
        t = min(max((y - tip[1]) / (cy + r - tip[1]), 0), 1)
        c = (int(96 + (37 - 96) * t), int(165 + (99 - 165) * t), int(250 + (235 - 250) * t), 255)
        for x in range(S):
            gp[x, y] = c
    img.paste(grad, (0, 0), drop.split()[3])

    hl = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    hd = ImageDraw.Draw(hl)
    hd.ellipse((cx - 150, cy - 40, cx - 70, cy + 120), fill=(255, 255, 255, 120))
    hl = hl.filter(ImageFilter.GaussianBlur(10))
    return Image.alpha_composite(img, hl)


def square_icon():
    """Dark rounded square with the drop: the app icon."""
    img = Image.new("RGBA", (S, S), (0, 0, 0, 0))
    d = ImageDraw.Draw(img)
    d.rounded_rectangle((16, 16, S - 16, S - 16), radius=220, fill=DARK)
    d.rounded_rectangle((16, 16, S - 16, S - 16), radius=220, outline=EDGE, width=10)
    return draw_drop(img)


def drop_only():
    """The drop alone on a transparent canvas, cropped to its bounds."""
    img = draw_drop(Image.new("RGBA", (S, S), (0, 0, 0, 0)))
    return img.crop(img.getbbox())


def wide_tile(width, height, drop):
    """Dark tile with the drop centred; the whole tile is filled (a tile has no rounded corners to show)."""
    tile = Image.new("RGBA", (width, height), DARK)
    target_h = int(height * 0.66)
    target_w = round(drop.width * target_h / drop.height)
    small = drop.resize((target_w, target_h), Image.LANCZOS)
    tile.alpha_composite(small, ((width - target_w) // 2, (height - target_h) // 2))
    return tile


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    icon = square_icon()
    drop = drop_only()
    for scale in (1, 2):
        suffix = "" if scale == 1 else ".scale-200"
        for name, size in (("Square44x44Logo", 44), ("Square150x150Logo", 150), ("StoreLogo", 50)):
            px = size * scale
            path = OUT / f"{name}{suffix}.png"
            icon.resize((px, px), Image.LANCZOS).save(path, format="PNG")
            print("wrote", path)
        path = OUT / f"Wide310x150Logo{suffix}.png"
        wide_tile(310 * scale, 150 * scale, drop).save(path, format="PNG")
        print("wrote", path)


if __name__ == "__main__":
    main()
