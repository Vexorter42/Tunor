"""Writes tutorial/scenes/*.json, the animated pictures of the tutorial.

    python make_scenes.py            writes them next to this file
    python make_scenes.py how        only one

The pictures are drawn in the app's own palette on a 480x270 canvas; the program scales that to
whatever room it has. The text on them is what a person reads first, so it is short.
"""
import os
import sys

from scenekit import Scene

OUT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "scenes")

DISCORD = "#5865F2"
BROWSER = "#FFC857"
STEAM = "#7A8699"


def chip(S, id, x, y, w, h, label, dot=None, fill="panel", stroke="border", tc="text", bold=False):
    """A labelled pill; the coloured dot, when there is one, is a second item called id+'_dot'."""
    S.rect(id, x, y, w, h, r=h / 2 - 1, fill=fill, stroke=stroke, text=label, size=12, tc=tc, bold=bold)
    ids = [id]
    if dot:
        S.circle(id + "_dot", x + 14, y + h / 2, 5, fill=dot)
        ids.append(id + "_dot")
    return ids


def window(S, p, x, y, w, h, title="Tunor", o=1.0):
    """The outline of a program window: a frame, a title strip and three dots. Returns the ids."""
    S.rect(p + "_f", x, y, w, h, r=10, fill="bg", stroke="border", sw=1.5, o=o)
    S.rect(p + "_t", x + 1, y + 1, w - 2, 22, r=9, fill="panel", o=o)
    S.text(p + "_n", x + 38, y + 12, title, size=11, tc="dim", align="l", bold=True, o=o)
    ids = [p + "_f", p + "_t", p + "_n"]
    for i, c in enumerate(("danger", "warn", "accent")):
        S.circle(f"{p}_d{i}", x + 11 + i * 11, y + 12, 3.2, fill=c, o=o)
        ids.append(f"{p}_d{i}")
    return ids


def both(S, ids, fn, *a, **k):
    for i in ids:
        fn(i, *a, **k)


# =============================================================================== 1. how it works

