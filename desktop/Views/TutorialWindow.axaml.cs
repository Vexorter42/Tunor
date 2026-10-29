using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Tunor.Services;

namespace Tunor.Desktop.Views;

/// <summary>
/// The tutorial: terms, six short animated lessons each followed by a question, the two things to
/// set up, an exam, and a way in. It is how a new person learns what the program is, and it cannot
/// be clicked through — see <see cref="TutorialSession"/> for what stands between each screen and
/// the next. The text is in tutorial/course.json, the pictures in tutorial/scenes; this draws.
/// The same screens as the Windows front end's TutorialDialog.
/// </summary>
public partial class TutorialWindow : DialogPanel
{
    /// <summary>The way in is not one stray key from closing.</summary>
    public override bool DismissOnEscape => false;

    /// <summary>True when the person got to the end; false when they closed it.</summary>
    public bool Completed { get; private set; }

    private readonly TutorialSession _s;
    private readonly bool _required;
    private readonly Course _course;
    private readonly Border[] _panels;
    private readonly DispatcherTimer _warpTimer = new() { Interval = TimeSpan.FromMilliseconds(1200) };
    private readonly bool _withTerms;
    private bool _listsStarted;
    private bool _asking;

    public TutorialWindow() : this(false) { }

    /// <param name="required">A new install: closing it closes the program too, and the confirmation says so.</param>
    public TutorialWindow(bool required)
    {
        InitializeComponent();
        _required = required;
        _course = Course.Load();

        bool accepted;
        try { accepted = SettingsService.Load().Accept; } catch { accepted = false; }
        _withTerms = !accepted;
        _s = new TutorialSession(_course, _withTerms);

        _panels = new[] { PTerms, PLesson, PCheck, PSetup, PExam, PResult, PDone };
        AgreementText.Text = TutorialText.Agreement;
        ChkAccept.Content = _course.TermsAccept;
        SetupLists.Text = _course.SetupLists;
        SetupBot.Text = _course.SetupBot;
        SetupAdd.Text = _course.SetupAdd;

        LessonScene.Finished += (_, _) =>
        {
            if (_s.Current.Kind == StepKind.Lesson) _s.LessonWatched(_s.Current.Lesson);
            LessonBar.Value = 1;
            UpdateNav();
        };
        LessonScene.Tick += p => LessonBar.Value = p;

        _warpTimer.Tick += (_, _) => ShowWarpStatus();
        DetachedFromVisualTree += (_, _) => { _warpTimer.Stop(); LessonScene.Stop(); DoneScene.Stop(); };

        Render();
    }

    // ------------------------------------------------------------ closing

    public override void Dismiss()
    {
        if (_asking) return;
        _ = AskToClose();
    }

    private async Task AskToClose()
    {
        _asking = true;
        try
        {
            var ask = new ConfirmWindow(
                "Закрыть обучение?",
                _required ? "Без обучения Tunor не запустится: программа закроется."
                          : "Пройденное не сохранится.",
                _required ? "Выйти" : "Закрыть");
            await ask.ShowDialog(this);
            if (ask.Confirmed) Close();
        }
        finally { _asking = false; }
    }

    private void Finish()
    {
        try { TutorialState.Complete(_course.Version); }
        catch { /* the settings file is not writable: the tutorial will come again, which is the lesser harm */ }
        Completed = true;
        Close();
    }

    // ------------------------------------------------------------ drawing the current step

    private void Render()
    {
        LessonScene.Stop();
        DoneScene.Stop();
        _warpTimer.Stop();
        var step = _s.Current;
        foreach (var p in _panels) p.IsVisible = false;

        switch (step.Kind)
        {
            case StepKind.Terms: RenderTerms(); break;
            case StepKind.Lesson: RenderLesson(step.Lesson); break;
            case StepKind.Check: RenderCheck(step.Lesson); break;
            case StepKind.Setup: RenderSetup(); break;
            case StepKind.Exam: RenderExam(step.Index); break;
            case StepKind.Result: RenderResult(); break;
            default: RenderDone(); break;
        }
        BuildProgress();
        UpdateNav();
    }

    private void RenderTerms()
    {
        PTerms.IsVisible = true;
        StepTitle.Text = _course.TermsTitle;
        StepSubtitle.Text = _course.TermsLead;
    }

