using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace Nyx.Services;

public class LogEventArgs : EventArgs
{
    public string Line { get; }
    public bool IsError { get; }
    public LogEventArgs(string line, bool isError) { Line = line; IsError = isError; }
}

public static class ProcessService
{
    public static event EventHandler? StatusChanged;
    public static event EventHandler<LogEventArgs>? LogReceived;

    /// <summary>The tunnel is down and the watchdog will not bring it back. Carries the reason.</summary>
    public static event EventHandler<string>? Alert;

    private static Process? _liveProcess;
    private static Timer? _statusTimer;
    private static bool _lastRunning;

    /// <summary>Recent stderr, used to recognise a failed start.</summary>
    private static readonly List<string> _recentErrors = new();
    private static readonly object _errLock = new();

    public static bool IsRunning => GetSingBoxProcesses().Length > 0;

    /// <summary>
    /// Plain-language reason the engine last died on its own, or null. Cleared on start.
    /// Without it the only symptom was the status quietly flipping back to "stopped".
    /// </summary>
    public static string? LastFailure { get; private set; }

    /// <summary>Set while we stop the engine ourselves, so that is not reported as a failure.</summary>
    private static volatile bool _stopping;

    public static void StartStatusPolling()
    {
        _lastRunning = IsRunning;
        _statusTimer = new Timer(_ =>
        {
            var running = IsRunning;
            if (running != _lastRunning)
            {
                _lastRunning = running;
                StatusChanged?.Invoke(null, EventArgs.Empty);
            }
        }, null, 0, 1500);
    }

    /// <summary>
    /// Nyx's own engine — not every process called sing-box. Matching by name alone meant
    /// that another sing-box on the machine (Hiddify, v2rayN, a second test copy) counted
    /// as "running", and Stop / Restart / exit killed it along with ours. Ours is the one
    /// whose image is Paths.SingBoxExe. A process whose path cannot be read (another
    /// user's, or more privileged) is by definition not one Nyx started.
    /// </summary>
    private static Process[] GetSingBoxProcesses()
    {
        var ours = new List<Process>();
        try
        {
            var target = System.IO.Path.GetFullPath(Paths.SingBoxExe);
            foreach (var p in Process.GetProcessesByName("sing-box"))
            {
                if (IsOurEngine(p, target)) ours.Add(p);
                else p.Dispose();
            }
        }
        catch { /* treat as none */ }
        return ours.ToArray();
    }

