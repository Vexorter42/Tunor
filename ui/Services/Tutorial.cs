using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Tunor.Services;

/// <summary>Which system the wording is for: a few lessons say "tray" on one and "menu bar" on another.</summary>
public enum TutorialOs { Windows, Mac, Linux }

/// <summary>One question, as written in the course.</summary>
public sealed class Question
{
    public string Text = "";
    public string[] Options = Array.Empty<string>();
    public int Correct;
    public string Why = "";
}

public sealed class Lesson
{
    public string Id = "";
    public string Title = "";
    public string Lead = "";
    /// <summary>The animation that goes with it (a file in tutorial/scenes).</summary>
    public string Scene = "";
    public string[] Points = Array.Empty<string>();
    /// <summary>The first one is asked straight after the lesson; the others are for the exam.</summary>
    public Question[] Questions = Array.Empty<Question>();
}

/// <summary>
/// What a new person is taught before the program lets them use it, and what they are asked
/// to show they took in. It is data, not code: tutorial/course.json is the one place the
/// lessons and the questions live, so the Windows and the macOS/Linux front ends cannot drift
/// apart, and the video about the program is written from the same text.
/// </summary>
public sealed class Course
{
    public int Version;
    /// <summary>How many exam answers have to be right.</summary>
    public int PassMark;
    public Lesson[] Lessons = Array.Empty<Lesson>();

    public string TermsTitle = "", TermsLead = "", TermsAccept = "";
    public string SetupTitle = "", SetupLead = "", SetupLists = "", SetupBot = "", SetupAdd = "", SetupFound = "", SetupMissing = "";
    public string ExamTitle = "", ExamLead = "", ExamPass = "", ExamFail = "";
    public string DoneTitle = "", DoneLead = "", DoneScene = "";
    public string[] DonePoints = Array.Empty<string>();

    private static readonly Dictionary<TutorialOs, Course> Cache = new();

    public static TutorialOs CurrentOs => OperatingSystem.IsWindows() ? TutorialOs.Windows
        : OperatingSystem.IsMacOS() ? TutorialOs.Mac : TutorialOs.Linux;

    /// <summary>The course that ships inside the program, worded for this system.</summary>
    public static Course Load() => Load(CurrentOs);

