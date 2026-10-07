using System;
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
        _tick.Tick += (_, _) => Refresh();
        // The watchdog speaks when the tunnel is down for good; without this the only
        // symptom is the status quietly flipping back to stopped.
        EngineService.Alert += (_, text) => Dispatcher.UIThread.Post(() =>
        {
            StatusText.Text = text;
            StatusText.Foreground = Palette.Brush("DangerBrush");
            Refresh();
        });
        AttachedToVisualTree += (_, _) => { Refresh(); _ = CheckService(); _tick.Start(); };
        DetachedFromVisualTree += (_, _) => _tick.Stop();
    }

    private void Refresh()
    {
        var running = EngineService.IsRunning;
        var privileged = EngineService.PrivilegedRunning;
        StateDot.Fill = Palette.Brush(running ? "SuccessBrush" : "DangerBrush");
        StateText.Text = running
            ? (privileged ? "Запущен — с правами администратора" : "Запущен")
            : "Остановлен";
        BtnStart.Content = running ? "↻  Перезапустить" : "▶  Запустить";
        BtnStart.IsEnabled = !privileged;
        // An engine this app may not signal needs the other button, and offering the one
        // that cannot work would just be a button that lies.
        BtnStop.IsVisible = !privileged;
        BtnStop.IsEnabled = running;
        BtnStopElevated.IsVisible = privileged;

        VersionText.Text = "Текущая версия: " + UpdateService.CurrentVersionString;
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

    private async void Service_Click(object? sender, RoutedEventArgs e) => await CheckService();

    private async System.Threading.Tasks.Task CheckService()
    {
        ServiceText.Text = "проверяю…";
        BtnInstall.IsVisible = false;
        var state = await EngineService.PrivilegedService();
        ServiceText.Text = EngineService.Explain(state);
        // With a service installed the daemon owns the core, and the app's own Start
        // would raise a second engine against it. Saying so beats letting them collide.
        StateHint.Text = state == EngineService.ServiceState.Running
            ? "Пока служба стоит, туннелем управляет она — кнопки выше работать не будут."
            : "";
        StateHint.IsVisible = StateHint.Text.Length > 0;
        var good = state == EngineService.ServiceState.Running;
        ServiceText.Foreground = Palette.Brush(good ? "AccentBrush" : "TextDimBrush");
        // Offering to install it only where this app can: elsewhere the engine's own
        // command does it, and saying so is better than a button that cannot work.
        BtnInstall.IsVisible = OperatingSystem.IsMacOS() && state is
            EngineService.ServiceState.NotInstalled or
            EngineService.ServiceState.NeedsReinstall or
            EngineService.ServiceState.CopyOnly;
        // Removing it is the engine's only documented off switch, so it is offered
        // wherever the service exists at all.
        BtnUninstall.IsVisible = OperatingSystem.IsMacOS() && state is
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

    private async void Update_Click(object? sender, RoutedEventArgs e)
    {
        BtnUpdate.IsEnabled = false;
        UpdateStatus.Text = "проверяю…";
        try
        {
            var info = await UpdateService.CheckAsync();
            // The installer it offers is a Windows one, so this says what is available
            // rather than offering to install it: a macOS build is replaced by hand.
            UpdateStatus.Text = info.Error is { Length: > 0 } err ? err
                : info.Kind == UpdateKind.None ? "Установлена последняя версия."
                : $"Доступна {info.Latest}. Скачать: github.com/Vexorter42/Tunor/releases";
        }
        catch (Exception ex) { UpdateStatus.Text = "Не удалось проверить: " + ex.Message; }
        finally { BtnUpdate.IsEnabled = true; }
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
                r.Country,
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
