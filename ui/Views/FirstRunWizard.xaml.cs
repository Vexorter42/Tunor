using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Nyx.Services;

namespace Nyx.Views;

public partial class FirstRunWizard : Window
{
    private int _step;
    private bool _listsStarted;
    private Border[] _panels = Array.Empty<Border>();

    private static readonly (string title, string subtitle)[] Steps =
    {
        ("Условия использования", "Прочитай и прими, чтобы продолжить"),
        ("Как это работает",      "Коротко о том, из чего состоит Nyx"),
        ("Списки правил",         "Что пойдёт через туннель, а что напрямую"),
        ("Конфиг WARP",           "Основной туннель — его нужно сгенерировать"),
        ("Готово",                "Осталось пару шагов, и можно пользоваться"),
    };

    private const string Agreement =
        "Nyx — бесплатная программа с открытым исходным кодом, которая управляет туннелем " +
        "sing-box. Устанавливая и используя её, ты соглашаешься со следующим.\n\n" +

        "1. БЕЗ ГАРАНТИЙ\n" +
        "Программа предоставляется «как есть», без каких-либо гарантий — явных или " +
        "подразумеваемых, включая гарантии работоспособности, пригодности для конкретных " +
        "целей и отсутствия ошибок.\n\n" +

        "2. БЕЗ ОТВЕТСТВЕННОСТИ\n" +
        "Автор Nyx не несёт никакой ответственности за любой прямой или косвенный ущерб, " +
        "связанный с использованием программы: потерю или утечку данных, нарушение работы " +
        "сети, блокировку аккаунтов в сторонних сервисах, упущенную выгоду и любые иные " +
        "последствия. Вся ответственность за использование лежит на тебе.\n\n" +

        "3. ЗАКОННОСТЬ ИСПОЛЬЗОВАНИЯ\n" +
        "Ты сам отвечаешь за соблюдение законов своей страны и условий использования " +
        "сервисов, к которым подключаешься. Nyx — это лишь интерфейс; решение о том, как " +
        "его применять, принимаешь ты.\n\n" +

        "4. NYX НЕ ЯВЛЯЕТСЯ VPN-СЕРВИСОМ\n" +
        "Программа не предоставляет серверов и не передаёт твой трафик через инфраструктуру " +
        "автора. Туннели ты настраиваешь сам своими конфигурациями от сторонних поставщиков " +
        "(Cloudflare WARP, ProtonVPN и т. п.). Их доступность, скорость и политика " +
        "конфиденциальности — зона ответственности этих поставщиков.\n\n" +

        "5. КЛЮЧИ И КОНФИГИ\n" +
        "Приватные ключи туннелей хранятся локально на твоём компьютере и никуда не " +
        "отправляются. Nyx не собирает телеметрию.\n\n" +

        "6. СТОРОННИЕ КОМПОНЕНТЫ\n" +
        "В состав входит движок sing-box (сборка sing-box-lx) под лицензией GNU GPL v3 — " +
        "полные тексты лицензий и ссылки на исходный код лежат в папке программы: " +
        "THIRD-PARTY-NOTICES.md и licenses/.\n\n" +

        "7. ОТСУТСТВИЕ АФФИЛИАЦИИ\n" +
        "Nyx не аффилирован с проектами sing-box, AmneziaWG, Cloudflare, ProtonVPN и не " +
        "одобрен ими. Их названия используются только для описания совместимости.";

    public FirstRunWizard()
    {
        InitializeComponent();

        _panels = new[] { Step0, Step1, Step2, Step3, Step4 };
        AgreementText.Text = Agreement;

        // Reopened from Settings — the terms were already accepted once.
        try { ChkAccept.IsChecked = SettingsService.Load().Accept; } catch { }

        BuildDots();
        BuildHowItWorks();
        BuildWarpSteps();
        BuildDoneSteps();
        ShowStep(0);
    }

    // ------------------------------------------------------------ navigation

    private void ShowStep(int index)
    {
        _step = Math.Clamp(index, 0, _panels.Length - 1);
        for (var i = 0; i < _panels.Length; i++)
            _panels[i].Visibility = i == _step ? Visibility.Visible : Visibility.Collapsed;

        StepTitle.Text = Steps[_step].title;
        StepSubtitle.Text = Steps[_step].subtitle;

        BtnBack.Visibility = _step == 0 ? Visibility.Collapsed : Visibility.Visible;
        BtnNext.Content = _step == _panels.Length - 1 ? "Начать пользоваться" : "Далее";
        UpdateNextEnabled();
        BuildDots();

        // First visit to the rule-lists step: start the download right away,
        // unless the lists are already on disk (wizard reopened from Settings).
        if (_step == 2 && !_listsStarted && !HasAnyRuleSet())
            DownloadLists_Click(this, new RoutedEventArgs());
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

    private void UpdateNextEnabled()
        => BtnNext.IsEnabled = _step != 0 || ChkAccept.IsChecked == true;

    private void Accept_Changed(object sender, RoutedEventArgs e) => UpdateNextEnabled();

    private void Back_Click(object sender, RoutedEventArgs e) => ShowStep(_step - 1);

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_step < _panels.Length - 1) { ShowStep(_step + 1); return; }

