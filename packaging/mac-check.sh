#!/bin/bash
# Does the Tunor engine work on this Mac?
#
# Downloads sing-box-lx for this machine, checks it against the published hashes, and asks
# it a few questions. Nothing is installed, nothing needs sudo, no tunnel is raised and
# the network is not touched — everything lands in one folder you can delete afterwards.
#
#   bash mac-check.sh
#
# Report back whatever it prints.

set -u
VER="1.14.2-lx.11"
WORK="$HOME/tunor-mac-check"
REL="https://github.com/Leadaxe/sing-box-lx/releases/download/v$VER"

ok()   { printf '  \033[32m✓\033[0m %s\n' "$1"; }
bad()  { printf '  \033[31m✗\033[0m %s\n' "$1"; }
info() { printf '  · %s\n' "$1"; }
head_() { printf '\n\033[1m%s\033[0m\n' "$1"; }

head_ "Машина"
info "macOS $(sw_vers -productVersion 2>/dev/null || echo '?')  ·  $(uname -m)"
case "$(uname -m)" in
  arm64) ARCH=arm64 ;;
  x86_64) ARCH=amd64 ;;
  *) bad "неизвестная архитектура $(uname -m)"; exit 1 ;;
esac
TAR="sing-box-$VER-darwin-$ARCH.tar.gz"

head_ "Скачиваю движок"
mkdir -p "$WORK" && cd "$WORK" || exit 1
curl -fsSL -o "$TAR" "$REL/$TAR" || { bad "не скачалось — проверь, открыт ли github"; exit 1; }
curl -fsSL -o SHA256SUMS "$REL/SHA256SUMS" || { bad "не скачались контрольные суммы"; exit 1; }
ok "$TAR  ($(du -h "$TAR" | cut -f1))"

head_ "Проверяю подлинность"
WANT=$(grep " $TAR\$" SHA256SUMS | awk '{print $1}')
HAVE=$(shasum -a 256 "$TAR" | awk '{print $1}')
if [ -n "$WANT" ] && [ "$WANT" = "$HAVE" ]; then
  ok "контрольная сумма совпала"
else
  bad "СУММА НЕ СОВПАЛА — файл скачался неправильно или подменён"
  info "ожидалось: ${WANT:-не найдено в списке}"
  info "получено:  $HAVE"
  exit 1
fi

tar -xzf "$TAR" || { bad "архив не распаковался"; exit 1; }
SB="$WORK/sing-box-$VER-darwin-$ARCH/sing-box"
chmod +x "$SB" 2>/dev/null

head_ "Запускается ли"
# Gatekeeper puts a quarantine flag on anything downloaded; for a command-line binary
# run by hand it is enough to clear it.
xattr -d com.apple.quarantine "$SB" 2>/dev/null
if OUT=$("$SB" version 2>&1); then
  ok "$(echo "$OUT" | head -1)"
  info "$(echo "$OUT" | grep -i '^Environment' || true)"
else
  bad "движок не запустился:"
  echo "$OUT" | sed 's/^/      /'
  exit 1
fi

head_ "Что в этой сборке есть"
TAGS=$("$SB" version 2>/dev/null | grep -i '^Tags' | cut -d: -f2- | tr -d ' ')
for need in with_awg with_gvisor with_lxd with_quic with_utls; do
  case ",$TAGS," in
    *",$need,"*) ok "$need" ;;
    *) bad "$need — НЕТ, это важно" ;;
  esac
done

head_ "Понимает ли конфиг в формате Tunor"
cat > probe.json <<'JSON'
{
  "log": { "level": "warn" },
  "dns": { "servers": [ { "type": "https", "server": "1.1.1.1", "tag": "main-dns" } ], "final": "main-dns" },
  "inbounds": [
    { "type": "tun", "address": "172.18.0.1/30", "auto_route": true, "stack": "gvisor", "tag": "main-in" },
    { "type": "mixed", "tag": "proxy-in", "listen": "127.0.0.1", "listen_port": 31080 }
  ],
  "outbounds": [
    { "type": "direct", "tag": "direct-out" },
    { "type": "vless", "tag": "vless-out", "server": "203.0.113.10", "server_port": 443,
      "uuid": "8c1f0e4a-1d2b-4c3d-9e5f-6a7b8c9d0e1f", "flow": "xtls-rprx-vision",
      "tls": { "enabled": true, "server_name": "www.microsoft.com",
               "utls": { "enabled": true, "fingerprint": "chrome" },
               "reality": { "enabled": true, "public_key": "jNXHt1yRo0vDuchQlIP6Z0ZvjT3KtzVI-T4E7RoLJS0", "short_id": "6ba85179e30d4fc2" } } }
  ],
  "endpoints": [
    { "type": "wireguard", "tag": "warp-out", "mtu": 1280,
      "address": [ "172.16.0.2/32" ],
      "private_key": "iGnYvHk6aQMkGgzXr2VOq7ZoNY1MlYDXwVkPWUH5aFQ=",
      "peers": [ { "address": "162.159.192.1", "port": 2408,
                   "public_key": "bmXOC+F1FxEMF9dyiK2H5/1SUtzH0JuVo51h2wPfgyo=",
                   "allowed_ips": [ "0.0.0.0/0" ] } ] }
  ],
  "route": { "final": "direct-out", "auto_detect_interface": true, "default_domain_resolver": "main-dns" }
}
JSON
if OUT=$("$SB" check -c probe.json 2>&1); then
  ok "конфиг принят: TUN, WireGuard и VLESS с Reality"
else
  bad "конфиг отклонён:"
  echo "$OUT" | sed 's/^/      /'
fi

head_ "Определяет ли программы по трафику (страница «Приложения»)"
sed 's/"final": "direct-out", "auto_detect_interface"/"final": "direct-out", "find_process": true, "auto_detect_interface"/' probe.json > probe-fp.json
if "$SB" check -c probe-fp.json >/dev/null 2>&1; then
  ok "find_process принимается"
else
  info "find_process не принимается — на macOS страница «Приложения» работать не будет"
fi

head_ "Служба с правами (нужна для режима TUN)"
"$SB" lxd --service status >/dev/null 2>&1
case $? in
  0) ok "служба уже установлена и работает" ;;
  2) info "служба установлена, но требует переустановки" ;;
  3) ok "служба не установлена — это нормально, режим поддерживается" ;;
  4) info "есть только защищённая копия, службы нет" ;;
  5) info "служба установлена, но не запущена" ;;
  *) bad "режим lxd на этой сборке не отвечает — TUN потребует запуска от root" ;;
esac

head_ "Итог"
info "Всё лежит в $WORK — можно удалить: rm -rf \"$WORK\""
info "Ничего не установлено, сеть не затронута, туннель не поднимался."
