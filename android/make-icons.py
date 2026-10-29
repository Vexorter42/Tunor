#!/usr/bin/env python3
"""Draws the launcher icon of the Android app from the logo of the desktop one (ui/Assets/app.png).

    python make-icons.py

An Android icon is two layers, a background and a foreground, and the system cuts them to whatever
shape the phone's launcher uses. The logo of the desktop app is a dark rounded tile with a green
sign on it; the sign is taken out of it (everything green) and set on a plain dark background.
"""
import os

from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
SRC = os.path.join(HERE, "..", "ui", "Assets", "app.png")
RES = os.path.join(HERE, "app", "src", "main", "res")
ACCENT = (61, 220, 92)


def sign_only(img):
    """The green sign of the logo on a transparent layer."""
    img = img.convert("RGB")
    out = Image.new("RGBA", img.size, (0, 0, 0, 0))
    src, dst = img.load(), out.load()
    for y in range(img.height):
        for x in range(img.width):
            r, g, b = src[x, y]
            greenness = g - max(r, b)
            a = max(0.0, min(1.0, (greenness - 20) / 100))
            if a > 0:
                dst[x, y] = (*ACCENT, round(a * 255))
    return out


def main():
    logo = Image.open(SRC).convert("RGBA")
    sign = sign_only(logo)
    box = sign.getbbox()
    sign = sign.crop(box)

    # Adaptive icon: 108 dp, of which the middle 66 dp always shows. The sign sits in about 50 dp.
    size = 432
    layer = Image.new("RGBA", (size, size), (0, 0, 0, 0))
    target = round(size * 0.46)
    k = target / max(sign.size)
    resized = sign.resize((round(sign.width * k), round(sign.height * k)), Image.LANCZOS)
    layer.paste(resized, ((size - resized.width) // 2, (size - resized.height) // 2), resized)
    os.makedirs(os.path.join(RES, "drawable-nodpi"), exist_ok=True)
    layer.save(os.path.join(RES, "drawable-nodpi", "ic_launcher_foreground.png"), optimize=True)

    os.makedirs(os.path.join(RES, "mipmap-anydpi-v26"), exist_ok=True)
    with open(os.path.join(RES, "mipmap-anydpi-v26", "ic_launcher.xml"), "w", encoding="utf-8", newline="\n") as f:
        f.write('<?xml version="1.0" encoding="utf-8"?>\n'
                '<adaptive-icon xmlns:android="http://schemas.android.com/apk/res/android">\n'
                '    <background android:drawable="@color/ic_launcher_background"/>\n'
                '    <foreground android:drawable="@drawable/ic_launcher_foreground"/>\n'
                '</adaptive-icon>\n')
    with open(os.path.join(RES, "values", "colors.xml"), "w", encoding="utf-8", newline="\n") as f:
        f.write('<?xml version="1.0" encoding="utf-8"?>\n<resources>\n'
                '    <color name="ic_launcher_background">#1A1E25</color>\n</resources>\n')

    # Before Android 8 there are no layers: a finished square picture is what is used.
    for folder, px in (("mdpi", 48), ("hdpi", 72), ("xhdpi", 96), ("xxhdpi", 144), ("xxxhdpi", 192)):
        d = os.path.join(RES, "mipmap-" + folder)
        os.makedirs(d, exist_ok=True)
        logo.resize((px, px), Image.LANCZOS).save(os.path.join(d, "ic_launcher.png"), optimize=True)
    print("значок нарисован:", RES)


if __name__ == "__main__":
    main()
