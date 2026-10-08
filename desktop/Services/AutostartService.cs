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

    /// <summary>
    /// What the unit is called on Linux. Reverse-DNS is a launchd convention and looks
    /// out of place in systemctl, where people type the name themselves:
    /// `systemctl --user status tunor` reads better than the label above.
    /// </summary>
    private const string Unit_ = "tunor";

    private static string PlistPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "Library", "LaunchAgents", Label + ".plist");

    /// <summary>Where systemd --user looks for the units a login should start.</summary>
    private static string UnitPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "systemd", "user", Unit_ + ".service");

    public static bool Supported => OperatingSystem.IsMacOS() || OperatingSystem.IsLinux();

    private static string File_ => OperatingSystem.IsLinux() ? UnitPath : PlistPath;

    /// <summary>
    /// What systemd starts at login is what `enable` has linked into the target, not
    /// the unit file beside it — so that link is the question, and the file alone is
    /// not an answer.
    /// </summary>
    private static string WantPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".config", "systemd", "user", "default.target.wants", Unit_ + ".service");

    public static bool Enabled => Supported &&
        (OperatingSystem.IsLinux() ? File.Exists(WantPath) || Directory.Exists(WantPath)
                                   : File.Exists(File_));

    /// <summary>
    /// The app to launch. Inside a bundle the running file is Tunor.app/Contents/MacOS/Tunor;
    /// launchd is given that directly rather than the bundle, so it needs no `open`.
    /// </summary>
    /// <summary>
    /// What the autostart entry should point at.
    ///
    /// Not ProcessPath when running from an AppImage: that is a mount under /tmp made
    /// for this run and gone by the next one, so a unit written from it would point at
    /// nothing after a reboot. $APPIMAGE is the file the user actually keeps.
    /// </summary>
    private static string AppPath =>
        Environment.GetEnvironmentVariable("APPIMAGE") is { Length: > 0 } image && File.Exists(image)
            ? image
            : Environment.ProcessPath ?? Paths.UiExe;

    // ---------------------------------------------------------------- menu entry

    /// <summary>
    /// Where a desktop looks for the programs it offers. The user's own directory, so
    /// nothing here needs a password or touches the system.
    /// </summary>
    private static string MenuEntry => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".local", "share", "applications", Unit_ + ".desktop");

    /// <summary>Only Linux has this notion; macOS finds apps by where they live.</summary>
    public static bool MenuSupported => OperatingSystem.IsLinux();

    public static bool InMenu => MenuSupported && File.Exists(MenuEntry);

    public static (bool Ok, string Message) AddToMenu()
    {
        if (!MenuSupported) return (false, "не для этой системы");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MenuEntry)!);
            File.WriteAllText(MenuEntry, string.Join("\n",
                "[Desktop Entry]",
                "Type=Application",
                "Name=Tunor",
                "Comment=Туннель на базе sing-box с поддержкой AmneziaWG",
                // Quoted: an AppImage is often kept somewhere with a space in the path.
                $"Exec=\"{AppPath}\"",
                "Icon=" + IconName(),
                "Categories=Network;Utility;",
                "Terminal=false",
                "StartupWMClass=TunorDesktop",
                ""));
            // Desktops cache this directory; the ones that do watch for the stamp.
            Run("update-desktop-database", $"\"{Path.GetDirectoryName(MenuEntry)}\"");
            return (true, "Tunor появится в меню приложений");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    public static (bool Ok, string Message) RemoveFromMenu()
    {
        try
        {
            if (File.Exists(MenuEntry)) File.Delete(MenuEntry);
            return (true, "убрано из меню");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    /// <summary>
    /// The icon to name in the entry. Inside an AppImage one is already installed under
    /// the mount; copying it into the user's own icon directory means the menu keeps
    /// showing it after the app is closed and the mount is gone.
    /// </summary>
    private static string IconName()
    {
        try
        {
            var inside = Path.Combine(AppContext.BaseDirectory, "..", "..", "tunor.png");
            var dest = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".local", "share", "icons", "hicolor", "256x256", "apps", "tunor.png");
            if (File.Exists(inside))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(inside, dest, overwrite: true);
            }
            return File.Exists(dest) ? "tunor" : "network-vpn";
        }
        catch { return "network-vpn"; }   // a generic icon beats a broken one
    }

    public static (bool Ok, string Message) Enable()
    {
        if (!Supported) return (false, "автозапуск поддерживается только на macOS");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(File_)!);
            File.WriteAllText(File_, OperatingSystem.IsLinux() ? Unit() : Plist());
            if (OperatingSystem.IsLinux())
            {
                // systemd reads its units once, and starts only what is linked into a
                // target. Neither is optional here, and a failure is reported rather
                // than swallowed: the file on its own does nothing at the next login.
                Run("systemctl", "--user daemon-reload");
                var (ok, said) = Ask("systemctl", $"--user enable {Unit_}.service");
                if (!ok || !Enabled)
                {
                    try { File.Delete(File_); } catch { }
                    return (false, said.Length > 0
                        ? "systemd не принял: " + said
                        : "systemd не включил юнит");
                }
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
                if (OperatingSystem.IsLinux()) Run("systemctl", $"--user disable {Unit_}.service");
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

    /// <summary>Runs a command and reports whether it worked and what it said.</summary>
    private static (bool Ok, string Said) Ask(string file, string args)
    {
        try
        {
            var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(file, args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            });
            if (p == null) return (false, "не запустилось");
            var err = p.StandardError.ReadToEnd().Trim();
            var outp = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(15_000);
            var said = err.Length > 0 ? err : outp;
            return (p.ExitCode == 0, said.Replace(LFCHAR, ' ').Trim());
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    private const char LFCHAR = '\n';

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
