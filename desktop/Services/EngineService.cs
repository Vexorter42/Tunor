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

    /// <summary>
    /// The names an engine runs under. The privileged service does not run the binary
    /// this app knows about: it installs a root-owned copy called sing-box-lxd and runs
    /// that, which is why looking only for "sing-box" found nothing while the tunnel was
    /// plainly up — and why the app then offered to start a second one.
    /// </summary>
    private static string[] ProcessNames => new[]
    {
        Path.GetFileNameWithoutExtension(ExePath),      // sing-box, ours
        "sing-box-lxd",                                 // the service's own copy
    };

    /// <summary>Every engine running, with whether this app may signal it.</summary>
    public static List<EngineProcess> Running()
        => OperatingSystem.IsWindows() ? RunningWindows() : RunningUnix();

    /// <summary>
    /// On macOS the engine usually runs as root — started with rights for TUN, or by the
    /// service — and .NET cannot read another user's process details there, so
    /// Process.GetProcessesByName finds nothing and the app reports a running tunnel as
    /// stopped. pgrep asks the kernel directly and answers for every owner, which is what
    /// ps does and what the user sees.
    /// </summary>
    private static List<EngineProcess> RunningUnix()
    {
        var found = new List<EngineProcess>();
        var me = Environment.UserName;
        foreach (var name in ProcessNames.Distinct())
        {
            // -x: whole name only, so "sing-box" never matches "sing-box-lxd" twice.
            var lines = Shell("pgrep", $"-x -l -u root,{me} {name}");
            foreach (var line in lines)
            {
                var pid = line.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
                if (!int.TryParse(pid, out var id)) continue;
                // Ours to signal only when it runs as this user; root's needs elevation,
                // and the service's copy is never ours whoever owns it.
                var mine = name != "sing-box-lxd" && Shell("pgrep", $"-x -u {me} {name}")
                    .Any(l => l.Trim() == id.ToString());
                found.Add(new EngineProcess(id, mine));
            }
        }
        return found;
    }

    private static List<EngineProcess> RunningWindows()
    {
        var found = new List<EngineProcess>();
        var seen = new HashSet<int>();
        foreach (var name in ProcessNames.Distinct())
        {
            Process[] all;
            try { all = Process.GetProcessesByName(name); } catch { continue; }
            foreach (var p in all)
            {
                using (p)
                {
                    if (!seen.Add(p.Id)) continue;
                    // The service's copy is never ours to signal, whatever its path says.
                    if (name == "sing-box-lxd") { found.Add(new EngineProcess(p.Id, false)); continue; }
                    try
                    {
                        var path = p.MainModule?.FileName;
                        // A readable path that is not ours belongs to another install.
                        if (path != null && !PathEquals(path, ExePath)) continue;
                        found.Add(new EngineProcess(p.Id, true));
                    }
                    catch
                    {
                        // Unreadable means elevated, and the name already matched.
                        found.Add(new EngineProcess(p.Id, false));
                    }
                }
            }
        }
        return found;
    }

    /// <summary>Runs a command and returns its output lines; empty on any failure.</summary>
    private static List<string> Shell(string file, string args)
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo
            {
                FileName = file, Arguments = args,
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            });
            if (p == null) return new List<string>();
            var text = p.StandardOutput.ReadToEnd();
            p.WaitForExit(3000);
            return text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                       .Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        }
        catch { return new List<string>(); }
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

        // TUN raises a network interface, and the rights for that belong to root. Rather
        // than fail with the engine's "operation not permitted", or send the user off to
        // install a service, the engine is started with those rights: macOS asks for the
        // password in its own box, and the engine runs as root from there. It then shows
        // up as a privileged process, which is what the Stop button already handles.
        if (NeedsPrivileges()) return await StartElevatedAsync();

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
            if (!p.HasExited) { Monitor(elevated: false); return (true, "запущен"); }

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
        // Telling the watchdog first: otherwise it sees the engine go and brings back
        // exactly what the user just asked to stop.
        _wantRunning = false;
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
    /// Removes the privileged service, which is the engine's own documented off switch:
    /// `--service` offers install, copy, uninstall and status, and nothing that merely
    /// pauses it. Killing the daemon instead would leave launchd to start it again.
    /// The tunnel stops with it; the app can then run its own engine, without TUN.
    /// </summary>
    public static async Task<(bool Ok, string Message)> UninstallService()
    {
        if (!IsInstalled) return (false, "движок не найден");
        if (!OperatingSystem.IsMacOS())
            return (false, "удаление службы отсюда поддерживается только на macOS");

        var script = "do shell script \"" + $"\\\"{ExePath}\\\" lxd --service uninstall"
                   + "\" with administrator privileges";
        var (ok, err, code) = await Elevated(script);
        if (ok)
        {
            EngineLog.Add("--- служба удалена");
            StateChanged?.Invoke(null, EventArgs.Empty);
            return (true, "служба удалена, туннель остановлен");
        }
        if (err.Contains("-128") || err.Contains("User canceled")) return (false, "отменено");
        var why = Readable(err);
        return (false, why.Length > 0 ? why : $"код выхода {code}");
    }

    /// <summary>Runs one osascript, which raises the system's own password box.</summary>
    private static async Task<(bool Ok, string Err, int Code)> Elevated(string script)
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo
            {
                FileName = "osascript",
                ArgumentList = { "-e", script },
                WorkingDirectory = Paths.BuildDir,
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            });
            if (p == null) return (false, "не удалось запросить права", -1);
            var err = await p.StandardError.ReadToEndAsync();
            await p.WaitForExitAsync();
            return (p.ExitCode == 0, err, p.ExitCode);
        }
        catch (Exception ex) { return (false, ex.Message, -1); }
    }

    /// <summary>
    /// Stops a privileged engine, asking macOS for the rights to do it. The password goes
    /// into the system's own box; this process never sees it.
    /// </summary>
    public static async Task<(bool Ok, string Message)> StopElevatedAsync()
    {
        _wantRunning = false;
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

    /// <summary>
    /// True when the config asks for something this app cannot do unaided, with what to
    /// do about it. Checked before starting, so the answer is advice rather than the
    /// engine's complaint after the fact.
    /// </summary>
    private static bool NeedsPrivileges()
    {
        if (OperatingSystem.IsWindows()) return false;   // the WPF app runs elevated
        try { return SettingsService.Load().Tun; } catch { return false; }
    }

    /// <summary>
    /// Starts the engine as root, for TUN. The output cannot be piped back through
    /// osascript, so the engine is told to write its log beside the config and the Logs
    /// page reads that file instead — the user still sees why, if it will not start.
    /// </summary>
    private static async Task<(bool Ok, string Message)> StartElevatedAsync()
    {
        if (!OperatingSystem.IsMacOS())
            return (false, "запуск с правами отсюда поддерживается только на macOS");

        EngineLog.BeginRun();
        EngineLog.Add("--- запуск с правами администратора");

        // osascript waits for the command's output to close, so the engine is backgrounded
        // with every stream redirected — including stdin, or it keeps the handle open and
        // the password box never goes away. nohup was the first attempt and fails here
        // with "can't detach from console": there is no controlling terminal to leave.
        //
        // The log is created and handed to the user first, because root writing it would
        // leave a file the app itself could not reopen on the next ordinary start.
        var log = EngineLog.LogPath;
        var user = Environment.UserName;
        var cmd = $"/usr/bin/touch \\\"{log}\\\"; /usr/sbin/chown {user} \\\"{log}\\\"; "
                + $"\\\"{ExePath}\\\" run -c \\\"{Paths.ConfigJson}\\\" "
                + $"</dev/null >> \\\"{log}\\\" 2>&1 &";
        var script = $"do shell script \"{cmd}\" with administrator privileges";

        var (ok, err, code) = await Elevated(script);
        if (!ok)
        {
            if (err.Contains("-128") || err.Contains("User canceled")) return (false, "отменено");
            var why = Readable(err);
            return (false, why.Length > 0 ? why : $"код выхода {code}");
        }

        // Same reasoning as the ordinary start: a refused config dies at once.
        await Task.Delay(1500);
        StateChanged?.Invoke(null, EventArgs.Empty);
        if (IsRunning) { Monitor(elevated: true); return (true, "запущен с правами администратора"); }

        var reason = TailOfLog();
        return (false, reason.Length > 0 ? reason : "движок сразу завершился");
    }

    /// <summary>The last complaint in the log file, for a start we could not watch.</summary>
    private static string TailOfLog()
    {
        try
        {
            if (!File.Exists(EngineLog.LogPath)) return "";
            var lines = File.ReadAllLines(EngineLog.LogPath);
            foreach (var l in lines) EngineLog.Add(l);
            return EngineLog.Reason() ?? "";
        }
        catch { return ""; }
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

    // ---------------------------------------------------------------- watchdog

    /// <summary>True from a start until someone asks for a stop — the watchdog's mandate.</summary>
    private static volatile bool _wantRunning;
    private static readonly List<DateTime> _restarts = new();
    private static readonly int[] BackoffSeconds = { 3, 10, 30 };

    /// <summary>Said when the tunnel went down and is not coming back by itself.</summary>
    public static event EventHandler<string>? Alert;

    private static Task? _monitor;

    /// <summary>
    /// Watches for the engine going away. A process started with rights belongs to root,
    /// so there is no exit event to subscribe to — asking every couple of seconds works
    /// for that one and for our own alike, and costs one pgrep.
    /// </summary>
    private static void Monitor(bool elevated)
    {
        _wantRunning = true;
        if (_monitor is { IsCompleted: false }) return;
        _monitor = Task.Run(async () =>
        {
            while (_wantRunning)
            {
                await Task.Delay(2000);
                if (!_wantRunning || IsRunning) continue;
                await WatchdogAsync(elevated);
                return;
            }
        });
    }

    /// <summary>
    /// Brings the engine back after it dies on its own, bounded to three attempts in five
    /// minutes so a config it will never accept does not spin forever.
    ///
    /// An engine started with administrator rights is not restarted: doing so would raise
    /// the password box on its own, which is not something a program should do while
    /// nobody is looking. That case is reported instead.
    /// </summary>
    private static async Task WatchdogAsync(bool wasElevated)
    {
        if (!_wantRunning) return;                  // stopped on purpose: nothing to say

        var enabled = true;
        try { enabled = SettingsService.Load().Watchdog; } catch { }
        var reason = EngineLog.Reason();

        if (!enabled || wasElevated)
        {
            EngineLog.Add("--- движок упал" + (enabled ? ", но перезапуск потребовал бы пароля" : ""));
            Alert?.Invoke(null, reason ?? "Туннель остановился.");
            return;
        }

        int attempt;
        lock (_restarts)
        {
            _restarts.RemoveAll(t => DateTime.UtcNow - t > TimeSpan.FromMinutes(5));
            if (_restarts.Count >= BackoffSeconds.Length)
            {
                EngineLog.Add("--- движок падает снова и снова, больше не перезапускаю");
                Alert?.Invoke(null, "Туннель падает снова и снова, Tunor перестал его перезапускать. "
                                    + (reason ?? ""));
                return;
            }
            _restarts.Add(DateTime.UtcNow);
            attempt = _restarts.Count;
        }

        var delay = BackoffSeconds[attempt - 1];
        EngineLog.Add($"--- перезапускаю через {delay} с (попытка {attempt} из {BackoffSeconds.Length})");
        await Task.Delay(TimeSpan.FromSeconds(delay));

        // The user may have stopped it, or started it by hand, in the meantime.
        if (!_wantRunning || IsRunning) return;
        await StartAsync();
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
        ServiceState.NotInstalled => "служба не установлена — TUN работает, но пароль спросят при каждом запуске",
        ServiceState.NotSupported => "движок не найден",
        _ => "состояние службы неизвестно",
    };
}