def how():
    S = Scene("how", 13.0)

    # the programs on the left
    apps = [("Discord", 52, DISCORD), ("Браузер", 110, BROWSER), ("Steam", 168, STEAM)]
    for i, (name, y, col) in enumerate(apps):
        ids = chip(S, f"app{i}", 16, y, 104, 32, name, dot=col)
        S.pop(ids[0], 0.2 + 0.25 * i)
        S.show(ids[1], 0.2 + 0.25 * i + 0.15)

    # the rules in the middle
    S.rect("rules", 184, 78, 112, 112, r=12, fill="panel", stroke="accent", sw=2)
    S.text("rules_t", 240, 98, "Правила", size=14, bold=True)
    S.text("rules_s", 240, 114, "Tunor", size=11, tc="dim")
    bars = [(50, "accent"), (38, "accent"), (44, "border"), (30, "border")]
    for i, (bw, col) in enumerate(bars):
        S.rect(f"bar{i}", 212 - 0 + (60 - bw) / 2 - 0, 130 + i * 12, bw, 5, r=2.5, fill=col)
    rules_ids = ["rules", "rules_t", "rules_s"] + [f"bar{i}" for i in range(len(bars))]
    for j in rules_ids:
        S.show(j, 1.0)

    # the two ways out on the right
    S.rect("glowT", 336, 38, 128, 62, r=12, fill="soft", o=0.0)
    S.rect("lane0", 336, 38, 128, 62, r=12, fill="panel", stroke="accent", sw=2, text="Туннель", size=14, tc="accent", bold=True)
    S.rect("glowD", 336, 164, 128, 62, r=12, fill="bluesoft", o=0.0)
    S.rect("lane1", 336, 164, 128, 62, r=12, fill="panel", stroke="blue", sw=2, text="Напрямую", size=14, tc="blue", bold=True)
    S.text("lane0_s", 400, 86, "WARP, VPN", size=10, tc="dim")
    S.text("lane1_s", 400, 212, "без туннеля", size=10, tc="dim")
    S.pop("lane0", 1.5)
    S.pop("lane1", 1.7)
    S.show("lane0_s", 1.9)
    S.show("lane1_s", 2.1)

    # the roads between them
    for i, (name, y, _) in enumerate(apps):
        S.poly(f"in{i}", 0, 0, [[120, y + 16], [184, 134]], stroke="border", sw=2, draw=0.0)
        S.draw(f"in{i}", 1.4 + i * 0.1, 2.0)
    S.poly("out0", 0, 0, [[296, 134], [336, 69]], stroke="border", sw=2, draw=0.0)
    S.poly("out1", 0, 0, [[296, 134], [336, 195]], stroke="border", sw=2, draw=0.0)
    S.draw("out0", 1.7, 2.2)
    S.draw("out1", 1.8, 2.3)

    # what the person reads, one line at a time
    S.caption("c1", "Каждое соединение сначала попадает к правилам", 2.3, 5.5)
    S.caption("c2", "Что есть в правилах — уходит в туннель", 5.8, 8.8)
    S.caption("c3", "Чего нет — идёт напрямую, скорость не страдает", 9.1)

    def packet(n, src_y, start, to_lane, color):
        """A dot goes from a program to the rules, and comes out coloured on the lane it was sent to."""
        a = [(120, src_y + 16), (184, 134), (240, 134)]
        b = [(240, 134), (296, 134), (336, 69 if to_lane == 0 else 195)]
        S.circle(f"p{n}", a[0][0], a[0][1], 5.5, fill="text", o=0.0)
        S.show(f"p{n}", start, 0.15)
        S.path(f"p{n}", start, start + 1.1, a)
        S.hide(f"p{n}", start + 1.1, 0.01)
        S.circle(f"q{n}", b[0][0], b[0][1], 6, fill=color, o=0.0)
        S.show(f"q{n}", start + 1.1, 0.01)
        S.path(f"q{n}", start + 1.1, start + 2.2, b)
        S.hide(f"q{n}", start + 2.2, 0.01)
        glow = "glowT" if to_lane == 0 else "glowD"
        S.to(glow, "o", start + 2.1, start + 2.4, 1.0, "o")
        S.to(glow, "o", start + 2.4, start + 3.1, 0.0, "o")

    packet(0, 52, 2.6, 0, "accent")
    packet(1, 110, 3.6, 0, "accent")
    packet(2, 168, 6.6, 1, "blue")
    return S


# =============================================================================== 2. the config

