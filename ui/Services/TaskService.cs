using System.Diagnostics;
using System.Threading.Tasks;

namespace Tunor.Services;

public static class TaskService
{
    public const string TaskName = "Tunor";
    // Task names used by previous versions — cleaned up on (re)install.
    public static readonly string[] LegacyTaskNames = { "Nyx", "SSnet-UI", "SSnet" };
    public const string AutostartArg = "--autostart";

    public static bool IsInstalled() => QueryExists(TaskName);

    /// <summary>
    /// Carries autostart across a rename. A task made under the old name still points at
    /// the old executable, which this version's installer removes — so autostart would
    /// quietly stop working, and the switch in the app would show it as off. Re-creates
    /// it under the current name, pointing at this build.
    /// </summary>
    public static async Task MigrateLegacyAsync()
    {
        try
        {
            if (IsInstalled()) return;
            if (!System.Array.Exists(LegacyTaskNames, QueryExists)) return;

            var (ok, output) = await InstallAsync();
            ProcessService.Note(ok
                ? "автозапуск перенесён на новое имя программы"
                : "не удалось перенести автозапуск: " + output, !ok);
        }
        catch { /* autostart is a convenience; never let it break startup */ }
    }

    private static bool QueryExists(string name)
    {
        var (ok, _) = RunSchtasks("/query", "/tn", name);
        return ok;
    }

    public static Task<(bool ok, string output)> InstallAsync() => Task.Run<(bool, string)>(() =>
    {
        try
        {
            foreach (var legacy in LegacyTaskNames)
                if (QueryExists(legacy)) RunSchtasks("/delete", "/tn", legacy, "/f");

            var exe = Paths.UiExe;
            if (string.IsNullOrEmpty(exe))
                return (false, "Не удалось определить путь к Tunor.exe");

            var trValue = $"\"{exe}\" {AutostartArg}";
            return RunSchtasks("/create", "/tn", TaskName, "/tr", trValue,
                "/sc", "onlogon", "/rl", "highest", "/f");
        }
        catch (System.Exception ex)
        {
            return (false, ex.Message);
        }
    });

    public static Task<(bool ok, string output)> DeleteAsync() => Task.Run<(bool, string)>(() =>
    {
        try
        {
            var (ok, out1) = RunSchtasks("/delete", "/tn", TaskName, "/f");
            foreach (var legacy in LegacyTaskNames)
                if (QueryExists(legacy)) RunSchtasks("/delete", "/tn", legacy, "/f");
            return (ok, out1);
        }
        catch (System.Exception ex)
        {
            return (false, ex.Message);
        }
    });

    private static (bool ok, string output) RunSchtasks(params string[] args)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "schtasks.exe",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = OemEncoding,
                StandardErrorEncoding = OemEncoding,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            using var p = Process.Start(psi);
            if (p == null) return (false, "failed to start schtasks.exe");
            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            p.WaitForExit(5000);
            var msg = (stderr + "\n" + stdout).Trim();
            return (p.ExitCode == 0, msg);
        }
        catch (System.Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private static readonly System.Text.Encoding OemEncoding = ResolveOemEncoding();

    private static System.Text.Encoding ResolveOemEncoding()
    {
        try
        {
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            var cp = System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage;
            return System.Text.Encoding.GetEncoding(cp);
        }
        catch { return System.Text.Encoding.UTF8; }
    }
}
