# Разработка

Как собрать Tunor из исходников, чем он проверяется и как устроен репозиторий. Что умеет программа — в [README](../README.md).

- [Устройство репозитория](#устройство-репозитория)
- [Сборка из исходников](#сборка-из-исходников)
- [Проверка экранов без экрана](#проверка-экранов-без-экрана)
- [Обучение](#обучение)
- [Версия и выпуск](#версия-и-выпуск)
- [Где что лежит на диске](#где-что-лежит-на-диске)

## Устройство репозитория

Один репозиторий: код общий, поэтому правка правил, каталога или обучения не нужна в трёх местах сразу. Релизов два вида: программа для компьютера (Windows, macOS, Linux) и Android.

| Папка | Что это | Системы | Лицензия |
|---|---|---|---|
| [`ui/`](../ui) | приложение на WPF и общий код служб | Windows | MIT |
| [`core/`](../core) | тот же общий код как отдельная сборка без Windows-зависимостей (компилятор следит, чтобы в него не просочился Windows-API) | все | MIT |
| [`desktop/`](../desktop) | приложение на Avalonia | macOS, Linux | MIT |
| [`android/`](../android) | приложение для телефона на Kotlin и Compose, на библиотеке движка `libbox` | Android | GPL v3 |
| [`smoke/`](../smoke) | проверка экранов и общих служб, запускается на каждой системе | все | MIT |
| [`catalog/`](../catalog) | каталог готовых списков, один на компьютер и телефон | все | MIT |
| [`tutorial/`](../tutorial) | обучение: уроки, вопросы, анимации и то, чем они пишутся; ролики для соцсетей | все | MIT |
| [`packaging/`](../packaging) | сборка релиза: установщик Windows, `mac-install.sh` | — | MIT |
| [`.github/workflows/`](../.github/workflows) | проверки на каждой системе: Windows, Linux, Apple Silicon и настоящий Intel-мак; сборка `Tunor.app` и Android | — | — |

## Сборка из исходников

Нужен [.NET SDK 8+](https://dotnet.microsoft.com/download).

```bash
# Windows
cd ui
dotnet publish -c Release -r win-x64 --self-contained true

# macOS (бандл собирается на любой системе)
cd desktop
dotnet publish -c Release -r osx-arm64 --self-contained true
python make-app.py            # -> build-mac/Tunor.app и Tunor-mac-arm64.tar.gz

# macOS на процессоре Intel — то же, но с x64
dotnet publish -c Release -r osx-x64 --self-contained true
python make-app.py x64        # -> build-mac-x64/Tunor.app и Tunor-mac-x64.tar.gz

# Linux (упаковка — на Linux, там appimagetool)
cd desktop
dotnet publish -c Release -r linux-x64 --self-contained true
bash make-appimage.sh         # -> build-linux/Tunor-<версия>-x86_64.AppImage и .tar.gz
```

Сборка установщика для Windows описана отдельно — см. [`packaging/README.md`](../packaging/README.md). Сборка Android — в [`android/README.md`](../android/README.md#сборка): нужны JDK 17+ и Android SDK, а движок (120 МБ) скачивается скриптом и в репозитории его нет.

**То, что раздаётся для macOS, собирается на маке**, и это не придирка: `codesign` есть только там, а .NET подписывает лишь исполняемый файл и не запечатывает бандл. Такой бандл запускается, пока ничто не пометило его как скачанный, и называется повреждённым, как только пометит:

```bash
bash make-dmg.sh [путь/к/Tunor.app] [куда/положить]
```

Скрипт подписывает бандл ad-hoc, пересобирает из подписанного `.tar.gz` для обновлений и делает `.dmg` с оформленным окном. Фон окна рисуется отдельно — `python make-dmg-background.py`.

## Проверка экранов без экрана

```bash
cd smoke
dotnet run
```

Открывает все страницы и окна приложения вхолостую и говорит, какие упали. Ловит то, что не ловит компилятор: ресурс, запрошенный до присоединения к дереву, обработчик, стреляющий внутри `InitializeComponent`, привязку к несуществующему свойству. Заодно печатает, что приложение видит о запущенном движке. Ничего не меняет; пересборку `config.json` включает только `TUNOR_SMOKE_WRITE=1`.

Те же проверки идут в CI на Windows, Linux, Mac на Apple Silicon и на настоящем Intel-маке.

## Обучение

Что оно спрашивает и чему учит — один файл, [`tutorial/course.json`](../tutorial/course.json): уроки, вопросы, объяснения, слова для каждой системы (где на Windows «трей», на Mac «строка меню»). Его читают и версия для Windows, и для macOS/Linux, поэтому они не расходятся. Анимации — [`tutorial/scenes/*.json`](../tutorial/scenes): список фигур и моментов, в которые у фигуры меняется положение, размер или прозрачность. Что на экране в момент *t*, считает один общий код ([`SceneModel.cs`](../ui/Services/SceneModel.cs)); окна лишь двигают свои элементы. Сцены пишутся не руками, а на небольшом языке:

```bash
cd tutorial
python make_scenes.py                                    # пересобрать tutorial/scenes/*.json
python scenerender.py sheet scenes/how.json лист.png     # посмотреть сцену кадрами (нужен Pillow)
```

Правила «пропустить нельзя» живут в [`Tutorial.cs`](../ui/Services/Tutorial.cs) и проверяются в `smoke` — там же проверка, что новичку обучение обязательно, а тому, кто пользовался раньше, только предлагается.

Из тех же анимаций собираются ролики; как — в [`tutorial/video/README.md`](../tutorial/video/README.md).

## Версия и выпуск

Номер версии записан в четырёх местах: [`ui/Tunor.csproj`](../ui/Tunor.csproj), [`desktop/Tunor.Desktop.csproj`](../desktop/Tunor.Desktop.csproj), [`desktop/make-app.py`](../desktop/make-app.py) и [`packaging/tunor.iss`](../packaging/tunor.iss). CI проверяет, что они совпадают. У Android версия своя, в [`android/app/build.gradle.kts`](../android/app/build.gradle.kts).

Релиз программы для компьютера несёт файлы Windows, macOS и Linux и `version.json`, из которого берут обновление уже установленные программы. Android выходит отдельным релизом `android-v*`, который «последним» не помечается, и его файлы описаны в том же `version.json` (порядок — в [`android/README.md`](../android/README.md#выпуск)). Как устроен манифест — в [`updates.md`](updates.md).

## Где что лежит на диске

```
<корень>/
  settings.json            # режимы (tun / proxy / final / logging)
  update.json              # { "repo": "...", "mirror": "..." }
  THIRD-PARTY-NOTICES.md   # сведения о сторонних компонентах
  licenses/                # GPL-3.0.txt, sing-box-LICENSE.txt
  logs/                    # журнал по дням, неделя
  build/
    sing-box[.exe]         # движок (sing-box-lx, собран с тегом with_awg)
    sing-box.version       # какая это сборка движка и её sha256
    config.json            # генерируется приложением
  data/
    tunnels.json           # список туннелей и что через что идёт
    rules.json             # группы маршрутизации
    warp.conf              # AmneziaWG-конфиг WARP
    geo.conf               # WireGuard-конфиг geo
    cache.db               # кэш движка
    catalog.json           # последний скачанный каталог списков
    health.jsonl           # история проверок туннелей за неделю
    backups/               # то, что заменила загрузка резервной копии
    rulesets/*.srs         # локальные списки (скачиваются приложением)
  ui/Tunor.exe             # приложение (Windows)
```

На Windows корень — папка установки. На macOS и Linux программа живёт отдельно от своих файлов: на macOS они в `~/Library/Application Support/Tunor`, на Linux — в `~/.config/tunor` (или в `$XDG_CONFIG_HOME/tunor`, если он задан).
