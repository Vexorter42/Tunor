using System;
using System.Linq;
using System.Threading;
using System.Windows;

namespace Nyx;

public partial class App : Application
{
    /// <summary>True when the app was launched with --autostart (by Task Scheduler at logon).</summary>
    public static bool IsAutostart { get; private set; }

    /// <summary>Delay before the auto-restart fires.</summary>
    public static TimeSpan AutostartDelay { get; } = TimeSpan.FromSeconds(5);

    // Single-instance: the mutex also lets the Inno Setup installer (AppMutex) detect
    // and close a running instance during a silent OTA update.
    private const string MutexName = "Nyx_AppMutex";
    private const string ShowEventName = "Nyx_ShowEvent";
    private static Mutex? _mutex;
    private static EventWaitHandle? _showEvent;

    protected override void OnStartup(StartupEventArgs e)
    {
        IsAutostart = e.Args.Any(a => string.Equals(a, Services.TaskService.AutostartArg,
            StringComparison.OrdinalIgnoreCase));

        bool createdNew = true;
        try { _mutex = new Mutex(true, MutexName, out createdNew); }
        catch { createdNew = true; }

        if (!createdNew)
        {
            // Another instance is already running — ask it to surface its window, then quit.
            try
            {
                if (EventWaitHandle.TryOpenExisting(ShowEventName, out var ev))
                    ev.Set();
            }
            catch { }
            Shutdown();
            return;
        }

        try
        {
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            var pump = new Thread(ShowEventPump) { IsBackground = true };
            pump.Start();
        }
        catch { }

        base.OnStartup(e);
    }

    // Waits for a second instance to signal, then brings the main window to the front.
    private void ShowEventPump()
    {
        while (_showEvent != null)
        {
            try
            {
                if (!_showEvent.WaitOne()) break;
                Dispatcher.BeginInvoke(new Action(() =>
                    (Current.MainWindow as MainWindow)?.RestoreWindow()));
            }
            catch { break; }
        }
    }
}
