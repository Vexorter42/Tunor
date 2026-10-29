#!/usr/bin/env python3
"""Builds the videos described in storyboard.py.

    python build.py render tunor-tiktok [--shots DIR] [--out DIR]     the picture, with captions where the format has them
    python build.py text   tunor-tiktok [--out DIR]                    the script to read, subtitles (.srt), chapters
    python build.py still  tunor-tiktok 12.5 frame.png                 one frame, to look at
    python build.py sheet  tunor-tiktok sheet.png                      a frame from every segment, side by side
    python build.py thumbs [--out DIR]                                 the cover pictures
    python build.py mux    tunor-tiktok voice.wav [--out DIR]          the picture with the author's voice on it
    python build.py fit    tunor-tiktok voice.wav [--out DIR]          the recording decides how long each part is: re-times, renders, adds the voice

Frames are drawn with Pillow (scenerender.py draws the animations), encoded in pieces on every
core at once, and joined without re-encoding. Needs ffmpeg and Pillow; Segoe UI where it is installed.
"""
import argparse
import functools
import json
import math
import os
import re
import subprocess
import sys
import tempfile
import time
from concurrent.futures import ProcessPoolExecutor

from PIL import Image, ImageDraw, ImageFilter

HERE = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.dirname(HERE))      # tutorial/, where scenerender is
sys.path.insert(0, HERE)

import scenerender as R          # noqa: E402
import storyboard as SB          # noqa: E402

FPS = 30
FADE = 0.35                      # seconds of cross-fade where one segment follows another
SIZES = {"tt": (1080, 1920), "yt": (1920, 1080)}
SCENES = os.path.join(os.path.dirname(HERE), "scenes")
ICON = os.path.join(os.path.dirname(os.path.dirname(HERE)), "ui", "Assets", "app.png")
DEFAULT_SHOTS = os.path.join(HERE, "shots")
DEFAULT_OUT = os.path.join(HERE, "out")


def col(token, a=1.0):
    return R.rgba(token, a)


def ease(u):
    u = min(1.0, max(0.0, u))
    return u * u * (3 - 2 * u)


# ================================================================================ timeline

class Timeline:
    def __init__(self, story, durations=None):
        self.story = story
        self.starts, self.durs = [], []
        t = 0.0
        for i, s in enumerate(story.segs):
            d = durations[i] if durations else s.seconds()
            self.starts.append(t)
            self.durs.append(d)
            t += d
        self.total = t

    def at(self, T):
        T = min(max(T, 0.0), self.total - 1e-6)
        for i in range(len(self.starts) - 1, -1, -1):
            if T >= self.starts[i]:
                return i, T - self.starts[i]
        return 0, 0.0


# ================================================================================ text and patches

def wrap(text, font, maxw):
    lines = []
    for para in text.split("\n"):
        words, cur = para.split(" "), ""
        for w in words:
            trial = (cur + " " + w).strip()
            if font.getlength(trial) <= maxw or not cur:
                cur = trial
            else:
                lines.append(cur)
                cur = w
        lines.append(cur)
    return lines


def patch(w, h, draw_fn):
    """A transparent picture of its own to draw on; it can be laid on a frame at any strength."""
    p = Image.new("RGBA", (max(1, int(w)), max(1, int(h))), (0, 0, 0, 0))
    draw_fn(ImageDraw.Draw(p))
    return p


def lay(img, p, x, y, alpha=1.0):
    if alpha <= 0.003:
        return
    if alpha < 1:
        p = p.copy()
        p.putalpha(p.getchannel("A").point(lambda v: round(v * alpha)))
    x, y = int(round(x)), int(round(y))
    x0, y0 = max(0, x), max(0, y)
    x1, y1 = min(img.width, x + p.width), min(img.height, y + p.height)
    if x1 <= x0 or y1 <= y0:
        return
    base = img if img.mode == "RGBA" else None
    crop = p.crop((x0 - x, y0 - y, x1 - x, y1 - y))
    if base is None:
        region = img.crop((x0, y0, x1, y1)).convert("RGBA")
        region.alpha_composite(crop)
        img.paste(region.convert("RGB"), (x0, y0))
    else:
        img.alpha_composite(crop, (x0, y0))


