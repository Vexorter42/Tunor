"""Draws the tutorial's scenes (tutorial/scenes/*.json) into pictures.

The program itself draws a scene with WPF or Avalonia (ui/Services/SceneModel.cs works out
what is where at time t). This is the same arithmetic written a second time for the one place
that has no UI to ask: turning the scenes into frames for a video, and looking at a scene while
it is being written.

    python scenerender.py sheet scenes/how.json out.png [t1,t2,...]    contact sheet of a scene
    python scenerender.py frame scenes/how.json 3.5 out.png [width]    one frame

Needs Pillow. Text is set in Segoe UI where it is installed (any Windows), DejaVu Sans elsewhere.
"""
import json
import math
import os
import sys

from PIL import Image, ImageDraw, ImageFont

COLORS = {
    "bg": 0xFF1A1E25, "deep": 0xFF11141A, "panel": 0xFF242932, "input": 0xFF1F242B,
    "hover": 0xFF2E343E, "border": 0xFF3A404A, "text": 0xFFF4F6F7, "dim": 0xFF959DA9,
    "accent": 0xFF3DDC5C, "soft": 0x383DDC5C, "ink": 0xFF06120A, "danger": 0xFFFF6B6B,
    "dangersoft": 0x38FF6B6B, "warn": 0xFFFFC857, "blue": 0xFF59A5FF, "bluesoft": 0x3859A5FF,
}

_FONT_DIRS = [r"C:\Windows\Fonts", "/usr/share/fonts/truetype/dejavu", "/Library/Fonts", "/System/Library/Fonts/Supplemental"]
_FONT_FILES = {False: ["segoeui.ttf", "DejaVuSans.ttf", "Arial.ttf"], True: ["segoeuib.ttf", "DejaVuSans-Bold.ttf", "Arial Bold.ttf"]}
_font_cache = {}


def font(size, bold=False):
    key = (round(size * 4), bold)
    if key not in _font_cache:
        for d in _FONT_DIRS:
            for f in _FONT_FILES[bold]:
                p = os.path.join(d, f)
                if os.path.exists(p):
                    _font_cache[key] = ImageFont.truetype(p, max(1, round(size)))
                    break
            if key in _font_cache:
                break
        else:
            _font_cache[key] = ImageFont.load_default()
    return _font_cache[key]


def rgba(token, opacity=1.0):
    if token in (None, "", "none"):
        return None
    v = COLORS[token] if token in COLORS else (int(token[1:], 16) | (0xFF000000 if len(token) == 7 else 0))
    a = (v >> 24) & 0xFF
    return ((v >> 16) & 0xFF, (v >> 8) & 0xFF, v & 0xFF, round(a * opacity))


# ---------------------------------------------------------------------------- evaluation

def _ease(e, u):
    u = min(1.0, max(0.0, u))
    if e == "l":
        return u
    if e == "i":
        return u ** 3
    if e == "o":
        return 1 - (1 - u) ** 3
    if e == "h":
        return 0.0
    if e == "b":
        c1 = 1.70158
        c3 = c1 + 1
        m = u - 1
        return 1 + c3 * m ** 3 + c1 * m ** 2
    return 4 * u ** 3 if u < 0.5 else 1 - (-2 * u + 2) ** 3 / 2


def _sample(tr, t):
    k = tr["k"]
    if t <= k[0][0]:
        return k[0][1]
    if t >= k[-1][0]:
        return k[-1][1]
    i = 0
    while i < len(k) - 2 and t >= k[i + 1][0]:
        i += 1
    span = k[i + 1][0] - k[i][0]
    if span <= 0:
        return k[i + 1][1]
    e = k[i][2] if len(k[i]) > 2 else "io"
    u = _ease("x" if e == "io" else e, (t - k[i][0]) / span)
    return k[i][1] + (k[i + 1][1] - k[i][1]) * u


def _partial(pts, fraction):
    lengths = [math.dist(pts[i], pts[i + 1]) for i in range(len(pts) - 1)]
    want = sum(lengths) * fraction
    out = [pts[0]]
    if want <= 0:
        return out
    done = 0.0
    for i, ln in enumerate(lengths):
        if done + ln <= want:
            out.append(pts[i + 1])
            done += ln
            continue
        u = 0 if ln <= 0 else (want - done) / ln
        out.append((pts[i][0] + (pts[i + 1][0] - pts[i][0]) * u, pts[i][1] + (pts[i + 1][1] - pts[i][1]) * u))
        break
    return out