    private void RenderLesson(int index)
    {
        var lesson = _course.Lessons[index];
        PLesson.IsVisible = true;
        StepTitle.Text = lesson.Title;
        StepSubtitle.Text = lesson.Lead;

        LessonPoints.Children.Clear();
        foreach (var point in lesson.Points) AddPoint(LessonPoints, point);

        LessonBar.Value = 0;
        LessonScene.Play(Scene.Load(lesson.Scene));
    }

    private void RenderCheck(int index)
    {
        var lesson = _course.Lessons[index];
        var q = _s.CheckQuestion(index);
        PCheck.IsVisible = true;
        StepTitle.Text = "Проверь себя";
        StepSubtitle.Text = $"Урок {index + 1}: {lesson.Title}";
        CheckKicker.Text = "Выбери верный ответ";
        CheckQuestion.Text = q.Source.Text;

        var passed = _s.IsCheckPassed(index);
        var tried = _s.Tried(index);
        FillOptions(CheckOptions, q,
            i => passed ? (i == q.Correct ? Mark.Right : Mark.Muted)
                        : tried.Contains(i) ? Mark.Wrong : Mark.Normal,
            passed ? null : i => { _s.AnswerCheck(index, i); Render(); });

        if (passed)
            Feedback(CheckFeedback, "Верно. " + q.Source.Why, true);
        else if (tried.Count > 0)
            Feedback(CheckFeedback, "Не то. Подумай ещё. Если нужно — «Назад» вернёт к уроку.", false);
        else
            CheckFeedback.Text = "";
    }

    private void RenderSetup()
    {
        PSetup.IsVisible = true;
        StepTitle.Text = _course.SetupTitle;
        StepSubtitle.Text = _course.SetupLead;
        ShowWarpStatus();
        _warpTimer.Start();

        if (!_listsStarted && !HasAnyRuleSet()) DownloadLists_Click(this, new RoutedEventArgs());
    }

    private void RenderExam(int index)
    {
        var q = _s.ExamQuestion(index);
        PExam.IsVisible = true;
        StepTitle.Text = _course.ExamTitle;
        StepSubtitle.Text = _course.ExamLead;
        ExamKicker.Text = "Выбери верный ответ";
        ExamQuestion.Text = q.Source.Text;

        var given = _s.ExamAnswer(index);
        FillOptions(ExamOptions, q,
            i => given < 0 ? Mark.Normal
               : i == q.Correct ? Mark.Right
               : i == given ? Mark.Wrong : Mark.Muted,
            given >= 0 ? null : i => { _s.AnswerExam(index, i); Render(); });

        if (given < 0) ExamFeedback.Text = "";
        else if (given == q.Correct) Feedback(ExamFeedback, "Верно. " + q.Source.Why, true);
        else Feedback(ExamFeedback, "Неверно. Правильный ответ выделен. " + q.Source.Why, false);
    }

    private void RenderResult()
    {
        PResult.IsVisible = true;
        var total = _s.Exam.Count;
        StepTitle.Text = "Итог";
        StepSubtitle.Text = "";
        ResultBody.Children.Clear();

        if (_s.Passed)
        {
            ResultHead.Text = string.Format(_course.ExamPass, _s.Score, total);
            ResultHead.Foreground = Palette.Brush("AccentBrush");
            AddText(ResultBody, "Можно переходить дальше.", dim: true);
        }
        else
        {
            ResultHead.Text = string.Format(_course.ExamFail, _s.Score, total, _course.PassMark);
            ResultHead.Foreground = Palette.Brush("DangerBrush");
        }

        var wrong = _s.Exam.Select((q, i) => (q, i)).Where(x => _s.ExamAnswer(x.i) != x.q.Correct).OrderBy(x => x.q.Lesson).ToList();
        if (wrong.Count > 0)
        {
            AddText(ResultBody, _s.Passed ? "Что стоит запомнить:" : "Где ошибся:", bold: true, margin: new Thickness(0, 12, 0, 4));
            foreach (var (q, _) in wrong)
            {
                var lesson = _course.Lessons[q.Lesson];
                AddText(ResultBody, $"Урок «{lesson.Title}»", dim: true, margin: new Thickness(0, 10, 0, 0));
                AddText(ResultBody, q.Source.Text, bold: true);
                AddText(ResultBody, "Верно: " + q.Options[q.Correct], accent: true);
                AddText(ResultBody, q.Source.Why, dim: true);
            }
        }
    }