def text_patch(lines, size, color, bold=True, align="c", line_h=1.22, stroke=0, runs=None, maxw=None):
    """Lines of text on a patch of their own. `runs` is a list of (text, color) for one line of mixed colour."""
    f = R.font(size, bold)
    if runs:
        widths = [f.getlength(t) for t, _ in runs]
        w, h = sum(widths), size * line_h
        pad = stroke + 4

        def draw(d):
            x = pad
            for (t, c), rw in zip(runs, widths):
                d.text((x, pad), t, font=f, fill=col(c), stroke_width=stroke, stroke_fill=(0, 0, 0, 255))
                x += rw
        return patch(w + 2 * pad, h + 2 * pad, draw)

    w = max(f.getlength(l) for l in lines) if lines else 1
    lh = size * line_h
    pad = stroke + 4

    def draw(d):
        for i, l in enumerate(lines):
            lw = f.getlength(l)
            x = pad + ((w - lw) / 2 if align == "c" else (w - lw if align == "r" else 0))
            d.text((x, pad + i * lh), l, font=f, fill=col(color) if isinstance(color, str) else color,
                   stroke_width=stroke, stroke_fill=(0, 0, 0, 255))
    return patch(w + 2 * pad, lh * len(lines) + 2 * pad, draw)


def box_patch(lines, size, pad, radius, fill=(14, 17, 22, 205), outline=(58, 64, 74, 255), color="text", maxw=None):
    f = R.font(size, True)
    w = max(f.getlength(l) for l in lines)
    lh = size * 1.25
    W, H = int(w + 2 * pad), int(lh * len(lines) + 2 * pad - size * 0.1)

    def draw(d):
        d.rounded_rectangle([0, 0, W - 1, H - 1], radius=radius, fill=fill, outline=outline, width=2)
        for i, l in enumerate(lines):
            d.text(((W - f.getlength(l)) / 2, pad - size * 0.04 + i * lh), l, font=f, fill=col(color))
    return patch(W, H, draw)


# ================================================================================ the composer

