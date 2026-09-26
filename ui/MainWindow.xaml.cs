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
    private readonly ConnectionsPage _connections = new();
    private readonly AppsPage _appsPage = new();
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
        ProcessService.Alert += (_, reason) =>
            Dispatcher.BeginInvoke(new Action(() => _tray?.Notify("Nyx: туннель остановлен", reason)));
        ProcessService.StartStatusPolling();
        // Records which program talks to which address, for the Apps page.
        TrafficRecorder.Start();
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

            _ = ListsUpdater.RunIfDueAsync();
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
        var accent = Ui.Solid(running ? "SuccessBrush" : "DangerBrush");

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
            "connections" => _connections,
            "apps" => _appsPage,
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

    /// <summary>What was dragged in, as far as an import is concerned.</summary>
    private enum DropKind { Nothing, Configs, OtherFiles, Virtual, Text }

    /// <summary>Formats a chat or mail window offers when it hands over a file it holds itself.</summary>
    private static readonly string[] VirtualFileFormats =
        { "FileGroupDescriptorW", "FileGroupDescriptor", "FileContents" };

    private void Window_DragEnter(object sender, DragEventArgs e) => UpdateDropState(e);
    private void Window_DragOver(object sender, DragEventArgs e) => UpdateDropState(e);

    private void Window_DragLeave(object sender, DragEventArgs e)
        => DropOverlay.Visibility = Visibility.Collapsed;

    private void UpdateDropState(DragEventArgs e)
    {
        var kind = Classify(e.Data, out var configs, out _);
        var ok = kind is DropKind.Configs or DropKind.Text;

        // A refusal used to be silent: the cursor said "no" and the window said nothing,
        // which looks exactly like a broken feature. Say what is wrong instead.
        if (kind == DropKind.Nothing) DropOverlay.Visibility = Visibility.Collapsed;
        else ShowDropOverlay(kind, configs.Count);

        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void ShowDropOverlay(DropKind kind, int count)
    {
        var (title, hint) = kind switch
        {
            DropKind.Configs => (count > 1 ? $"Отпустите файлы ({count})" : "Отпустите файл",
                                 "Конфиг .conf будет определён автоматически — WARP или geo"),
            DropKind.Text => ("Отпустите текст",
                              "Похоже на конфиг WireGuard — вставлю его как файл"),
            DropKind.OtherFiles => ("Это не конфиг",
                                    "Нужен файл .conf с секцией [Interface] и строкой PrivateKey"),
            // ⁠ keeps "Ctrl+V" from being split across lines at the plus.
            _ => ("Так файл не передаётся",
                  "Перетаскивание прямо из окна мессенджера не даёт Windows путь к файлу. " +
                  "Сохрани .conf в папку и перетащи оттуда — или скопируй конфиг текстом " +
                  "и нажми Ctrl⁠+⁠V прямо здесь."),
        };

        var good = kind is DropKind.Configs or DropKind.Text;
        DropTitle.Text = title;
        DropTitle.Foreground = Ui.Brush(good ? "AccentBrush" : "DangerBrush");
        DropFrame.BorderBrush = Ui.Brush(good ? "AccentBrush" : "DangerBrush");
        DropHint.Text = hint;
        DropOverlay.Visibility = Visibility.Visible;
    }

    private static DropKind Classify(IDataObject? data, out List<string> configs, out string formats)
    {
        configs = new List<string>();
        formats = "";
        if (data == null) return DropKind.Nothing;

        try
        {
            formats = string.Join(", ", data.GetFormats());

            if (data.GetDataPresent(DataFormats.FileDrop) && data.GetData(DataFormats.FileDrop) is string[] files)
            {
                foreach (var f in files)
                    if (ConfImporter.LooksLikeConf(f)) configs.Add(f);
                return configs.Count > 0 ? DropKind.Configs : DropKind.OtherFiles;
            }

            if (LooksLikeConfText(TextOf(data))) return DropKind.Text;

            foreach (var f in VirtualFileFormats)
                if (data.GetDataPresent(f)) return DropKind.Virtual;
        }
        catch { /* a source can refuse to hand anything over; treat it as nothing */ }

        return DropKind.Nothing;
    }

    private static string? TextOf(IDataObject data)
    {
        foreach (var f in new[] { DataFormats.UnicodeText, DataFormats.Text })
            if (data.GetDataPresent(f) && data.GetData(f) is string s && s.Length > 0)
                return s;
        return null;
    }

    private static bool LooksLikeConfText(string? text)
        => text != null
           && text.Contains("[Interface]", StringComparison.OrdinalIgnoreCase)
           && text.Contains("PrivateKey", StringComparison.OrdinalIgnoreCase);

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        e.Handled = true;
        await AcceptAsync(e.Data, "перетаскивание");
    }

    /// <summary>
    /// Ctrl+V does what dragging does. It is the way in that always works: a window
    /// running as administrator cannot always be dropped on, but the clipboard is
    /// unaffected by that, and a config pasted as text needs no file at all.
    /// </summary>
    private async void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Handled || e.Key != System.Windows.Input.Key.V) return;
        if (System.Windows.Input.Keyboard.Modifiers != System.Windows.Input.ModifierKeys.Control) return;
        // Pasting into a text field is the field's own business.
        if (System.Windows.Input.Keyboard.FocusedElement is System.Windows.Controls.Primitives.TextBoxBase) return;

        IDataObject? data = null;
        try { data = System.Windows.Clipboard.GetDataObject(); } catch { }
        if (Classify(data, out _, out _) is DropKind.Nothing or DropKind.Virtual or DropKind.OtherFiles) return;

        e.Handled = true;
        await AcceptAsync(data, "вставка");
    }

    private async Task AcceptAsync(IDataObject? data, string how)
    {
        var kind = Classify(data, out var configs, out var formats);
        var temp = (string?)null;

        try
        {
            if (kind == DropKind.Text)
            {
                // Detection reads a file; keep the text in one next to the config files,
                // and delete it afterwards — it holds a private key.
                temp = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                                              $"nyx-{DateTime.Now:yyyyMMdd-HHmmss}.conf");
                System.IO.File.WriteAllText(temp, TextOf(data!) ?? "");
                configs.Add(temp);
            }
            else if (kind != DropKind.Configs)
            {
                ProcessService.Note($"{how}: конфиг не получен ({kind}), форматы — {formats}", true);
                return;
            }

            await ImportConfigsAsync(configs);
        }
        finally
        {
            if (temp != null) { try { System.IO.File.Delete(temp); } catch { } }
        }
    }

    /// <summary>
    /// Asks about each config and installs it. Shared by every way a config gets in:
    /// dragging, Ctrl+V and the "Добавить файл" button on the home page.
    /// </summary>
    public async Task ImportConfigsAsync(IEnumerable<string> files)
    {
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
