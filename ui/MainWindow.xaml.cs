using System;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Nyx.Services;
using Nyx.Views;

namespace Nyx;

public partial class MainWindow : Window
{
    private readonly HomePage _home = new();
    private readonly SettingsPage _settings = new();
    private readonly RulesPage _rules = new();
    private readonly ConfigsPage _configs = new();
    private readonly LogsPage _logs = new();
    private readonly AutostartPage _autostart = new();

    private TrayIcon? _tray;

    public MainWindow()
    {
        InitializeComponent();
        PageHost.Content = _home;
        VersionLabel.Text = "v" + Services.UpdateService.CurrentVersionString;

        // Rebuild config.json if it is missing or still in the old engine's format.
        ConfigGenerator.EnsureCompatible();

        ProcessService.StatusChanged += OnStatusChanged;
        ProcessService.StartStatusPolling();
        UpdateStatus();

        // Restart sing-box automatically when the system wakes up from sleep/hibernation
        // or when the workstation is unlocked — its TUN interface usually breaks on resume.
        PowerService.Start();
        PowerService.SystemResumed += OnSystemResumed;

        // Autostart mode: start hidden in tray, schedule auto-restart.
        if (App.IsAutostart)
        {
            // Prevent flashing window: hide before it shows.
            WindowState = WindowState.Minimized;
            ShowInTaskbar = false;
            Visibility = Visibility.Hidden;

            SourceInitialized += (_, _) => Hide();
            ScheduleAutoRestart();
        }

        Loaded += (_, _) =>
        {
            _tray = new TrayIcon(this);
            _tray.Show();
            if (App.IsAutostart) Hide();
        };

        Closing += OnClosing;
    }

    private DateTime _lastResumeRestart = DateTime.MinValue;

    private void OnSystemResumed(object? sender, EventArgs e)
    {
        // Debounce — resume/unlock can fire in quick succession.
        if ((DateTime.UtcNow - _lastResumeRestart).TotalSeconds < 30) return;
        _lastResumeRestart = DateTime.UtcNow;

        Dispatcher.BeginInvoke(new Action(async () =>
        {
            // Give the network stack a moment to come back up.
            await Task.Delay(TimeSpan.FromSeconds(5));
            try { await ProcessService.RestartAsync(); } catch { }
        }));
    }

    private void ScheduleAutoRestart()
    {
        var timer = new DispatcherTimer { Interval = App.AutostartDelay };
        timer.Tick += async (_, _) =>
        {
            timer.Stop();
            try { await ProcessService.RestartAsync(); }
            catch { /* swallow — UI is in tray */ }
        };
        timer.Start();
    }

    private bool _forceExit;

    /// <summary>Real exit (bypasses minimize-to-tray) — used before an OTA update installs.</summary>
    public void ShutdownForUpdate()
    {
        _forceExit = true;
        Close();
    }

    /// <summary>Brings the window back from the tray / minimized / hidden state.</summary>
    public void RestoreWindow()
    {
        ShowInTaskbar = true;
        Visibility = Visibility.Visible;
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show();
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!_forceExit && _tray != null && !_tray.IsExiting)
        {
            e.Cancel = true;
            ShowInTaskbar = false;
            Hide();
        }
        else
        {
            PowerService.Stop();
            ProcessService.Shutdown();
            _tray?.Dispose();
            Application.Current.Shutdown();
        }
    }

    private void OnStatusChanged(object? sender, EventArgs e)
    {
        Dispatcher.Invoke(UpdateStatus);
    }

    private void UpdateStatus()
    {
        var running = ProcessService.IsRunning;
        var accent = (SolidColorBrush)FindResource(running ? "SuccessBrush" : "DangerBrush");

        StatusDot.Fill = accent;
        StatusText.Text = running ? "Подключено" : "Отключено";
        StatusText.Foreground = accent;
        // Tint the pill with a translucent version of the same colour.
        var c = accent.Color;
        StatusPill.Background = new SolidColorBrush(Color.FromArgb(0x2E, c.R, c.G, c.B));

        _tray?.UpdateStatus(running);
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton rb || PageHost == null) return;
        PageHost.Content = (rb.Tag as string) switch
        {
            "home" => _home,
            "settings" => _settings,
            "rules" => _rules,
            "configs" => _configs,
            "logs" => _logs,
            "autostart" => _autostart,
            _ => _home,
        };
    }
}