def config():
    S = Scene("config", 14.0)

    # the bot, in a chat window
    tg = window(S, "tg", 14, 18, 206, 214, "Telegram")
    S.text("tg_bot", 117, 60, "@warp_generator_bot", size=11, tc="blue", bold=True)
    for j in tg + ["tg_bot"]:
        S.show(j, 0.2)

    S.rect("u_b", 86, 76, 124, 26, r=12, fill="soft", stroke="accent", sw=1.2)
    S.text("u_t", 98, 89, "Cloudflare WARP", size=11, align="l", tc="text", n=0.0)
    S.show("u_b", 0.8, 0.2)
    S.type("u_t", 1.0, 1.9)

    S.rect("b_b", 22, 112, 150, 26, r=12, fill="panel", stroke="border", sw=1.2, o=0.0)
    S.text("b_t", 34, 125, "Готово, вот конфиг:", size=11, align="l", tc="text", o=0.0)
    S.show("b_b", 2.5, 0.25)
    S.show("b_t", 2.5, 0.25)

    S.rect("file", 22, 146, 170, 46, r=10, fill="panel", stroke="border", sw=1.2, o=0.0)
    S.rect("file_i", 32, 154, 22, 30, r=4, fill="blue", o=0.0)
    S.text("file_n", 64, 164, "warp.conf", size=12, align="l", bold=True, o=0.0)
    S.text("file_s", 64, 179, "1 КБ", size=10, align="l", tc="dim", o=0.0)
    S.pop("file", 3.2)
    for j in ("file_i", "file_n", "file_s"):
        S.show(j, 3.35)

    # Tunor, to the right
    tn = window(S, "tn", 252, 18, 214, 214, "Tunor")
    S.text("tn_h", 266, 62, "Туннели", size=13, bold=True, align="l")
    S.rect("add", 266, 76, 130, 28, r=8, fill="panel", stroke="accent", sw=1.6, text="Добавить .conf", size=12, tc="accent", bold=True)
    S.rect("row", 266, 120, 186, 52, r=10, fill="panel", stroke="border", sw=1.2)
    S.text("row_n", 280, 137, "WARP", size=13, bold=True, align="l")
    S.text("row_s0", 280, 156, "конфига нет", size=11, tc="dim", align="l")
    S.text("row_s1", 280, 156, "warp.conf добавлен", size=11, tc="accent", align="l", o=0.0)
    S.circle("dot0", 436, 146, 6, fill="border")
    S.circle("dot1", 436, 146, 6, fill="accent", o=0.0)
    S.tick("tick", 436, 146, size=4.5, stroke="ink", sw=2, draw=0.0)
    for j in tn + ["tn_h", "add", "row", "row_n", "row_s0", "dot0"]:
        S.show(j, 0.5)

    S.caption("c1", "1. Бот присылает файл .conf", 3.4, 7.0)
    S.caption("c2", "2. Файл нужно добавить в Tunor", 7.3, 10.6)
    S.caption("c3", "Tunor сам поймёт, WARP это или другой туннель", 10.9)

    # the file travels to the button
    S.rect("fly", 22, 146, 170, 46, r=10, fill="hover", stroke="accent", sw=1.6, text="warp.conf", size=12, tc="text", bold=True, o=0.0)
    S.click(100, 170, 4.9)
    S.show("fly", 5.0, 0.12)
    for j in ("file", "file_i", "file_n", "file_s"):
        S.to(j, "o", 5.0, 5.2, 0.3)
    S.move("fly", 5.1, 6.7, x=268, y=72, ease="io")
    S.resize("fly", 5.1, 6.7, w=126, h=36, ease="io")
    S.hide("fly", 6.75, 0.25)
    S.click(331, 90, 6.8)
    S.to("add", "s", 6.7, 6.85, 0.94)
    S.to("add", "s", 6.85, 7.15, 1.0, "b")

    S.show("row_s1", 7.2, 0.4)
    S.hide("row_s0", 7.2, 0.3)
    S.show("dot1", 7.2, 0.4)
    S.draw("tick", 7.5, 8.0)
    S.show("tick", 7.5, 0.01)
    S.to("row", "s", 7.2, 7.4, 1.04)
    S.to("row", "s", 7.4, 7.8, 1.0, "b")

    S.cursor("cur", 150, 226)
    S.show("cur", 3.8, 0.3)
    S.move("cur", 3.8, 4.7, x=100, y=170, ease="io")
    S.move("cur", 5.1, 6.7, x=325, y=88, ease="io")
    S.hide("cur", 7.3, 0.4)
    return S


# =============================================================================== 3. the rules

