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
        AttachedToVisualTree += (_, _) => { Refresh(); RefreshAutostart(); _ = CheckService(); _tick.Start(); };
        DetachedFromVisualTree += (_, _) => _tick.Stop();
    }

    private void Refresh()
    {
        var running = EngineService.IsRunning;
        StateDot.Fill = (IBrush?)this.FindResource(running ? "SuccessBrush" : "DangerBrush");
        StateText.Text = running ? "Запущен" : "Остановлен";
        BtnStart.Content = running ? "↻  Перезапустить" : "▶  Запустить";
        BtnStop.IsEnabled = running;

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
            StatusText.Foreground = (IBrush?)this.FindResource(ok ? "TextDimBrush" : "DangerBrush");
        }
        finally { BtnStart.IsEnabled = true; Refresh(); }
    }

    private async void Stop_Click(object? sender, RoutedEventArgs e)
    {
        StatusText.Text = "останавливаю…";
        var (_, msg) = await EngineService.StopAsync();
        StatusText.Text = "Движок " + msg;
        Refresh();
    }

    private async void Service_Click(object? sender, RoutedEventArgs e) => await CheckService();

    private async System.Threading.Tasks.Task CheckService()
    {
        ServiceText.Text = "проверяю…";
        BtnInstall.IsVisible = false;
        var state = await EngineService.PrivilegedService();
        ServiceText.Text = EngineService.Explain(state);
        var good = state == EngineService.ServiceState.Running;
        ServiceText.Foreground = (IBrush?)this.FindResource(good ? "AccentBrush" : "TextDimBrush");
        // Offering to install it only where this app can: elsewhere the engine's own
        // command does it, and saying so is better than a button that cannot work.
        BtnInstall.IsVisible = OperatingSystem.IsMacOS() && state is
            EngineService.ServiceState.NotInstalled or
            EngineService.ServiceState.NeedsReinstall or
            EngineService.ServiceState.CopyOnly;
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
                (IBrush?)this.FindResource(r.Status switch
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

    private void Autostart_Changed(object? sender, RoutedEventArgs e)
    {
        if (_loadingAutostart) return;
        var want = ChkAutostart.IsChecked == true;
        var (ok, msg) = want ? AutostartService.Enable() : AutostartService.Disable();
        AutostartText.Text = ok ? "Автозапуск " + msg : "Не удалось: " + msg;
        if (!ok) RefreshAutostart();
    }

    private bool _loadingAutostart;

    private void RefreshAutostart()
    {
        _loadingAutostart = true;
        try
        {
            AutostartCard.IsVisible = AutostartService.Supported;
            ChkAutostart.IsChecked = AutostartService.Enabled;
            AutostartText.Text = AutostartService.Supported
                ? "Через LaunchAgent в ~/Library/LaunchAgents — пароль не нужен."
                : "";
        }
        finally { _loadingAutostart = false; }
    }
}
