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

    private static Process? _liveProcess;
    private static Timer? _statusTimer;
    private static bool _lastRunning;

    /// <summary>Recent stderr, used to recognise a failed start.</summary>
    private static readonly List<string> _recentErrors = new();
    private static readonly object _errLock = new();

    public static bool IsRunning => GetSingBoxProcesses().Length > 0;

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

    private static Process[] GetSingBoxProcesses()
    {
        try { return Process.GetProcessesByName("sing-box"); }
        catch { return Array.Empty<Process>(); }
    }

    private static void Log(string line, bool isError = false)
        => LogReceived?.Invoke(null, new LogEventArgs(StripAnsi(line), isError));

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

    private static bool LastStartHitStaleAdapter()
    {
        lock (_errLock)
        {
            foreach (var line in _recentErrors)
            {
                if (line.Contains("configure tun interface", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("create adapter", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }

    /// <summary>Removes a leftover sing-tun adapter (only safe while the engine is down).</summary>
    private static bool RemoveStaleTunAdapter()
    {
        if (IsRunning) return false;
        try
        {
            var script =
                "$a = Get-NetAdapter -IncludeHidden -ErrorAction SilentlyContinue | " +
                "Where-Object { $_.InterfaceDescription -like '*sing-tun*' }; " +
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