    public static Course Load(TutorialOs os)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue(os, out var hit)) return hit;
            using var s = typeof(Course).Assembly.GetManifestResourceStream("course.json")
                          ?? throw new FileNotFoundException("В программе нет course.json.");
            using var r = new StreamReader(s);
            return Cache[os] = Parse(r.ReadToEnd(), os);
        }
    }

    public static Course Parse(string json, TutorialOs os)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var c = new Course
        {
            Version = root.GetProperty("version").GetInt32(),
            PassMark = root.GetProperty("pass").GetInt32(),
        };
        if (c.Version <= 0) throw new FormatException("Курс без версии.");

        var pages = root.GetProperty("pages");
        var terms = pages.GetProperty("terms");
        c.TermsTitle = Text(terms.GetProperty("title"), os);
        c.TermsLead = Text(terms.GetProperty("lead"), os);
        c.TermsAccept = Text(terms.GetProperty("accept"), os);

        var setup = pages.GetProperty("setup");
        c.SetupTitle = Text(setup.GetProperty("title"), os);
        c.SetupLead = Text(setup.GetProperty("lead"), os);
        c.SetupLists = Text(setup.GetProperty("lists"), os);
        c.SetupBot = Text(setup.GetProperty("bot"), os);
        c.SetupAdd = Text(setup.GetProperty("add"), os);
        c.SetupFound = Text(setup.GetProperty("found"), os);
        c.SetupMissing = Text(setup.GetProperty("missing"), os);

        var exam = pages.GetProperty("exam");
        c.ExamTitle = Text(exam.GetProperty("title"), os);
        c.ExamLead = Text(exam.GetProperty("lead"), os);
        c.ExamPass = Text(exam.GetProperty("pass"), os);
        c.ExamFail = Text(exam.GetProperty("fail"), os);

        var done = pages.GetProperty("done");
        c.DoneTitle = Text(done.GetProperty("title"), os);
        c.DoneLead = Text(done.GetProperty("lead"), os);
        c.DoneScene = Text(done.GetProperty("scene"), os);
        c.DonePoints = Texts(done.GetProperty("points"), os);

        var lessons = new List<Lesson>();
        foreach (var el in root.GetProperty("lessons").EnumerateArray())
        {
            var l = new Lesson
            {
                Id = Text(el.GetProperty("id"), os),
                Title = Text(el.GetProperty("title"), os),
                Lead = Text(el.GetProperty("lead"), os),
                Scene = Text(el.GetProperty("scene"), os),
                Points = Texts(el.GetProperty("points"), os),
            };
            var qs = new List<Question>();
            foreach (var q in el.GetProperty("questions").EnumerateArray())
            {
                var opts = Texts(q.GetProperty("a"), os);
                var ok = q.GetProperty("ok").GetInt32();
                var question = new Question
                {
                    Text = Text(q.GetProperty("q"), os),
                    Options = opts,
                    Correct = ok,
                    Why = Text(q.GetProperty("why"), os),
                };
                if (question.Text.Length == 0) throw new FormatException($"Урок «{l.Id}»: вопрос без текста.");
                if (opts.Length < 2 || opts.Length > 4) throw new FormatException($"Урок «{l.Id}»: у вопроса «{question.Text}» должно быть от 2 до 4 ответов.");
                if (ok < 0 || ok >= opts.Length) throw new FormatException($"Урок «{l.Id}»: верный ответ вопроса «{question.Text}» вне списка.");
                if (opts.Any(string.IsNullOrWhiteSpace)) throw new FormatException($"Урок «{l.Id}»: пустой ответ в «{question.Text}».");
                if (question.Why.Length == 0) throw new FormatException($"Урок «{l.Id}»: к вопросу «{question.Text}» нет объяснения.");
                qs.Add(question);
            }
            l.Questions = qs.ToArray();

            if (l.Id.Length == 0 || l.Title.Length == 0 || l.Scene.Length == 0) throw new FormatException("Урок без названия, имени или сцены.");
            if (l.Points.Length == 0) throw new FormatException($"Урок «{l.Id}» без текста.");
            // One question follows the lesson, and the exam needs a different one to draw from.
            if (l.Questions.Length < 2) throw new FormatException($"В уроке «{l.Id}» нужно хотя бы два вопроса.");
            lessons.Add(l);
        }
        c.Lessons = lessons.ToArray();
        if (c.Lessons.Length == 0) throw new FormatException("В курсе нет уроков.");
        if (c.Lessons.Select(l => l.Id).Distinct().Count() != c.Lessons.Length) throw new FormatException("Два урока с одним именем.");
        if (c.PassMark < 1 || c.PassMark > c.Lessons.Length) throw new FormatException("Проходной балл вне числа вопросов экзамена.");
        return c;
    }

    /// <summary>A string, or an object with one string per system ("w", "m", "l"); an empty string means "nothing here".</summary>
    private static string Text(JsonElement el, TutorialOs os)
    {
        if (el.ValueKind == JsonValueKind.String) return el.GetString() ?? "";
        if (el.ValueKind != JsonValueKind.Object) throw new FormatException("Ожидался текст.");
        var key = os switch { TutorialOs.Mac => "m", TutorialOs.Linux => "l", _ => "w" };
        if (el.TryGetProperty(key, out var own)) return own.GetString() ?? "";
        if (el.TryGetProperty("w", out var win)) return win.GetString() ?? "";
        throw new FormatException("Нет текста для этой системы.");
    }

    private static string[] Texts(JsonElement el, TutorialOs os)
        => el.EnumerateArray().Select(e => Text(e, os)).Where(t => t.Length > 0).ToArray();
}

/// <summary>A question as it is put to a person: the answers in an order of its own.</summary>
public sealed class Asked
{
    public Question Source { get; init; } = new();
    public int Lesson { get; init; }
    /// <summary>The answers, shuffled.</summary>
    public string[] Options { get; init; } = Array.Empty<string>();
    /// <summary>Where the right one ended up.</summary>
    public int Correct { get; init; }
    public string Key { get; init; } = "";

    public static Asked From(Question q, int lesson, string key, Random rng)
    {
        // The right answer stays where the author put it only by chance: a person who learns
        // "the second one" has learnt nothing about the program.
        var order = Enumerable.Range(0, q.Options.Length).OrderBy(_ => rng.Next()).ToArray();
        return new Asked
        {
            Source = q,
            Lesson = lesson,
            Key = key,
            Options = order.Select(i => q.Options[i]).ToArray(),
            Correct = Array.IndexOf(order, q.Correct),
        };
    }
}

