"""What the two videos say and show.

A video is a list of segments. Each has a line that is said aloud, a picture for as long as it
takes to say it, and, for the vertical one, a caption of that same line, because most people
watch those with the sound off. The pictures are the tutorial's own animations
(tutorial/scenes), the real screens of the tutorial, and a card at each end.

The author reads the lines himself. How long a line takes is therefore a guess (CPS, below, is
how many characters a second a calm explanation runs at), and the video is built to be recorded
against: it plays with the line as a caption, in the time a person needs to say it, and if the
real recording comes out different, `build.py fit` re-times the video to it.
"""
from dataclasses import dataclass

#: How many characters of Russian a second a person says when explaining something, pauses included.
CPS = 13.5

SITE = "github.com/Vexorter42/Tunor"


@dataclass
class Seg:
    kind: str                           # "scene", "card" or "images"
    line: str = ""                      # what is said
    min: float = 0.0                    # at least this long, whatever the line
    pad: float = 0.7                    # a breath after the line
    dur: float | None = None            # fixed length, when the line does not decide it
    scene: str = ""                     # scene: which, and the part of it to play (seconds)
    a: float = 0.0
    b: float | None = None
    loop: bool = False                  # play the part again for as long as the line goes on
    view: tuple | None = None           # (x, y, w, h) of the scene to show, as a closer camera
    images: tuple = ()                  # images: files of tutorial/video/shots, shown one after another
    title: str = ""                     # vertical: above the picture. card: the big words
    sub: str = ""                       # card: under the big words
    link: str = ""                      # card: a line in the accent colour
    chapter: str = ""                   # wide: starts a chapter of this name (and a mark in the description)

    def seconds(self) -> float:
        if self.dur is not None:
            return self.dur
        return max(self.min, len(self.line) / CPS + self.pad)


@dataclass
class Story:
    name: str
    fmt: str                            # "tt" (1080x1920) or "yt" (1920x1080)
    segs: list


# ============================================================================ vertical, about a minute

TIKTOK = Story("tunor-tiktok", "tt", [
    Seg("card", title="Не открываются\nсайты?", sub="Или VPN тормозит всё подряд",
        line="Не открываются сайты? Или через VPN тормозит вообще всё?", min=3.4, pad=0.5),

    Seg("scene", scene="how", a=2.0, b=11.8, title="1 · Как это работает",
        line="Tunor — бесплатная программа. Она сама решает, что пустить через туннель, а что напрямую."),

    Seg("images", images=("60-terms.png", "61-lesson1-b.png", "68-exam-wrong.png"), title="2 · Обучение",
        line="При первом запуске он проведёт короткое обучение: анимации и вопросы. Пропустить нельзя — зато потом не придётся гуглить."),

    Seg("scene", scene="config", a=0.4, b=9.4, title="3 · Конфиг",
        line="Конфиг берёшь у телеграм-бота, бесплатно. Кидаешь файл в Tunor — туннель готов."),

    Seg("scene", scene="rules", a=1.0, b=12.6, title="4 · Правила",
        line="Дальше правила: пишешь сайт, или выбираешь программу целиком, например Дискорд, или берёшь готовый список."),

    Seg("scene", scene="start", a=0.6, b=9.8, title="5 · Запуск",
        line="Нажимаешь «Запустить» и проверяешь соединение: страна выхода другая — значит, всё работает."),

    Seg("scene", scene="trouble", a=1.0, b=13.4, title="6 · Не работает?",
        line="Что-то не открылось? Логи и проверка соединения покажут, где именно обрыв."),

    Seg("scene", scene="tray", a=2.2, b=10.6, title="7 · Трей",
        line="Закрыл окно — Tunor остался в трее и продолжает работать. Выйти можно через значок."),

    Seg("card", title="Скачать — в описании", sub="Бесплатно · открытый код · Windows, macOS, Linux", link=SITE,
        line="Ссылка на скачивание — в описании. Программа бесплатная, а код открытый.", min=4.0),
])


# ============================================================================ wide, a few minutes