def load(path):
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def states(scene, t):
    """The scene's items at time t, in drawing order, each with its final geometry."""
    ids = {it["id"]: i for i, it in enumerate(scene["items"])}
    cur = []
    for it in scene["items"]:
        cur.append({"x": it.get("x", 0), "y": it.get("y", 0), "w": it.get("w", 0), "h": it.get("h", 0),
                    "o": it.get("o", 1), "s": it.get("s", 1), "draw": it.get("draw", 1), "n": it.get("n", 1)})
    for tr in scene.get("tracks", []):
        if tr["k"]:
            cur[ids[tr["i"]]][tr["p"]] = _sample(tr, t)

    out = []
    for it, c in zip(scene["items"], cur):
        k = c["s"]
        st = {"item": it, "o": min(1.0, max(0.0, c["o"]))}
        kind = it["k"]
        if kind == "rect":
            cx, cy = c["x"] + c["w"] / 2, c["y"] + c["h"] / 2
            w, h = c["w"] * k, c["h"] * k
            st.update(x=cx - w / 2, y=cy - h / 2, w=w, h=h, r=it.get("r", 0) * k,
                      size=it.get("size", 13) * k, text=_typed(it.get("text", ""), c["n"]))
        elif kind == "circle":
            st.update(x=c["x"], y=c["y"], r=it.get("r", 0) * k)
        elif kind == "text":
            st.update(x=c["x"], y=c["y"], size=it.get("size", 13) * k, text=_typed(it.get("text", ""), c["n"]))
        else:
            pts = [(c["x"] + px * k, c["y"] + py * k) for px, py in it["pts"]]
            if not it.get("closed") and c["draw"] < 1:
                pts = _partial(pts, min(1.0, max(0.0, c["draw"])))
            st.update(pts=pts)
        out.append(st)
    return out


def _typed(text, fraction):
    if fraction >= 1 or not text:
        return text
    return text[: max(0, math.floor(min(1.0, max(0.0, fraction)) * len(text)))]


# ---------------------------------------------------------------------------- drawing

def render(scene, t, width=960, ss=2, background="deep", height=None):
    """One frame, width pixels wide, the scene's own proportions unless height is given
    (then the scene is centred, for a video frame of another shape)."""
    scale = width / scene["w"]
    W = width
    H = height or round(scene["h"] * scale)
    off_y = (H - scene["h"] * scale) / 2
    img = Image.new("RGBA", (W, H), rgba(background))
    for st in states(scene, t):
        if st["o"] <= 0.004:
            continue
        _paint(img, st, scale, off_y, ss)
    return img.convert("RGB")


def _paint(img, st, scale, off_y, ss):
    it = st["item"]
    kind = it["k"]
    sw = it.get("sw", 1.5)
    pad = 6 * scale + sw * scale

    if kind == "text":
        _paint_text(img, st, scale, off_y, ss)
        return

    # Work out the box this item covers, draw it supersampled on a layer of its own, shrink, mix in.
    if kind == "rect":
        x0, y0, x1, y1 = st["x"], st["y"], st["x"] + st["w"], st["y"] + st["h"]
    elif kind == "circle":
        x0, y0, x1, y1 = st["x"] - st["r"], st["y"] - st["r"], st["x"] + st["r"], st["y"] + st["r"]
    else:
        xs = [p[0] for p in st["pts"]]
        ys = [p[1] for p in st["pts"]]
        x0, y0, x1, y1 = min(xs), min(ys), max(xs), max(ys)

    bx0 = math.floor(x0 * scale - pad)
    by0 = math.floor(y0 * scale + off_y - pad)
    bw = math.ceil((x1 - x0) * scale + 2 * pad) + 1
    bh = math.ceil((y1 - y0) * scale + 2 * pad) + 1
    layer = Image.new("RGBA", (bw * ss, bh * ss), (0, 0, 0, 0))
    d = ImageDraw.Draw(layer)

    def px(x, y):
        return ((x * scale - bx0) * ss, (y * scale + off_y - by0) * ss)

    fill = rgba(it.get("fill"))
    stroke = rgba(it.get("stroke"))
    wpx = sw * scale * ss

    if kind == "rect":
        a, b = px(st["x"], st["y"]), px(st["x"] + st["w"], st["y"] + st["h"])
        r = min(st["r"] * scale * ss, (b[0] - a[0]) / 2, (b[1] - a[1]) / 2)
        d.rounded_rectangle([a, b], radius=max(0, r), fill=fill, outline=stroke, width=round(wpx) if stroke else 0)
    elif kind == "circle":
        a, b = px(st["x"] - st["r"], st["y"] - st["r"]), px(st["x"] + st["r"], st["y"] + st["r"])
        d.ellipse([a, b], fill=fill, outline=stroke, width=round(wpx) if stroke else 0)
    else:
        pts = [px(*p) for p in st["pts"]]
        if it.get("closed"):
            if fill:
                d.polygon(pts, fill=fill)
            if stroke:
                d.line(pts + [pts[0]], fill=stroke, width=round(wpx), joint="curve")
        elif stroke and len(pts) > 1:
            d.line(pts, fill=stroke, width=round(wpx), joint="curve")
            r = wpx / 2
            for p in (pts[0], pts[-1]):          # round caps
                d.ellipse([p[0] - r, p[1] - r, p[0] + r, p[1] + r], fill=stroke)

    if kind == "rect" and st.get("text"):
        f = font(st["size"] * scale * ss, it.get("bold", False))
        a, b = px(st["x"], st["y"]), px(st["x"] + st["w"], st["y"] + st["h"])
        d.text(((a[0] + b[0]) / 2, (a[1] + b[1]) / 2), st["text"], font=f, fill=rgba(it.get("tc", "text")), anchor="mm")

    layer = layer.resize((bw, bh), Image.LANCZOS)
    if st["o"] < 1:
        alpha = layer.getchannel("A").point(lambda v: round(v * st["o"]))
        layer.putalpha(alpha)
    img.alpha_composite(layer, (bx0, by0)) if (bx0 >= 0 and by0 >= 0 and bx0 + bw <= img.width and by0 + bh <= img.height) \
        else _composite_clipped(img, layer, bx0, by0)


