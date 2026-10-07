#!/bin/bash
# Wraps Tunor.app into a .dmg — the envelope macOS users expect to be handed.
#
#   bash make-dmg.sh [путь/к/Tunor.app] [куда/положить]
#
# Defaults to build-mac/Tunor.app beside this script, writing the .dmg next to it. Must
# run on macOS: only hdiutil makes a disk image, which is why make-app.py stops at the
# bundle and the .tar.gz — that one can be assembled anywhere, and keeps the executable
# bit a .zip would lose.
#
# The image holds the app and a link to /Applications, so installing is one drag. It is
# not signed or notarised — there is no Apple developer account behind this build — so
# the first launch still needs right-click → Open, or the quarantine flag cleared.

set -u
HERE="$(cd "$(dirname "${BASH_SOURCE[0]:-$PWD}")" && pwd)"
APP="${1:-$HERE/build-mac/Tunor.app}"
OUT_DIR="${2:-$(dirname "$APP")}"

ok()  { printf '  \033[32m✓\033[0m %s\n' "$1"; }
bad() { printf '  \033[31m✗\033[0m %s\n' "$1"; }

[ "$(uname)" = "Darwin" ] || { bad "образ собирается только на macOS — здесь $(uname)"; exit 1; }
[ -d "$APP" ] || { bad "не нашёл $APP — сначала собери бандл: python make-app.py"; exit 1; }
[ -x "$APP/Contents/MacOS/TunorDesktop" ] \
  || { bad "в бандле нет исполняемого файла — распаковывал .zip вместо .tar.gz?"; exit 1; }

VERSION="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' \
  "$APP/Contents/Info.plist" 2>/dev/null)"
[ -n "$VERSION" ] || VERSION="unknown"
ARCH="$(/usr/bin/file "$APP/Contents/MacOS/TunorDesktop" | grep -q arm64 && echo arm64 || echo x64)"
DMG="$OUT_DIR/Tunor-$VERSION-$ARCH.dmg"

# A staging folder, because the image gets exactly what is in it: the app, and the link
# that makes the drag obvious. Copied with ditto, which keeps permissions and the bundle
# structure intact.
STAGE="$(mktemp -d)"
trap 'rm -rf "$STAGE"' EXIT
/usr/bin/ditto "$APP" "$STAGE/Tunor.app" || { bad "не скопировался бандл"; exit 1; }
ln -s /Applications "$STAGE/Applications"

mkdir -p "$OUT_DIR"
rm -f "$DMG"
# UDZO: compressed and read-only, which is what a download should be.
hdiutil create -volname "Tunor $VERSION" -srcfolder "$STAGE" \
  -fs HFS+ -format UDZO -ov -quiet "$DMG" || { bad "hdiutil не собрал образ"; exit 1; }

ok "$DMG  ($(du -h "$DMG" | cut -f1))"
printf '  · установка: открыть образ и перетащить Tunor в Applications\n'
printf '  · первый запуск: правый клик → Открыть (сборка не подписана у Apple)\n'
