using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
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

    public static async Task StartAsync()
    {
        if (IsRunning) return;

        // Launch via run.bat in background, capturing logs from sing-box stdout
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
                    _liveProcess.OutputDataReceived += (_, e) => { if (e.Data != null) LogReceived?.Invoke(null, new LogEventArgs(e.Data, false)); };
                    _liveProcess.ErrorDataReceived += (_, e) => { if (e.Data != null) LogReceived?.Invoke(null, new LogEventArgs(e.Data, true)); };
                    _liveProcess.BeginOutputReadLine();
                    _liveProcess.BeginErrorReadLine();
                }
                LogReceived?.Invoke(null, new LogEventArgs("[ui] sing-box started", false));
            }
            catch (Exception ex)
            {
                LogReceived?.Invoke(null, new LogEventArgs($"[ui] start failed: {ex.Message}", true));
            }
        });

        await Task.Delay(500);
        StatusChanged?.Invoke(null, EventArgs.Empty);
    }

    public static async Task StopAsync()
    {
        await Task.Run(() =>
        {
            try
            {
                foreach (var p in GetSingBoxProcesses())
                {
                    try { p.Kill(true); p.WaitForExit(3000); }
                    catch (Exception ex) { LogReceived?.Invoke(null, new LogEventArgs($"[ui] kill failed: {ex.Message}", true)); }
                }
                StopLiveCapture();
                LogReceived?.Invoke(null, new LogEventArgs("[ui] sing-box stopped", false));
            }
            catch (Exception ex)
            {
                LogReceived?.Invoke(null, new LogEventArgs($"[ui] stop failed: {ex.Message}", true));
            }
        });

        await Task.Delay(300);
        StatusChanged?.Invoke(null, EventArgs.Empty);
    }

    public static async Task RestartAsync()
    {
        await StopAsync();
        await Task.Delay(400);
        await StartAsync();
    }

    private static void StopLiveCapture()
    {
        try
        {
            if (_liveProcess != null && !_liveProcess.HasExited)
            {
                try { _liveProcess.Kill(true); } catch { }
            }
        }
        catch { }
        _liveProcess = null;
    }

    public static void Shutdown()
    {
        _statusTimer?.Dispose();
        StopLiveCapture();
    }
}
