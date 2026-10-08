using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Tunor.Services;

namespace Tunor.Desktop.Services;

/// <summary>
/// Starting Tunor when the user logs in.
///
/// macOS does this with a LaunchAgent: a plist in ~/Library/LaunchAgents that launchd
/// reads at login. It belongs to the user, needs no password, and removing the file
/// undoes it — which is why this writes a file rather than shelling out to a tool.
///
/// The Windows app registers a scheduled task instead and keeps doing so; this is for
/// the ports, and says so plainly where it cannot help.
/// </summary>
public static class AutostartService
{
    private const string Label = "com.vexorter.tunor";

    private static string PlistPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library", "LaunchAgents", Label + ".plist");

    /// <summary>Where systemd --user looks for the units a login should start.</summary>
    private static string UnitPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "systemd", "user", Label + ".service");

    public static bool Supported => OperatingSystem.IsMacOS() || OperatingSystem.IsLinux();

    private static string File_ => OperatingSystem.IsLinux() ? UnitPath : PlistPath;

    public static bool Enabled => Supported && File.Exists(File_);

    /// <summary>
    /// The app to launch. Inside a bundle the running file is Tunor.app/Contents/MacOS/Tunor;
    /// launchd is given that directly rather than the bundle, so it needs no `open`.
    /// </summary>
    private static string AppPath => Environment.ProcessPath ?? Paths.UiExe;

    public static (bool Ok, string Message) Enable()
    {
        if (!Supported) return (false, "автозапуск поддерживается только на macOS");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(File_)!);
            File.WriteAllText(File_, OperatingSystem.IsLinux() ? Unit() : Plist());
            if (OperatingSystem.IsLinux())
            {
                // systemd reads its units once; without this the file is there and
                // nothing knows about it until the next login.
                Run("systemctl", "--user daemon-reload");
                Run("systemctl", $"--user enable {Label}.service");
                return (true, "автозапуск включён");
            }
            // launchctl picks the file up at next login on its own; loading it now means
            // the setting takes effect without one.
            Run("launchctl", $"load -w \"{PlistPath}\"");
            return (true, "включён");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    public static (bool Ok, string Message) Disable()
    {
        if (!Supported) return (false, "автозапуск поддерживается только на macOS");
        try
        {
            if (File.Exists(File_))
            {
                if (OperatingSystem.IsLinux()) Run("systemctl", $"--user disable {Label}.service");
                else Run("launchctl", $"unload -w \"{PlistPath}\"");
                File.Delete(File_);
                if (OperatingSystem.IsLinux()) Run("systemctl", "--user daemon-reload");
            }
            return (true, "выключен");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    /// <summary>
    /// The unit a login starts. Wanted by default.target rather than graphical-session,
    /// because the app is useful on a machine with no desktop session at all — and
    /// RestartSec keeps a failed start from spinning.
    /// </summary>
    private static string Unit() => $"""
        [Unit]
        Description=Tunor
        After=network.target

        [Service]
        Type=simple
        ExecStart="{AppPath}"
        Restart=on-failure
        RestartSec=5

        [Install]
        WantedBy=default.target
        """;

    private static string Plist() => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
            <key>Label</key>
            <string>{Label}</string>
            <key>ProgramArguments</key>
            <array>
                <string>{AppPath}</string>
            </array>
            <key>RunAtLoad</key>
            <true/>
            <!-- Only at login: launchd must not bring it back when the user quits it. -->
            <key>KeepAlive</key>
            <false/>
            <key>ProcessType</key>
            <string>Interactive</string>
        </dict>
        </plist>
        """;

    private static void Run(string file, string args)
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo
            {
                FileName = file, Arguments = args,
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
            });
            p?.WaitForExit(5000);
        }
        catch { /* the plist is what matters; launchctl only saves a re-login */ }
    }
}
