using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
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
            else ShowFirstRunIfNeeded();
        };

        Closing += OnClosing;
    }

    /// <summary>
    /// First launch after an install: show the licence agreement + tutorial.
    /// Accepting is a gate — without it the app closes again.
    /// </summary>
    private void ShowFirstRunIfNeeded()
    {
        try
        {
            if (SettingsService.Load().Accept) return;

            new FirstRunWizard { Owner = this }.ShowDialog();

            if (!SettingsService.Load().Accept)
            {
                _forceExit = true;
                Close();
                return;
            }

            // Rule lists may have been downloaded during the wizard.
            _rules.Reload();
        }
        catch { /* never block the UI on the wizard */ }
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

    // ------------------------------------------------------ drag & drop import

    /// <summary>
    /// Nyx runs elevated, Explorer does not. UIPI silently drops window messages sent
    /// from a lower integrity level to a higher one, so dragging a .conf from Explorer
    /// onto this window never arrived — nothing highlighted, nothing happened. These
    /// three messages are what OLE drag-and-drop needs; allowing them is the standard
    /// (and deliberately narrow) exception an elevated app has to make to accept drops.
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        try
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;
            foreach (var msg in new uint[] { WM_DROPFILES, WM_COPYDATA, WM_COPYGLOBALDATA })
                ChangeWindowMessageFilterEx(hwnd, msg, MSGFLT_ALLOW, IntPtr.Zero);
        }
        catch { /* drag-and-drop is a convenience; never block startup on it */ }
    }

    private const uint WM_DROPFILES = 0x0233;
    private const uint WM_COPYDATA = 0x004A;
    private const uint WM_COPYGLOBALDATA = 0x0049;
    private const uint MSGFLT_ALLOW = 1;

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern bool ChangeWindowMessageFilterEx(
        IntPtr hwnd, uint message, uint action, IntPtr changeInfo);

    private void Window_DragEnter(object sender, DragEventArgs e) => UpdateDropState(e);
    private void Window_DragOver(object sender, DragEventArgs e) => UpdateDropState(e);

    private void Window_DragLeave(object sender, DragEventArgs e)
        => DropOverlay.Visibility = Visibility.Collapsed;

    private void UpdateDropState(DragEventArgs e)
    {
        var ok = GetDroppedConfigs(e).Count > 0;
        DropOverlay.Visibility = ok ? Visibility.Visible : Visibility.Collapsed;
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private static List<string> GetDroppedConfigs(DragEventArgs e)
    {
        var result = new List<string>();
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return result;
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return result;
        foreach (var f in files)
            if (ConfImporter.LooksLikeConf(f)) result.Add(f);
        return result;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        e.Handled = true;

        var files = GetDroppedConfigs(e);
        if (files.Count == 0) return;

        var restart = false;
        foreach (var file in files.Take(4))
        {
            var detection = ConfImporter.Detect(file);
            var dlg = new ImportConfDialog(file, detection) { Owner = this };
            dlg.ShowDialog();
            if (dlg.Applied && dlg.ShouldRestart) restart = true;
        }

        if (restart)
        {
            try { await ProcessService.RestartAsync(); } catch { }
        }
    }
}