def _composite_clipped(img, layer, x, y):
    # alpha_composite refuses a layer that hangs over the edge; paste with the layer as its own mask does not.
    base = Image.new("RGBA", img.size, (0, 0, 0, 0))
    base.paste(layer, (x, y))
    img.alpha_composite(base)


def _paint_text(img, st, scale, off_y, ss):
    it = st["item"]
    text = st.get("text") or ""
    if not text:
        return
    f = font(st["size"] * scale * ss, it.get("bold", False))
    col = rgba(it.get("tc", "text"), st["o"])
    align = it.get("align", "c")
    anchor = {"c": "mm", "l": "lm", "r": "rm"}[align]
    # Supersampled on a layer of its own, like the shapes.
    lines = text.split("\n")
    lh = st["size"] * scale * 1.35
    total = lh * len(lines)
    wmax = max(f.getlength(ln) for ln in lines) / ss
    pad = 6 * scale
    bw = math.ceil(wmax + 2 * pad) + 1
    bh = math.ceil(total + 2 * pad) + 1
    ax = st["x"] * scale
    ay = st["y"] * scale + off_y
    if align == "c":
        bx0 = math.floor(ax - wmax / 2 - pad)
    elif align == "l":
        bx0 = math.floor(ax - pad)
    else:
        bx0 = math.floor(ax - wmax - pad)
    by0 = math.floor(ay - total / 2 - pad)
    layer = Image.new("RGBA", (bw * ss, bh * ss), (0, 0, 0, 0))
    d = ImageDraw.Draw(layer)
    for i, ln in enumerate(lines):
        y = (ay - total / 2 + lh * (i + 0.5) - by0) * ss
        x = (ax - bx0) * ss
        d.text((x, y), ln, font=f, fill=col, anchor=anchor)
    layer = layer.resize((bw, bh), Image.LANCZOS)
    _composite_clipped(img, layer, bx0, by0)


# ---------------------------------------------------------------------------- command line

def sheet(path, out, times=None, cols=3, width=560):
    scene = load(path)
    dur = scene["dur"]
    ts = times or [round(dur * i / 11, 2) for i in range(12)]
    frames = [render(scene, t, width) for t in ts]
    w, h = frames[0].size
    rows = math.ceil(len(frames) / cols)
    gap = 6
    img = Image.new("RGB", (cols * w + (cols + 1) * gap, rows * (h + 18) + gap), (40, 40, 48))
    d = ImageDraw.Draw(img)
    for i, (t, fr) in enumerate(zip(ts, frames)):
        x = gap + (i % cols) * (w + gap)
        y = gap + (i // cols) * (h + 18)
        img.paste(fr, (x, y + 14))
        d.text((x + 2, y), f"t={t}", fill=(200, 200, 200))
    img.save(out)


if __name__ == "__main__":
    a = sys.argv[1:]
    if len(a) >= 3 and a[0] == "sheet":
        sheet(a[1], a[2], [float(x) for x in a[3].split(",")] if len(a) > 3 else None)
    elif len(a) >= 4 and a[0] == "frame":
        render(load(a[1]), float(a[2]), int(a[4]) if len(a) > 4 else 960).save(a[3])
    else:
        print(__doc__)
