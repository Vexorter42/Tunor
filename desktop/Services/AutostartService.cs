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

    public static bool Supported => OperatingSystem.IsMacOS();

    public static bool Enabled => Supported && File.Exists(PlistPath);

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
            Directory.CreateDirectory(Path.GetDirectoryName(PlistPath)!);
            File.WriteAllText(PlistPath, Plist());
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
            if (File.Exists(PlistPath))
            {
                Run("launchctl", $"unload -w \"{PlistPath}\"");
                File.Delete(PlistPath);
            }
            return (true, "выключен");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

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