public enum StepKind { Terms, Lesson, Check, Setup, Exam, Result, Done }

public readonly record struct Step(StepKind Kind, int Lesson = -1, int Index = -1);

/// <summary>
/// Where a person is in the tutorial, and what stands between them and the next screen.
///
/// The tutorial exists because people click "Next" through a wall of text and then ask what is
/// in it. So nothing here lets a screen be skipped: a lesson has to be watched to the end
/// before its question appears, the question has to be answered right before the next lesson,
/// and the end is an exam that has to be passed. The front ends only draw this; whether the
/// button is allowed is decided here, where it can be tested.
/// </summary>
public sealed class TutorialSession
{
    private readonly Random _rng;
    private List<Step> _steps = new();
    private readonly HashSet<int> _watched = new();
    private readonly Dictionary<int, Asked> _checks = new();
    private readonly Dictionary<int, HashSet<int>> _tried = new();
    private readonly HashSet<int> _checkPassed = new();
    private readonly HashSet<string> _seenExam = new();
    private List<Asked> _exam = new();
    private int[] _answers = Array.Empty<int>();

    public Course Course { get; }
    public IReadOnlyList<Step> Steps => _steps;
    public int Position { get; private set; }
    public bool TermsAccepted { get; set; }

    public TutorialSession(Course course, bool withTerms, Random? rng = null)
    {
        Course = course;
        _rng = rng ?? new Random();

        if (withTerms) _steps.Add(new Step(StepKind.Terms));
        for (var i = 0; i < course.Lessons.Length; i++)
        {
            _steps.Add(new Step(StepKind.Lesson, i));
            _steps.Add(new Step(StepKind.Check, i));
        }
        _steps.Add(new Step(StepKind.Setup));
        AddExam(Enumerable.Range(0, course.Lessons.Length));
        _steps.Add(new Step(StepKind.Result));
        _steps.Add(new Step(StepKind.Done));
        // Terms already accepted once (the tutorial opened again from Settings): nothing to tick.
        TermsAccepted = !withTerms;
    }

    public Step Current => _steps[Position];

    // ------------------------------------------------------------ lessons

    /// <summary>The animation of the lesson has played through once.</summary>
    public void LessonWatched(int lesson) => _watched.Add(lesson);
    public bool IsWatched(int lesson) => _watched.Contains(lesson);

    /// <summary>The question that follows a lesson, put the same way every time the screen is redrawn.</summary>
    public Asked CheckQuestion(int lesson)
    {
        if (!_checks.TryGetValue(lesson, out var asked))
            _checks[lesson] = asked = Asked.From(Course.Lessons[lesson].Questions[0], lesson, Course.Lessons[lesson].Id + "#0", _rng);
        return asked;
    }

    /// <summary>The answers already tried and found wrong.</summary>
    public IReadOnlyCollection<int> Tried(int lesson)
        => _tried.TryGetValue(lesson, out var set) ? set : Array.Empty<int>();

    /// <summary>True when it was right. A wrong answer is remembered, so it cannot be chosen again.</summary>
    public bool AnswerCheck(int lesson, int option)
    {
        var asked = CheckQuestion(lesson);
        if (option == asked.Correct) { _checkPassed.Add(lesson); return true; }
        if (!_tried.TryGetValue(lesson, out var set)) _tried[lesson] = set = new HashSet<int>();
        set.Add(option);
        return false;
    }

    public bool IsCheckPassed(int lesson) => _checkPassed.Contains(lesson);

    // ------------------------------------------------------------ exam

    public IReadOnlyList<Asked> Exam => _exam;

    public Asked ExamQuestion(int index) => _exam[index];

    /// <summary>The answer given to exam question <paramref name="index"/>, or -1.</summary>
    public int ExamAnswer(int index) => index < _answers.Length ? _answers[index] : -1;

    /// <summary>An exam answer is final. True when it was right; asking again changes nothing.</summary>
    public bool AnswerExam(int index, int option)
    {
        if (_answers[index] >= 0) return _answers[index] == _exam[index].Correct;
        _answers[index] = option;
        return option == _exam[index].Correct;
    }

    public int Score => _answers.Select((a, i) => a >= 0 && a == _exam[i].Correct ? 1 : 0).Sum();
    public bool Passed => _answers.All(a => a >= 0) && Score >= Course.PassMark;

