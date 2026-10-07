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
# The window the image opens in is laid out here: a backdrop with an arrow, the app on
# the left, Applications on the right. That layout lives in the volume's .DS_Store, and
# the only thing that writes one is Finder — so it is set through AppleScript, which
# needs permission to drive Finder. Run over ssh that permission is usually refused;
# the image is still made, just with the plain list view, and the script says so.
#
# Not signed or notarised — there is no Apple developer account behind this build — so
# the first launch still needs right-click → Open. The backdrop says that too.

set -u
HERE="$(cd "$(dirname "${BASH_SOURCE[0]:-$PWD}")" && pwd)"
APP="${1:-$HERE/build-mac/Tunor.app}"
OUT_DIR="${2:-$(dirname "$APP")}"
BACKDROP="$HERE/build-mac/dmg-background.png"
BACKDROP2X="$HERE/build-mac/dmg-background@2x.png"

# The window, and where the two icons sit in it. make-dmg-background.py draws the arrow
# to match these numbers, so the two files change together.
WIN_W=620
WIN_H=400
ICON_SIZE=128
APP_X=160
APPS_X=460
ICON_Y=190

ok()   { printf '  \033[32m✓\033[0m %s\n' "$1"; }
bad()  { printf '  \033[31m✗\033[0m %s\n' "$1"; }
info() { printf '  · %s\n' "$1"; }

[ "$(uname)" = "Darwin" ] || { bad "образ собирается только на macOS — здесь $(uname)"; exit 1; }
[ -d "$APP" ] || { bad "не нашёл $APP — сначала собери бандл: python make-app.py"; exit 1; }
[ -x "$APP/Contents/MacOS/TunorDesktop" ] \
  || { bad "в бандле нет исполняемого файла — распаковывал .zip вместо .tar.gz?"; exit 1; }

VERSION="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' \
  "$APP/Contents/Info.plist" 2>/dev/null)"
[ -n "$VERSION" ] || VERSION="unknown"
/usr/bin/file "$APP/Contents/MacOS/TunorDesktop" | grep -q arm64 && ARCH=arm64 || ARCH=x64
VOLUME="Tunor $VERSION"
DMG="$OUT_DIR/Tunor-$VERSION-$ARCH.dmg"

# A staging folder, because the image gets exactly what is in it: the app, the link that
# makes the drag obvious, and the backdrop in a hidden folder. ditto keeps permissions
# and the bundle structure intact where cp -r would not.
STAGE="$(mktemp -d)"
MOUNT=""
cleanup() {
  [ -n "$MOUNT" ] && hdiutil detach "$MOUNT" -quiet 2>/dev/null
  rm -rf "$STAGE"
}
trap cleanup EXIT

/usr/bin/ditto "$APP" "$STAGE/Tunor.app" || { bad "не скопировался бандл"; exit 1; }
ln -s /Applications "$STAGE/Applications"

HAVE_BACKDROP=0
if [ -f "$BACKDROP" ]; then
  mkdir -p "$STAGE/.background"
  cp "$BACKDROP" "$STAGE/.background/background.png"
  [ -f "$BACKDROP2X" ] && cp "$BACKDROP2X" "$STAGE/.background/background@2x.png"
  HAVE_BACKDROP=1
else
  info "фона нет — сделай его: python make-dmg-background.py"
fi

mkdir -p "$OUT_DIR"
rm -f "$DMG"

# Read-write first: Finder has to be able to write the .DS_Store that holds the layout.
# Sized from what is actually there, with room for that file and the filesystem's own.
RW="$STAGE/../tunor-rw-$$.dmg"
SIZE_MB=$(( $(du -sm "$STAGE" | cut -f1) + 60 ))
hdiutil create -volname "$VOLUME" -srcfolder "$STAGE" -fs HFS+ \
  -format UDRW -size "${SIZE_MB}m" -ov -quiet "$RW" \
  || { bad "hdiutil не собрал черновой образ"; exit 1; }

MOUNT="$(hdiutil attach "$RW" -nobrowse -noautoopen 2>/dev/null \
  | grep -o '/Volumes/.*' | head -1)"
[ -n "$MOUNT" ] || { bad "черновой образ не примонтировался"; exit 1; }

if [ "$HAVE_BACKDROP" = 1 ]; then
  # Finder is the only thing that writes a .DS_Store, so the layout is dictated to it.
  # Quietly skipped when it refuses: over ssh it is not allowed to send Apple events.
  /usr/bin/osascript <<APPLESCRIPT >/dev/null 2>&1
tell application "Finder"
  tell disk "$VOLUME"
    open
    set current view of container window to icon view
    set toolbar visible of container window to false
    set statusbar visible of container window to false
    set the bounds of container window to {200, 120, $((200 + WIN_W)), $((120 + WIN_H))}
    set opts to the icon view options of container window
    set arrangement of opts to not arranged
    set icon size of opts to $ICON_SIZE
    set text size of opts to 13
    set background picture of opts to file ".background:background.png"
    set position of item "Tunor.app" of container window to {$APP_X, $ICON_Y}
    set position of item "Applications" of container window to {$APPS_X, $ICON_Y}
    close
    open
    update without registering applications
    delay 2
    close
  end tell
end tell
APPLESCRIPT
  if [ $? -eq 0 ] && [ -f "$MOUNT/.DS_Store" ]; then
    ok "окно оформлено: фон, значки по $ICON_SIZE, стрелка"
  else
    info "Finder не дал разложить окно (так бывает по ssh) — образ собран с обычным видом"
    info "чтобы с оформлением: запусти этот скрипт прямо на маке, в Терминале"
  fi
fi

sync
hdiutil detach "$MOUNT" -quiet || hdiutil detach "$MOUNT" -force -quiet
MOUNT=""

# UDZO: compressed and read-only, which is what a download should be.
hdiutil convert "$RW" -format UDZO -imagekey zlib-level=9 -ov -quiet -o "$DMG" \
  || { bad "hdiutil не сжал образ"; exit 1; }
rm -f "$RW"

ok "$DMG  ($(du -h "$DMG" | cut -f1))"
printf '  · установка: открыть образ и перетащить Tunor в Applications\n'
printf '  · первый запуск: правый клик → Открыть (сборка не подписана у Apple)\n'
