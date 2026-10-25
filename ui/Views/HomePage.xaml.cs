using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Ellipse = System.Windows.Shapes.Ellipse;
using Tunor.Services;

namespace Tunor.Views;

public partial class HomePage : UserControl
{
    public HomePage()
    {
        InitializeComponent();
        ProcessService.StatusChanged += (_, _) => Dispatcher.Invoke(UpdateState);
        UpdateWatcher.Changed += (_, _) => Dispatcher.BeginInvoke(new Action(ShowUpdate));
        HealthMonitor.Updated += () => Dispatcher.BeginInvoke(new Action(RefreshHealth));
        Loaded += (_, _) =>
        {
            RefreshHealth();
            UpdateState();
            ShowUpdate();
            VersionText.Text = "Текущая версия: " + Services.UpdateService.CurrentVersionString;
            RefreshVpnList();
        };
    }

    // ------------------------------------------------------------ tunnels added by link

    /// <summary>One row of the list under the tunnel buttons.</summary>
    private sealed record VpnRow(string Id, string Title, string Badge, string Summary);

    private void RefreshVpnList()
    {
        var rows = new System.Collections.Generic.List<VpnRow>();
        foreach (var t in TunnelService.Load())
        {
            if (t.IsWireguard) continue;                 // WARP and geo have their own buttons
            var where = t.Detour.Length > 0 ? " · через WARP" : "";
            string what;
            if (t.IsSubscription)
                what = $"{t.Nodes.Count} серверов · "
                       + (t.IsAuto ? "быстрейший автоматически" : t.Node);
            else
            {
                var p = ProxyLink.Parse(t.Url);
                what = p.Ok ? p.Summary : "ссылка не читается: " + p.Problem;
            }
            rows.Add(new VpnRow(t.Id, t.Title,
                t.IsSubscription ? "ПОДПИСКА" : t.Kind.ToUpperInvariant(), what + where));
        }
        VpnList.ItemsSource = rows;
        VpnList.Visibility = rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BtnAddVpn_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new AddVpnDialog { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() == true || dlg.Saved) AfterVpnChange();
    }