    /// <summary>The lessons whose exam question was missed, in course order.</summary>
    public IReadOnlyList<int> Missed()
        => _exam.Select((q, i) => (q, i)).Where(x => _answers[x.i] != x.q.Correct).Select(x => x.q.Lesson).Distinct().OrderBy(i => i).ToList();

    private void AddExam(IEnumerable<int> lessons)
    {
        var pick = new List<Asked>();
        foreach (var l in lessons)
        {
            var lesson = Course.Lessons[l];
            var pool = Enumerable.Range(1, lesson.Questions.Length - 1).ToList();
            var fresh = pool.Where(i => !_seenExam.Contains(lesson.Id + "#" + i)).ToList();
            if (fresh.Count == 0)
            {
                // All of this lesson's questions have been asked once: start over rather than run dry.
                foreach (var i in pool) _seenExam.Remove(lesson.Id + "#" + i);
                fresh = pool;
            }
            var n = fresh[_rng.Next(fresh.Count)];
            _seenExam.Add(lesson.Id + "#" + n);
            pick.Add(Asked.From(lesson.Questions[n], l, lesson.Id + "#" + n, _rng));
        }
        // Not in the order of the lessons: the exam is not the course read back.
        _exam = pick.OrderBy(_ => _rng.Next()).ToList();
        _answers = Enumerable.Repeat(-1, _exam.Count).ToArray();
        for (var i = 0; i < _exam.Count; i++) _steps.Add(new Step(StepKind.Exam, -1, i));
    }

    // ------------------------------------------------------------ moving

    public bool CanAdvance => Current.Kind switch
    {
        StepKind.Terms => TermsAccepted,
        StepKind.Lesson => IsWatched(Current.Lesson),
        StepKind.Check => IsCheckPassed(Current.Lesson),
        StepKind.Setup => true,
        StepKind.Exam => _answers[Current.Index] >= 0,
        StepKind.Result => Passed,
        StepKind.Done => false,          // the last screen has its own button, which finishes
        _ => false,
    };

    /// <summary>Back is for re-reading. Once the exam has begun there is no going back to look the answer up.</summary>
    public bool CanGoBack => Position > 0 && Current.Kind is StepKind.Lesson or StepKind.Check or StepKind.Setup;

    public bool Next()
    {
        if (!CanAdvance || Position >= _steps.Count - 1) return false;
        Position++;
        return true;
    }

    public bool Back()
    {
        if (!CanGoBack) return false;
        Position--;
        return true;
    }

    /// <summary>
    /// After a failed exam: the lessons that were missed again, each with its question, then a new
    /// exam with other questions where the course has them.
    /// </summary>
    public void Retake()
    {
        var missed = Missed();
        var steps = new List<Step>();
        foreach (var l in missed)
        {
            _watched.Remove(l);
            _checkPassed.Remove(l);
            _checks.Remove(l);
            _tried.Remove(l);
            steps.Add(new Step(StepKind.Lesson, l));
            steps.Add(new Step(StepKind.Check, l));
        }
        _steps = steps;
        AddExam(Enumerable.Range(0, Course.Lessons.Length));
        _steps.Add(new Step(StepKind.Result));
        _steps.Add(new Step(StepKind.Done));
        Position = 0;
    }

    /// <summary>Which of the course's lessons a dots/progress strip should call the current one (1-based), or 0 outside the lessons.</summary>
    public int LessonNumber => Current.Kind is StepKind.Lesson or StepKind.Check ? Current.Lesson + 1 : 0;
}

public enum TutorialNeed
{
    /// <summary>Nothing to ask of this person.</summary>
    None,
    /// <summary>Someone who has used the program before: it is offered once, and they may say no.</summary>
    Offer,
    /// <summary>A new install: the program is not used before the tutorial is passed.</summary>
    Required,
}