    private void RenderDone()
    {
        PDone.IsVisible = true;
        StepTitle.Text = _course.DoneTitle;
        StepSubtitle.Text = _course.DoneLead;
        DonePoints.Children.Clear();
        foreach (var p in _course.DonePoints) AddPoint(DonePoints, p);
        DoneScene.Play(Scene.Load(_course.DoneScene));
    }

    // ------------------------------------------------------------ the strip and the buttons

    private void BuildProgress()
    {
        var n = _course.Lessons.Length;
        var first = _withTerms ? 1 : 0;
        var segments = first + n + 2;                       // terms?, lessons, setup, exam
        var step = _s.Current;
        var at = step.Kind switch
        {
            StepKind.Terms => 0,
            StepKind.Lesson or StepKind.Check => first + step.Lesson,
            StepKind.Setup => first + n,
            StepKind.Exam or StepKind.Result => first + n + 1,
            _ => segments,                                  // done: all of it
        };

        ProgressText.Text = step.Kind switch
        {
            StepKind.Lesson => $"Урок {step.Lesson + 1} из {n}",
            StepKind.Check => $"Вопрос после урока {step.Lesson + 1}",
            StepKind.Exam => $"Вопрос {step.Index + 1} из {_s.Exam.Count}",
            _ => "",
        };

        Progress.Children.Clear();
        for (var i = 0; i < segments; i++)
        {
            Progress.Children.Add(new Border
            {
                Width = i == at ? 46 : 30,
                Height = 5,
                CornerRadius = new CornerRadius(3),
                Margin = new Thickness(0, 0, 6, 0),
                Background = Palette.Brush(i <= at ? "AccentBrush" : "BorderBrush"),
            });
        }
    }

    private void UpdateNav()
    {
        var step = _s.Current;
        BtnBack.IsVisible = _s.CanGoBack;

        BtnNext.Content = step.Kind switch
        {
            StepKind.Lesson => "Проверить себя",
            StepKind.Setup => "К проверке",
            StepKind.Result => _s.Passed ? "Далее" : "Повторить пропущенное",
            StepKind.Done => "Начать пользоваться",
            StepKind.Exam => step.Index == _s.Exam.Count - 1 ? "Результат" : "Далее",
            _ => "Далее",
        };
        // The last screen and a failed result have buttons of their own; the rest wait to be allowed.
        BtnNext.IsEnabled = _s.CanAdvance || step.Kind == StepKind.Done || (step.Kind == StepKind.Result && !_s.Passed);

        NavHint.Text = step.Kind switch
        {
            StepKind.Lesson => _s.IsWatched(step.Lesson) ? "Можно переходить к вопросу" : "Досмотри картинку до конца — тогда откроется вопрос",
            StepKind.Check => _s.IsCheckPassed(step.Lesson) ? "" : "Без верного ответа дальше не пустит",
            StepKind.Exam => _s.ExamAnswer(step.Index) < 0 ? "Ответ нельзя изменить" : "",
            StepKind.Terms => _s.TermsAccepted ? "" : "Нужно принять условия, чтобы продолжить",
            _ => "",
        };
    }

    private void Accept_Changed(object? sender, RoutedEventArgs e)
    {
        _s.TermsAccepted = ChkAccept.IsChecked == true;
        UpdateNav();
    }

    private void Back_Click(object? sender, RoutedEventArgs e)
    {
        if (_s.Back()) Render();
    }

    private void Next_Click(object? sender, RoutedEventArgs e)
    {
        var kind = _s.Current.Kind;
        if (kind == StepKind.Done) { Finish(); return; }
        if (kind == StepKind.Result && !_s.Passed) { _s.Retake(); Render(); return; }
        if (_s.Next()) Render();
    }

    // ------------------------------------------------------------ pieces

    private enum Mark { Normal, Right, Wrong, Muted }

