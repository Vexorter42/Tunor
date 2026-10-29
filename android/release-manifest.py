r"""Puts the Android part into a release's version.json, the file the apps read to find out whether there is something newer.

    python release-manifest.py VERSION_JSON APK_DIR TAG VERSION CODE "notes"

APK_DIR holds the release's files, one for each processor: Tunor-android-<VERSION>-arm64-v8a.apk and
Tunor-android-<VERSION>-x86_64.apk. VERSION_JSON is the manifest the desktop builds already have (it is made if there is
none), and only its "android" section is written: whatever else it says is left as it is.

The hashes are read from the files that will be uploaded, not typed in: the app refuses a download whose SHA-256
differs from the one written here. CODE is the build number (versionCode in app/build.gradle.kts); an app takes
an update only when this is higher than its own.
"""
import hashlib
import io
import json
import os
import sys

ABIS = ("arm64-v8a", "x86_64")
REPO = "Vexorter42/Tunor"


def main() -> None:
    if len(sys.argv) != 7:
        sys.exit(__doc__)
    manifest, folder, tag, version, code, notes = sys.argv[1:]

    section = {"version": version, "code": int(code), "notes": notes}
    base = f"https://github.com/{REPO}/releases/download/{tag}/"
    for abi in ABIS:
        name = f"Tunor-android-{version}-{abi}.apk"
        path = os.path.join(folder, name)
        if not os.path.isfile(path):
            sys.exit(f"нет файла {path}")
        with open(path, "rb") as fh:
            data = fh.read()
        section[abi] = {"url": base + name, "sha256": hashlib.sha256(data).hexdigest(), "size": len(data)}

    doc = {}
    if os.path.isfile(manifest):
        with open(manifest, encoding="utf-8") as fh:
            doc = json.load(fh)
    doc["android"] = section
    with io.open(manifest, "w", encoding="utf-8", newline="\n") as fh:
        fh.write(json.dumps(doc, ensure_ascii=False, indent=2) + "\n")
    print(json.dumps(section, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