        // Finished — remember the acceptance so the wizard never shows again.
        try
        {
            var s = SettingsService.Load();
            s.Accept = true;
            SettingsService.Save(s);
        }
        catch { }
        Close();
    }

    private void BuildDots()
    {
        Dots.Items.Clear();
        for (var i = 0; i < Steps.Length; i++)
        {
            var done = i <= _step;
            Dots.Items.Add(new Border
            {
                Width = i == _step ? 26 : 8,
                Height = 8,
                CornerRadius = new CornerRadius(4),
                Margin = new Thickness(0, 0, 6, 0),
                Background = done
                    ? (Brush)FindResource("AccentBrush")
                    : (Brush)FindResource("BorderBrush"),
            });
        }
    }

    // --------------------------------------------------------------- content

    private void BuildHowItWorks()
    {
        Add(HowItWorks, 1, "WARP — основной туннель. Обычно через него идут заблокированные сайты. Конфиг берётся у Telegram-бота, бесплатно.");
        Add(HowItWorks, 2, "geo — необязательный второй туннель для сервисов, которым важна страна выхода (Roblox, TikTok, Google AI). Идёт цепочкой через WARP.");
        Add(HowItWorks, 3, "Правила решают, что куда направить: списки доменов, имена процессов (например Discord.exe) и готовые наборы от сообщества.");
        Add(HowItWorks, 4, "Всё остальное идёт напрямую, без туннеля — скорость не страдает.");
        Add(HowItWorks, 5, "Подсказка: конфиг .conf можно просто перетащить на окно Nyx — он сам поймёт, WARP это или geo.");
    }

    private void BuildWarpSteps()
    {
        Add(WarpSteps, 1, "Открой бота @warp_generator_bot (кнопка ниже).");
        Add(WarpSteps, 2, "Сгенерируй конфиг: провайдер Cloudflare WARP, формат AmneziaWG. Версия AWG — любая, Nyx понимает 1.0, 2.0 и 3.x.");
        Add(WarpSteps, 3, "Бот пришлёт файл .conf — перетащи его прямо на окно Nyx. Или скопируй текст и вставь в раздел «Конфиги» → warp.conf.");
        Add(WarpSteps, 4, "Нажми «Сохранить» — config.json соберётся сам. Если туннель уже запущен, перезапусти его.");
    }

    private void BuildDoneSteps()
    {
        Add(DoneSteps, 1, "Вставь конфиг WARP, если ещё не сделал этого.");
        Add(DoneSteps, 2, "Нажми «Запустить» на главной — появится статус «Подключено».");
        Add(DoneSteps, 3, "Хочешь, чтобы Nyx включался вместе с Windows — включи это в разделе «Автозапуск».");
        Add(DoneSteps, 4, "Если что-то не поднимается — загляни в «Логи», там видно причину.");
        Add(DoneSteps, 5, "Обновления приходят сами: Главная → «Проверить обновления».");
    }

    private void Add(StackPanel host, int n, string text)
    {
        var grid = new Grid { Margin = new Thickness(0, 6, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var badge = new Border
        {
            Background = (Brush)FindResource("AccentBrush"),
            CornerRadius = new CornerRadius(11),
            Width = 22,
            Height = 22,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 1, 12, 0),
            Child = new TextBlock
            {
                Text = n.ToString(),
                Foreground = new SolidColorBrush(Color.FromRgb(0x06, 0x12, 0x0A)),
                FontWeight = FontWeights.Bold,
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        var t = new TextBlock
        {
            Text = text,
            Foreground = (Brush)FindResource("TextBrush"),
            FontSize = 12.5,
            LineHeight = 19,
            TextWrapping = TextWrapping.Wrap,
        };
        Grid.SetColumn(t, 1);
        grid.Children.Add(badge);
        grid.Children.Add(t);
        host.Children.Add(grid);
    }

    // ------------------------------------------------------------ rule lists

    private async void DownloadLists_Click(object sender, RoutedEventArgs e)
    {
        _listsStarted = true;
        BtnDownloadLists.IsEnabled = false;
        ListsBar.Visibility = Visibility.Visible;
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
            ListsBar.Visibility = Visibility.Collapsed;
            ListsStatus.Text = "Не удалось скачать: " + ex.Message +
                               "\nНичего страшного — это можно сделать позже в разделе «Правила».";
        }
        finally
        {
            BtnDownloadLists.IsEnabled = true;
        }
    }

    private void OpenBot_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://t.me/warp_generator_bot") { UseShellExecute = true });
        }
        catch { }
    }
}
