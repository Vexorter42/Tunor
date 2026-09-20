"""Packs the published osx-arm64 build into Nyx.app and a .tar.gz.

Why tar.gz and not zip: a zip written on Windows carries no POSIX permissions, so the
extracted binaries would arrive without the executable bit and macOS would refuse to
launch them. tar stores the mode explicitly, and it is set here.

The .icns is assembled by hand — it is just a magic header followed by typed PNG
chunks — because there is no macOS tooling on this machine.
"""
import io
import os
import shutil
import struct
import subprocess
import tarfile

from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
PROJ = os.path.join(HERE, "Nyx.Mac")
PUB = os.path.join(PROJ, "bin", "Release", "net8.0", "osx-arm64", "publish")
ENGINE = os.path.join(HERE, "engine", "sing-box-1.14.1-lx.8-darwin-arm64", "sing-box")
ICON_PNG = os.path.join(HERE, "..", "ui", "Assets", "app.png")
OUT = os.path.join(HERE, "out")
VERSION = "0.1.0"

# ICNS type codes paired with the pixel size each one expects.
ICNS_TYPES = [
    (b"icp4", 16), (b"icp5", 32), (b"ic07", 128),
    (b"ic08", 256), (b"ic09", 512), (b"ic13", 256), (b"ic14", 512),
]


def build_icns(src_png, dest):
    master = Image.open(src_png).convert("RGBA")
    chunks = []
    for code, size in ICNS_TYPES:
        buf = io.BytesIO()
        master.resize((size, size), Image.LANCZOS).save(buf, format="PNG")
        data = buf.getvalue()
        chunks.append(code + struct.pack(">I", len(data) + 8) + data)
    body = b"".join(chunks)
    with open(dest, "wb") as f:
        f.write(b"icns" + struct.pack(">I", len(body) + 8) + body)
    return len(body) + 8


INFO_PLIST = """<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>Nyx</string>
  <key>CFBundleDisplayName</key><string>Nyx</string>
  <key>CFBundleIdentifier</key><string>com.vexorter.nyx</string>
  <key>CFBundleVersion</key><string>%(v)s</string>
  <key>CFBundleShortVersionString</key><string>%(v)s</string>
  <key>CFBundleExecutable</key><string>Nyx</string>
  <key>CFBundleIconFile</key><string>AppIcon</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>LSMinimumSystemVersion</key><string>12.0</string>
  <key>NSHighResolutionCapable</key><true/>
  <key>LSApplicationCategoryType</key><string>public.app-category.utilities</string>
</dict>
</plist>
"""


def main():
    if not os.path.isdir(PUB):
        raise SystemExit("no publish output — run: dotnet publish -c Release -r osx-arm64 "
                         "--self-contained true")
    if not os.path.isfile(ENGINE):
        raise SystemExit("engine missing: " + ENGINE)

    app = os.path.join(OUT, "Nyx.app")
    if os.path.exists(OUT):
        shutil.rmtree(OUT)
    macos = os.path.join(app, "Contents", "MacOS")
    res = os.path.join(app, "Contents", "Resources")
    os.makedirs(macos)
    os.makedirs(res)

    for name in os.listdir(PUB):
        s = os.path.join(PUB, name)
        shutil.copy2(s, os.path.join(macos, name)) if os.path.isfile(s) \
            else shutil.copytree(s, os.path.join(macos, name))

    shutil.copy2(ENGINE, os.path.join(res, "sing-box"))
    size = build_icns(ICON_PNG, os.path.join(res, "AppIcon.icns"))
    with open(os.path.join(app, "Contents", "Info.plist"), "w", encoding="utf-8") as f:
        f.write(INFO_PLIST % {"v": VERSION})

    # --- tar.gz with real POSIX modes
    archive = os.path.join(OUT, "Nyx-%s-macos-arm64.tar.gz" % VERSION)
    executables = {"Contents/MacOS/Nyx", "Contents/Resources/sing-box"}

    def fix(ti):
        rel = ti.name[len("Nyx.app/"):] if ti.name.startswith("Nyx.app/") else ti.name
        if ti.isdir():
            ti.mode = 0o755
        elif rel in executables or rel.endswith((".dylib", ".so")):
            ti.mode = 0o755
        else:
            ti.mode = 0o644
        ti.uid = ti.gid = 0
        ti.uname = ti.gname = ""
        return ti

    with tarfile.open(archive, "w:gz") as tar:
        tar.add(app, arcname="Nyx.app", filter=fix)

    mb = os.path.getsize(archive) / 1024 / 1024
    print("Nyx.app   :", app)
    print("AppIcon   :", size, "bytes")
    print("archive   : %s  (%.1f MB)" % (os.path.basename(archive), mb))

    with tarfile.open(archive) as tar:
        for m in tar.getmembers():
            if m.name.endswith(("MacOS/Nyx", "Resources/sing-box")):
                print("  %s  mode=%s" % (m.name, oct(m.mode)))


main()