def rules():
    S = Scene("rules", 14.5)

    S.rect("top", 298, 20, 168, 130, r=14, fill="panel", stroke="accent", sw=2)
    S.text("top_t", 382, 40, "Через туннель", size=14, bold=True, tc="accent")
    S.rect("bot", 298, 158, 168, 90, r=14, fill="panel", stroke="blue", sw=2)
    S.text("bot_t", 382, 175, "Напрямую", size=14, bold=True, tc="blue")
    for j in ("top", "top_t"):
        S.show(j, 1.3)
    for j in ("bot", "bot_t"):
        S.show(j, 1.6)

    items = [
        ("r0", "youtube.com", "Правила · домен", 46),
        ("r1", "Discord", "Приложения · программа", 100),
        ("r2", "Telegram", "Правила → Каталог · список", 154),
    ]
    for i, (id, label, tag, y) in enumerate(items):
        S.text(id + "_g", 16, y - 9, tag, size=10, tc="dim", align="l", o=0.0)
        S.rect(id, 16, y, 160, 32, r=15, fill="panel", stroke="border", text=label, size=12.5, bold=True)
        S.pop(id, 0.2 + 0.4 * i)
        S.show(id + "_g", 0.4 + 0.4 * i)

    S.caption("c1", "В туннель можно отправить домен, программу или готовый список", 1.8, 6.6, y=260, size=12)

    land = [(52, "r0"), (84, "r1"), (116, "r2")]
    for k, (ly, id) in enumerate(land):
        t = 2.6 + 0.9 * k
        y0 = items[k][3]
        S.hide(id + "_g", t, 0.25)
        S.move(id, t, t + 1.0, x=314, y=ly, ease="io")
        S.resize(id, t, t + 1.0, w=136, h=26, ease="io")
        S.tick(id + "_ok", 440, ly + 13, size=4.5, draw=0.0)
        S.draw(id + "_ok", t + 1.0, t + 1.4)
        S.show(id + "_ok", t + 1.0, 0.01)

    # what is not in the rules
    direct = [("d0", "bank.ru", 100), ("d1", "Windows Update", 154)]
    for i, (id, label, y) in enumerate(direct):
        S.rect(id, 16, y, 160, 32, r=15, fill="panel", stroke="border", text=label, size=12.5, o=0.0, s=0.7)
    S.caption("c2", "Всё остальное идёт напрямую", 7.0, y=260)
    for i, (id, label, y) in enumerate(direct):
        t = 7.2 + 0.4 * i
        S.pop(id, t)
        S.move(id, t + 0.9, t + 1.9, x=314, y=188 + i * 28, ease="io")
        S.resize(id, t + 0.9, t + 1.9, w=136, h=24, ease="io")
    return S


# =============================================================================== 4. starting

def start():
    S = Scene("start", 14.0)

    wn = window(S, "w", 40, 14, 400, 228, "Tunor")
    S.show("w_f", 0.0, 0.01)
    S.text("w_h", 60, 48, "Главная", size=14, bold=True, align="l")

    S.circle("st0", 316, 48, 5, fill="dim")
    S.text("st0_t", 328, 48, "Остановлен", size=12, tc="dim", align="l")
    S.circle("st1", 316, 48, 5, fill="accent", o=0.0)
    S.text("st1_t", 328, 48, "Подключено", size=12, tc="accent", bold=True, align="l", o=0.0)

    S.rect("go0", 60, 66, 168, 36, r=10, fill="accent", text="Запустить", size=14, tc="ink", bold=True)
    S.rect("go1", 60, 66, 168, 36, r=10, fill="panel", stroke="accent", sw=1.6, text="Перезапустить", size=13, tc="accent", bold=True, o=0.0)

    S.text("ck_h", 60, 126, "Проверка соединения", size=12, tc="dim", align="l", bold=True)
    S.rect("ck0", 60, 138, 200, 30, r=9, fill="panel", stroke="border", sw=1.4, text="Проверить соединение", size=12)
    S.rect("ck1", 60, 138, 200, 30, r=9, fill="panel", stroke="border", sw=1.4, text="Проверяю...", size=12, tc="dim", o=0.0)

    for i, (name, where, col, y) in enumerate([("Туннель WARP", "Швеция · ARN", "accent", 180),
                                              ("Напрямую", "твоя страна", "blue", 210)]):
        S.rect(f"rw{i}", 60, y, 360, 26, r=8, fill="input", stroke="border", sw=1.2, o=0.0)
        S.text(f"rw{i}_n", 74, y + 13, name, size=12, bold=True, align="l", tc=col, o=0.0)
        S.text(f"rw{i}_w", 330, y + 13, where, size=12, align="r", tc="text", o=0.0)
        S.tick(f"rw{i}_k", 404, y + 13, size=4.5, stroke=col, draw=0.0)

    S.caption("c1", "1. Нажми «Запустить» — статус станет «Подключено»", 1.0, 5.4, size=12.5)
    S.caption("c2", "2. «Проверить соединение» покажет страну выхода", 5.8, size=12.5)

    # click on Start
    S.click(144, 84, 2.6)
    S.to("go0", "s", 2.6, 2.75, 0.95)
    S.hide("go0", 2.8, 0.2)
    S.show("go1", 2.8, 0.3)
    S.hide("st0", 2.9, 0.25)
    S.hide("st0_t", 2.9, 0.25)
    S.show("st1", 2.9, 0.3)
    S.show("st1_t", 2.9, 0.3)

    # click on Check
    S.click(160, 153, 6.4)
    S.hide("ck0", 6.45, 0.15)
    S.show("ck1", 6.45, 0.15)
    S.hide("ck1", 7.5, 0.2)
    S.show("ck0", 7.5, 0.2)
    for i in range(2):
        t = 7.4 + 0.7 * i
        for suf in ("", "_n", "_w"):
            S.show(f"rw{i}{suf}", t, 0.3)
        S.draw(f"rw{i}_k", t + 0.3, t + 0.7)
        S.show(f"rw{i}_k", t + 0.3, 0.01)

    S.cursor("cur", 380, 236)
    S.show("cur", 0.9, 0.3)
    S.move("cur", 1.0, 2.5, x=140, y=80, ease="io")
    S.move("cur", 3.4, 6.3, x=156, y=150, ease="io")
    S.hide("cur", 8.0, 0.4)
    return S


