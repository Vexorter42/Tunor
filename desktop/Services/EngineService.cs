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
    /// One running engine. <paramref name="Ours"/> is false when this app cannot read
    /// the process — which is what happens with the privileged one: root's under the
    /// macOS service, elevated on Windows. That one is visible but not ours to signal.
    /// </summary>
    public sealed record EngineProcess(int Pid, bool Ours);

    /// <summary>Every engine running out of this install, with whether we can touch it.</summary>
    public static List<EngineProcess> Running()
    {
        var name = Path.GetFileNameWithoutExtension(ExePath);
        var found = new List<EngineProcess>();
        foreach (var p in Process.GetProcessesByName(name))
        {
            using (p)
            {
                try
                {
                    var path = p.MainModule?.FileName;
                    // A readable path that is not ours belongs to another install.
                    if (path != null && !PathEquals(path, ExePath)) continue;
                    found.Add(new EngineProcess(p.Id, true));
                }
                catch
                {
                    // Unreadable means privileged, and the name already matched.
                    found.Add(new EngineProcess(p.Id, false));
                }
            }
        }
        return found;
    }

    public static bool IsRunning => Running().Count > 0;

    /// <summary>True when an engine is up that this app has no right to stop.</summary>
    public static bool PrivilegedRunning => Running().Any(p => !p.Ours);

    private static bool PathEquals(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    /// <summary>
    /// Starts the engine and waits a moment to see whether it stays up. A config the
    /// engine refuses makes it print why and quit within milliseconds, so reporting
    /// "запущен" the instant the process exists would be a lie the user then has to
    /// investigate on their own.
    /// </summary>
    public static async Task<(bool Ok, string Message)> StartAsync()
    {
        if (!IsInstalled) return (false, $"движок не найден: {ExePath}");
        if (!File.Exists(Paths.ConfigJson)) return (false, $"нет конфига: {Paths.ConfigJson}");
        // Starting a second engine over a privileged one gives two of them fighting for
        // the same ports and the same interface, and the new one usually loses noisily.
        if (PrivilegedRunning)
            return (false, "движок уже работает с правами администратора — сначала останови его");
        if (IsRunning) return (true, "уже запущен");

        EngineLog.BeginRun();
        EngineLog.Add($"--- запуск: {ExePath} run -c {Paths.ConfigJson}");
        try
        {
            var p = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = ExePath,
                    Arguments = $"run -c \"{Paths.ConfigJson}\"",
                    WorkingDirectory = Paths.BuildDir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                },
                EnableRaisingEvents = true,
            };
            // The engine writes its log to stderr and little to stdout; both are read, or
            // a full pipe would stall it.
            p.OutputDataReceived += (_, e) => { if (e.Data != null) EngineLog.Add(e.Data); };
            p.ErrorDataReceived += (_, e) => { if (e.Data != null) EngineLog.Add(e.Data); };
            p.Exited += (_, _) =>
            {
                EngineLog.Add($"--- движок завершился, код {p.ExitCode}");
                StateChanged?.Invoke(null, EventArgs.Empty);
            };

            if (!p.Start()) return (false, "процесс не стартовал");
            p.BeginOutputReadLine();
            p.BeginErrorReadLine();
            _own = p;
            StateChanged?.Invoke(null, EventArgs.Empty);

            // Long enough for a refused config to fail, short enough not to feel stuck.
            await Task.Delay(1200);
            if (!p.HasExited) return (true, "запущен");

            var why = EngineLog.Reason();
            return (false, string.IsNullOrWhiteSpace(why)
                ? $"движок сразу завершился, код {p.ExitCode}"
                : why);
        }
        catch (Exception ex)
        {
            EngineLog.Add("--- не удалось запустить: " + ex.Message);
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// Asks the engine to stop, then insists. SIGTERM lets it close its interface and
    /// put the routes back; killing it outright can leave a TUN device behind.
    /// </summary>
    public static async Task<(bool Ok, string Message)> StopAsync()
    {
        var procs = Running();
        if (procs.Count == 0) return (true, "уже остановлен");

        foreach (var p in procs.Where(p => p.Ours)) Signal(p.Pid, SIGTERM);
        for (var i = 0; i < 50 && Running().Any(p => p.Ours); i++) await Task.Delay(100);
        foreach (var p in Running().Where(p => p.Ours)) Signal(p.Pid, SIGKILL);
        await Task.Delay(300);

        var left = Running();
        if (left.Count == 0)
        {
            EngineLog.Add("--- остановлен");
            EngineLog.EndRun();
            StateChanged?.Invoke(null, EventArgs.Empty);
            return (true, "остановлен");
        }

        // Saying "остановлен" while the tunnel carries on is the one answer that must
        // never be given: the user turns it off, believes it, and keeps browsing through
        // it. A privileged engine cannot be signalled from here — say so, and say what
        // will stop it.
        StateChanged?.Invoke(null, EventArgs.Empty);
        var privileged = left.Count(p => !p.Ours);
        EngineLog.Add($"--- остановить не удалось: процессов осталось {left.Count}");
        return (false, privileged > 0
            ? "движок работает с правами администратора — отсюда его не остановить"
            : $"не остановился, процессов осталось {left.Count}");
    }

    /// <summary>
    /// Stops a privileged engine, asking macOS for the rights to do it. The password goes
    /// into the system's own box; this process never sees it.
    /// </summary>
    public static async Task<(bool Ok, string Message)> StopElevatedAsync()
    {
        var pids = Running().Where(p => !p.Ours).Select(p => p.Pid).ToList();
        if (pids.Count == 0) return (true, "нечего останавливать");
        if (!OperatingSystem.IsMacOS())
            return (false, "остановить с правами отсюда можно только на macOS");

        var cmd = "/bin/kill " + string.Join(" ", pids);
        var script = $"do shell script \"{cmd}\" with administrator privileges";
        try
        {
            var p = Process.Start(new ProcessStartInfo
            {
                FileName = "osascript",
                ArgumentList = { "-e", script },
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            });
            if (p == null) return (false, "не удалось запросить права");
            var err = await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();

            for (var i = 0; i < 30 && IsRunning; i++) await Task.Delay(100);
            StateChanged?.Invoke(null, EventArgs.Empty);

            if (!IsRunning)
            {
                EngineLog.Add("--- остановлен с правами администратора");
                EngineLog.EndRun();
                return (true, "остановлен");
            }
            if (err.Contains("-128") || err.Contains("User canceled")) return (false, "отменено");
            var why = Readable(err);
            return (false, why.Length > 0 ? why : "движок всё ещё работает");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    /// <summary>Sends a signal, or kills on Windows where signals do not exist.</summary>
    private static void Signal(int pid, int sig)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var p = Process.GetProcessById(pid);
                p.Kill();
            }
            else Kill(pid, sig);
        }
        catch { /* already gone, or not ours after all */ }
    }

    private const int SIGTERM = 15;
    private const int SIGKILL = 9;

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
        //
        // /Library/PrivilegedHelperTools is where the root-owned copy goes. It ships with
        // macOS, but on a machine where nothing has ever installed a privileged helper it
        // can be missing, and the engine then refuses with exactly that complaint. Making
        // it here costs nothing when it is already there and saves a dead end when it is
        // not — and it rides in the same elevated command, so still only one password.
        var cmd = "/bin/mkdir -m 0755 -p /Library/PrivilegedHelperTools && "
                + $"/usr/sbin/chown root:wheel /Library/PrivilegedHelperTools && "
                + $"\\\"{ExePath}\\\" lxd --service install";
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
            if (err.Contains("-128") || err.Contains("User canceled")) return (false, "отменено");
            var why = Readable(err);
            return (false, why.Length > 0 ? why : $"код выхода {p.ExitCode}");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    /// <summary>
    /// The engine's complaint, fit to show. It writes for a terminal: colour codes, a
    /// FATAL banner and a timestamp, all of which arrive as rubbish in a label. Only the
    /// sentence is kept, and osascript's own wrapper around it is dropped.
    /// </summary>
    private static string Readable(string raw)
    {
        var text = System.Text.RegularExpressions.Regex.Replace(raw, @"\x1B\[[0-9;]*[A-Za-z]", "");
        // "0:133: execution error: " is osascript saying the command failed, not why.
        var at = text.IndexOf("execution error:", StringComparison.Ordinal);
        if (at >= 0) text = text[(at + "execution error:".Length)..];
        text = System.Text.RegularExpressions.Regex.Replace(text, @"^\s*(FATAL|ERROR)\s*\[[^\]]*\]\s*", "",
            System.Text.RegularExpressions.RegexOptions.Multiline);
        return text.Replace("\r", " ").Replace("\n", " ").Trim();
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
