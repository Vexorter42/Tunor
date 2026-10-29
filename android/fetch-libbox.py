#!/usr/bin/env python3
"""Fetches the engine library the app is built on: sing-box-lx's libbox for Android.

    python fetch-libbox.py [--mirror https://gh-proxy.com/]

It is 120 MB and not kept in the repository. The file is checked against the SHA-256 that is pinned
below (and that the release publishes in its SHA256SUMS), so a mirror may carry it, but cannot
change it. Changing VERSION and SHA256 is how the engine of the Android app is updated.
"""
import argparse
import hashlib
import os
import sys
import urllib.request

VERSION = "1.14.4-lx.25"
SHA256 = "442b4830ea01966dc47a35735633fd7925eb46866eaa94a82788a130ef1719d4"
URL = f"https://github.com/Leadaxe/sing-box-lx/releases/download/v{VERSION}/libbox-{VERSION}.aar"
TARGET = os.path.join(os.path.dirname(os.path.abspath(__file__)), "app", "libs", "libbox.aar")


def sha256(path):
    h = hashlib.sha256()
    with open(path, "rb") as f:
        for block in iter(lambda: f.read(1 << 20), b""):
            h.update(block)
    return h.hexdigest()


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--mirror", default="", help="a prefix in front of the GitHub address, for where GitHub is slow")
    a = ap.parse_args()

    if os.path.exists(TARGET) and sha256(TARGET) == SHA256:
        print("libbox", VERSION, "уже на месте")
        return 0

    os.makedirs(os.path.dirname(TARGET), exist_ok=True)
    part = TARGET + ".part"
    url = a.mirror + URL
    print("качаю", url)
    with urllib.request.urlopen(url) as r, open(part, "wb") as f:
        done, total = 0, int(r.headers.get("Content-Length") or 0)
        while True:
            block = r.read(1 << 20)
            if not block:
                break
            f.write(block)
            done += len(block)
            if total:
                print(f"\r  {done / 1e6:6.1f} / {total / 1e6:.1f} МБ", end="", flush=True)
    print()
    got = sha256(part)
    if got != SHA256:
        os.remove(part)
        print("КОНТРОЛЬНАЯ СУММА НЕ СОВПАЛА:", got, "ждали", SHA256, file=sys.stderr)
        return 1
    os.replace(part, TARGET)
    print("готово:", TARGET)
    return 0


if __name__ == "__main__":
    sys.exit(main())
