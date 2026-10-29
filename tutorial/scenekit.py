"""A small language for writing the tutorial's scenes.

A scene is shapes plus, for each shape, the moments at which a property has a value. Writing
those keys by hand is how a picture ends up wrong by a quarter of a second in forty places, so
the scenes are written as what happens (this appears, that moves there, this is typed) and this
turns it into the JSON that tutorial/scenes holds and that the program plays.

Every call that changes something over time takes its moments in seconds and must come in time
order for the item it touches; the language holds a value until the next thing happens to it.
"""
import json
import math


class Scene:
    def __init__(self, name, dur, w=480, h=270):
        self.name, self.dur, self.w, self.h = name, dur, w, h
        self.items = []
        self._by_id = {}
        self.keys = {}          # (id, prop) -> [[t, v, ease], ...]
        self._clicks = 0

    # ------------------------------------------------------------ things

    def _add(self, d):
        assert d["id"] not in self._by_id, "two items called " + d["id"]
        self._by_id[d["id"]] = d
        self.items.append(d)
        return d["id"]

    def rect(self, id, x, y, w, h, r=8, fill=None, stroke=None, sw=1.5, text=None, size=13,
             tc="text", bold=False, o=1.0, s=1.0):
        d = dict(id=id, k="rect", x=x, y=y, w=w, h=h, r=r, fill=fill, stroke=stroke, sw=sw,
                 text=text, size=size, tc=tc, bold=bold, o=o, s=s)
        return self._add(d)

    def circle(self, id, x, y, r, fill=None, stroke=None, sw=1.5, o=1.0, s=1.0):
        return self._add(dict(id=id, k="circle", x=x, y=y, r=r, fill=fill, stroke=stroke, sw=sw, o=o, s=s))

    def poly(self, id, x, y, pts, stroke=None, fill=None, sw=2.0, closed=False, o=1.0, s=1.0, draw=1.0):
        return self._add(dict(id=id, k="poly", x=x, y=y, pts=pts, stroke=stroke, fill=fill, sw=sw,
                              closed=closed, o=o, s=s, draw=draw))

    def text(self, id, x, y, text, size=13, tc="text", bold=False, align="c", o=1.0, s=1.0, n=1.0):
        return self._add(dict(id=id, k="text", x=x, y=y, text=text, size=size, tc=tc, bold=bold,
                              align=align, o=o, s=s, n=n))

    def arrow(self, id, pts, stroke="dim", sw=2.0, head=6, o=1.0, draw=1.0):
        """A line with a head; the head is part of the same line so that it is drawn on with it."""
        (x0, y0), (x1, y1) = pts[-2], pts[-1]
        a = math.atan2(y1 - y0, x1 - x0)
        h1 = (x1 - head * math.cos(a - 0.5), y1 - head * math.sin(a - 0.5))
        h2 = (x1 - head * math.cos(a + 0.5), y1 - head * math.sin(a + 0.5))
        line = list(pts) + [h1, (x1, y1), h2]
        return self.poly(id, 0, 0, [[round(px, 1), round(py, 1)] for px, py in line], stroke=stroke, sw=sw, o=o, draw=draw)

    def tick(self, id, cx, cy, size=7, stroke="accent", sw=2.4, o=1.0, draw=1.0):
        pts = [[-size * 0.8, 0], [-size * 0.2, size * 0.6], [size * 0.9, -size * 0.6]]
        return self.poly(id, cx, cy, pts, stroke=stroke, sw=sw, o=o, draw=draw)

    def cross(self, id, cx, cy, size=6, stroke="danger", sw=2.4, o=1.0, draw=1.0):
        # One line that goes there and back, so that it is drawn as one stroke.
        pts = [[-size, -size], [size, size], [0, 0], [size, -size], [-size, size]]
        return self.poly(id, cx, cy, pts, stroke=stroke, sw=sw, o=o, draw=draw)

    def cursor(self, id, x, y, o=0.0):
        pts = [[0, 0], [0, 16], [4.2, 12.2], [7, 18.5], [9.6, 17.3], [6.8, 11], [12, 11]]
        return self.poly(id, x, y, pts, stroke="deep", fill="text", sw=1.2, closed=True, o=o)

    # ------------------------------------------------------------ time

    def _base(self, id, prop):
        it = self._by_id[id]
        if prop in ("w", "h", "x", "y"):
            return it[prop]
        return it.get({"o": "o", "s": "s", "draw": "draw", "n": "n"}[prop], 1.0)

    def _set_base(self, id, prop, v):
        self._by_id[id][{"o": "o", "s": "s", "draw": "draw", "n": "n", "x": "x", "y": "y", "w": "w", "h": "h"}[prop]] = v

    def _track(self, id, prop):
        return self.keys.setdefault((id, prop), [])

    def key(self, id, prop, t, v, ease=None):
        k = self._track(id, prop)
        if k and t < k[-1][0] - 1e-9:
            raise ValueError(f"{self.name}: {id}.{prop} at {t} comes before {k[-1][0]}")
        if k and abs(k[-1][0] - t) < 1e-9:
            k[-1] = [t, v, ease]
        else:
            k.append([t, v, ease])

    def _hold(self, id, prop, t):
        k = self._track(id, prop)
        if not k:
            self.key(id, prop, t, self._base(id, prop))
        elif k[-1][0] < t - 1e-9:
            self.key(id, prop, t, k[-1][1])

    def _value(self, id, prop):
        k = self.keys.get((id, prop))
        return k[-1][1] if k else self._base(id, prop)

    def to(self, id, prop, t0, t1, v, ease=None):
        """prop goes to v between t0 and t1."""
        self._hold(id, prop, t0)
        self.key(id, prop, t1, v, ease)

    def at(self, id, prop, t, v):
        """prop jumps to v at time t."""
        self._hold(id, prop, t)
        self.key(id, prop, t + 0.001, v)

    # ------------------------------------------------------------ what happens

    def show(self, id, t, d=0.35):
        if not self.keys.get((id, "o")):
            self._set_base(id, "o", 0.0)
        self.to(id, "o", t, t + d, 1.0, "o")

    def hide(self, id, t, d=0.3):
        self.to(id, "o", t, t + d, 0.0, "o")

    def pop(self, id, t, d=0.45):
        """Appears with a small bounce."""
        if not self.keys.get((id, "o")):
            self._set_base(id, "o", 0.0)
        if not self.keys.get((id, "s")):
            self._set_base(id, "s", 0.6)
        self.to(id, "o", t, t + d * 0.55, 1.0, "o")
        self.to(id, "s", t, t + d, 1.0, "b")

    def move(self, id, t0, t1, x=None, y=None, ease=None):
        if x is not None:
            self.to(id, "x", t0, t1, x, ease)
        if y is not None:
            self.to(id, "y", t0, t1, y, ease)

    def path(self, id, t0, t1, pts, ease="l"):
        """Travels along a line of points, at an even speed."""
        seg = [math.dist(pts[i], pts[i + 1]) for i in range(len(pts) - 1)]
        total = sum(seg) or 1
        t = t0
        self._hold(id, "x", t0)
        self._hold(id, "y", t0)
        self.key(id, "x", t0, pts[0][0])
        self.key(id, "y", t0, pts[0][1])
        for i, ln in enumerate(seg):
            t += (t1 - t0) * ln / total
            self.key(id, "x", t, pts[i + 1][0], ease)
            self.key(id, "y", t, pts[i + 1][1], ease)

    def scale(self, id, t0, t1, v, ease=None):
        self.to(id, "s", t0, t1, v, ease)

    def resize(self, id, t0, t1, w=None, h=None, ease=None):
        if w is not None:
            self.to(id, "w", t0, t1, w, ease)
        if h is not None:
            self.to(id, "h", t0, t1, h, ease)

    def draw(self, id, t0, t1, ease="o"):
        if not self.keys.get((id, "draw")):
            self._set_base(id, "draw", 0.0)
        self.to(id, "draw", t0, t1, 1.0, ease)

    def type(self, id, t0, t1):
        if not self.keys.get((id, "n")):
            self._set_base(id, "n", 0.0)
        self.to(id, "n", t0, t1, 1.0, "l")

    def caption(self, id, text, t_in, t_out=None, y=252, size=13, tc="text"):
        """A line of explanation along the bottom; it is replaced by the next one."""
        self.text(id, self.w / 2, y, text, size=size, tc=tc, bold=True, o=0.0)
        self.show(id, t_in, 0.4)
        if t_out is not None:
            self.hide(id, t_out, 0.3)

    def click(self, x, y, t, color="accent"):
        """A ring that opens where the pointer pressed."""
        self._clicks += 1
        cid = f"click{self._clicks}"
        self.circle(cid, x, y, 14, stroke=color, sw=2.2, o=0.0, s=0.3)
        self.to(cid, "o", t, t + 0.001, 0.9)
        self.to(cid, "o", t + 0.001, t + 0.5, 0.0, "o")
        self.to(cid, "s", t, t + 0.5, 1.5, "o")

    # ------------------------------------------------------------ output

    def build(self):
        items = []
        for it in self.items:
            d = {}
            for k, v in it.items():
                if v is None or v is False:
                    continue
                if k in ("o", "s", "draw", "n") and v == 1.0:
                    continue
                if k == "sw" and v == 1.5:
                    continue
                if k == "tc" and v == "text":
                    continue
                if k == "size" and v == 13:
                    continue
                if k == "align" and v == "c":
                    continue
                if k == "r" and v == 0:
                    continue
                d[k] = round(v, 2) if isinstance(v, float) else v
            items.append(d)

        tracks = []
        for (id, prop), ks in self.keys.items():
            clean = []
            for t, v, e in ks:
                row = [round(t, 3), round(v, 2)]
                if e not in (None, "io"):
                    row.append(e)
                clean.append(row)
            tracks.append({"i": id, "p": prop, "k": clean})

        return {"w": self.w, "h": self.h, "dur": self.dur, "items": items, "tracks": tracks}

    def save(self, folder):
        import os
        data = self.build()
        os.makedirs(folder, exist_ok=True)
        path = os.path.join(folder, self.name + ".json")
        with open(path, "w", encoding="utf-8", newline="\n") as f:
            json.dump(data, f, ensure_ascii=False, separators=(",", ":"))
            f.write("\n")
        return path, len(open(path, "rb").read())
