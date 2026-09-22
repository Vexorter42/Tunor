using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Nyx.Services;

namespace Nyx.Views;

public partial class HomePage : UserControl
{
    public HomePage()
    {
        InitializeComponent();
        ProcessService.StatusChanged += (_, _) => Dispatcher.Invoke(UpdateState);
        Loaded += (_, _) =>
        {
            UpdateState();
            VersionText.Text = "Текущая версия: " + Services.UpdateService.CurrentVersionString;
        };
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
        Cell(3, ok ? r.Country : "", Ui.Brush("TextBrush"));
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
                   "Если и новый не отвечает, сеть может блокировать WireGuard: попробуй конфиг с AmneziaWG 2.0 или 3.x.";
        if (geo?.Status == "fail")
            return "geo не отвечает — сервер geo недоступен или конфиг устарел. WARP при этом работает.";
        if (warp?.Status == "ok" && direct?.Status == "ok" && warp.Country == direct.Country)
            return "Всё работает. WARP выходит в твоей же стране — так и должно быть: он обходит блокировки, " +
                   "но страну не меняет. Для другой страны есть geo.";
        if (direct?.Status == "fail" && warp?.Status == "ok")
            return "Напрямую сервер проверки не открылся — похоже, провайдер его ограничивает. Туннель при этом работает.";
        return "Всё работает.";
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

    private void BtnGenGeo_Click(object sender, RoutedEventArgs e)
    {
        var dlg = GenerateConfigDialog.ForGeo();
        dlg.Owner = Window.GetWindow(this);
        dlg.ShowDialog();
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
                MessageBox.Show(info.Error, "Обновление", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            if (!info.Available)
            {
                UpdateStatus.Text = $"Установлена последняя версия ({info.Current})";
                return;
            }

            UpdateStatus.Text = $"Доступна версия {info.Latest}";

            var dlg = new UpdateDialog(info) { Owner = Window.GetWindow(this) };
            dlg.ShowDialog();

            if (dlg.InstallStarted)
                (Window.GetWindow(this) as MainWindow)?.ShutdownForUpdate();
            else
                UpdateStatus.Text = $"Доступна версия {info.Latest} — обновление отложено";
        }
        catch (Exception ex)
        {
            UpdateStatus.Text = "Ошибка";
            MessageBox.Show(ex.Message, "Обновление", MessageBoxButton.OK, MessageBoxImage.Error);
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
            Process.Start(new ProcessStartInfo("https://www.donationalerts.com/r/vexorter")
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
