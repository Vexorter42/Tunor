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
        => LogReceived?.Invoke(null, new LogEventArgs(line, isError));

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
            Log($"[ui] очистка TUN-адаптера: {(string.IsNullOrEmpty(stdout) ? "нет результата" : stdout)}");
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
                        // Last resort — this is what leaves the adapter behind.
                        p.Kill(true);
                        p.WaitForExit(3000);
                        Log("[ui] sing-box остановлен принудительно (мягкая остановка не сработала)", true);
                    }
                }
                catch (Exception ex)
                {
                    Log($"[ui] stop failed: {ex.Message}", true);
                }
                finally { p.Dispose(); }
            }
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
    private static bool TryGracefulStop(Process p, int timeoutMs = 6000)
    {
        try
        {
            FreeConsole();
            if (!AttachConsole((uint)p.Id)) return false;
            try
            {
                // NULL handler with add=true makes *this* process ignore the Ctrl+C we raise.
                SetConsoleCtrlHandler(IntPtr.Zero, true);
                if (!GenerateConsoleCtrlEvent(CTRL_C_EVENT, 0)) return false;
            }
            finally
            {
                SetConsoleCtrlHandler(IntPtr.Zero, false);
                FreeConsole();
            }
            return p.WaitForExit(timeoutMs);
        }
        catch { return false; }
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
                    if (!TryGracefulStop(p, 3000))
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

    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool AttachConsole(uint pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool FreeConsole();
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetConsoleCtrlHandler(IntPtr handler, bool add);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool GenerateConsoleCtrlEvent(uint ctrlEvent, uint processGroupId);
}
