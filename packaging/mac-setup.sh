#!/bin/bash
# Unpacks Tunor.app and puts the engine where it looks for it.
#
# Run it from the folder holding Tunor-mac-arm64.tar.gz:
#
#   bash mac-setup.sh
#
# What it does, and nothing else:
#   · unpacks Tunor.app into this folder
#   · clears the quarantine flag, because the build is not signed by an Apple developer
#   · downloads the engine (or reuses the one mac-check left) into
#     ~/Library/Application Support/Tunor/build/
#
# No sudo, no system changes, no autostart. Deleting the folder and that Application
# Support directory undoes everything.

set -u
VER="1.14.2-lx.11"
TAR="Tunor-mac-arm64.tar.gz"
ROOT="$HOME/Library/Application Support/Tunor"
REL="https://github.com/Leadaxe/sing-box-lx/releases/download/v$VER"

ok()   { printf '  \033[32m✓\033[0m %s\n' "$1"; }
bad()  { printf '  \033[31m✗\033[0m %s\n' "$1"; }
info() { printf '  · %s\n' "$1"; }
head_() { printf '\n\033[1m%s\033[0m\n' "$1"; }

head_ "Проверяю"
[ "$(uname -m)" = "arm64" ] || { bad "эта сборка для Apple Silicon, а тут $(uname -m)"; exit 1; }
[ -f "$TAR" ] || { bad "рядом нет $TAR — положи скрипт в ту же папку"; exit 1; }
ok "macOS $(sw_vers -productVersion) · arm64"

head_ "Распаковываю приложение"
rm -rf Tunor.app
tar -xzf "$TAR" || { bad "архив не распаковался"; exit 1; }
[ -x "Tunor.app/Contents/MacOS/TunorDesktop" ] || chmod +x "Tunor.app/Contents/MacOS/TunorDesktop"
ok "Tunor.app ($(du -sh Tunor.app | cut -f1))"

head_ "Снимаю карантин"
# The build has no Apple Developer signature, so Gatekeeper would refuse to open it.
# Clearing the flag is what the right-click → Open dance does, done once.
xattr -dr com.apple.quarantine Tunor.app 2>/dev/null
ok "сделано — приложение откроется обычным двойным щелчком"

head_ "Ставлю движок"
mkdir -p "$ROOT/build" "$ROOT/data"
if [ -f "$ROOT/build/sing-box" ]; then
  ok "уже на месте: $("$ROOT/build/sing-box" version 2>/dev/null | head -1)"
else
  FOUND=$(find "$HOME/tunor-mac-check" -name sing-box -type f 2>/dev/null | head -1)
  if [ -n "$FOUND" ]; then
    cp "$FOUND" "$ROOT/build/sing-box"
    ok "взял из проверки, которую ты уже запускал"
  else
    info "скачиваю $VER…"
    TMP=$(mktemp -d)
    if curl -fsSL -o "$TMP/e.tar.gz" "$REL/sing-box-$VER-darwin-arm64.tar.gz" \
       && curl -fsSL -o "$TMP/S" "$REL/SHA256SUMS"; then
      W=$(grep " sing-box-$VER-darwin-arm64.tar.gz\$" "$TMP/S" | awk '{print $1}')
      H=$(shasum -a 256 "$TMP/e.tar.gz" | awk '{print $1}')
      if [ "$W" = "$H" ]; then
        tar -xzf "$TMP/e.tar.gz" -C "$TMP"
        cp "$TMP/sing-box-$VER-darwin-arm64/sing-box" "$ROOT/build/sing-box"
        ok "скачан и проверен по контрольной сумме"
      else
        bad "контрольная сумма не совпала — движок не установлен"
      fi
    else
      bad "не скачалось"
    fi
    rm -rf "$TMP"
  fi
  chmod +x "$ROOT/build/sing-box" 2>/dev/null
  xattr -d com.apple.quarantine "$ROOT/build/sing-box" 2>/dev/null
fi

if [ -f "$ROOT/build/sing-box" ]; then
  cat > "$ROOT/build/sing-box.version" <<EOF
version = $VER
sha256 = $(shasum -a 256 "$ROOT/build/sing-box" | awk '{print $1}')
source = https://github.com/Leadaxe/sing-box-lx/releases/tag/v$VER
EOF
  ok "штамп версии записан"
fi

head_ "Готово"
info "Запуск:  open Tunor.app"
info "Файлы приложения: $ROOT"
info "Удалить всё:  rm -rf Tunor.app \"$ROOT\""
printf '\n'
info "Это первая сборка — работают: состояние движка, запуск и остановка,"
info "список туннелей, добавление VPN по ссылке и подписке, настройки."
info "TUN пока не включай: для него нужна служба с правами, её кнопка на главной."
