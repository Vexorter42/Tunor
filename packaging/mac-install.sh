#!/bin/bash
# Installs the current macOS build of Tunor, straight from GitHub.
#
#   curl -fsSL https://raw.githubusercontent.com/Vexorter42/Tunor/main/packaging/mac-install.sh | bash
#
# or, having saved it:  bash mac-install.sh
#
# Reads the release manifest to find the current build, downloads and checks it, unpacks
# it beside this script, clears the quarantine flag
# (the build is not signed by an Apple developer) and puts the engine where the app looks
# for it. No sudo, no system changes: the app asks for rights itself when it needs them.

set -u
REPO="Vexorter42/Tunor"
ENGINE_VER="1.14.2-lx.11"
ROOT="$HOME/Library/Application Support/Tunor"
HERE="$(cd "$(dirname "${BASH_SOURCE[0]:-$PWD}")" && pwd)"

ok()   { printf '  \033[32m✓\033[0m %s\n' "$1"; }
bad()  { printf '  \033[31m✗\033[0m %s\n' "$1"; }
info() { printf '  · %s\n' "$1"; }
head_() { printf '\n\033[1m%s\033[0m\n' "$1"; }

head_ "Проверяю"
[ "$(uname -m)" = "arm64" ] || { bad "эта сборка для Apple Silicon, а тут $(uname -m)"; exit 1; }
ok "macOS $(sw_vers -productVersion) · arm64"

head_ "Смотрю, что лежит в релизе"
cd "$HERE" || exit 1
# The same manifest the app reads when it checks for updates, so this script cannot
# drift away from it: one place says which build is current, for both systems.
JSON="$(curl -fsSL "https://github.com/$REPO/releases/latest/download/version.json")" \
  || { bad "манифест не скачался — проверь, открыт ли github"; exit 1; }
# The macOS half of the manifest, read without a JSON parser: python3 is not on a
# clean macOS, and asking for the developer tools is not what this script is for.
MAC="${JSON#*\"mac\"}"
field() { printf '%s' "$MAC" | tr ',' '\n' | sed -n "s/.*\"$1\"[[:space:]]*:[[:space:]]*\"\([^\"]*\)\".*/\\1/p" | head -1; }
URL="$(field url)"
WANT="$(field sha256)"
[ -n "$URL" ] || { bad "в этом релизе нет сборки для macOS"; exit 1; }
ok "в релизе: $(field version)"

head_ "Скачиваю"
curl -fL --progress-bar -o Tunor-mac-arm64.tar.gz "$URL" \
  || { bad "не скачалось"; exit 1; }
ok "$(du -h Tunor-mac-arm64.tar.gz | cut -f1)"
# The engine below is checked against its published sums; the app deserves the same.
if [ -n "$WANT" ]; then
  GOT="$(shasum -a 256 Tunor-mac-arm64.tar.gz | awk '{print $1}')"
  if [ "$WANT" = "$GOT" ]; then ok "контрольная сумма совпала"
  else bad "контрольная сумма не совпала — загрузка отклонена"; exit 1
  fi
else
  info "в манифесте нет контрольной суммы — пропускаю проверку"
fi

head_ "Закрываю приложение, если открыто"
pkill -x TunorDesktop 2>/dev/null && ok "закрыл" || info "не было запущено"

head_ "Распаковываю"
rm -rf Tunor.app
tar -xzf Tunor-mac-arm64.tar.gz || { bad "архив не распаковался"; exit 1; }
chmod +x "Tunor.app/Contents/MacOS/TunorDesktop" 2>/dev/null
# Not signed by an Apple developer, so Gatekeeper would refuse to open it; clearing the
# flag is the right-click → Open dance, done once.
xattr -dr com.apple.quarantine Tunor.app 2>/dev/null
ok "Tunor.app готов"

head_ "Движок"
mkdir -p "$ROOT/build" "$ROOT/data"
if [ -x "$ROOT/build/sing-box" ]; then
  ok "уже на месте: $("$ROOT/build/sing-box" version 2>/dev/null | head -1)"
else
  REL="https://github.com/Leadaxe/sing-box-lx/releases/download/v$ENGINE_VER"
  TMP=$(mktemp -d)
  if curl -fsSL -o "$TMP/e.tar.gz" "$REL/sing-box-$ENGINE_VER-darwin-arm64.tar.gz" \
     && curl -fsSL -o "$TMP/S" "$REL/SHA256SUMS"; then
    W=$(grep " sing-box-$ENGINE_VER-darwin-arm64.tar.gz\$" "$TMP/S" | awk '{print $1}')
    H=$(shasum -a 256 "$TMP/e.tar.gz" | awk '{print $1}')
    if [ "$W" = "$H" ]; then
      tar -xzf "$TMP/e.tar.gz" -C "$TMP"
      cp "$TMP/sing-box-$ENGINE_VER-darwin-arm64/sing-box" "$ROOT/build/sing-box"
      chmod +x "$ROOT/build/sing-box"
      xattr -d com.apple.quarantine "$ROOT/build/sing-box" 2>/dev/null
      printf 'version = %s\nsha256 = %s\nsource = %s\n' \
        "$ENGINE_VER" "$(shasum -a 256 "$ROOT/build/sing-box" | awk '{print $1}')" \
        "https://github.com/Leadaxe/sing-box-lx/releases/tag/v$ENGINE_VER" \
        > "$ROOT/build/sing-box.version"
      ok "скачан и проверен по контрольной сумме"
    else
      bad "контрольная сумма движка не совпала — не установлен"
    fi
  else
    bad "движок не скачался"
  fi
  rm -rf "$TMP"
fi

head_ "Готово"
info "Запуск:  open \"$HERE/Tunor.app\""
info "Обновиться потом:  bash \"$HERE/mac-install.sh\""
printf '\n'
open "$HERE/Tunor.app" 2>/dev/null && ok "запустил"