    private static void FillOptions(StackPanel host, Asked q, Func<int, Mark> mark, Action<int>? pick)
    {
        host.Children.Clear();
        for (var i = 0; i < q.Options.Length; i++)
        {
            var index = i;
            var m = mark(i);
            var button = new Button
            {
                Content = new TextBlock { Text = q.Options[i], TextWrapping = TextWrapping.Wrap, LineHeight = 19 },
            };
            button.Classes.Add("option");
            switch (m)
            {
                case Mark.Right: button.Classes.Add("right"); break;
                case Mark.Wrong: button.Classes.Add("wrong"); break;
                case Mark.Muted: button.Classes.Add("muted"); break;
            }
            if (pick == null || m == Mark.Wrong)
            {
                // Decided: it looks as it was marked, and nothing on it can be clicked.
                button.IsHitTestVisible = false;
                button.Focusable = false;
            }
            else button.Click += (_, _) => pick(index);
            host.Children.Add(button);
        }
    }

    private static void Feedback(TextBlock box, string text, bool good)
    {
        box.Text = text;
        box.Foreground = Palette.Brush(good ? "AccentBrush" : "DangerBrush");
    }

    private static void AddPoint(StackPanel host, string text)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), Margin = new Thickness(0, 3, 0, 3) };
        var dot = new Border
        {
            Width = 7, Height = 7, CornerRadius = new CornerRadius(4),
            Background = Palette.Brush("AccentBrush"),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 6, 12, 0),
        };
        var tb = new TextBlock
        {
            Text = text,
            FontSize = 12.5,
            LineHeight = 18,
            TextWrapping = TextWrapping.Wrap,
        };
        Grid.SetColumn(tb, 1);
        grid.Children.Add(dot);
        grid.Children.Add(tb);
        host.Children.Add(grid);
    }

    private static void AddText(StackPanel host, string text, bool dim = false, bool bold = false, bool accent = false, Thickness? margin = null)
    {
        host.Children.Add(new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12.5,
            LineHeight = 19,
            FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal,
            Foreground = Palette.Brush(accent ? "AccentBrush" : dim ? "TextDimBrush" : "TextBrush"),
            Margin = margin ?? new Thickness(0, 2, 0, 0),
        });
    }

    // ------------------------------------------------------------ setting up

    private void ShowWarpStatus()
    {
        var ok = false;
        try { ok = ConfigGenerator.WarpConfigured; } catch { }
        WarpStatus.Text = (ok ? "✓  " : "○  ") + (ok ? _course.SetupFound : _course.SetupMissing);
        WarpStatus.Foreground = Palette.Brush(ok ? "AccentBrush" : "TextDimBrush");
    }

    private static bool HasAnyRuleSet()
    {
        try
        {
            return Directory.Exists(Paths.RulesetsDir) &&
                   Directory.EnumerateFiles(Paths.RulesetsDir, "*.srs").Any();
        }
        catch { return false; }
    }

    private async void DownloadLists_Click(object? sender, RoutedEventArgs e)
    {
        _listsStarted = true;
        BtnDownloadLists.IsEnabled = false;
        ListsBar.IsVisible = true;
        ListsBar.IsIndeterminate = true;
        ListsStatus.Text = "Скачиваем списки…";

        try
        {
            var groups = RulesService.Load();
            // Same mirror the OTA updater uses (update.json) — GitHub itself is blocked
            // in some countries.
            var mirror = UpdateConfig.Load().Mirror;
            var results = await RulesetDownloader.DownloadAllAsync(groups, mirror);
            var ok = results.Count(r => r.Ok);

            if (ok > 0)
            {
                RulesService.Save(groups);
                ConfigGenerator.Generate();
                ListsUpdater.MarkUpdated();
            }

            ListsBar.IsIndeterminate = false;
            ListsBar.Value = 100;
            ListsStatus.Text = ok == results.Count
                ? $"Готово — скачано списков: {ok}"
                : $"Скачано {ok} из {results.Count}. Остальные можно дотянуть позже в разделе «Правила».";
        }
        catch (Exception ex)
        {
            ListsBar.IsVisible = false;
            ListsStatus.Text = "Не удалось скачать: " + ex.Message +
                               "\nНичего страшного — это можно сделать позже в разделе «Правила».";
        }
        finally
        {
            BtnDownloadLists.IsEnabled = true;
        }
    }

    private void OpenBot_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var file = OperatingSystem.IsMacOS() ? "open" : OperatingSystem.IsWindows() ? "explorer.exe" : "xdg-open";
            Process.Start(new ProcessStartInfo(file, "\"https://t.me/warp_generator_bot\"") { UseShellExecute = false });
        }
        catch { }
    }
}
