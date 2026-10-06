using System;
using System.IO;
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
        StatusText.Text = "…";
        if (EngineService.IsRunning) await EngineService.StopAsync();
        var (ok, msg) = EngineService.Start();
        StatusText.Text = ok ? "Движок " + msg : "Не удалось запустить: " + msg;
        Refresh();
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