YOUTUBE = Story("tunor-youtube", "yt", [
    Seg("card", title="Tunor", sub="Настройка за пять минут", chapter="Вступление", min=5.0,
        line="Привет! В этом видео — как установить и настроить Tunor: программу для Windows, macOS и Linux, "
             "которая сама решает, какие сайты и приложения пускать через туннель, а какие напрямую. "
             "Разберём всё по шагам, с нуля. Показываю на Windows; на Mac и Linux всё то же самое."),

    Seg("scene", scene="how", a=0.0, b=11.8, loop=True, chapter="Как это работает",
        line="Сначала главное. Tunor не гонит через туннель всё подряд. Каждое соединение сначала попадает к правилам: "
             "что в правилах есть — уходит в туннель, а всё остальное идёт напрямую, поэтому скорость не страдает. "
             "Своих серверов у программы нет: туннель — это конфиг или подписка, которые добавляешь ты сам."),

    Seg("images", images=("60-terms.png", "61-lesson1-b.png", "63-check-wrong.png", "68-exam-wrong.png", "72-done.png"),
        chapter="Установка и обучение",
        line="Скачай установщик со страницы релизов на гитхабе — ссылка в описании. При первом запуске Tunor покажет условия "
             "и обучение. Это шесть коротких уроков с анимациями, после каждого вопрос, а в конце экзамен. "
             "Пропустить обучение нельзя, и это не вредность: так вопросов потом будет в разы меньше."),

    Seg("scene", scene="config", a=0.0, b=11.0, loop=True, chapter="Откуда взять конфиг",
        line="Теперь конфиг. Самый простой вариант — WARP от Cloudflare. Открой в телеграме бота warp generator bot, "
             "выбери Cloudflare WARP и формат AmneziaWG, и бот пришлёт файл с расширением conf. "
             "Этот файл добавь в Tunor: кнопкой «Добавить файл» на главной или просто перетащи на окно. "
             "Tunor сам поймёт, что это за туннель. Если у тебя VPN-подписка — добавь её ссылкой. "
             "Только помни: ссылка подписки — как пароль, никому её не показывай."),

    Seg("scene", scene="rules", a=0.4, b=12.6, loop=True, chapter="Правила",
        line="Теперь правила — главная настройка. В туннель можно отправить домен: добавляешь youtube точка com, "
             "и через туннель пойдёт любой адрес этого сайта. Можно отправить программу целиком: раздел «Приложения», "
             "выбираешь запущенную программу и отправляешь в туннель. А можно не набирать вручную и взять готовые списки "
             "из каталога. Поменял правила — сохрани их, и конфиг пересоберётся."),

    Seg("scene", scene="start", a=0.4, b=11.0, loop=True, chapter="Запуск и проверка",
        line="Идём на главную и нажимаем «Запустить». Статус сменится на «Подключено». Дальше — проверка соединения: "
             "Tunor делает запрос через каждый туннель и показывает страну выхода. Если страна не твоя, значит туннель работает. "
             "Поменял конфиг, пока всё запущено, — перезапусти туннель."),

    Seg("scene", scene="trouble", a=0.6, b=13.6, loop=True, chapter="Если что-то не работает",
        line="Если что-то не открывается, действуем по порядку. Сначала раздел «Логи» — чаще всего причина написана там. "
             "Потом проверка соединения: видно, какой туннель молчит. И третье: программы для обхода блокировок — "
             "zapret, гудбай ди пи ай — работают на том же уровне, что и туннель, и могут ему мешать. Выключи их на время. "
             "Не разобрался — в разделе «Логи» есть кнопка «Собрать отчёт»: в нём нет ключей и паролей, его можно отправить как есть."),

    Seg("scene", scene="tray", a=1.6, b=10.8, loop=True, chapter="Трей, автозапуск, обновления",
        line="И последнее. Если закрыть окно крестиком, Tunor не выключится — он уйдёт в трей, и туннель продолжит работать. "
             "Чтобы выйти совсем, нажми на значок в трее и выбери «Выход». Чтобы Tunor включался вместе с компьютером, "
             "зайди в раздел «Автозапуск». А обновления приходят сами: на главной есть кнопка «Проверить обновления»."),

    Seg("scene", scene="done", a=0.0, b=4.5, loop=True, chapter="Итог",
        line="Вот и всё. Программа бесплатная, код открытый, ссылка на скачивание — в описании. "
             "Если что-то не получилось — сначала пройди обучение ещё раз: «Настройки», «Пройти обучение заново». "
             "А вопросы пиши в комментариях. Спасибо, что досмотрел!"),

    Seg("card", title="Tunor", sub="Бесплатно · открытый код · Windows, macOS, Linux", link=SITE, min=5.0, dur=5.0),
])


STORIES = {s.name: s for s in (TIKTOK, YOUTUBE)}