    private static bool IsOurEngine(Process p, string target)
    {
        try
        {
            var path = p.MainModule?.FileName;
            return path != null && string.Equals(System.IO.Path.GetFullPath(path), target,
                                                  StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static void Log(string line, bool isError = false)
        => LogReceived?.Invoke(null, new LogEventArgs(StripAnsi(line), isError));

    /// <summary>Puts an app-side message into the log, marked as coming from the UI.</summary>
    public static void Note(string message, bool isError = false) => Log("[ui] " + message, isError);

    /// <summary>
    /// The engine colours its output with ANSI escapes. A WPF TextBox renders those as
    /// literal junk ("[31mERROR[0m ..."), so strip them before the line goes anywhere.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex AnsiRe =
        new("\x1b\\[[0-9;?]*[ -/]*[@-~]", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static string StripAnsi(string s)
        => string.IsNullOrEmpty(s) || !s.Contains('\x1b') ? s : AnsiRe.Replace(s, "");

    // ---------------------------------------------------------------- start

    public static async Task StartAsync()
    {
        if (IsRunning) return;
        LastFailure = null;
        _wantRunning = true;

        if (!await LaunchAsync())
        {
            // A previous hard kill can leave the sing-tun adapter registered; the
            // engine then refuses to start. Clean it up and try once more.
            if (LastStartHitStaleAdapter())
            {
                Log("[ui] похоже, остался старый TUN-адаптер — убираю и пробую снова");
                if (RemoveStaleTunAdapter())
                    await LaunchAsync();
                else
                    Log("[ui] не удалось убрать адаптер автоматически — поможет перезагрузка", true);
            }
        }

        StatusChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>Starts the engine and waits briefly to see whether it survives.</summary>
    private static async Task<bool> LaunchAsync()
    {
        lock (_errLock) _recentErrors.Clear();

        await Task.Run(() =>
        {
            try
            {
                StopLiveCapture();
                var psi = new ProcessStartInfo
                {
                    FileName = Paths.SingBoxExe,
                    Arguments = "run",
                    WorkingDirectory = Paths.BuildDir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                _liveProcess = Process.Start(psi);
                if (_liveProcess != null)
                {
                    _liveProcess.EnableRaisingEvents = true;
                    _liveProcess.Exited += (_, _) => OnEngineExited();
                    _liveProcess.OutputDataReceived += (_, e) => { if (e.Data != null) Log(e.Data); };
                    _liveProcess.ErrorDataReceived += (_, e) =>
                    {
                        if (e.Data == null) return;
                        lock (_errLock)
                        {
                            _recentErrors.Add(e.Data);
                            if (_recentErrors.Count > 200) _recentErrors.RemoveAt(0);
                        }
                        Log(e.Data, true);
                    };
                    _liveProcess.BeginOutputReadLine();
                    _liveProcess.BeginErrorReadLine();
                }
                Log("[ui] sing-box started");
            }
            catch (Exception ex)
            {
                Log($"[ui] start failed: {ex.Message}", true);
            }
        });

        // The TUN failure surfaces within a few seconds; give it a moment.
        for (var i = 0; i < 16; i++)
        {
            await Task.Delay(500);
            if (_liveProcess is { HasExited: true }) return false;
            if (i >= 4 && IsRunning) return true;   // survived the risky window
        }
        return IsRunning;
    }

    /// <summary>
    /// Only the leftover-adapter signature. "configure tun interface" alone is too broad:
    /// it also covers Windows failing to create the adapter at all, where removing a
    /// sing-tun adapter that does not exist fixes nothing.
    /// </summary>
    private static bool LastStartHitStaleAdapter()
    {
        lock (_errLock)
        {
            foreach (var line in _recentErrors)
            {
                if (line.Contains("create adapter", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("already exists", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    private static void OnEngineExited()
    {
        if (_stopping) return;   // we stopped it ourselves

        // Stderr lines can still be in flight when Exited fires; give them a moment.
        Thread.Sleep(300);

        string joined;
        lock (_errLock) joined = string.Join('\n', _recentErrors);

        var (reason, retryable) = Explain(joined);

        // The stats API port was taken after it was chosen. Pick a free one and rebuild
        // config.json now, so the watchdog's restart does not hit the same wall.
        if (joined.Contains("external controller", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                ConfigGenerator.ResetControllerPort();
                ConfigGenerator.Generate();
            }
            catch { retryable = false; }
        }

        LastFailure = reason ?? "Движок завершился с ошибкой — подробности в разделе «Логи».";
        Log("[ui] " + LastFailure, true);
        StatusChanged?.Invoke(null, EventArgs.Empty);

        _ = WatchdogAsync(retryable);
    }

    // ------------------------------------------------------------ watchdog

    /// <summary>True from a start until someone asks for a stop — the watchdog's mandate.</summary>
    private static volatile bool _wantRunning;
    private static readonly List<DateTime> _restarts = new();
    private static readonly int[] BackoffSeconds = { 3, 10, 30 };

    /// <summary>
    /// Brings the engine back after it dies on its own. Deterministic failures (no TUN
    /// adapter, busy port, unreadable config) are never retried: a restart would only
    /// fail the same way, in a loop. Bounded to three attempts in five minutes.
    /// </summary>
    private static async Task WatchdogAsync(bool retryable)
    {
        if (!_wantRunning) return;   // stopped on purpose: nothing to report

        var enabled = true;
        try { enabled = SettingsService.Load().Watchdog; } catch { }
        if (!retryable || !enabled)
        {
            // Down for good: say so, or with the window in the tray nobody notices until
            // sites stop opening.
            Alert?.Invoke(null, LastFailure ?? "Туннель остановился.");
            return;
        }

        int attempt;
        lock (_restarts)
        {
            _restarts.RemoveAll(t => DateTime.UtcNow - t > TimeSpan.FromMinutes(5));
            if (_restarts.Count >= BackoffSeconds.Length)
            {
                Log("[ui] движок падает снова и снова — больше не перезапускаю сам", true);
                Alert?.Invoke(null, "Туннель падает снова и снова, Nyx перестал его перезапускать. " +
                                    (LastFailure ?? ""));
                return;
            }
            _restarts.Add(DateTime.UtcNow);
            attempt = _restarts.Count;
        }

        var delay = BackoffSeconds[attempt - 1];
        Log($"[ui] перезапускаю движок через {delay} с (попытка {attempt} из {BackoffSeconds.Length})");
        await Task.Delay(TimeSpan.FromSeconds(delay));

        // The user may have stopped it, or started it by hand, in the meantime.
        if (!_wantRunning || IsRunning) return;
        await StartAsync();
    }

    /// <summary>
    /// Known fatal engine errors, in terms a user can act on, and whether restarting
    /// could possibly help.
    /// </summary>
    private static (string? text, bool retryable) Explain(string errors)
    {
        bool Has(string s) => errors.Contains(s, StringComparison.OrdinalIgnoreCase);

        // Must come before the generic port check: this one heals itself (see OnEngineExited).
        if (Has("external controller"))
            return ("Порт статистики соединений оказался занят другой программой. Nyx выбрал " +
                    "другой и перезапускает туннель.", true);

        if (Has("configure tun interface") &&
            (Has("cannot find the file specified") || Has("не удается найти указанный файл")))
            return ("Windows не смогла создать сетевой адаптер TUN (драйвер Wintun). Чаще всего " +
                    "мешает другой VPN с TUN-режимом — Hiddify, v2rayN, Clash, AmneziaVPN, " +
                    "WireGuard: закрой их полностью, включая значок в трее. Если не помогло — " +
                    "перезагрузи компьютер и проверь, не блокирует ли антивирус. Проверить сам " +
                    "туннель можно без TUN: выключи его в «Настройках» и подключись через прокси " +
                    "127.0.0.1:1080.", false);

        if (Has("create adapter") || Has("already exists"))
            // StartAsync removes the leftover adapter, so a retry genuinely can succeed.
            return ("От прошлого запуска остался сетевой адаптер. Nyx убирает его сам — если " +
                    "ошибка повторяется, поможет перезагрузка.", true);

        if (Has("configure tun interface") || Has("inbound/tun"))
            return ("Не удалось поднять TUN-интерфейс. Закрой другие VPN-программы и перезапусти; " +
                    "как временную меру можно выключить TUN в «Настройках» и работать через прокси.",
                    false);

        if (Has("address already in use") || Has("only one usage of each socket address"))
            return ("Порт прокси (1080–1083) занят другой программой. Закрой её или выключи " +
                    "режим прокси в «Настройках».", false);

        if (Has("decode config") || Has("unknown field") || Has("parse config"))
            return ("Движок не смог прочитать config.json. Нажми «Сохранить и применить» в " +
                    "«Правилах» — конфиг соберётся заново.", false);

        // A tunnel file the engine refuses: bad key, bad address. Nyx now checks keys
        // before building the config, so this is the backstop for whatever slips past.
        if (Has("initialize endpoint") || Has("private key") || Has("public key") || Has("pre-shared key"))
        {
            var file = Has("geo-out") ? "geo.conf" : Has("warp-out") ? "warp.conf" : "конфиге туннеля";
            var what = Has("illegal base64") ? "ключ испорчен — часто к нему прилипают кавычки или невидимые символы при копировании из мессенджера"
                     : Has("missing private key") ? "нет ключа PrivateKey"
                     : "движок не принял параметры туннеля";
            return ($"Проблема в {file}: {what}. Открой «Конфиги» → {file} и вставь конфиг заново " +
                    "(или перетащи .conf-файл на окно).", false);
        }

        // Anything else that fails while the engine is being built is a config problem
        // too: it will fail identically on every restart.
        if (Has("create service:"))
            return ("Движок не принял конфигурацию — подробности в разделе «Логи».", false);

        return (null, true);   // unknown: worth another try
    }

    /// <summary>Removes a leftover sing-tun adapter (only safe while the engine is down).</summary>
    private static bool RemoveStaleTunAdapter()
    {
        if (IsRunning) return false;
        try
        {
            // Only adapters that are not up: a live sing-tun adapter belongs to a running
            // engine — possibly another app's (Hiddify, v2rayN) — and must not be removed.
            // A leftover from a killed engine is never up.
            var script =
                "$a = Get-NetAdapter -IncludeHidden -ErrorAction SilentlyContinue | " +
                "Where-Object { $_.InterfaceDescription -like '*sing-tun*' -and $_.Status -ne 'Up' }; " +
                "if ($a) { $a | Remove-NetAdapter -Confirm:$false -ErrorAction SilentlyContinue; " +
                "'removed' } else { 'none' }";

            var psi = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in new[] { "-NoProfile", "-NonInteractive", "-Command", script })
                psi.ArgumentList.Add(a);

            using var p = Process.Start(psi);
            if (p == null) return false;
            var stdout = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(15000);
            if (stdout.Contains("removed", StringComparison.OrdinalIgnoreCase))
                Log("[ui] убран оставшийся TUN-адаптер");
            return stdout.Contains("removed", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            Log($"[ui] очистка адаптера не удалась: {ex.Message}", true);
            return false;
        }
    }

    // ----------------------------------------------------------------- stop

    public static async Task StopAsync()
    {
        _stopping = true;
        _wantRunning = false;
        LastFailure = null;
        try { await StopCoreAsync(); }
        finally
        {
            // Exited fires asynchronously after the kill; keep the flag up until it has.
            await Task.Delay(500);
            _stopping = false;
        }
    }

    private static async Task StopCoreAsync()
    {
        await Task.Run(() =>
        {
            var killed = false;
            foreach (var p in GetSingBoxProcesses())
            {
                try
                {
                    if (TryGracefulStop(p))
                    {
                        Log("[ui] sing-box stopped");
                    }
                    else
                    {
                        p.Kill(true);
                        p.WaitForExit(3000);
                        killed = true;
                        Log("[ui] sing-box остановлен");
                    }
                }
                catch (Exception ex)
                {
                    Log($"[ui] stop failed: {ex.Message}", true);
                }
                finally { p.Dispose(); }
            }

            // A killed engine leaves its sing-tun adapter registered, and the next start
            // then dies with "create adapter: file already exists". Clearing it here makes
            // the following start succeed first time instead of failing and retrying.
            if (killed) RemoveStaleTunAdapter();

            StopLiveCapture();
        });

        await Task.Delay(300);
        StatusChanged?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>
    /// Asks the engine to shut down cleanly by raising Ctrl+C on its console, so it
    /// removes its own TUN adapter. Killing it outright leaves the adapter registered
    /// and the next start fails with "create adapter: file already exists".
    /// </summary>
    /// <remarks>
    /// Measured against sing-box 1.14.1-lx.8: it does not exit on Ctrl+C even with its
    /// own console and no redirection. The attempt is kept because it costs little and a
    /// future engine build may honour it, but the timeout is short — in practice the kill
    /// below is what stops the engine, and StopAsync cleans up the TUN adapter afterwards.
    /// </remarks>
    private static bool TryGracefulStop(Process p, int timeoutMs = 1500)
    {
        try
        {
            EnsureCtrlHandlerInstalled();
            FreeConsole();
            if (!AttachConsole((uint)p.Id)) return false;
            try
            {
                ReassertCtrlHandler();
                // Group 0 is mandatory for CTRL_C_EVENT (the API refuses any other group),
                // so the signal reaches every process on this console — this one included.
                // _ctrlHandler swallows it.
                if (!GenerateConsoleCtrlEvent(CTRL_C_EVENT, 0)) return false;
                return p.WaitForExit(timeoutMs);
            }
            finally { FreeConsole(); }
        }
        catch { return false; }
    }

    /// <summary>
    /// Installs a permanent Ctrl+C handler that swallows the signal we raise on the
    /// engine's console.
    ///
    /// This must be permanent. The previous version set the "ignore" flag just before
    /// raising the event and cleared it in a finally block — but GenerateConsoleCtrlEvent
    /// only *queues* the signal, and delivery runs on a separate thread in each attached
    /// process. The guard was routinely gone by the time the signal came back to us, the
    /// default handler ran, and the UI died: "the app closes when I press Restart".
    /// </summary>
    private static void EnsureCtrlHandlerInstalled()
    {
        lock (_ctrlLock)
        {
            if (_ctrlHandler != null) return;
            // Held in a static field: the CLR must not collect a delegate the OS calls.
            var handler = new ConsoleCtrlDelegate(
                type => type == CTRL_C_EVENT || type == CTRL_BREAK_EVENT);
            if (SetConsoleCtrlHandler(handler, true)) _ctrlHandler = handler;
        }
    }

    /// <summary>
    /// Re-registers the handler after attaching to another process's console. Attaching
    /// can reset the console-control state, and losing the handler here would put the
    /// self-kill back. Remove-then-add keeps exactly one registration.
    /// </summary>
    private static void ReassertCtrlHandler()
    {
        lock (_ctrlLock)
        {
            if (_ctrlHandler == null) return;
            SetConsoleCtrlHandler(_ctrlHandler, false);
            SetConsoleCtrlHandler(_ctrlHandler, true);
        }
    }

    public static async Task RestartAsync()
    {
        await StopAsync();
        await Task.Delay(600);
        await StartAsync();
    }

    private static void StopLiveCapture()
    {
        // Only drops our log pipes; the engine itself is stopped via StopAsync.
        _liveProcess = null;
    }

    /// <summary>
    /// Called when the UI really exits. Stops the engine *gracefully* so it removes
    /// its TUN adapter — a hard kill here was what orphaned the adapter and broke
    /// the next start.
    /// </summary>
    public static void Shutdown()
    {
        _stopping = true;
        _wantRunning = false;
        _statusTimer?.Dispose();
        try
        {
            foreach (var p in GetSingBoxProcesses())
            {
                try
                {
                    if (!TryGracefulStop(p, 1200))
                    {
                        p.Kill(true);
                        p.WaitForExit(1500);
                    }
                }
                catch { }
                finally { p.Dispose(); }
            }
        }
        catch { }
        _liveProcess = null;
    }

    private const uint CTRL_C_EVENT = 0;
    private const uint CTRL_BREAK_EVENT = 1;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate bool ConsoleCtrlDelegate(uint ctrlType);

    private static ConsoleCtrlDelegate? _ctrlHandler;
    private static readonly object _ctrlLock = new();

    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AttachConsole(uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool FreeConsole();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetConsoleCtrlHandler(ConsoleCtrlDelegate handler, bool add);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GenerateConsoleCtrlEvent(uint ctrlEvent, uint processGroupId);
}