# =============================================================================== 5. when it does not work

def trouble():
    S = Scene("trouble", 16.0)

    # the site that does not open
    site = window(S, "s", 150, 12, 180, 66, "site.com")
    for j in site:
        S.show(j, 0.0, 0.01)
    S.text("s_msg", 240, 52, "Сайт не открывается", size=12, tc="dim")
    S.cross("s_x", 168, 52, size=6, draw=0.0)
    S.draw("s_x", 0.6, 1.1)
    S.show("s_x", 0.6, 0.01)
    S.tick("s_ok", 168, 52, size=6, draw=0.0)
    S.text("s_msg2", 240, 52, "Открывается", size=12, tc="accent", bold=True, o=0.0)

    S.caption("c0", "Не работает? Идём по порядку", 1.2, 3.6)

    cards = [
        ("a", 14, "1", "Логи", "что пишет движок", "accent"),
        ("b", 168, "2", "Проверка", "какой туннель молчит", "accent"),
        ("c", 322, "3", "Конфликты", "zapret, GoodbyeDPI", "warn"),
    ]
    for key, x, n, title, sub, col in cards:
        S.rect(f"{key}_c", x, 96, 144, 76, r=12, fill="panel", stroke="border", sw=1.5)
        S.rect(f"{key}_hl", x, 96, 144, 76, r=12, stroke=col, sw=2.2, o=0.0)
        S.circle(f"{key}_n", x + 20, 118, 11, fill=col)
        S.text(f"{key}_nn", x + 20, 118, n, size=13, bold=True, tc="ink")
        S.text(f"{key}_t", x + 40, 118, title, size=13, bold=True, align="l")
        S.text(f"{key}_s", x + 14, 148, sub, size=11, tc="dim", align="l")
    for i, (key, *_r) in enumerate(cards):
        t = 3.4 + 2.2 * i
        for suf in ("_c", "_n", "_nn", "_t", "_s"):
            S.pop(f"{key}{suf}", t) if suf == "_c" else S.show(f"{key}{suf}", t + 0.1, 0.3)
        S.to(f"{key}_hl", "o", t + 0.4, t + 0.7, 1.0, "o")
        S.to(f"{key}_hl", "o", t + 2.0, t + 2.4, 0.0, "o")

    S.caption("c1", "Логи и проверка соединения показывают, где обрыв", 4.2, 7.9, size=12.5)

    # zapret next to TUN
    S.rect("z", 120, 196, 90, 30, r=14, fill="panel", stroke="danger", sw=1.8, text="zapret", size=12.5, bold=True, o=0.0)
    S.rect("t", 270, 196, 90, 30, r=14, fill="panel", stroke="accent", sw=1.8, text="TUN", size=12.5, bold=True, tc="accent", o=0.0)
    S.cross("zx", 240, 211, size=7, draw=0.0)
    S.text("zc", 240, 234, "мешают друг другу", size=10.5, tc="danger", o=0.0)
    S.caption("c2", "zapret и GoodbyeDPI мешают режиму TUN — выключи их на время", 8.0, 14.0, size=12)
    S.pop("z", 8.1)
    S.pop("t", 8.4)
    S.draw("zx", 9.0, 9.5)
    S.show("zx", 9.0, 0.01)
    S.show("zc", 9.4, 0.4)

    # off, and the site opens
    S.to("z", "o", 11.0, 11.6, 0.4)
    S.poly("zs", 165, 211, [[-32, 0], [32, 0]], stroke="danger", sw=2.4, draw=0.0)
    S.draw("zs", 11.0, 11.6)
    S.hide("zx", 11.3, 0.3)
    S.hide("zc", 11.3, 0.3)
    S.hide("s_x", 12.0, 0.2)
    S.hide("s_msg", 12.0, 0.2)
    S.show("s_msg2", 12.2, 0.3)
    S.show("s_ok", 12.2, 0.01)
    S.draw("s_ok", 12.2, 12.7)
    return S


