using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Tunor.Desktop.Services;
using Tunor.Services;

namespace Tunor.Desktop.Views;

public partial class HomeView : UserControl
{
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromSeconds(2) };

    public HomeView()
    {
        InitializeComponent();
        // The service is asked on the same tick: with it paired, its answer is the only
        // thing that says whether the tunnel is up, and a stale one freezes the buttons.
        _tick.Tick += async (_, _) => { await EngineService.RefreshServiceCoreAsync(); Refresh(); };
        // The watchdog speaks when the tunnel is down for good; without this the only
        // symptom is the status quietly flipping back to stopped.
        EngineService.Alert += (_, text) => Dispatcher.UIThread.Post(() =>
        {
            StatusText.Text = text;
            StatusText.Foreground = Palette.Brush("DangerBrush");
            Refresh();
        });
        AttachedToVisualTree += async (_, _) =>
        {
            Refresh();
            await EngineService.RefreshServiceCoreAsync();
            Refresh();
            _ = CheckService();
            _tick.Start();
        };
        DetachedFromVisualTree += (_, _) => _tick.Stop();

        UpdateWatcher.Changed += (_, _) => Dispatcher.UIThread.Post(ShowUpdate);
        AttachedToVisualTree += (_, _) => ShowUpdate();

        HealthMonitor.Updated += () => Dispatcher.UIThread.Post(RefreshHealth);
        AttachedToVisualTree += (_, _) => RefreshHealth();
    }

    /// <summary>Shows or hides the green banner, from what the background check has found.</summary>
    private void ShowUpdate()
    {
        var found = UpdateWatcher.Available;
        UpdateBanner.IsVisible = found != null;
        if (found == null) return;

        UpdateBannerTitle.Text = $"Доступна версия {found.Latest}";
        var headline = FirstLine(found.Notes);
        UpdateBannerText.Text = $"Сейчас {found.Current}" + (headline.Length > 0 ? " · " + headline : "");
    }

    private async void UpdateNow_Click(object? sender, RoutedEventArgs e)
    {
        if (UpdateWatcher.Available is { } found)
            await Install(found, UpdateBannerText, BtnUpdateNow);
    }

    private void Refresh()
    {
        var running = EngineService.IsRunning;
        // A service that answers this app is not an engine it cannot touch: the buttons
        // work, so the elevated-stop one is not needed.
        var privileged = EngineService.PrivilegedRunning && !ServiceClient.Paired;
        StateDot.Fill = Palette.Brush(running ? "SuccessBrush" : "DangerBrush");
        StateText.Text = running
            ? ServiceClient.Paired ? "Запущен — через службу"
            : privileged ? "Запущен — с правами администратора"
            : "Запущен"
            // A service sitting on a core that refused to start is not simply stopped,
            // and saying so sends the user to the logs instead of pressing Start again.
            : EngineService.ServiceCoreStatus == "fatal"
                ? "Остановлен — служба не смогла поднять движок, причина в «Логах»"
                : "Остановлен";
        BtnStart.Content = running ? "↻  Перезапустить" : "▶  Запустить";
        BtnStart.IsEnabled = !privileged;
        // An engine this app may not signal needs the other button, and offering the one
        // that cannot work would just be a button that lies.
        BtnStop.IsVisible = !privileged;
        BtnStop.IsEnabled = running;
        BtnStopElevated.IsVisible = privileged;
        UpdateDpiHint();
        UpdateTutorialOffer();

        VersionText.Text = "Текущая версия: " + UpdateService.CurrentVersionString;
        // Said before an update is attempted rather than after it fails: a copy running
        // out of a disk image cannot replace itself, and nothing on screen hints at it.
        InstallHint.Text = UpdateService.InstallProblem() ?? "";
        InstallHint.IsVisible = InstallHint.Text.Length > 0;
        EngineText.Text = EngineService.IsInstalled
            ? Stamp() + "\n" + EngineService.ExePath
            : "не найден по пути " + EngineService.ExePath;
    }

    /// <summary>The engine's own build stamp, written beside it when it was installed.</summary>
    private static string Stamp()
    {
        var path = Path.Combine(Paths.BuildDir, "sing-box.version");
        if (!File.Exists(path)) return "версия не указана";
        try
        {
            foreach (var line in File.ReadAllLines(path))
                if (line.StartsWith("version", StringComparison.OrdinalIgnoreCase))
                    return "sing-box-lx " + line.Split('=', 2)[^1].Trim();
        }
        catch { }
        return "версия не читается";
    }

    private async void Start_Click(object? sender, RoutedEventArgs e)
    {
        BtnStart.IsEnabled = false;
        StatusText.Text = "запускаю…";
        try
        {
            if (EngineService.IsRunning) await EngineService.StopAsync();
            var (ok, msg) = await EngineService.StartAsync();
            StatusText.Text = ok ? "Движок " + msg : "Не удалось запустить: " + msg;
            // A failure is the engine's own words, and the rest of them are one page away.
            StatusText.Foreground = Palette.Brush(ok ? "TextDimBrush" : "DangerBrush");
        }
        finally { BtnStart.IsEnabled = true; Refresh(); }
    }

    private async void Stop_Click(object? sender, RoutedEventArgs e)
    {
        BtnStop.IsEnabled = false;
        StatusText.Text = "останавливаю…";
        try
        {
            var (ok, msg) = await EngineService.StopAsync();
            StatusText.Text = ok ? "Движок " + msg : "Не удалось остановить: " + msg;
            StatusText.Foreground = Palette.Brush(ok ? "TextDimBrush" : "DangerBrush");
        }
        finally { BtnStop.IsEnabled = true; Refresh(); }
    }

    private async void StopElevated_Click(object? sender, RoutedEventArgs e)
    {
        BtnStopElevated.IsEnabled = false;
        StatusText.Text = "запрашиваю права…";
        try
        {
            var (ok, msg) = await EngineService.StopElevatedAsync();
            StatusText.Text = ok ? "Движок " + msg : "Не удалось остановить: " + msg;
            StatusText.Foreground = Palette.Brush(ok ? "TextDimBrush" : "DangerBrush");
        }
        finally { BtnStopElevated.IsEnabled = true; Refresh(); }
    }

    /// <summary>
    /// The card's words, which differ by system because the mechanism does. On Linux
    /// the right is a capability on the engine's own file and is granted once, with
    /// nothing asked afterwards; elsewhere it belongs to a service that has to be
    /// installed and kept.
    /// </summary>
    private void Rights()
    {
        var linux = OperatingSystem.IsLinux();
        RightsText.Text = linux
            ? "Перехват всего трафика поднимает сетевой интерфейс, а на это нужны права root. "
              + "Их можно выдать один раз самому движку — после этого пароль не спрашивают вовсе."
            : "Перехват всего трафика поднимает сетевой интерфейс, а на это нужны права root. "
              + "Без службы приложение спрашивает пароль при каждом запуске; со службой — "
              + "один раз при её установке.";
        BtnInstall.Content = linux ? "Выдать права движку" : "Установить службу";
        BtnUninstall.Content = linux ? "Снять права" : "Удалить службу";
    }

    private async void Service_Click(object? sender, RoutedEventArgs e) => await CheckService();

    private async System.Threading.Tasks.Task CheckService()
    {
        Rights();
        ServiceText.Text = "проверяю…";
        BtnInstall.IsVisible = false;
        var state = await EngineService.PrivilegedService();
        ServiceText.Text = EngineService.Explain(state)
                           + (ServiceClient.Paired ? " · приложение подключено" : "");
        // With a service installed the daemon owns the core, and the app's own Start
        // would raise a second engine against it. Saying so beats letting them collide.
        StateHint.Text = state == EngineService.ServiceState.Running && !ServiceClient.Paired
            ? "Служба стоит, но приложение к ней не подключено — переустанови её, чтобы кнопки заработали."
            : "";
        StateHint.IsVisible = StateHint.Text.Length > 0;
        var good = state == EngineService.ServiceState.Running;
        ServiceText.Foreground = Palette.Brush(good ? "AccentBrush" : "TextDimBrush");
        // Offering to install it only where this app can: elsewhere the engine's own
        // command does it, and saying so is better than a button that cannot work.
        BtnInstall.IsVisible = (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux()) && state is
            EngineService.ServiceState.NotInstalled or
            EngineService.ServiceState.NeedsReinstall or
            EngineService.ServiceState.CopyOnly;
        // Removing it is the engine's only documented off switch, so it is offered
        // wherever the service exists at all.
        BtnUninstall.IsVisible = (OperatingSystem.IsMacOS() || OperatingSystem.IsLinux()) && state is
            EngineService.ServiceState.Running or
            EngineService.ServiceState.Stopped or
            EngineService.ServiceState.NeedsReinstall;
    }

    private async void Uninstall_Click(object? sender, RoutedEventArgs e)
    {
        BtnUninstall.IsEnabled = false;
        ServiceText.Text = "запрашиваю права…";
        try
        {
            var (ok, msg) = await EngineService.UninstallService();
            ServiceText.Text = ok ? msg : "не удалось: " + msg;
            await CheckService();
        }
        finally { BtnUninstall.IsEnabled = true; Refresh(); }
    }

    // ------------------------------------------------------------ updates and links

    /// <summary>The update found by the last check, kept for the button beside it.</summary>
    private UpdateInfo? _update;

    private async void Update_Click(object? sender, RoutedEventArgs e)
    {
        BtnUpdate.IsEnabled = false;
        BtnInstallUpdate.IsVisible = false;
        UpdateStatus.Text = "проверяю…";
        try
        {
            var info = await UpdateService.CheckAsync();
            _update = info.Available ? info : null;
            // What was found here is what the banner shows too.
            if (info.Error is not { Length: > 0 }) UpdateWatcher.Set(_update);
            UpdateStatus.Text = info.Error is { Length: > 0 } err ? err
                : info.Kind == UpdateKind.None ? "Установлена последняя версия."
                : $"Доступна {info.Latest}"
                  + (info.Notes is { Length: > 0 } n ? " — " + FirstLine(n) : "");
            BtnInstallUpdate.IsVisible = _update != null;
        }
        catch (Exception ex) { UpdateStatus.Text = "Не удалось проверить: " + ex.Message; }
        finally { BtnUpdate.IsEnabled = true; }
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

    /// <summary>
    /// Downloads the new bundle and hands the swap to a helper, then quits: the app
    /// cannot replace the folder it is running out of, so it has to be gone first.
    /// </summary>
    private async void InstallUpdate_Click(object? sender, RoutedEventArgs e)
    {
        if (_update != null) await Install(_update, UpdateStatus, BtnInstallUpdate);
    }

    /// <summary>
    /// The download and the swap, with progress written into <paramref name="status"/> and
    /// <paramref name="button"/> held off while it runs — one place for the banner and the
    /// Updates card.
    /// </summary>
    private async System.Threading.Tasks.Task Install(UpdateInfo info, TextBlock status, Button button)
    {
        button.IsEnabled = false;
        status.Text = "скачиваю…";
        try
        {
            var progress = new Progress<double>(p => Dispatcher.UIThread.Post(
                () => status.Text = $"скачиваю… {p * 100:0}%"));
            var (ok, message) = await UpdateService.DownloadAndApplyAsync(info, progress);
            status.Text = message;
            if (!ok) { button.IsEnabled = true; return; }

            // A moment for the message to be read, then out of the way of the swap. Out of the
            // way means gone: closing the window only hides it (see App.Quit).
            await System.Threading.Tasks.Task.Delay(1500);
            App.Quit();
        }
        catch (Exception ex)
        {
            status.Text = "Не удалось обновить: " + ex.Message;
            button.IsEnabled = true;
        }
    }

    private void OpenPath_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string which }) return;
        var path = which switch
        {
            "settings" => Paths.SettingsJson,
            "config" => Paths.ConfigJson,
            "rules" => Paths.RulesJson,
            _ => Paths.AppRoot,
        };
        Launch(path, reveal: which != "root");
    }

    private void Donate_Click(object? sender, RoutedEventArgs e)
        => Launch("https://www.donationalerts.com/r/nick556655", reveal: false);

    /// <summary>Hands a path or a link to the system to open as it sees fit.</summary>
    private void Launch(string target, bool reveal)
    {
        try
        {
            var (file, args) = OperatingSystem.IsMacOS()
                ? ("open", reveal ? $"-R \"{target}\"" : $"\"{target}\"")
                : OperatingSystem.IsWindows()
                    ? ("explorer.exe", reveal ? $"/select,\"{target}\"" : $"\"{target}\"")
                    : ("xdg-open", $"\"{target}\"");
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(file, args) { UseShellExecute = false });
        }
        catch (Exception ex) { StatusText.Text = "Не удалось открыть: " + ex.Message; }
    }

    // ------------------------------------------------------------ connection check

    /// <summary>One line of the check: what came back through one path.</summary>
    public sealed record CheckRow(string Name, string Mark, IBrush? MarkBrush,
                                  string Ip, string Country, string Note);

    private async void Check_Click(object? sender, RoutedEventArgs e)
    {
        var why = ConnectionCheck.Unavailable();
        if (why != null)
        {
            CheckList.ItemsSource = null;
            CheckNote.Text = why;
            return;
        }

        BtnCheck.IsEnabled = false;
        CheckNote.Text = "проверяю…";
        CheckList.ItemsSource = null;
        try
        {
            var results = await ConnectionCheck.RunAsync();
            CheckList.ItemsSource = results.Select(r => new CheckRow(
                r.Name,
                r.Status switch { "ok" => "✓", "fail" => "✕", _ => "—" },
                Palette.Brush(r.Status switch
                {
                    "ok" => "AccentBrush", "fail" => "DangerBrush", _ => "TextDimBrush",
                }),
                r.Status == "ok" ? r.Ip : "",
                r.Status == "ok" ? r.Country + (r.Colo.Length > 0 ? " · " + r.Colo : "") : "",
                r.Status == "ok"
                    ? $"{r.Ms} мс" + (r.ViaWarp ? " · Cloudflare видит WARP" : "")
                    : r.Note)).ToList();

            var good = results.Count(r => r.Status == "ok");
            CheckNote.Text = good == 0
                ? "Ни один путь не ответил. Посмотри «Логи» — там причина."
                : $"Ответило путей: {good} из {results.Count}.";
        }
        catch (Exception ex) { CheckNote.Text = "Проверка не удалась: " + ex.Message; }
        finally { BtnCheck.IsEnabled = true; }
    }

    private void UpdateTutorialOffer()
    {
        try { TutorialCard.IsVisible = TutorialState.Needed(SettingsService.Load(), Course.Load().Version) == TutorialNeed.Offer; }
        catch { TutorialCard.IsVisible = false; }
    }

    private async void StartTutorial_Click(object? sender, RoutedEventArgs e)
    {
        TutorialState.MarkOffered();
        TutorialCard.IsVisible = false;
        await new TutorialWindow(false).ShowDialog(this);
    }

    private void SkipTutorial_Click(object? sender, RoutedEventArgs e)
    {
        TutorialState.MarkOffered();
        TutorialCard.IsVisible = false;
    }

    private void UpdateDpiHint()
    {
        var hint = DpiTools.Hint();
        DpiText.Text = hint ?? "";
        DpiCard.IsVisible = hint != null;
    }

    private void HideDpi_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            var s = SettingsService.Load();
            s.HideDpiHint = true;
            SettingsService.Save(s);
        }
        catch { }
        UpdateDpiHint();
        UpdateTutorialOffer();
    }

    private async void WarpPoint_Click(object? sender, RoutedEventArgs e)
        => await new WarpEndpointWindow().ShowDialog(this);

    // ------------------------------------------------------------ stability

    private void RefreshHealth()
    {
        HealthList.Children.Clear();
        List<RouteHealth> summary;
        List<(DateTime At, bool Down, string Text)> events;
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
            var row = new StackPanel { Orientation = Avalonia.Layout.Orientation.Horizontal, Spacing = 10 };
            row.Children.Add(new Avalonia.Controls.Shapes.Ellipse
            {
                Width = 9, Height = 9, VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                Fill = Palette.Brush(r.Up ? "SuccessBrush" : "DangerBrush"),
            });
            row.Children.Add(new TextBlock { Text = r.Route, Width = 120, FontWeight = Avalonia.Media.FontWeight.SemiBold });
            row.Children.Add(new TextBlock
            {
                Text = $"{r.UptimePercent:0.#} % ответов за сутки · " + (r.Up ? $"сейчас отвечает, {r.LastMs} мс" : "сейчас не отвечает"),
                Foreground = Palette.Brush(r.Up ? "TextDimBrush" : "DangerBrush"),
            });
            HealthList.Children.Add(row);
        }
        HealthNote.Text = events.Count == 0
            ? "Перебоев за неделю не было."
            : string.Join("\n", events.Select(x => $"{x.At:d MMM, HH:mm} — {x.Text}"));
    }

    private async void Install_Click(object? sender, RoutedEventArgs e)
    {
        BtnInstall.IsEnabled = false;
        ServiceText.Text = "запрашиваю права…";
        try
        {
            var (ok, msg) = await EngineService.InstallService();
            ServiceText.Text = ok ? msg : "не установлено: " + msg;
            if (ok) await CheckService();
        }
        finally { BtnInstall.IsEnabled = true; }
    }

}
