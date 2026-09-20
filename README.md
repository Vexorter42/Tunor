# Nyx

Графический интерфейс для [sing-box](https://github.com/SagerNet/sing-box) на Windows с поддержкой **AmneziaWG 1.0 / 2.0 / 3.x**: управление туннелем, редактор правил маршрутизации, помощники генерации конфигов и обновление «по воздуху».

> A Windows GUI for sing-box with AmneziaWG (1.0 / 2.0 / 3.x) — service control, routing-rules editor, guided config generation and OTA updates.

---

## Возможности

- **Управление сервисом** — одна кнопка Запустить/Перезапустить + Остановить, индикатор состояния в окне и в трее.
- **AmneziaWG 1.0 / 2.0 / 3.x** — движок на базе [sing-box-lx](https://github.com/Leadaxe/sing-box-lx). Поддерживаются `Jc/Jmin/Jmax`, `S1–S4`, `H1–H4`, `I1–I5`, а также 3.x: `ContentPaddingAddition`, `RekeyAfterTime`, `RejectAfterTime`, `KeepaliveTimeout`, `MaxHandshakeAttempts`, `RandomTrailers`, `DisableCookies`, `HeaderProtectionKey`.
- **Собственный генератор конфига** — `config.json` собирается самим приложением из `warp.conf`, `geo.conf`, `rules.json` и `settings.json`. Внешние CLI-утилиты не нужны.
- **Drag & drop конфигов** — перетащи `.conf` на окно: Nyx сам определит, WARP это или geo, покажет версии AWG и предложит перезапустить туннель.
- **Редактор правил** — группы `inline` (домены и имена процессов), `remote` (внешние `.srs`) и `local` (локальные списки).
- **Локальные списки** — `.srs` скачиваются при первом запуске через настраиваемое зеркало (обход блокировки GitHub), после чего движок не зависит от сети при старте.
- **Приветственное окно** — условия использования и короткий туториал при первой установке; открыть повторно можно в **Настройках**.
- **Помощники генерации конфигов** — пошаговые инструкции:
  - **WARP** → Telegram-бот [@warp_generator_bot](https://t.me/warp_generator_bot) (Cloudflare WARP + AmneziaWG).
  - **geo** → [ProtonVPN](https://account.protonvpn.com/downloads) или любой сервис, выдающий WireGuard-конфиг.
- **Цепочка туннелей** — geo-трафик идёт через WARP (`detour`).
- **Автозапуск** — старт свёрнутым в трей при входе в Windows через планировщик задач.
- **Авто-перезапуск после сна/гибернации** — при пробуждении туннель поднимается заново.
- **Обновления по воздуху (OTA)** — проверка новой версии на GitHub Releases через зеркало, сверка SHA256, тихая установка и перезапуск.
- **Тёмная тема**, плавная прокрутка, системный трей, режим одного экземпляра, работа от прав администратора без повторных UAC.

## Установка

1. Скачай последний **`Nyx-Setup.exe`** со страницы [Releases](../../releases/latest).
2. Запусти установщик (нужны права администратора).
3. При первом запуске прими условия использования и пройди короткий туториал — там же кнопка **«Скачать списки»** (правила маршрутизации).
4. Сгенерируй конфиг WARP в [@warp_generator_bot](https://t.me/warp_generator_bot) и перетащи присланный `.conf` прямо на окно Nyx (или вставь текст в **Конфиги → warp.conf** и нажми «Сохранить и применить»).
5. *(опционально)* Для geo-туннеля — любой WireGuard-конфиг: так же drag & drop либо **Конфиги → geo.conf**.
6. Нажми **Запустить**.

Установщик не содержит чьих-либо приватных ключей, личных списков доменов и самих файлов правил — всё настраивается и скачивается на месте.

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
  settings.json            # режимы (tun / proxy / final / logging)
  update.json              # { "repo": "...", "mirror": "..." }
  THIRD-PARTY-NOTICES.md   # сведения о сторонних компонентах
  licenses/                # GPL-3.0.txt, sing-box-LICENSE.txt
  build/
    sing-box.exe           # движок (sing-box-lx, с тегом with_awg)
    config.json            # генерируется приложением
  data/
    rules.json             # группы маршрутизации
    warp.conf              # AmneziaWG-конфиг WARP
    geo.conf               # WireGuard-конфиг geo
    rulesets/*.srs         # локальные списки (скачиваются приложением)
  ui/Nyx.exe               # это приложение
```

## Поддержать

Проект развивается в свободное время. Поддержать можно здесь: **https://www.donationalerts.com/r/vexorter** ❤

## Благодарности

- [sing-box](https://github.com/SagerNet/sing-box) — основа движка.
- [sing-box-lx](https://github.com/Leadaxe/sing-box-lx) — форк с поддержкой AmneziaWG 3.x.
- [itdoginfo/allow-domains](https://github.com/itdoginfo/allow-domains) — списки правил.

## Лицензия

Исходный код Nyx (каталог `ui/`) — [MIT](LICENSE).

### Сторонние компоненты

**sing-box** распространяется под **GNU General Public License v3 или новее**
(Copyright © 2022 nekohasekai `<contact-sagernet@sekai.icu>`) с дополнительным
условием автора:

> In addition, no derivative work may use the name or imply association
> with this application without prior consent.

В установщик Nyx вкладывается **неизменённый** исполняемый файл
`sing-box 1.14.1-lx.8` (windows-amd64, собран с тегом `with_awg`). Соответствующий
исходный код:

- сборка: <https://github.com/Leadaxe/sing-box-lx> — тег `v1.14.1-lx.8`
- upstream: <https://github.com/SagerNet/sing-box>

Эти ссылки остаются действующими всё время, пока распространяется данная сборка;
если они перестанут работать — откройте issue, и исходники будут предоставлены.
Полные тексты лицензий лежат в папке установки (`licenses/`) и описаны в
[`THIRD-PARTY-NOTICES.md`](THIRD-PARTY-NOTICES.md).

Nyx — **отдельная программа**: она запускает `sing-box.exe` как самостоятельный
процесс и передаёт ему конфигурационный файл, а не линкуется с ним.

**Файлы правил (`.srs`)** из проекта
[itdoginfo/allow-domains](https://github.com/itdoginfo/allow-domains)
**не входят** в установщик — Nyx скачивает их из первоисточника по запросу
пользователя. На момент сборки лицензия в этом репозитории не указана.

### Отказ от аффилиации

Nyx не аффилирован с проектами sing-box, sing-box-lx, AmneziaWG, Cloudflare и
ProtonVPN, не одобрен ими и не поддерживается ими. Их названия используются
исключительно для описания совместимости.

### Отказ от ответственности

Программа предоставляется «как есть», без каких-либо гарантий. Автор не несёт
ответственности за любой ущерб, связанный с её использованием. Ответственность за
соблюдение законов своей страны и условий использования сторонних сервисов лежит
на пользователе.
