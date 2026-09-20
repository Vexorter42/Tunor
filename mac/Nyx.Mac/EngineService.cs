using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Nyx.Services;

namespace Nyx.Mac;

public sealed class LogLine
{
    public string Text { get; init; } = "";
    public bool IsError { get; init; }
}

/// <summary>
/// Runs the sing-box engine. The macOS counterpart of the Windows ProcessService — and a
/// far smaller one: stopping is a real SIGINT that the engine honours, instead of the
/// console-attach dance Windows forces (which, on top of everything, the engine ignored).
///
/// No TUN and no root here: the engine listens as a local proxy, so nothing needs
/// privileges and there is no network interface to leave behind.
/// </summary>
public static class EngineService
{
    public static event EventHandler? StatusChanged;
    public static event EventHandler<LogLine>? LogReceived;

    private static Process? _proc;
    private static readonly object _lock = new();

    public static bool IsRunning
    {
        get { lock (_lock) return _proc is { HasExited: false }; }
    }

    // The engine colours its output; a text box would render the escapes as junk.
    private static readonly Regex Ansi = new("\x1b\\[[0-9;?]*[ -/]*[@-~]", RegexOptions.Compiled);

    private static void Log(string line, bool error = false)
        => LogReceived?.Invoke(null, new LogLine
        {
            Text = line.Contains('\x1b') ? Ansi.Replace(line, "") : line,
            IsError = error,
        });

    public static async Task<bool> StartAsync()
    {
        if (IsRunning) return true;

        if (!File.Exists(Paths.SingBox))
        {
            Log($"движок не найден: {Paths.SingBox}", true);
            return false;
        }

        try
        {
            Paths.EnsureLayout();
            ConfigGenerator.Generate();
        }
        catch (Exception ex)
        {
            Log("не удалось собрать config.json: " + ex.Message, true);
            return false;
        }

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = Paths.SingBox,
                WorkingDirectory = Paths.BuildDir,   // rule-set paths resolve from here
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            psi.ArgumentList.Add("run");
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(Paths.ConfigJson);

            var p = Process.Start(psi);
            if (p == null) { Log("не удалось запустить движок", true); return false; }

            p.OutputDataReceived += (_, e) => { if (e.Data != null) Log(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) Log(e.Data, true); };
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();

            lock (_lock) _proc = p;
            Log("sing-box запущен");
        }
        catch (Exception ex)
        {
            Log("запуск не удался: " + ex.Message, true);
            return false;
        }

        // A bad config kills the engine within the first second or two.
        await Task.Delay(1500);
        var ok = IsRunning;
        if (!ok) Log("движок завершился сразу после старта — смотри строки выше", true);
        StatusChanged?.Invoke(null, EventArgs.Empty);
        return ok;
    }

    public static async Task StopAsync()
    {
        Process? p;
        lock (_lock) { p = _proc; _proc = null; }
        if (p == null || p.HasExited) { StatusChanged?.Invoke(null, EventArgs.Empty); return; }

        try
        {
            // SIGINT: the engine shuts itself down cleanly. SIGKILL is only a fallback.
            if (kill(p.Id, SIGINT) != 0)
                Log("не удалось послать SIGINT, убиваю жёстко", true);

            var stopped = await Task.Run(() => p.WaitForExit(5000));
            if (!stopped)
            {
                Log("движок не ответил на SIGINT — убиваю", true);
                p.Kill(true);
                await Task.Run(() => p.WaitForExit(2000));
            }
            Log("sing-box остановлен");
        }
        catch (Exception ex)
        {
            Log("остановка не удалась: " + ex.Message, true);
        }
        finally
        {
            p.Dispose();
            StatusChanged?.Invoke(null, EventArgs.Empty);
        }
    }

    public static async Task RestartAsync()
    {
        await StopAsync();
        await Task.Delay(400);
        await StartAsync();
    }

    /// <summary>Which local ports the engine is listening on, for the setup instructions.</summary>
    public static IReadOnlyList<(string name, int port)> ProxyPorts { get; } = new[]
    {
        ("по правилам", 1080), ("напрямую", 1081), ("через WARP", 1082), ("через geo", 1083),
    };

    private const int SIGINT = 2;

    [DllImport("libc", SetLastError = true)]
    private static extern int kill(int pid, int sig);
}
