"""Assembles Tunor.app out of a macOS publish, from any machine.

A .app is not a file format — it is a folder laid out the way macOS expects, so it can be
built here and unpacked there. What it cannot carry across is the executable bit, which
Windows has no notion of; the result therefore leaves as a .tar.gz, which records
permissions, rather than a .zip, which would arrive unrunnable.

    python make-app.py            -> build-mac/Tunor.app and Tunor-mac-arm64.tar.gz

The bundle this makes is NOT fully signed, and cannot be: .NET signs the executable and
seals nothing else, and codesign only exists on macOS. Such a bundle runs when nothing
has marked it as downloaded, and macOS calls it damaged when something has — which is
everyone who downloads it. So what ships is made on a Mac by make-dmg.sh: it signs the
bundle, writes the .dmg and repacks the .tar.gz. What comes out of here is for trying
locally.
"""
import os
import plistlib
import shutil
import struct
import subprocess
import sys
import tarfile

HERE = os.path.dirname(os.path.abspath(__file__))
PUB = os.path.join(HERE, "bin", "Release", "net8.0", "osx-arm64", "publish")
OUT = os.path.join(HERE, "build-mac")
APP = os.path.join(OUT, "Tunor.app")
EXE = "TunorDesktop"                       # what the publish produced
ICON_SRC = os.path.join(os.path.dirname(HERE), "ui", "Assets", "app.png")

VERSION = "1.11.5"
BUNDLE_ID = "com.vexorter.tunor"


def icns(png_path, dest):
    """An .icns out of one PNG, by hand.

    The format is a header and then one chunk per size; each chunk here is a PNG, which
    every macOS since 10.7 accepts. iconutil would do this on a Mac, so the sizes it
    would produce are the sizes written here.
    """
    try:
        from PIL import Image
    except ImportError:
        return False
    try:
        src = Image.open(png_path).convert("RGBA")
    except Exception:
        return False

    # (chunk name, pixel size) — the pairs macOS looks for, smallest first
    wanted = [(b"icp4", 16), (b"icp5", 32), (b"icp6", 64),
              (b"ic07", 128), (b"ic08", 256), (b"ic09", 512), (b"ic10", 1024)]
    chunks = []
    for name, size in wanted:
        img = src.resize((size, size), Image.LANCZOS)
        import io
        buf = io.BytesIO()
        img.save(buf, format="PNG")
        data = buf.getvalue()
        chunks.append(name + struct.pack(">I", len(data) + 8) + data)

    body = b"".join(chunks)
    with open(dest, "wb") as f:
        f.write(b"icns" + struct.pack(">I", len(body) + 8) + body)
    return True


def main():
    if not os.path.isdir(PUB):
        sys.exit("нет сборки под macOS. Сначала:\n"
                 "  dotnet publish -c Release -r osx-arm64 --self-contained true")

    if os.path.exists(OUT):
        shutil.rmtree(OUT)
    macos = os.path.join(APP, "Contents", "MacOS")
    res = os.path.join(APP, "Contents", "Resources")
    os.makedirs(macos)
    os.makedirs(res)

    for name in os.listdir(PUB):
        s = os.path.join(PUB, name)
        shutil.copy2(s, os.path.join(macos, name)) if os.path.isfile(s) \
            else shutil.copytree(s, os.path.join(macos, name))

    has_icon = icns(ICON_SRC, os.path.join(res, "AppIcon.icns"))

    info = {
        "CFBundleName": "Tunor",
        "CFBundleDisplayName": "Tunor",
        "CFBundleIdentifier": BUNDLE_ID,
        "CFBundleVersion": VERSION,
        "CFBundleShortVersionString": VERSION,
        "CFBundlePackageType": "APPL",
        "CFBundleExecutable": EXE,
        "CFBundleInfoDictionaryVersion": "6.0",
        "LSMinimumSystemVersion": "11.0",
        # Without this the window opens behind everything and there is no Dock icon.
        "NSHighResolutionCapable": True,
        "LSApplicationCategoryType": "public.app-category.utilities",
        "NSHumanReadableCopyright": "© 2026 Vexorter42 · MIT",
    }
    if has_icon:
        info["CFBundleIconFile"] = "AppIcon"
    with open(os.path.join(APP, "Contents", "Info.plist"), "wb") as f:
        plistlib.dump(info, f)

    # The tar carries the mode bits macOS needs; a zip from Windows would not.
    tar_path = os.path.join(OUT, "Tunor-mac-arm64.tar.gz")
    with tarfile.open(tar_path, "w:gz") as tar:
        def fix(ti):
            name = os.path.basename(ti.name)
            # The launcher and the native libraries beside it have to be runnable.
            ti.mode = 0o755 if (name == EXE or name.endswith((".dylib", ".so"))) else 0o644
            if ti.isdir():
                ti.mode = 0o755
            ti.uid = ti.gid = 0
            ti.uname = ti.gname = ""
            return ti
        tar.add(APP, arcname="Tunor.app", filter=fix)

    size = os.path.getsize(tar_path) / 1e6
    print(f"Tunor.app собран{'' if has_icon else ' (без иконки — нет Pillow)'}")
    print(f"  файлов внутри: {len(os.listdir(macos))}")
    print(f"  {tar_path}  ({size:.1f} МБ)")


main()