# =============================================================================== 6. tray and autostart

def tray():
    S = Scene("tray", 15.0)

    wn = window(S, "w", 90, 12, 300, 176, "Tunor")
    S.text("w_ok", 240, 84, "Подключено", size=18, bold=True, tc="accent")
    S.circle("w_dot", 240, 56, 9, fill="accent")
    for i, wd in enumerate((150, 120, 136)):
        S.rect(f"w_b{i}", 150, 112 + i * 16, wd, 6, r=3, fill="border")
    S.rect("hover", 366, 14, 24, 20, r=6, fill="dangersoft", o=0.0)
    S.cross("w_x", 378, 24, size=4.5, stroke="dim", sw=1.8)
    win_ids = wn + ["w_ok", "w_dot", "w_b0", "w_b1", "w_b2", "hover", "w_x"]

    # the bar along the bottom, with the icon
    S.rect("bar", -2, 232, 484, 40, r=0, fill="panel", stroke="border", sw=1.2)
    for i, x in enumerate((392, 412, 432)):
        S.circle(f"ic{i}", x, 251, 5, fill="border")
    S.circle("tr_off", 456, 251, 7.5, fill="dim", o=0.0)
    S.circle("tr_on", 456, 251, 7.5, fill="accent")
    S.circle("tr_ring", 456, 251, 7.5, stroke="accent", sw=2, o=0.0)
    S.text("tr_hint", 360, 218, "Tunor — подключено", size=11, tc="text", align="r", o=0.0)

    S.caption("c1", "Крестик только прячет окно — туннель продолжает работать", 2.6, 7.0, y=92, size=12.5)
    S.caption("c2", "Выйти совсем — значок Tunor → «Выход»", 8.0, y=92, size=12.5)

    # close the window
    S.cursor("cur", 330, 150)
    S.show("cur", 0.6, 0.3)
    S.move("cur", 0.7, 1.7, x=374, y=24, ease="io")
    S.to("hover", "o", 1.7, 1.9, 1.0, "o")
    S.click(378, 24, 2.3, color="danger")
    for j in win_ids:
        S.hide(j, 2.5, 0.35)

    # the icon stays, and pulses to say so
    S.show("tr_hint", 3.2, 0.4)
    for t in (3.2, 4.8):
        S.to("tr_ring", "o", t, t + 0.01, 0.9)
        S.to("tr_ring", "o", t + 0.01, t + 0.9, 0.0, "o")
        S.to("tr_ring", "s", t, t + 0.9, 2.2, "o")
        S.to("tr_ring", "s", t + 0.9, t + 0.91, 1.0)
    S.hide("tr_hint", 6.6, 0.3)

    # the way out
    S.rect("menu", 342, 120, 124, 98, r=10, fill="panel", stroke="border", sw=1.5, o=0.0)
    labels = ["Показать окно", "Перезапустить", "Выход"]
    for i, lab in enumerate(labels):
        S.rect(f"mi{i}", 348, 126 + i * 30, 112, 26, r=6, fill="hover" if i == 2 else None, o=0.0)
        S.text(f"mt{i}", 360, 139 + i * 30, lab, size=12, align="l", bold=(i == 2),
               tc="danger" if i == 2 else "text", o=0.0)
    S.move("cur", 6.0, 7.2, x=454, y=250, ease="io")
    S.click(456, 251, 7.6, color="dim")
    for j in ["menu"] + [f"mi{i}" for i in range(3)] + [f"mt{i}" for i in range(3)]:
        S.show(j, 7.7, 0.25)
    S.move("cur", 8.4, 9.4, x=398, y=212, ease="io")
    S.click(404, 220, 9.8, color="danger")
    for j in ["menu"] + [f"mi{i}" for i in range(3)] + [f"mt{i}" for i in range(3)]:
        S.hide(j, 10.0, 0.25)
    S.hide("tr_on", 10.3, 0.4)
    S.show("tr_off", 10.3, 0.4)
    S.text("tr_end", 360, 218, "туннель остановлен", size=11, tc="dim", align="r", o=0.0)
    S.show("tr_end", 10.8, 0.4)
    S.hide("cur", 10.6, 0.4)
    return S


