"""Generates Assets/app.ico and Assets/app.png — the Nyx mark.

The mark: traffic leaving a closed ring. The ring is broken where the arrow exits,
which is what keeps it from reading as a "forbidden" sign.

Run it after changing anything here:  python make_icon.py     (needs Pillow)

Every size is drawn on its own rather than downscaled from one master, because
strokes that look right at 256px vanish at 16px. Stroke weight therefore grows as
the icon shrinks, and the gap in the ring widens so the arrow stays readable.
"""
import math
import os
from PIL import Image, ImageDraw

SIZES = (16, 24, 32, 48, 64, 128, 256)
SS = 8                                   # supersampling factor

BG_TOP = (42, 48, 58)                    # matches Styles/Theme.xaml
BG_BOT = (20, 23, 28)
ACCENT = (61, 220, 92)
ACCENT2 = (91, 232, 119)
HERE = os.path.dirname(os.path.abspath(__file__))

EXIT_DEG = 315.0                         # arrow leaves towards the upper right


def weights(size):
    """Proportions per size. Small icons need fat strokes and a wide gap."""
    if size <= 16:
        return dict(ring=0.165, shaft=0.165, head=0.42, gap=104, reach=1.34)
    if size <= 24:
        return dict(ring=0.140, shaft=0.140, head=0.38, gap=94, reach=1.34)
    if size <= 32:
        return dict(ring=0.120, shaft=0.122, head=0.34, gap=86, reach=1.33)
    if size <= 48:
        return dict(ring=0.102, shaft=0.104, head=0.31, gap=80, reach=1.32)
    return dict(ring=0.090, shaft=0.092, head=0.29, gap=74, reach=1.32)


def render(size):
    W = size * SS
    w = weights(size)

    # --- background plate: vertical gradient clipped to a rounded square
    grad = Image.new("RGB", (1, W))
    for y in range(W):
        t = y / (W - 1)
        grad.putpixel((0, y), tuple(int(BG_TOP[i] + (BG_BOT[i] - BG_TOP[i]) * t) for i in range(3)))
    plate = Image.new("L", (W, W), 0)
    ImageDraw.Draw(plate).rounded_rectangle([0, 0, W - 1, W - 1], radius=int(W * 0.24), fill=255)

    img = Image.new("RGBA", (W, W), (0, 0, 0, 0))
    img.paste(grad.resize((W, W)), (0, 0), plate)

    def paint(m, colour):
        img.paste(Image.new("RGBA", (W, W), colour + (255,)), (0, 0), m)

    cx, cy = W * 0.485, W * 0.520
    r = W * 0.285

    # --- ring, with the gap centred on the direction the arrow takes
    half = w["gap"] / 2
    m = Image.new("L", (W, W), 0)
    ImageDraw.Draw(m).arc([cx - r, cy - r, cx + r, cy + r],
                          start=EXIT_DEG + half, end=EXIT_DEG - half + 360,
                          fill=255, width=max(1, int(W * w["ring"])))
    paint(m, ACCENT)

    # --- arrow: shaft and head share one direction vector, so they join seamlessly
    ux, uy = math.cos(math.radians(EXIT_DEG)), math.sin(math.radians(EXIT_DEG))
    px, py = -uy, ux                                     # perpendicular

    tip = (cx + ux * r * w["reach"], cy + uy * r * w["reach"])
    head_len = r * w["head"] * 2.0
    base = (tip[0] - ux * head_len, tip[1] - uy * head_len)
    half_base = r * w["head"]
    tail = (cx - ux * r * 0.34, cy - uy * r * 0.34)      # starts inside the ring

    a = Image.new("L", (W, W), 0)
    d = ImageDraw.Draw(a)
    shaft = max(1, int(W * w["shaft"]))
    # Stop the shaft inside the head so the two never show a seam.
    shaft_end = (base[0] + ux * head_len * 0.45, base[1] + uy * head_len * 0.45)
    d.line([tail, shaft_end], fill=255, width=shaft)
    d.polygon([tip,
               (base[0] + px * half_base, base[1] + py * half_base),
               (base[0] - px * half_base, base[1] - py * half_base)], fill=255)
    paint(a, ACCENT2)

    return img.resize((size, size), Image.LANCZOS)


def main():
    frames = [render(s) for s in SIZES]
    master = frames[-1]
    master.save(os.path.join(HERE, "app.png"))

    # Pillow embeds every size; each frame is stored PNG-compressed.
    master.save(os.path.join(HERE, "app.ico"), format="ICO", sizes=[(s, s) for s in SIZES])
    print("wrote app.png (256) and app.ico:", ", ".join(f"{s}x{s}" for s in SIZES))


main()
