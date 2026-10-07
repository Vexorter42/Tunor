"""Draws the backdrop the disk image window shows behind the two icons.

A .dmg opens as a Finder window, and by default that window is a plain list with no
hint of what to do with it. The backdrop drawn here says it: the app on the left, the
Applications folder on the right, an arrow between them, in the app's own colours.

    python make-dmg-background.py            -> build-mac/dmg-background.png (and @2x)

Two files, because Finder picks the @2x one on a Retina screen and every Mac this ships
to has one. The window is 620x400 points; the icons sit where make-dmg.sh puts them, so
the arrow is drawn to match and the two must be changed together.
"""
import os
import sys

from PIL import Image, ImageDraw, ImageFont

HERE = os.path.dirname(os.path.abspath(__file__))
OUT = os.path.join(HERE, "build-mac")

W, H = 620, 400                      # window size in points, as make-dmg.sh sets it
APP_AT, APPS_AT = 160, 460           # icon centres, matching make-dmg.sh
ICON_Y = 190                         # icon centre line

# Light, and not by taste: Finder draws the icon labels under the two icons in black,
# whatever the system appearance is set to, and on the app's own dark background they
# disappeared into it. The accent stays, so the window is still recognisably Tunor.
BG = (244, 246, 249)
PANEL = (255, 255, 255)
ACCENT = (34, 170, 68)               # darker than the app's #3DDC5C, to carry on white
INK = (26, 30, 37)                   # BgColor, now used for text
DIM = (108, 116, 130)


def font(size, bold=False):
    """A system font at this size, or the default if none of them are here."""
    for name in (["seguisb.ttf", "segoeuib.ttf"] if bold else ["segoeui.ttf"]) + \
                ["Helvetica.ttc", "DejaVuSans.ttf", "arial.ttf"]:
        try:
            return ImageFont.truetype(name, size)
        except OSError:
            continue
    return ImageFont.load_default()


def centred(d, text, top_y, f, fill, scale):
    """Text centred across the window, placed by its top edge."""
    left, top, right, _ = d.textbbox((0, 0), text, font=f)
    d.text(((W * scale - (right - left)) / 2 - left, top_y - top), text, font=f, fill=fill)


def arrow(d, x0, x1, y, scale):
    """A flat arrow from one icon to the other, thick enough to read at a glance."""
    shaft = 7 * scale
    head = 26 * scale
    x0, x1, y = x0 * scale, x1 * scale, y * scale
    d.rounded_rectangle([x0, y - shaft / 2, x1 - head, y + shaft / 2],
                        radius=shaft / 2, fill=ACCENT)
    d.polygon([(x1, y), (x1 - head, y - head * 0.62), (x1 - head, y + head * 0.62)],
              fill=ACCENT)


def draw(scale):
    img = Image.new("RGB", (W * scale, H * scale), BG)
    d = ImageDraw.Draw(img)

    # A panel behind the icons and, crucially, behind their labels: the black text has
    # to land on something lighter than itself.
    d.rounded_rectangle([40 * scale, 110 * scale, (W - 40) * scale, 285 * scale],
                        radius=18 * scale, fill=PANEL,
                        outline=(224, 228, 234), width=max(1, scale))

    # The headline, the one instruction, and the thing everyone trips over on an
    # unsigned build — which is the whole reason this window has words on it at all.
    centred(d, "Tunor", 42 * scale, font(26 * scale, bold=True), INK, scale)
    centred(d, "Перетащи приложение в папку Applications",
            80 * scale, font(14 * scale), DIM, scale)

    arrow(d, APP_AT + 68, APPS_AT - 68, ICON_Y, scale)

    centred(d, "Первый запуск: правый клик по значку → «Открыть»",
            332 * scale, font(12 * scale), DIM, scale)
    return img


def main():
    os.makedirs(OUT, exist_ok=True)
    one = os.path.join(OUT, "dmg-background.png")
    two = os.path.join(OUT, "dmg-background@2x.png")
    draw(1).save(one)
    draw(2).save(two)
    print(f"  {one}")
    print(f"  {two}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