# =============================================================================== the way out: done

def done():
    """Not a lesson: the picture on the last screen, a tick that draws itself and a burst of dots."""
    import math
    S = Scene("done", 4.5, w=480, h=150)
    cx, cy = 240, 75
    S.circle("pulse", cx, cy, 50, stroke="accent", sw=2.5, o=0.0)
    S.circle("disc", cx, cy, 50, fill="soft", stroke="accent", sw=3.5, o=0.0)
    S.tick("tick", cx, cy + 2, size=26, sw=7, draw=0.0)
    S.pop("disc", 0.2, 0.6)
    S.draw("tick", 0.8, 1.4)
    S.show("tick", 0.8, 0.01)
    S.to("pulse", "o", 1.3, 1.31, 0.9)
    S.to("pulse", "o", 1.31, 2.4, 0.0, "o")
    S.to("pulse", "s", 1.3, 2.4, 1.9, "o")

    colors = ["accent", "blue", "warn", "accent", "blue", "warn", "accent", "text"]
    for i, col in enumerate(colors):
        a = math.tau * i / len(colors) + 0.3
        x0, y0 = cx + 62 * math.cos(a), cy + 62 * math.sin(a)
        x1, y1 = cx + 118 * math.cos(a), cy + 118 * math.sin(a) * 0.8
        S.circle(f"sp{i}", x0, y0, 3.5, fill=col, o=0.0)
        S.to(f"sp{i}", "o", 1.3, 1.4, 1.0)
        S.move(f"sp{i}", 1.3, 2.7, x=x1, y=y1, ease="o")
        S.to(f"sp{i}", "o", 1.9, 2.7, 0.0, "o")
    return S


SCENES = {"how": how, "config": config, "rules": rules, "start": start, "trouble": trouble, "tray": tray, "done": done}


if __name__ == "__main__":
    wanted = sys.argv[1:] or list(SCENES)
    for name in wanted:
        path, size = SCENES[name]().save(OUT)
        print(f"{name}: {size} B  ->  {path}")
