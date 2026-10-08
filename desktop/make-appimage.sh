#!/bin/bash
# Packs the Linux publish into an AppImage — one file that runs on any desktop.
#
#   bash make-appimage.sh [путь/к/publish] [куда/положить]
#
# Defaults to bin/Release/net8.0/linux-x64/publish beside this script, writing next to
# it. Must run on Linux: appimagetool is a Linux program, and the result has to be made
# where it will run.
#
# An AppImage is chosen over .deb and .rpm because it is one file on every distribution
# and needs no package manager to try. The libraries the app loads at runtime — X11,
# fontconfig, GL — are NOT bundled: they belong to the desktop the user already has, and
# every desktop has them. A machine with none, such as a bare container, needs them
# installed, and the release notes say which.

set -u
HERE="$(cd "$(dirname "${BASH_SOURCE[0]:-$PWD}")" && pwd)"
PUB="${1:-$HERE/bin/Release/net8.0/linux-x64/publish}"
OUT_DIR="${2:-$HERE/build-linux}"
ICON_SRC="$(cd "$HERE/.." 2>/dev/null && pwd)/ui/Assets/app.png"
[ -f "$ICON_SRC" ] || ICON_SRC="$HERE/app.png"
TOOL_URL="https://github.com/AppImage/AppImageKit/releases/download/continuous/appimagetool-x86_64.AppImage"

ok()   { printf '  \033[32m✓\033[0m %s\n' "$1"; }
bad()  { printf '  \033[31m✗\033[0m %s\n' "$1"; }
info() { printf '  · %s\n' "$1"; }

[ "$(uname)" = "Linux" ] || { bad "AppImage собирается только на Linux — здесь $(uname)"; exit 1; }
[ -x "$PUB/TunorDesktop" ] || { bad "нет сборки в $PUB — сначала: dotnet publish -c Release -r linux-x64 --self-contained true"; exit 1; }

# From the project file, not from the program: it is a windowed app and knows no
# --version, so asking it opened a window and waited for someone to close it.
VERSION="$(grep -oE '<Version>[^<]+' "$HERE/Tunor.Desktop.csproj" 2>/dev/null | head -1 | cut -d'>' -f2)"
[ -n "$VERSION" ] || VERSION="${TUNOR_VERSION:-unknown}"

mkdir -p "$OUT_DIR"
APPDIR="$OUT_DIR/Tunor.AppDir"
rm -rf "$APPDIR"
mkdir -p "$APPDIR/usr/bin" "$APPDIR/usr/share/applications" \
         "$APPDIR/usr/share/icons/hicolor/256x256/apps"

cp -r "$PUB/." "$APPDIR/usr/bin/"
chmod +x "$APPDIR/usr/bin/TunorDesktop"

# The desktop entry, which is what a menu reads. Network;Utility; because that is where
# people look for a VPN, and StartupWMClass so the running window is matched to it.
cat > "$APPDIR/tunor.desktop" <<'DESKTOP'
[Desktop Entry]
Type=Application
Name=Tunor
Comment=Туннель на базе sing-box с поддержкой AmneziaWG
Exec=TunorDesktop
Icon=tunor
Categories=Network;Utility;
Terminal=false
StartupWMClass=TunorDesktop
DESKTOP
cp "$APPDIR/tunor.desktop" "$APPDIR/usr/share/applications/tunor.desktop"

if [ -f "$ICON_SRC" ]; then
  cp "$ICON_SRC" "$APPDIR/tunor.png"
  cp "$ICON_SRC" "$APPDIR/usr/share/icons/hicolor/256x256/apps/tunor.png"
else
  info "значка нет по пути $ICON_SRC — AppImage соберётся без него"
fi

# AppRun is what the AppImage starts. The app keeps its own files under ~/.config, so
# nothing here needs to be writable; what it does need is to find its own libraries.
cat > "$APPDIR/AppRun" <<'APPRUN'
#!/bin/sh
HERE="$(dirname "$(readlink -f "$0")")"
export LD_LIBRARY_PATH="$HERE/usr/bin:${LD_LIBRARY_PATH:-}"
exec "$HERE/usr/bin/TunorDesktop" "$@"
APPRUN
chmod +x "$APPDIR/AppRun"
ok "AppDir собран: $(du -sh "$APPDIR" | cut -f1)"

TOOL="$OUT_DIR/appimagetool"
if [ ! -x "$TOOL" ]; then
  curl -fsSL -o "$TOOL" "$TOOL_URL" || { bad "appimagetool не скачался"; exit 1; }
  chmod +x "$TOOL"
fi

# --appimage-extract-and-run: appimagetool is itself an AppImage and would want FUSE,
# which a container or WSL often has not.
IMAGE="$OUT_DIR/Tunor-$VERSION-x86_64.AppImage"
rm -f "$IMAGE"
ARCH=x86_64 "$TOOL" --appimage-extract-and-run "$APPDIR" "$IMAGE" >/dev/null 2>&1 \
  || { bad "appimagetool не собрал образ"; exit 1; }
chmod +x "$IMAGE"
ok "$IMAGE  ($(du -h "$IMAGE" | cut -f1))"

# A plain archive beside it, for anyone who would rather unpack than run one file.
TARBALL="$OUT_DIR/Tunor-$VERSION-linux-x64.tar.gz"
rm -f "$TARBALL"
tar -czf "$TARBALL" -C "$OUT_DIR" --transform 's,^Tunor.AppDir,Tunor,' Tunor.AppDir \
  && ok "$TARBALL  ($(du -h "$TARBALL" | cut -f1))"

printf '  · запуск: chmod +x %s && %s\n' "$(basename "$IMAGE")" "./$(basename "$IMAGE")"
printf '  · TUN без пароля: на Главной нажми «Выдать права движку»\n'
