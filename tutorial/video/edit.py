"""Cuts a read-aloud recording into the voice track of a video: the best take of every sentence.

A person reading a script into a microphone stops and starts again. A paragraph comes out as three
attempts, the last of them in two halves. Cutting by pauses cannot tell the attempts from the
paragraph, so this reads what was said (transcribe.py, offline), finds each sentence of the script
in it, takes the take that matches best — the later one if two match as well — and joins them, in
the order of the script, with the room's own quiet between them.

What it returns is the cleaned recording and how long each line of the script takes in it, which is
what the picture is then timed to.
"""
import difflib
import re
import subprocess
import wave

import numpy as np

SR = 48000
GAP = 0.8               # what skipping a word costs the alignment
LEAD = 0.30             # the picture changes this long before the voice begins
TAIL = 0.25
WITHIN = 0.22           # quiet between two pieces of one line


def norm(word):
    return re.sub(r"[^0-9a-zа-я]", "", word.lower().replace("ё", "е"))


def sentences(line):
    return [p for p in re.split(r"(?<=[.!?…])\s+", line.strip()) if p]


def script_words(sentence):
    return [w for w in (norm(x) for x in sentence.split()) if w]


def sim(a, b):
    """How well two words agree: a recogniser hears 'тонер' for 'Tunor', so equal is not the only match."""
    if a == b:
        return 2.0
    r = difflib.SequenceMatcher(None, a, b).ratio()
    if r >= 0.78:
        return 2.0 * r
    if r >= 0.55:
        return 0.4
    return -1.0


def best_window(want, heard):
    """Where in the recording a sentence was said best: (score, first word index, last word index, matched).

    Local alignment of the sentence against the recognised words. Of several windows that score
    about the same, the last one: a later take is the one the speaker meant to keep."""
    n, m = len(want), len(heard)
    H = [[0.0] * (m + 1) for _ in range(n + 1)]
    P = [[(0, 0)] * (m + 1) for _ in range(n + 1)]
    top = 0.0
    for i in range(1, n + 1):
        for j in range(1, m + 1):
            s = sim(want[i - 1], heard[j - 1])
            opts = ((H[i - 1][j - 1] + s, (i - 1, j - 1)), (H[i - 1][j] - GAP, (i - 1, j)), (H[i][j - 1] - GAP, (i, j - 1)))
            h, p = max(opts, key=lambda o: o[0])
            if h <= 0:
                H[i][j], P[i][j] = 0.0, (0, 0)
            else:
                H[i][j], P[i][j] = h, p
            top = max(top, H[i][j])
    if top <= 0:
        return 0.0, 0, 0, 0
    end_i, end_j = max(((i, j) for i in range(1, n + 1) for j in range(1, m + 1) if H[i][j] >= top - 0.6),
                       key=lambda c: (c[1], c[0]))
    i, j, matched = end_i, end_j, 0
    first = end_j
    while i > 0 and j > 0 and H[i][j] > 0:
        pi, pj = P[i][j]
        if pi == i - 1 and pj == j - 1 and sim(want[i - 1], heard[j - 1]) > 0.3:
            matched += 1
            first = j
        i, j = pi, pj
    return H[end_i][end_j], first - 1, end_j - 1, matched


def plan(lines, words):
    """For every line of the script, the stretches of the recording that make it up, in order.

    Returns (per line: list of (first word, last word) runs, a report of how well each sentence was found)."""
    heard = [norm(w["w"]) for w in words]
    found, report = [], []
    for li, line in enumerate(lines):
        for si, sent in enumerate(sentences(line)):
            want = script_words(sent)
            if not want:
                continue
            score, a, b, matched = best_window(want, heard)
            cover = matched / len(want)
            found.append((li, a, b))
            report.append((li, si, sent, words[a]["s"] if words else 0.0, words[b]["e"] if words else 0.0, cover))
    runs = [[] for _ in lines]
    for li, a, b in found:
        r = runs[li]
        # the next sentence follows straight on in the recording: it is one stretch, not two
        if r and a - r[-1][1] <= 2 and a > r[-1][0]:
            r[-1] = (r[-1][0], b)
        else:
            r.append((a, b))
    return runs, report


