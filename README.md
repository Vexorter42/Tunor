# Nyx

Графический интерфейс для [sing-box](https://github.com/SagerNet/sing-box) на Windows с поддержкой **AmneziaWG 1.0 / 2.0 / 3.x**: управление туннелем, редактор правил маршрутизации, помощники генерации конфигов и обновление «по воздуху».

> A Windows GUI for sing-box with AmneziaWG (1.0 / 2.0 / 3.x) — service control, routing-rules editor, guided config generation and OTA updates.

---

## Возможности

- **Управление сервисом** — одна кнопка Запустить/Перезапустить + Остановить, индикатор состояния в окне и в трее.
- **AmneziaWG 1.0 / 2.0 / 3.x** — движок на базе [sing-box-lx](https://github.com/Leadaxe/sing-box-lx). Поддерживаются `Jc/Jmin/Jmax`, `S1–S4`, `H1–H4`, `I1–I5`, а также 3.x: `ContentPaddingAddition`, `RekeyAfterTime`, `RejectAfterTime`, `KeepaliveTimeout`, `MaxHandshakeAttempts`, `RandomTrailers`, `DisableCookies`, `HeaderProtectionKey`.
- **Собственный генератор конфига** — `config.json` собирается самим приложением из `warp.conf`, `geo.conf`, `rules.json` и `settings.json`. Внешние CLI-утилиты не нужны.
- **Редактор правил** — группы `inline` (домены и имена процессов), `remote` (внешние `.srs`) и `local` (локальные списки).
- **Локальные списки** — скачивание `.srs` через настраиваемое зеркало (обход блокировки GitHub), чтобы движок не зависел от сети при старте.
- **Помощники генерации конфигов** — пошаговые инструкции:
  - **WARP** → Telegram-бот [@warp_generator_bot](https://t.me/warp_generator_bot) (Cloudflare WARP + AmneziaWG).
  - **geo** → [ProtonVPN](https://account.protonvpn.com/downloads) или любой сервис, выдающий WireGuard-конфиг.
- **Цепочка туннелей** — geo-трафик идёт через WARP (`detour`).
- **Автозапуск** — старт свёрнутым в трей при входе в Windows через планировщик задач.
- **Авто-перезапуск после сна/гибернации** — при пробуждении туннель поднимается заново.
- **Обновления по воздуху (OTA)** — проверка новой версии на GitHub Releases через зеркало, сверка SHA256, тихая установка и перезапуск.
- **Тёмная тема**, системный трей, режим одного экземпляра, работа от прав администратора без повторных UAC.

## Установка

1. Скачай последний **`Nyx-Setup.exe`** со страницы [Releases](../../releases/latest).
2. Запусти установщик (нужны права администратора).
3. Открой Nyx → **«⚡ Сгенерировать WARP»**, следуй инструкции: сгенерируй конфиг в боте, вставь его в **Конфиги → warp.conf**, нажми «Сохранить и применить».
4. *(опционально)* Для geo-туннеля — **«🌍 Сгенерировать geo»**, вставь WireGuard-конфиг в **Конфиги → geo.conf** и «Сохранить и применить».
5. Нажми **Запустить**.

Установщик не содержит чьих-либо приватных ключей и личных списков — всё настраивается на месте.

## Какую версию AmneziaWG выбирать

Движок понимает **все** версии, так что выбирай любую:

| Версия | Параметры | Поддержка |
|---|---|---|
| AWG 1.0 | `S1–S4`, `Jc`, `Jmin`, `Jmax`, `H1–H4` | ✅ |
| AWG 2.0 | `I1–I5` | ✅ |
| AWG 3.0 | padding, rekey, reject, keepalive, handshake | ✅ |
| AWG 3.1 | `RandomTrailers`, `DisableCookies` | ✅ |

## Обновления по воздуху

Приложение читает `update.json` (репозиторий + зеркало), тянет `version.json` из последнего релиза и, если версия новее, скачивает установщик, сверяет `SHA256` и запускает тихую установку.

```json
{
  "repo": "Vexorter42/Nyx",
  "mirror": "https://ghproxy.net/"
}
```

Если зеркало перестало работать — поменяй `mirror` (например `https://ghfast.top/`) без пересборки.

## Сборка из исходников

Нужен [.NET SDK 8+](https://dotnet.microsoft.com/download).

```bash
cd ui
dotnet build -c Debug
# или self-contained сборка:
dotnet publish -c Release -r win-x64 --self-contained true
```

Приложение ожидает такую структуру папок:

```
<корень>/
  settings.json         # режимы (tun / proxy / final / logging)
  update.json           # { "repo": "...", "mirror": "..." }
  build/
    sing-box.exe        # движок (sing-box-lx, с тегом with_awg)
    config.json         # генерируется приложением
  data/
    rules.json          # группы маршрутизации
    warp.conf           # AmneziaWG-конфиг WARP
    geo.conf            # WireGuard-конфиг geo
    rulesets/*.srs      # локальные списки
  ui/Nyx.exe            # это приложение
```

## Поддержать

Проект развивается в свободное время. Поддержать можно здесь: **https://www.donationalerts.com/r/vexorter** ❤

## Благодарности

- [sing-box](https://github.com/SagerNet/sing-box) — основа движка.
- [sing-box-lx](https://github.com/Leadaxe/sing-box-lx) — форк с поддержкой AmneziaWG 3.x.
- [itdoginfo/allow-domains](https://github.com/itdoginfo/allow-domains) — списки правил.

## Лицензия

[MIT](LICENSE). Бандлящиеся компоненты (sing-box, списки правил) распространяются под своими лицензиями.
