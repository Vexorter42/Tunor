using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Tunor.Services;

namespace Tunor.Desktop.Services;

/// <summary>
/// Starts and stops the engine, and says whether it is up.
///
/// The Windows app does this through the console control API, which does not exist
/// anywhere else; here it is a plain process and a signal. On macOS the TUN interface
/// needs privileges the app does not have, and the engine's own `lxd` service is what
/// holds them — <see cref="PrivilegedService"/> reports whether that is in place, so the
/// UI can say what is missing instead of failing silently.
/// </summary>
public static class EngineService
{
    public static event EventHandler? StateChanged;

    private static Process? _own;

    /// <summary>The engine binary: sing-box.exe on Windows, sing-box elsewhere.</summary>
    public static string ExePath => Path.Combine(Paths.BuildDir,
        OperatingSystem.IsWindows() ? "sing-box.exe" : "sing-box");

    public static bool IsInstalled => File.Exists(ExePath);

    /// <summary>
    /// Any engine running out of this install, ours or one left behind. Matched by the
    /// path it was started from, so another copy on the machine is left alone.
    /// </summary>
    public static Process[] Running()
    {
        var name = Path.GetFileNameWithoutExtension(ExePath);
        var ours = new List<Process>();
        foreach (var p in Process.GetProcessesByName(name))
        {
            try
            {
                var path = p.MainModule?.FileName;
                if (path != null && PathEquals(path, ExePath)) { ours.Add(p); continue; }
            }
            catch { /* a process we may not inspect is not one of ours */ }
            p.Dispose();
        }
        return ours.ToArray();
    }

    public static bool IsRunning => Running().Length > 0;

    private static bool PathEquals(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    public static (bool Ok, string Message) Start()
    {
        if (!IsInstalled) return (false, $"движок не найден: {ExePath}");
        if (IsRunning) return (true, "уже запущен");

        try
        {
            _own = Process.Start(new ProcessStartInfo
            {
                FileName = ExePath,
                Arguments = $"run -c \"{Paths.ConfigJson}\"",
                WorkingDirectory = Paths.BuildDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            });
            StateChanged?.Invoke(null, EventArgs.Empty);
            return _own != null ? (true, "запущен") : (false, "процесс не стартовал");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    /// <summary>
    /// Asks the engine to stop, then insists. SIGTERM lets it close its interface and
    /// put the routes back; killing it outright can leave a TUN device behind.
    /// </summary>
    public static async Task<(bool Ok, string Message)> StopAsync()
    {
        var procs = Running();
        if (procs.Length == 0) return (true, "уже остановлен");

        foreach (var p in procs)
        {
            try
            {
                if (OperatingSystem.IsWindows()) p.Kill();     // WPF app's job; see the note above
                else Kill(p.Id, SIGTERM);
            }
            catch { /* already gone */ }
        }

        for (var i = 0; i < 50 && IsRunning; i++) await Task.Delay(100);

        foreach (var p in Running())
        {
            try { p.Kill(); } catch { }
            p.Dispose();
        }
        foreach (var p in procs) p.Dispose();

        StateChanged?.Invoke(null, EventArgs.Empty);
        return (true, "остановлен");
    }

    private const int SIGTERM = 15;

    [DllImport("libc", EntryPoint = "kill", SetLastError = true)]
    private static extern int Kill(int pid, int sig);

    // ---------------------------------------------------------------- privileges

    public enum ServiceState { NotSupported, NotInstalled, NeedsReinstall, CopyOnly, Stopped, Running, Unknown }

    /// <summary>
    /// What the engine's privileged service says about itself. TUN needs it; the proxy
    /// modes do not, which is why a missing service is a warning and not a failure.
    /// Exit codes are the ones `sing-box lxd --service status` documents.
    /// </summary>
    public static async Task<ServiceState> PrivilegedService()
    {
        if (!IsInstalled) return ServiceState.NotSupported;
        try
        {
            var p = Process.Start(new ProcessStartInfo
            {
                FileName = ExePath,
                Arguments = "lxd --service status",
                WorkingDirectory = Paths.BuildDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p == null) return ServiceState.Unknown;
            await p.WaitForExitAsync();
            return p.ExitCode switch
            {
                0 => ServiceState.Running,
                2 => ServiceState.NeedsReinstall,
                3 => ServiceState.NotInstalled,
                4 => ServiceState.CopyOnly,
                5 => ServiceState.Stopped,
                _ => ServiceState.Unknown,
            };
        }
        catch { return ServiceState.Unknown; }
    }

    /// <summary>
    /// Installs the privileged service. It needs an administrator, and the engine asks for
    /// one itself — on macOS through osascript, which raises the system's own password
    /// box. Nothing here handles the password: it is typed into that box and never
    /// reaches this process.
    /// </summary>
    public static async Task<(bool Ok, string Message)> InstallService()
    {
        if (!IsInstalled) return (false, "движок не найден");
        if (!OperatingSystem.IsMacOS())
            return (false, "установка службы отсюда поддерживается только на macOS");

        // osascript's own prompt is what asks for the password; the quoting below is what
        // it needs to run one command with administrator rights.
        var cmd = $"\\\"{ExePath}\\\" lxd --service install";
        var script = $"do shell script \"{cmd}\" with administrator privileges";
        try
        {
            var p = Process.Start(new ProcessStartInfo
            {
                FileName = "osascript",
                ArgumentList = { "-e", script },
                WorkingDirectory = Paths.BuildDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p == null) return (false, "не удалось запросить права");

            var err = await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            if (p.ExitCode == 0) return (true, "служба установлена");

            // -128 is what osascript returns when the user dismisses the password box.
            return err.Contains("-128") || err.Contains("User canceled")
                ? (false, "отменено")
                : (false, err.Trim().Length > 0 ? err.Trim() : $"код выхода {p.ExitCode}");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    /// <summary>What to tell the user about the service, in plain words.</summary>
    public static string Explain(ServiceState s) => s switch
    {
        ServiceState.Running => "служба с правами работает",
        ServiceState.Stopped => "служба установлена, но не запущена",
        ServiceState.NeedsReinstall => "службу нужно переустановить",
        ServiceState.CopyOnly => "есть копия движка, но служба не установлена",
        ServiceState.NotInstalled => "служба не установлена — для режима TUN её нужно поставить",
        ServiceState.NotSupported => "движок не найден",
        _ => "состояние службы неизвестно",
    };
}