/// <summary>What is remembered about the tutorial between runs.</summary>
public static class TutorialState
{
    /// <summary>
    /// Whether this run has to start with the tutorial. A person who has accepted the terms before
    /// (every Windows user of an earlier version) or has seen a "what's new" is not new; they are
    /// offered the tutorial once instead of being made to sit it.
    /// </summary>
    public static TutorialNeed Needed(AppSettings s, int courseVersion, bool? windows = null)
    {
        if (s.TutorialPassed >= courseVersion) return TutorialNeed.None;

        // Begun as a new install and not finished: it stays required, however the program is closed.
        if (s.TutorialRequired) return TutorialNeed.Required;

        // On Windows the terms were always asked, so accepting them is the one sign of an earlier
        // user: nothing else is trusted there, because an installer that carried the builder's
        // settings along would otherwise turn every new person into an old one. Mac and Linux never
        // asked, so what is left is what a used install leaves behind.
        var onWindows = windows ?? OperatingSystem.IsWindows();
        var existing = s.Accept || (!onWindows && (s.LastSeenVersion.Length > 0 || s.ListsUpdatedAt != null));
        if (!existing) return TutorialNeed.Required;
        return s.TutorialOffered ? TutorialNeed.None : TutorialNeed.Offer;
    }

    /// <summary>The tutorial that has to be passed is about to open: from here the program does not start without it.</summary>
    public static void MarkRequired()
    {
        try
        {
            var s = SettingsService.Load();
            if (s.TutorialRequired) return;
            s.TutorialRequired = true;
            SettingsService.Save(s);
        }
        catch { }
    }

    /// <summary>Passed: the terms are accepted, the course is done, and there is nothing left to offer.</summary>
    public static void Complete(int courseVersion)
    {
        var s = SettingsService.Load();
        s.Accept = true;
        s.TutorialPassed = courseVersion;
        s.TutorialOffered = true;
        s.TutorialRequired = false;
        SettingsService.Save(s);
    }

    /// <summary>The person has been shown the invitation and answered it, either way.</summary>
    public static void MarkOffered()
    {
        try
        {
            var s = SettingsService.Load();
            if (s.TutorialOffered) return;
            s.TutorialOffered = true;
            SettingsService.Save(s);
        }
        catch { }
    }
}

/// <summary>The words of the terms of use, which the tutorial opens with.</summary>
public static class TutorialText
{
    public const string Agreement =
        "Tunor — бесплатная программа с открытым исходным кодом, которая управляет туннелем " +
        "sing-box. Устанавливая и используя её, ты соглашаешься со следующим.\n\n" +

        "1. БЕЗ ГАРАНТИЙ\n" +
        "Программа предоставляется «как есть», без каких-либо гарантий — явных или " +
        "подразумеваемых, включая гарантии работоспособности, пригодности для конкретных " +
        "целей и отсутствия ошибок.\n\n" +

        "2. БЕЗ ОТВЕТСТВЕННОСТИ\n" +
        "Автор Tunor не несёт никакой ответственности за любой прямой или косвенный ущерб, " +
        "связанный с использованием программы: потерю или утечку данных, нарушение работы " +
        "сети, блокировку аккаунтов в сторонних сервисах, упущенную выгоду и любые иные " +
        "последствия. Вся ответственность за использование лежит на тебе.\n\n" +

        "3. ЗАКОННОСТЬ ИСПОЛЬЗОВАНИЯ\n" +
        "Ты сам отвечаешь за соблюдение законов своей страны и условий использования " +
        "сервисов, к которым подключаешься. Tunor — это лишь интерфейс; решение о том, как " +
        "его применять, принимаешь ты.\n\n" +

        "4. TUNOR НЕ ЯВЛЯЕТСЯ VPN-СЕРВИСОМ\n" +
        "Программа не предоставляет серверов и не передаёт твой трафик через инфраструктуру " +
        "автора. Туннели ты настраиваешь сам своими конфигурациями от сторонних поставщиков " +
        "(Cloudflare WARP, ProtonVPN и т. п.). Их доступность, скорость и политика " +
        "конфиденциальности — зона ответственности этих поставщиков.\n\n" +

        "5. КЛЮЧИ И КОНФИГИ\n" +
        "Приватные ключи туннелей хранятся локально на твоём компьютере и никуда не " +
        "отправляются. Tunor не собирает телеметрию.\n\n" +

        "6. СТОРОННИЕ КОМПОНЕНТЫ\n" +
        "В состав входит движок sing-box (сборка sing-box-lx) под лицензией GNU GPL v3 — " +
        "полные тексты лицензий и ссылки на исходный код лежат в папке программы: " +
        "THIRD-PARTY-NOTICES.md и licenses/.\n\n" +

        "7. ОТСУТСТВИЕ АФФИЛИАЦИИ\n" +
        "Tunor не аффилирован с проектами sing-box, AmneziaWG, Cloudflare, ProtonVPN и не " +
        "одобрен ими. Их названия используются только для описания совместимости.";
}