class Composer:
    def __init__(self, story, shots, durations=None):
        self.story = story
        self.shots = shots
        self.tl = Timeline(story, durations)
        self.W, self.H = SIZES[story.fmt]
        self.tt = story.fmt == "tt"
        self._scenes = {}
        self._patches = {}
        self._bg = None

    # ------------------------------------------------------------ cached pieces

    def scene(self, name):
        if name not in self._scenes:
            self._scenes[name] = R.load(os.path.join(SCENES, name + ".json"))
        return self._scenes[name]

    def cached(self, key, make):
        if key not in self._patches:
            self._patches[key] = make()
        return self._patches[key]

    def background(self):
        if self._bg is None:
            top, bot = col("deep")[:3], col("bg")[:3]
            g = Image.new("RGB", (1, self.H))
            for y in range(self.H):
                u = y / (self.H - 1)
                g.putpixel((0, y), tuple(round(top[k] + (bot[k] - top[k]) * u) for k in range(3)))
            self._bg = g.resize((self.W, self.H))
        return self._bg.copy()

    def icon(self, size):
        def make():
            im = Image.open(ICON).convert("RGBA")
            return im.resize((size, size), Image.LANCZOS)
        return self.cached(("icon", size), make)

    # ------------------------------------------------------------ frames

    def frame(self, T):
        i, t = self.tl.at(T)
        img = self.segment(i, t)
        if t < FADE:
            before = self.segment(i - 1, self.tl.durs[i - 1]) if i > 0 else Image.new("RGB", (self.W, self.H), col("deep")[:3])
            img = Image.blend(before, img, ease(t / FADE))
        tail = self.tl.total - T
        if tail < 0.5:
            img = Image.blend(img, Image.new("RGB", (self.W, self.H), col("deep")[:3]), ease(1 - tail / 0.5))
        self.progress(img, T)
        return img

    def segment(self, i, t):
        s = self.story.segs[i]
        d = self.tl.durs[i]
        if s.kind == "scene":
            img = self.scene_frame(s, t, d)
        elif s.kind == "images":
            img = self.images_frame(s, t, d)
        else:
            img = self.card_frame(s, t)
        if self.tt:
            self.vertical_chrome(img, s, i, t)
        elif s.chapter and t < 4.6:
            self.lower_third(img, s.chapter, t)
        return img

    # ------------------------------------------------------------ the animation

    def scene_time(self, s, t, d):
        sc = self.scene(s.scene)
        b = s.b if s.b is not None else sc["dur"]
        part = b - s.a
        if s.loop:
            cycle = part + 1.2
            return s.a + min(t % cycle, part)
        speed = 1.0 if d >= part else min(part / d, 1.5)
        return s.a + min(t * speed, part)

    def scene_frame(self, s, t, d):
        sc = self.scene(s.scene)
        st = self.scene_time(s, t, d)
        if self.tt:
            h = round(self.W * sc["h"] / sc["w"])
            pic = R.render(sc, st, self.W, height=h, view=s.view)
            img = self.background()
            img.paste(pic, (0, self.visual_top()))
            return img
        return R.render(sc, st, self.W, height=self.H, view=s.view)

    def visual_top(self):
        return 430       # the picture and its words sit clear of what the app lays over the top of the screen

    # ------------------------------------------------------------ real screens

    def screen(self, name, width, height):
        def make():
            im = Image.open(os.path.join(self.shots, name)).convert("RGB")
            k = im.width / 940.0                               # the shots are of a 940-wide stand-in for the window
            im = im.crop((round(140 * k), round(17 * k), round(800 * k), round(583 * k)))   # the dialog and nothing around it
            r = min(width / im.width, height / im.height)
            im = im.resize((round(im.width * r), round(im.height * r)), Image.LANCZOS)
            rad = round(im.width * 0.022)
            mask = Image.new("L", im.size, 0)
            ImageDraw.Draw(mask).rounded_rectangle([0, 0, im.width - 1, im.height - 1], radius=rad, fill=255)
            shadow = Image.new("RGBA", (im.width + 160, im.height + 160), (0, 0, 0, 0))
            ImageDraw.Draw(shadow).rounded_rectangle([80, 90, 80 + im.width, 90 + im.height], radius=rad, fill=(0, 0, 0, 170))
            shadow = shadow.filter(ImageFilter.GaussianBlur(26))
            out = shadow
            out.paste(im, (80, 80), mask)
            return out
        return self.cached(("screen", name, width, height), make)

    def images_frame(self, s, t, d):
        img = self.background()
        n = len(s.images)
        share = d / n
        k = min(n - 1, int(t // share))
        local = t - k * share
        areaw, areah = (980, 860) if self.tt else (1500, 940)
        top = 400 if self.tt else (self.H - areah) / 2
        zoom = 1.0 + 0.045 * (t / d)               # a slow push in, so that a still picture is not dead

        def put(name, alpha):
            p = self.screen(name, areaw, areah)
            p = p.resize((round(p.width * zoom), round(p.height * zoom)), Image.BILINEAR)
            lay(img, p, (self.W - p.width) / 2, top + (areah - p.height) / 2, alpha)

        put(s.images[k], 1.0)
        if k > 0 and local < 0.5:                  # the previous picture, going
            put(s.images[k - 1], 1 - ease(local / 0.5))
        return img

    # ------------------------------------------------------------ cards

    def card_frame(self, s, t):
        img = self.background()
        a = ease(t / 0.6)
        dy = (1 - a) * 36
        W, H = self.W, self.H
        big = 124 if self.tt else 170
        mid = 54 if self.tt else 56
        small = 50 if self.tt else 46

        rows = []                                            # (patch, gap above)
        if s.link:
            ic = self.icon(190 if self.tt else 150)
            rows.append((ic, 0))
        title_lines = s.title.split("\n")
        while big > 60 and max(R.font(big, True).getlength(l) for l in title_lines) > W - 120:
            big -= 4                                         # the longest line of the title has to fit the frame
        rows.append((self.cached(("t", s.title, big), lambda: text_patch(title_lines, big, "text", stroke=0)), 36 if s.link else 0))
        if s.sub:
            sub_lines = wrap(s.sub, R.font(mid, False), W - 160)
            rows.append((self.cached(("s", s.sub, mid), lambda: text_patch(sub_lines, mid, "dim", bold=False)), 30))
        if s.link:
            rows.append((self.cached(("l", s.link, small), lambda: text_patch([s.link], small, "accent")), 46))

        total = sum(p.height + g for p, g in rows)
        y = (H - total) / 2 - (0 if self.tt else 10) + dy
        for p, g in rows:
            y += g
            lay(img, p, (W - p.width) / 2, y, a)
            y += p.height
        return img

    # ------------------------------------------------------------ what goes around the picture

    def vertical_chrome(self, img, s, i, t):
        W = self.W
        # the name, small, at the top
        ic = self.icon(70)
        word = self.cached(("brand",), lambda: text_patch(["Tunor"], 46, "text"))
        lay(img, ic, 70, 128)
        lay(img, word, 154, 126)

        if s.kind != "card" and s.title:
            parts = s.title.split(" · ", 1)
            runs = [(parts[0] + " · ", "accent"), (parts[1], "text")] if len(parts) == 2 else [(s.title, "text")]
            tp = self.cached(("title", s.title), lambda: text_patch([""], 76, "text", runs=runs, stroke=0))
            lay(img, tp, (W - tp.width) / 2, 296, ease(t / 0.4))

        if s.line and s.kind != "card":          # a card already says in big letters what its line says
            top = (self.visual_top() + round(W * 270 / 480) + 70) if s.kind == "scene" else 1320
            lines = wrap(s.line, R.font(58, True), 900)
            cp = self.cached(("cap", s.line, 58), lambda: box_patch(lines, 58, 38, 44))
            lay(img, cp, (W - cp.width) / 2, top, ease(t / 0.3))

    def lower_third(self, img, chapter, t):
        a = ease(t / 0.4) * (1 - ease((t - 4.0) / 0.5))

        def make():
            f = R.font(52, True)
            w = int(f.getlength(chapter) + 110)

            def draw(d):
                d.rounded_rectangle([0, 0, w - 1, 108], radius=26, fill=(14, 17, 22, 225), outline=(58, 64, 74, 255), width=2)
                d.rounded_rectangle([20, 22, 28, 86], radius=4, fill=col("accent"))
                d.text((54, 24), chapter, font=f, fill=col("text"))
            return patch(w, 110, draw)
        p = self.cached(("lt", chapter), make)
        lay(img, p, 70 - (1 - ease(t / 0.4)) * 60, 64, a)

    def progress(self, img, T):
        d = ImageDraw.Draw(img)
        if self.tt:
            n = len(self.story.segs)
            gap, w = 12, 0
            w = (self.W - 2 * 70 - gap * (n - 1)) / n
            for i in range(n):
                x0 = 70 + i * (w + gap)
                s, e = self.tl.starts[i], self.tl.starts[i] + self.tl.durs[i]
                fill = 1.0 if T >= e else max(0.0, (T - s) / (e - s)) if T > s else 0.0
                d.rounded_rectangle([x0, 96, x0 + w, 104], radius=4, fill=col("border")[:3])
                if fill > 0:
                    d.rounded_rectangle([x0, 96, x0 + max(8, w * fill), 104], radius=4, fill=col("accent")[:3])
        else:
            u = T / self.tl.total
            d.rectangle([0, self.H - 6, round(self.W * u), self.H], fill=col("accent")[:3])


# ================================================================================ rendering

_C = None


def _init(story_name, shots, durations):
    global _C
    _C = Composer(SB.STORIES[story_name], shots, durations)


def _chunk(args):
    first, last, path, crf = args
    W, H = _C.W, _C.H
    cmd = ["ffmpeg", "-y", "-loglevel", "error", "-f", "rawvideo", "-pix_fmt", "rgb24", "-s", f"{W}x{H}", "-r", str(FPS),
           "-i", "-", "-an", "-c:v", "libx264", "-preset", "medium", "-crf", str(crf), "-pix_fmt", "yuv420p",
           "-x264-params", f"keyint={FPS * 2}:min-keyint={FPS * 2}:scenecut=0", path]
    p = subprocess.Popen(cmd, stdin=subprocess.PIPE)
    last_img, last_key = None, None
    for f in range(first, last):
        img = _C.frame(f / FPS)
        p.stdin.write(img.tobytes())
    p.stdin.close()
    if p.wait() != 0:
        raise RuntimeError("ffmpeg failed for chunk " + path)
    return path


def load_durations(path):
    if not path:
        return None
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def render(name, shots, out, crf=22, workers=10, durations=None, limit=None):
    story = SB.STORIES[name]
    tl = Timeline(story, durations)
    n = int(math.ceil(tl.total * FPS))
    if limit:
        n = min(n, int(limit * FPS))
    os.makedirs(out, exist_ok=True)
    tmp = tempfile.mkdtemp(prefix="tunor-video-")
    size = 6 * FPS
    jobs = [(a, min(n, a + size), os.path.join(tmp, f"chunk{k:04d}.mp4"), crf) for k, a in enumerate(range(0, n, size))]
    t0 = time.time()
    print(f"{name}: {tl.total:.1f} s, {n} кадров, {len(jobs)} кусков, {workers} процессов", flush=True)
    with ProcessPoolExecutor(workers, initializer=_init, initargs=(name, shots, durations)) as pool:
        done = 0
        for _ in pool.map(_chunk, jobs):
            done += 1
            print(f"  кусок {done}/{len(jobs)}  ({time.time() - t0:.0f} с)", flush=True)
    listing = os.path.join(tmp, "list.txt")
    with open(listing, "w", encoding="utf-8") as f:
        for _, _, p, _ in jobs:
            f.write("file '" + p.replace("\\", "/") + "'\n")
    target = os.path.join(out, name + ".mp4")
    subprocess.check_call(["ffmpeg", "-y", "-loglevel", "error", "-f", "concat", "-safe", "0", "-i", listing,
                           "-c", "copy", "-movflags", "+faststart", target])
    for _, _, p, _ in jobs:
        os.remove(p)
    os.remove(listing)
    os.rmdir(tmp)
    print(f"готово: {target}  {os.path.getsize(target) / 1e6:.1f} МБ  ({time.time() - t0:.0f} с)")
    return target


# ================================================================================ text outputs

def stamp(sec, srt=False):
    sec = max(0.0, sec)
    h, m, s = int(sec // 3600), int(sec % 3600 // 60), sec % 60
    if srt:
        ms = int(round((s - int(s)) * 1000))
        return f"{h:02d}:{m:02d}:{int(s):02d},{ms:03d}"
    return f"{int(sec // 60):02d}:{int(sec % 60):02d}"


def pieces(text, maxchars):
    """The line cut at the ends of sentences, then at commas, into parts a subtitle can hold."""
    out = []
    for sent in re.split(r"(?<=[.!?…])\s+", text.strip()):
        if len(sent) <= maxchars:
            out.append(sent)
            continue
        cur = ""
        for part in re.split(r"(?<=,)\s+", sent):
            if cur and len(cur) + 1 + len(part) > maxchars:
                out.append(cur)
                cur = part
            else:
                cur = (cur + " " + part).strip()
        if cur:
            out.append(cur)
    return out


def write_text(name, out, durations=None):
    story = SB.STORIES[name]
    tl = Timeline(story, durations)
    os.makedirs(out, exist_ok=True)

    srt, n = [], 1
    for i, s in enumerate(story.segs):
        if not s.line:
            continue
        start, end = tl.starts[i] + 0.15, tl.starts[i] + tl.durs[i] - 0.25
        ps = pieces(s.line, 78 if story.fmt == "yt" else 56)
        total = sum(len(p) for p in ps)
        t = start
        for p in ps:
            span = (end - start) * len(p) / total
            srt.append(f"{n}\n{stamp(t, True)} --> {stamp(t + span - 0.05, True)}\n{p}\n")
            t += span
            n += 1
    with open(os.path.join(out, name + ".srt"), "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(srt))

    md = [f"# {name}: что говорить\n",
          f"Общая длина {stamp(tl.total)} при темпе {SB.CPS:g} знаков в секунду. Читай по абзацу, между абзацами делай вдох: "
          "видео подстроится под запись (`build.py fit`).\n"]
    for i, s in enumerate(story.segs):
        if s.kind == "card" and not s.line:
            continue
        what = {"scene": f"анимация «{s.scene}»", "images": "экраны обучения", "card": "заставка"}[s.kind]
        md.append(f"## {stamp(tl.starts[i])} · {s.chapter or s.title.replace(chr(10), ' ') or what}\n")
        md.append(f"*на экране: {what}, {tl.durs[i]:.0f} с*\n")
        md.append(s.line + "\n")
    with open(os.path.join(out, name + "-script.md"), "w", encoding="utf-8", newline="\n") as f:
        f.write("\n".join(md))

    chapters = [(stamp(tl.starts[i]), s.chapter) for i, s in enumerate(story.segs) if s.chapter]
    if chapters:
        with open(os.path.join(out, name + "-chapters.txt"), "w", encoding="utf-8", newline="\n") as f:
            f.write("\n".join(f"{t} {c}" for t, c in chapters) + "\n")
    print(f"текст: {out}  ({len(srt)} строк субтитров)")


# ================================================================================ stills

def still(name, shots, T, path, durations=None):
    Composer(SB.STORIES[name], shots, durations).frame(T).save(path)


def sheet(name, shots, path, durations=None, per_row=3, width=520):
    c = Composer(SB.STORIES[name], shots, durations)
    frames = []
    for i, d in enumerate(c.tl.durs):
        T = c.tl.starts[i] + min(d * 0.72, d - 0.2)
        frames.append((T, c.frame(T)))
    w = width
    h = round(frames[0][1].height * w / frames[0][1].width)
    rows = math.ceil(len(frames) / per_row)
    out = Image.new("RGB", (per_row * (w + 8) + 8, rows * (h + 24) + 8), (40, 40, 48))
    d = ImageDraw.Draw(out)
    for k, (T, fr) in enumerate(frames):
        x, y = 8 + (k % per_row) * (w + 8), 8 + (k // per_row) * (h + 24)
        out.paste(fr.resize((w, h), Image.LANCZOS), (x, y + 16))
        d.text((x, y), f"{k}  t={T:.1f}", fill=(210, 210, 210))
    out.save(path)


def thumbs(out, shots):
    os.makedirs(out, exist_ok=True)
    # wide: the cover of the long video
    sc = R.load(os.path.join(SCENES, "how.json"))
    W, H = 1280, 720
    img = Image.new("RGB", (W, H), col("deep")[:3])
    pic = R.render(sc, 8.6, 760, height=430)
    img.paste(pic, (W - 760 - 40, 140))
    d = ImageDraw.Draw(img)
    ic = Image.open(ICON).convert("RGBA").resize((120, 120), Image.LANCZOS)
    img.paste(ic, (60, 70), ic)
    for k, (txt, size, color) in enumerate([("Tunor", 150, "text"), ("настройка", 84, "accent"), ("за 5 минут", 84, "text")]):
        d.text((60, 190 + [0, 170, 262][k]), txt, font=R.font(size, True), fill=col(color)[:3])
    d.text((60, 640), "бесплатно · открытый код", font=R.font(40, False), fill=col("dim")[:3])
    img.save(os.path.join(out, "youtube-cover.png"))

    # tall: the cover of the short one
    W, H = 1080, 1920
    img = Image.new("RGB", (W, H), col("deep")[:3])
    pic = R.render(sc, 8.6, 1080, height=608)
    img.paste(pic, (0, 760))
    d = ImageDraw.Draw(img)
    lines = ["Не открываются", "сайты?"]
    for k, l in enumerate(lines):
        w = R.font(112, True).getlength(l)
        d.text(((W - w) / 2, 270 + k * 150), l, font=R.font(112, True), fill=col("text")[:3])
    sub = "Tunor решает за тебя"
    w = R.font(60, False).getlength(sub)
    d.text(((W - w) / 2, 1480), sub, font=R.font(60, True), fill=col("accent")[:3])
    img.save(os.path.join(out, "tiktok-cover.png"))
    print("обложки:", out)


# ================================================================================ the voice

def probe(path):
    out = subprocess.check_output(["ffprobe", "-v", "error", "-show_entries", "format=duration", "-of", "csv=p=0", path])
    return float(out.decode().strip())


def mux(name, voice, out, shots=None):
    video = os.path.join(out, name + ".mp4")
    final = os.path.join(out, name + "-voice.mp4")
    vd, ad = probe(video), probe(voice)
    # The picture is held on its last frame if the voice runs on, and the voice is padded with silence if it stops early.
    subprocess.check_call(["ffmpeg", "-y", "-loglevel", "error", "-i", video, "-i", voice,
                           "-filter_complex", f"[0:v]tpad=stop_mode=clone:stop_duration={max(0.0, ad - vd) + 0.1:.2f}[v];"
                                              "[1:a]loudnorm=I=-16:TP=-1.5:LRA=11,apad[a]",
                           "-map", "[v]", "-map", "[a]", "-c:v", "libx264", "-preset", "medium", "-crf", "22", "-pix_fmt", "yuv420p",
                           "-c:a", "aac", "-b:a", "160k", "-t", f"{max(vd, ad) + 0.2:.2f}", "-movflags", "+faststart", final])
    print(f"готово: {final}  {os.path.getsize(final) / 1e6:.1f} МБ")


def speech_intervals(voice, noise="-35dB", gap=0.5):
    """Where a recording is being spoken: what is left when the silences longer than `gap` are taken out."""
    r = subprocess.run(["ffmpeg", "-hide_banner", "-i", voice, "-af", f"silencedetect=noise={noise}:d={gap}", "-f", "null", "-"],
                       stderr=subprocess.PIPE, text=True, encoding="utf-8", errors="replace")
    total = probe(voice)
    starts = [float(m) for m in re.findall(r"silence_start: ([0-9.]+)", r.stderr)]
    ends = [float(m) for m in re.findall(r"silence_end: ([0-9.]+)", r.stderr)]
    out, cursor = [], 0.0
    for k, st in enumerate(starts):
        if st - cursor > 0.05:
            out.append((cursor, st))
        cursor = ends[k] if k < len(ends) else total
    if total - cursor > 0.05:
        out.append((cursor, total))
    return out


def durations_for(name, voice):
    """How long each segment has to be so that each of its lines is said inside it.

    The author reads the lines one after another with a breath between them; every stretch of speech
    is one line. If the recording has more stretches than there are lines, the ones with the shortest
    pause between them are taken for one (a breath in the middle of a sentence)."""
    story = SB.STORIES[name]
    lined = [i for i, s in enumerate(story.segs) if s.line]
    spans = speech_intervals(voice)
    while len(spans) > len(lined):
        gaps = [spans[k + 1][0] - spans[k][1] for k in range(len(spans) - 1)]
        k = gaps.index(min(gaps))
        spans[k:k + 2] = [(spans[k][0], spans[k + 1][1])]
    if len(spans) < len(lined):
        raise SystemExit(f"В записи {len(spans)} кусков речи, а строк в сценарии {len(lined)}. "
                         "Между строками нужна пауза хотя бы в секунду: прочитай по абзацу с вдохом между ними.")

    durs = [s.seconds() for s in story.segs]
    bounds = [0.0]
    for k in range(1, len(spans)):
        # the picture changes a moment before the next line begins, not in the middle of the breath
        bounds.append(max(spans[k - 1][1], spans[k][0] - 0.3))
    bounds.append(spans[-1][1] + 0.9)
    for k, i in enumerate(lined):
        durs[i] = round(bounds[k + 1] - bounds[k], 3)
    return durs


def fit(name, voice, out, shots, crf=22, workers=10):
    durs = durations_for(name, voice)
    os.makedirs(out, exist_ok=True)
    path = os.path.join(out, name + "-durations.json")
    with open(path, "w", encoding="utf-8") as f:
        json.dump(durs, f)
    print("длительности по записи:", ", ".join(f"{d:.1f}" for d in durs))
    write_text(name, out, durs)
    render(name, shots, out, crf, workers, durs)
    mux(name, voice, out)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("cmd", choices=["render", "text", "still", "sheet", "thumbs", "mux", "fit"])
    ap.add_argument("args", nargs="*")
    ap.add_argument("--shots", default=DEFAULT_SHOTS)
    ap.add_argument("--out", default=DEFAULT_OUT)
    ap.add_argument("--crf", type=int, default=22)
    ap.add_argument("--workers", type=int, default=10)
    ap.add_argument("--durations", help="JSON: how long each segment is, when the real recording decides it")
    ap.add_argument("--limit", type=float, help="only the first N seconds (to try it)")
    a = ap.parse_args()
    dur = load_durations(a.durations)

    if a.cmd == "render":
        render(a.args[0], a.shots, a.out, a.crf, a.workers, dur, a.limit)
    elif a.cmd == "text":
        write_text(a.args[0], a.out, dur)
    elif a.cmd == "still":
        still(a.args[0], a.shots, float(a.args[1]), a.args[2], dur)
    elif a.cmd == "sheet":
        sheet(a.args[0], a.shots, a.args[1], dur)
    elif a.cmd == "thumbs":
        thumbs(a.out, a.shots)
    elif a.cmd == "mux":
        mux(a.args[0], a.args[1], a.out)
    elif a.cmd == "fit":
        fit(a.args[0], a.args[1], a.out, a.shots, a.crf, a.workers)


if __name__ == "__main__":
    main()