# ------------------------------------------------------------------ audio

def decode(path):
    raw = subprocess.run(["ffmpeg", "-v", "error", "-i", path, "-vn", "-ac", "1", "-ar", str(SR), "-f", "f32le", "-"],
                         stdout=subprocess.PIPE, check=True).stdout
    return np.frombuffer(raw, dtype=np.float32).copy()


def room_tone(x, words, seconds=1.2):
    """The quietest stretch of the recording with nobody talking in it: what 'nothing' sounds like in that room."""
    win = int(seconds * SR)
    frame = SR // 50
    c = np.concatenate([[0.0], np.cumsum(x.astype(np.float64) ** 2)])           # running sum: a moving average without the cost of a convolution
    energy = np.sqrt(np.maximum(0.0, (c[frame:] - c[:-frame]) / frame))
    energy = np.concatenate([energy, np.full(frame, energy[-1] if len(energy) else 0.0)])
    busy = np.zeros(len(x), dtype=bool)
    for w in words:
        busy[max(0, int((w["s"] - 0.15) * SR)):int((w["e"] + 0.15) * SR)] = True
    best, where = None, 0
    for a in range(0, max(1, len(x) - win), SR // 10):
        if busy[a:a + win].any():
            continue
        e = float(energy[a:a + win].mean())
        if best is None or e < best:
            best, where = e, a
    if best is None:                                              # no gap with nobody in it: the quietest stretch of any kind
        where = int(np.argmin([energy[a:a + win].mean() for a in range(0, max(1, len(x) - win), SR // 10)]) * (SR // 10))
    return x[where:where + win]


def tone(room, seconds):
    n = int(seconds * SR)
    if n <= 0:
        return np.zeros(0, dtype=np.float32)
    out, fade = [], int(0.02 * SR)
    got = 0
    while got < n:
        piece = room.copy()
        piece[:fade] *= np.linspace(0, 1, fade)
        piece[-fade:] *= np.linspace(1, 0, fade)
        out.append(piece)
        got += len(piece) - fade
    cat = out[0]
    for p in out[1:]:                                             # overlap the ends so that the join does not click
        cat = np.concatenate([cat[:-fade], cat[-fade:] + p[:fade], p[fade:]])
    return cat[:n]


def cut(x, words, a, b):
    """The audio of words a..b, with the breath before and the end of the last word, but none of the neighbours."""
    lo = words[a - 1]["e"] if a > 0 else 0.0
    hi = words[b + 1]["s"] if b + 1 < len(words) else len(x) / SR
    t0, t1 = words[a]["s"], words[b]["e"]
    start = min(max(t0 - 0.18, lo + 0.02), t0 - 0.06)
    end = max(min(t1 + 0.22, hi - 0.02), t1 + 0.08)
    seg = x[int(max(0, start) * SR):int(min(len(x) / SR, end) * SR)].copy()
    fade = int(0.015 * SR)
    if len(seg) > 2 * fade:
        seg[:fade] *= np.linspace(0, 1, fade)
        seg[-fade:] *= np.linspace(1, 0, fade)
    return seg


def assemble(audio, words, runs, out_wav, first_extra=0.3, last_extra=0.65):
    """Writes the cleaned voice track; returns how many seconds each line of it takes, lead and tail included."""
    x = decode(audio)
    room = room_tone(x, words)
    blocks = []
    for k, line_runs in enumerate(runs):
        parts = [tone(room, LEAD + (first_extra if k == 0 else 0.0))]
        for n, (a, b) in enumerate(line_runs):
            if n:
                parts.append(tone(room, WITHIN))
            parts.append(cut(x, words, a, b))
        parts.append(tone(room, TAIL + (last_extra if k == len(runs) - 1 else 0.0)))
        blocks.append(np.concatenate(parts))
    y = np.concatenate(blocks)
    peak = float(np.max(np.abs(y))) or 1.0
    if peak > 0.98:
        y = y * (0.98 / peak)
    with wave.open(out_wav, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(SR)
        w.writeframes((np.clip(y, -1, 1) * 32767).astype("<i2").tobytes())
    return [len(b) / SR for b in blocks]
