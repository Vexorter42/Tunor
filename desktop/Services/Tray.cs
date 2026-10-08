using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Tunor.Services;

namespace Tunor.Desktop.Services;

/// <summary>
/// The icon in the menu bar, with the same handful of commands the Windows tray has:
/// what the tunnel is doing, start, stop, restart, the window, and quit.
///
/// It is what makes closing the window mean "out of the way" rather than "off". Without
/// one there is nowhere to put a running tunnel: closing would either stop it or leave
/// it running with nothing on screen to say so.
///
/// Not every desktop has a tray. macOS always does; on Linux it is the StatusNotifier
/// spec, which most desktops implement and some do not, so a failure here is not allowed
/// to take the app down with it — the window still works, and that is all this adds to.
/// </summary>
public sealed class Tray : IDisposable
{
    private readonly TrayIcon _icon = new();
    private readonly NativeMenuItem _status = new("Остановлен") { IsEnabled = false };
    private readonly NativeMenuItem _start = new("Запустить");
    private readonly NativeMenuItem _stop = new("Остановить");
    private readonly NativeMenuItem _restart = new("Перезапустить");

    /// <summary>True once Quit was chosen, so closing the window may really close it.</summary>
    public bool Quitting { get; private set; }

    public Tray()
    {
        _start.Click += async (_, _) => await Do(() => EngineService.StartAsync());
        _stop.Click += async (_, _) => await Do(() => EngineService.StopAsync());
        _restart.Click += async (_, _) => await Do(async () =>
        {
            if (EngineService.IsRunning) await EngineService.StopAsync();
            return await EngineService.StartAsync();
        });

        var show = new NativeMenuItem("Показать окно");
        show.Click += (_, _) => ShowWindow();
        var quit = new NativeMenuItem("Выход");
        quit.Click += (_, _) => Quit();

        _icon.Menu = new NativeMenu
        {
            Items = { _status, new NativeMenuItemSeparator(), _start, _stop, _restart,
                      new NativeMenuItemSeparator(), show, quit },
        };
        _icon.Clicked += (_, _) => ShowWindow();
        _icon.Icon = Load();
        _icon.ToolTipText = "Tunor";
        _icon.IsVisible = true;

        EngineService.StateChanged += (_, _) => Dispatcher.UIThread.Post(Refresh);
        Refresh();
    }

    /// <summary>
    /// Creates one, or nothing at all. A desktop without a tray is a reason to go
    /// without the icon, not a reason to fail to start.
    /// </summary>
    public static Tray? TryCreate()
    {
        try { return new Tray(); }
        catch (Exception ex)
        {
            EngineLog.Add("--- значок в трее не создался: " + ex.Message);
            return null;
        }
    }

    private static WindowIcon? Load()
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://TunorDesktop/Assets/app.png"));
            return new WindowIcon(new Bitmap(stream));
        }
        catch { return null; }   // an icon-less entry still carries the menu
    }

    public void Refresh()
    {
        var running = EngineService.IsRunning;
        _status.Header = running ? "Туннель работает" : "Остановлен";
        _start.IsEnabled = !running;
        _stop.IsEnabled = running;
        _restart.IsEnabled = running;
        _icon.ToolTipText = running ? "Tunor — подключено" : "Tunor — отключено";
    }

    private async System.Threading.Tasks.Task Do(
        Func<System.Threading.Tasks.Task<(bool Ok, string Message)>> act)
    {
        try { await act(); }
        catch (Exception ex) { EngineLog.Add("--- из трея не вышло: " + ex.Message); }
        finally { Dispatcher.UIThread.Post(Refresh); }
    }

    public static void ShowWindow()
    {
        if (Application.Current?.ApplicationLifetime
            is not IClassicDesktopStyleApplicationLifetime desktop) return;
        var window = desktop.MainWindow;
        if (window == null) return;

        window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }

    private void Quit()
    {
        Quitting = true;
        if (Application.Current?.ApplicationLifetime
            is IClassicDesktopStyleApplicationLifetime desktop) desktop.Shutdown();
    }

    public void Dispose()
    {
        try { _icon.IsVisible = false; _icon.Dispose(); } catch { }
    }
}
