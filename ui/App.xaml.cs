using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace Tunor;

public partial class App : Application
{
    /// <summary>True when the app was launched with --autostart (by Task Scheduler at logon).</summary>
    public static bool IsAutostart { get; private set; }

    /// <summary>Delay before the auto-restart fires.</summary>
    public static TimeSpan AutostartDelay { get; } = TimeSpan.FromSeconds(5);

    // Single-instance: the mutex also lets the Inno Setup installer (AppMutex) detect
    // and close a running instance during a silent OTA update.
    private const string MutexName = "Tunor_AppMutex";
    private const string ShowEventName = "Tunor_ShowEvent";
    private static Mutex? _mutex;
    private static EventWaitHandle? _showEvent;

    protected override void OnStartup(StartupEventArgs e)
    {
        InstallCrashHandlers();

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

        Services.AppLog.Write($"=== Tunor {Services.UpdateService.CurrentVersionString} запущен · {Environment.OSVersion} ===");
        base.OnStartup(e);
    }

    // ------------------------------------------------------------ crash handling

    private static bool _crashReported;

    /// <summary>
    /// Without these an unhandled exception closes the window with no trace at all —
    /// the app just "vanishes". Everything is appended to crash.log next to the app,
    /// and a UI-thread exception no longer takes the process down with it.
    /// </summary>
    private void InstallCrashHandlers()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            WriteCrash("UI", args.Exception);
            args.Handled = true;            // keep the tray app alive; the log has the detail
            ShowCrashNoticeOnce(args.Exception);
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            WriteCrash("domain", args.ExceptionObject as Exception);

        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            // Marked observed either way: an unobserved exception from a background
            // poller must never be allowed to end the process.
            args.SetObserved();
            if (!IsRoutineNetworkFailure(args.Exception)) WriteCrash("task", args.Exception);
        };
    }

    /// <summary>
    /// Whether this is just the network, rather than something gone wrong.
    ///
    /// The statistics poller talks to the engine every second and a half. When the engine
    /// stops, or answers slower than its three seconds, the call is cancelled and handled
    /// — and the socket underneath fails afterwards with nobody left to hear it. Writing
    /// those into crash.log buries the entries that matter among dozens that do not, in
    /// the very file people are asked to send when something is wrong.
    /// </summary>
    private static bool IsRoutineNetworkFailure(AggregateException? ex)
    {
        if (ex == null) return false;
        var inner = ex.Flatten().InnerExceptions;
        return inner.Count > 0 && inner.All(e => e is System.Net.Sockets.SocketException
                                                   or System.IO.IOException
                                                   or System.Net.Http.HttpRequestException
                                                   or OperationCanceledException);
    }

    private static void WriteCrash(string source, Exception? ex)
    {
        if (ex == null) return;
        foreach (var path in CrashLogCandidates())
        {
            try
            {
                File.AppendAllText(path,
                    $"===== {DateTime.Now:yyyy-MM-dd HH:mm:ss}  ({source}, v{Services.UpdateService.CurrentVersionString})\r\n" +
                    ex + "\r\n\r\n");
                return;
            }
            catch { /* try the next location */ }
        }
    }

    private static IEnumerable<string> CrashLogCandidates()
    {
        yield return Path.Combine(Services.Paths.AppRoot, "crash.log");
        yield return Path.Combine(Path.GetTempPath(), "Tunor-crash.log");
    }

    private static void ShowCrashNoticeOnce(Exception ex)
    {
        if (_crashReported) return;         // one dialog per session, not one per exception
        _crashReported = true;
        try
        {
            MessageBox.Show(
                "Что-то пошло не так, но Tunor продолжает работать.\n\n" +
                ex.Message + "\n\nПодробности записаны в crash.log в папке программы.",
                "Tunor", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch { }
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