    private void EditVpn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string id }) return;
        var t = TunnelService.Load().FirstOrDefault(x => x.Id == id);
        if (t == null) return;
        var dlg = new AddVpnDialog(t) { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() == true || dlg.Saved) AfterVpnChange();
    }

    private void DeleteVpn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string id }) return;
        var tunnels = TunnelService.Load();
        var t = tunnels.FirstOrDefault(x => x.Id == id);
        if (t == null) return;

        if (Msg.Show(
                $"Удалить «{t.Title}»?\n\nПравила, которые отправляли трафик в этот туннель, " +
                "останутся на месте, но перестанут действовать — трафик пойдёт напрямую.",
                "Удаление туннеля", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        tunnels.Remove(t);
        TunnelService.Save(tunnels);
        ConfigGenerator.Generate();
        AfterVpnChange();
    }

    private void AfterVpnChange()
    {
        RefreshVpnList();
        UpdateState();
        if (ProcessService.IsRunning)
            Msg.Show("Список туннелей изменён. Перезапусти движок, чтобы это подействовало.",
                "Свой VPN", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void UpdateState()
    {
        var running = ProcessService.IsRunning;
        StateDot.Fill = Ui.Solid(running ? "SuccessBrush" : "DangerBrush");
        StateText.Text = running ? "Запущен" : "Остановлен";
        // One button: "Запустить" when stopped, "Перезапустить" when running.
        BtnStart.Content = running ? "↻  Перезапустить" : "▶  Запустить";
        BtnStart.IsEnabled = true;
        BtnStop.IsEnabled = running;
        UpdateSetupWarning();
    }

    // ------------------------------------------------------------ check

    private async void Check_Click(object sender, RoutedEventArgs e)
    {
        CheckGrid.Children.Clear();
        CheckGrid.RowDefinitions.Clear();

        var why = ConnectionCheck.Unavailable();
        if (why != null)
        {
            CheckGrid.Visibility = Visibility.Collapsed;
            ShowCheckHint(why);
            return;
        }

        BtnCheck.IsEnabled = false;
        BtnCheck.Content = "Проверяю…";
        CheckHint.Visibility = Visibility.Collapsed;
        try
        {
            var results = await ConnectionCheck.RunAsync();
            CheckGrid.Visibility = Visibility.Visible;
            for (var i = 0; i < results.Count; i++) AddCheckRow(i, results[i]);
            ShowCheckHint(Advice(results));
        }
        catch (Exception ex)
        {
            ShowCheckHint("Проверка не удалась: " + ex.Message);
        }
        finally
        {
            BtnCheck.IsEnabled = true;
            BtnCheck.Content = "Проверить снова";
        }
    }

    private void AddCheckRow(int row, PathResult r)
    {
        CheckGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var ok = r.Status == "ok";
        var dim = Ui.Brush("TextDimBrush");

        void Cell(int col, string text, Brush brush, bool bold = false)
        {
            var t = new TextBlock
            {
                Text = text, Foreground = brush, Margin = new Thickness(0, 4, 8, 4),
                FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
                TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetRow(t, row);
            Grid.SetColumn(t, col);
            CheckGrid.Children.Add(t);
        }

        Cell(0, r.Name, Ui.Brush("TextBrush"), bold: true);
        Cell(1, r.Status switch { "ok" => "✓", "fail" => "✗", _ => "—" },
             r.Status switch
             {
                 "ok" => Ui.Brush("AccentBrush"),
                 "fail" => Ui.Brush("DangerBrush"),
                 _ => dim,
             }, bold: true);
        Cell(2, ok ? r.Ip : "", Ui.Brush("TextBrush"));
        Cell(3, ok ? r.Country + (r.Colo.Length > 0 ? " · " + r.Colo : "") : "", Ui.Brush("TextBrush"));
        Cell(4, ok
                ? $"{r.Ms} мс" + (r.ViaWarp ? " · Cloudflare видит WARP" : "")
                : r.Note,
             ok ? dim : r.Status == "fail" ? Ui.Brush("DangerBrush") : dim);
    }

    /// <summary>One line of advice for the most important thing the results show.</summary>
    private static string Advice(System.Collections.Generic.List<PathResult> r)
    {
        var direct = r.Find(x => x.Name == "Напрямую");
        var warp = r.Find(x => x.Name == "WARP");
        var geo = r.Find(x => x.Name == "geo");

        if (warp?.Status == "fail")
            return "WARP не отвечает. Чаще всего конфиг устарел — сгенерируй новый в боте и перетащи .conf на окно. " +
                   "Если и новый не отвечает, сеть может блокировать WireGuard: попробуй конфиг с AmneziaWG 2.0 или 3.x. " +
                   "Либо другую точку выхода — кнопка «Точка выхода WARP» ниже.";
        if (geo?.Status == "fail")
            return "geo не отвечает — сервер geo недоступен или конфиг устарел. WARP при этом работает.";
        if (warp?.Status == "ok" && direct?.Status == "ok" && warp.Country == direct.Country)
            return "Всё работает. WARP выходит в твоей же стране — так и должно быть: он обходит блокировки, " +
                   "но страну не меняет. Для другой страны есть geo.";
        if (direct?.Status == "fail" && warp?.Status == "ok")
            return "Напрямую сервер проверки не открылся — похоже, провайдер его ограничивает. Туннель при этом работает.";
        return "Всё работает.";
    }

    private void WarpPoint_Click(object sender, RoutedEventArgs e)
        => new WarpEndpointDialog { Owner = Window.GetWindow(this) }.ShowDialog();

    // ------------------------------------------------------------ stability

    private void RefreshHealth()
    {
        HealthList.Children.Clear();
        System.Collections.Generic.List<RouteHealth> summary;
        System.Collections.Generic.List<(DateTime At, bool Down, string Text)> events;
        try
        {
            summary = HealthMonitor.Summary(TimeSpan.FromDays(1));
            events = HealthMonitor.Events(TimeSpan.FromDays(7), 5);
        }
        catch { return; }

        if (summary.Count == 0)
        {
            HealthNote.Text = SettingsService.Load().HealthNotify
                ? "Данные появятся через несколько минут после запуска движка."
                : "Слежение выключено — его можно включить в «Настройках».";
            return;
        }

        foreach (var r in summary)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 3) };
            row.Children.Add(new Ellipse
            {
                Width = 9, Height = 9, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center,
                Fill = Ui.Solid(r.Up ? "SuccessBrush" : "DangerBrush"),
            });
            row.Children.Add(new TextBlock
            {
                Text = r.Route, Width = 120, FontWeight = FontWeights.SemiBold, Foreground = Ui.Brush("TextBrush"),
                TextTrimming = TextTrimming.CharacterEllipsis,
            });
            row.Children.Add(new TextBlock
            {
                Text = $"{r.UptimePercent:0.#} % ответов за сутки · " + (r.Up ? $"сейчас отвечает, {r.LastMs} мс" : "сейчас не отвечает"),
                Foreground = Ui.Brush(r.Up ? "TextDimBrush" : "DangerBrush"),
            });
            HealthList.Children.Add(row);
        }
        HealthNote.Text = events.Count == 0
            ? "Перебоев за неделю не было."
            : string.Join("\n", events.Select(x => $"{x.At:d MMM, HH:mm} — {x.Text}"));
    }

    private void ShowCheckHint(string text)
    {
        CheckHint.Text = text;
        CheckHint.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Says so plainly when a tunnel is still the shipped placeholder, or filled in but
    /// broken. Otherwise the only clue was the engine repeating "WireGuard is not ready
    /// yet" in the log while every connection quietly went direct.
    /// </summary>
    private void UpdateSetupWarning()
    {
        string? text = null;
        var warp = ConfigGenerator.WarpState;
        var geo = ConfigGenerator.GeoState;

        // Why the engine died outranks any setup hint: it is the thing actually broken.
        if (!ProcessService.IsRunning && ProcessService.LastFailure != null)
            text = ProcessService.LastFailure;
        // A tunnel that is filled in but broken is left out of the config; say why, or it
        // looks exactly like "not configured" and nobody finds the cause.
        else if (warp.Problem != null)
            text = $"warp.conf не подходит: {warp.Problem}. Туннель WARP выключен, пока его не исправить — " +
                   "открой «Конфиги» → warp.conf или перетащи .conf на окно.";
        else if (warp.Placeholder)
            text = "Конфиг WARP ещё не заполнен — весь трафик идёт напрямую, мимо туннеля. " +
                   "Сгенерируй конфиг и перетащи .conf на окно (или вставь в «Конфиги» → warp.conf).";
        else if (geo.Problem != null)
            text = $"geo.conf не подходит: {geo.Problem}. geo выключен, его правила пока идут через WARP — " +
                   "всё остальное работает. Исправь в «Конфиги» → geo.conf.";
        else if (geo.Placeholder)
            text = "Конфиг geo не заполнен — правила geo-* пока идут через WARP. " +
                   "Это нормально: geo нужен, только если важна страна выхода.";

        SetupWarningText.Text = text ?? "";
        SetupWarning.Visibility = text == null ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void BtnStartOrRestart_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        try
        {
            if (ProcessService.IsRunning)
                await ProcessService.RestartAsync();
            else
                await ProcessService.StartAsync();
        }
        finally { SetBusy(false); UpdateState(); }
    }

    private async void BtnStop_Click(object sender, RoutedEventArgs e)
    {
        SetBusy(true);
        try { await ProcessService.StopAsync(); }
        finally { SetBusy(false); UpdateState(); }
    }

    private void SetBusy(bool busy)
    {
        BtnStart.IsEnabled = !busy;
        BtnStop.IsEnabled = !busy && ProcessService.IsRunning;
    }

    private void BtnGenWarp_Click(object sender, RoutedEventArgs e)
    {
        var dlg = GenerateConfigDialog.ForWarp();
        dlg.Owner = Window.GetWindow(this);
        dlg.ShowDialog();
    }

    /// <summary>
    /// The way in that always works. Dragging depends on what the source app hands over
    /// and on Windows letting it reach a window that runs as administrator; a file picker
    /// depends on neither.
    /// </summary>
    private async void BtnOpenConf_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Выбери файл конфига",
            Filter = "Конфиги WireGuard (*.conf;*.txt)|*.conf;*.txt|Все файлы|*.*",
            Multiselect = true,
            InitialDirectory = FirstExisting(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "Telegram Desktop"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads")),
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) != true) return;

        var good = new System.Collections.Generic.List<string>();
        foreach (var f in dlg.FileNames)
            if (ConfImporter.LooksLikeConf(f)) good.Add(f);

        if (good.Count == 0)
        {
            Msg.Show(
                "Это не похоже на конфиг WireGuard.\n\nВнутри файла должна быть секция [Interface] " +
                "и строка PrivateKey. Расширение при этом любое.",
                "Конфиг не распознан", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (Window.GetWindow(this) is MainWindow main) await main.ImportConfigsAsync(good);
    }

    private static string FirstExisting(params string[] paths)
    {
        foreach (var p in paths)
            if (Directory.Exists(p)) return p;
        return "";
    }

    private void BtnGenGeo_Click(object sender, RoutedEventArgs e)
    {
        var dlg = GenerateConfigDialog.ForGeo();
        dlg.Owner = Window.GetWindow(this);
        dlg.ShowDialog();
    }

    // ------------------------------------------------------------ a new version

    /// <summary>Shows or hides the green banner, from what the background check has found.</summary>
    private void ShowUpdate()
    {
        var found = UpdateWatcher.Available;
        UpdateBanner.Visibility = found == null ? Visibility.Collapsed : Visibility.Visible;
        if (found == null) return;

        UpdateBannerTitle.Text = $"Доступна версия {found.Latest}";
        var headline = FirstLine(found.Notes);
        UpdateBannerText.Text = $"Сейчас {found.Current}" + (headline.Length > 0 ? " · " + headline : "");
    }

    /// <summary>The first line of the release notes, which is the headline — and no more than a line or two.</summary>
    private static string FirstLine(string text)
    {
        var at = text.IndexOfAny(new[] { '\r', '\n' });
        var line = (at < 0 ? text : text[..at]).Trim();
        if (line.Length <= 140) return line;
        var cut = line.LastIndexOf(' ', 140);
        return line[..(cut > 60 ? cut : 140)].TrimEnd(',', ';', ':', ' ') + "…";
    }

    private void BtnUpdateNow_Click(object sender, RoutedEventArgs e)
    {
        if (UpdateWatcher.Available is { } found) ShowUpdateDialog(found);
    }

    /// <summary>The dialog with the release notes and the install button, shared by both ways in.</summary>
    private void ShowUpdateDialog(UpdateInfo info)
    {
        UpdateStatus.Text = $"Доступна версия {info.Latest}";

        var dlg = new UpdateDialog(info) { Owner = Window.GetWindow(this) };
        dlg.ShowDialog();

        if (dlg.InstallStarted)
            (Window.GetWindow(this) as MainWindow)?.ShutdownForUpdate();
        else
            UpdateStatus.Text = $"Доступна версия {info.Latest} — обновление отложено";
    }

    private async void BtnCheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        BtnCheckUpdate.IsEnabled = false;
        UpdateStatus.Text = "Проверяем…";
        try
        {
            var info = await Services.UpdateService.CheckAsync();
            if (!string.IsNullOrEmpty(info.Error))
            {
                UpdateStatus.Text = "Ошибка проверки";
                Msg.Show(info.Error, "Обновление", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            // What was found here is what the banner shows too.
            UpdateWatcher.Set(info.Available ? info : null);

            if (!info.Available)
            {
                UpdateStatus.Text = $"Установлена последняя версия ({info.Current})";
                return;
            }

            ShowUpdateDialog(info);
        }
        catch (Exception ex)
        {
            UpdateStatus.Text = "Ошибка";
            Msg.Show(ex.Message, "Обновление", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            BtnCheckUpdate.IsEnabled = true;
        }
    }

    private void BtnSupport_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("https://www.donationalerts.com/r/nick556655")
            { UseShellExecute = true });
        }
        catch { }
    }

    private void OpenAppFolder_Click(object s, RoutedEventArgs e) => Open(Paths.AppRoot);
    private void OpenSettingsFile_Click(object s, RoutedEventArgs e) => Open(Paths.SettingsJson);
    private void OpenConfigFile_Click(object s, RoutedEventArgs e) => Open(Paths.ConfigJson);
    private void OpenRulesFile_Click(object s, RoutedEventArgs e) => Open(Paths.RulesJson);

    private void Open(string path)
    {
        try
        {
            if (File.Exists(path) || Directory.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch { }
    }
}
