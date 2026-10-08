using System;
using System.IO;
using System.Reflection;

namespace Tunor.Services;

public static class Paths
{
    public static string AppRoot { get; }
    public static string BuildDir => Path.Combine(AppRoot, "build");
    public static string DataDir => Path.Combine(AppRoot, "data");
    public static string RulesetsDir => Path.Combine(DataDir, "rulesets");
    public static string LicensesDir => Path.Combine(AppRoot, "licenses");
    public static string ThirdPartyNotices => Path.Combine(AppRoot, "THIRD-PARTY-NOTICES.md");

    public static string SettingsJson => Path.Combine(AppRoot, "settings.json");
    public static string UpdateJson => Path.Combine(AppRoot, "update.json");
    public static string ConfigJson => Path.Combine(BuildDir, "config.json");
    public static string RulesJson => Path.Combine(DataDir, "rules.json");
    public static string WarpConf => Path.Combine(DataDir, "warp.conf");
    public static string GeoConf => Path.Combine(DataDir, "geo.conf");

    public static string SingBoxExe => Path.Combine(BuildDir, "sing-box.exe");
    // The helper scripts in build/ (run.bat, stop.bat, restart-headless.vbs) are for a
    // person to run by hand when the app itself will not open. Nothing in the app calls
    // them — it starts and stops the engine directly — so they are not named here.
    public static string UiExe { get; } = Environment.ProcessPath ?? Assembly.GetEntryAssembly()?.Location ?? "";

    /// <summary>
    /// Where a Linux build keeps its files: $XDG_CONFIG_HOME/tunor, or ~/.config/tunor
    /// when that is not set, which is what the XDG base-directory spec asks for. The
    /// program itself may sit in /usr/bin or inside an AppImage — neither is a place it
    /// can write — so nothing of the user's lives beside it.
    /// </summary>
    private static string LinuxRoot()
    {
        var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        if (string.IsNullOrWhiteSpace(config))
            config = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
        return Ensure(Path.Combine(config, "tunor"));
    }

    /// <summary>Makes the root and the two folders everything else assumes.</summary>
    private static string Ensure(string root)
    {
        try
        {
            Directory.CreateDirectory(root);
            Directory.CreateDirectory(Path.Combine(root, "build"));
            Directory.CreateDirectory(Path.Combine(root, "data"));
        }
        catch { /* an unwritable home is a problem the first save will report properly */ }
        return root;
    }

    /// <summary>
    /// Where a macOS build keeps its files. A program there lives in /Applications as a
    /// bundle that the user may not write to, and everything it owns belongs under
    /// Application Support instead — unlike Windows, where the install directory holds
    /// both. The folder is created on first look, so a fresh install has somewhere to
    /// write before anything has been set up.
    /// </summary>
    private static string MacRoot()
    {
        return Ensure(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "Application Support", "Tunor"));
    }

    static Paths()
    {
        // Neither of these keeps anything beside the program: on macOS it lives in a
        // bundle the user may not write to, on Linux in /usr/bin or an AppImage.
        if (OperatingSystem.IsMacOS()) { AppRoot = MacRoot(); return; }
        if (OperatingSystem.IsLinux()) { AppRoot = LinuxRoot(); return; }

        var exeDir = Path.GetDirectoryName(Assembly.GetEntryAssembly()?.Location
            ?? Environment.ProcessPath
            ?? AppContext.BaseDirectory)!;

        // UI lives at AppRoot\ui — walk up one level
        var parent = Directory.GetParent(exeDir);
        if (parent != null && File.Exists(Path.Combine(parent.FullName, "settings.json")))
        {
            AppRoot = parent.FullName;
        }
        else if (File.Exists(Path.Combine(exeDir, "settings.json")))
        {
            AppRoot = exeDir;
        }
        else
        {
            // bin\Debug\net8.0-windows fallback → walk up to find settings.json
            var dir = new DirectoryInfo(exeDir);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "settings.json")))
                {
                    AppRoot = dir.FullName;
                    return;
                }
                dir = dir.Parent;
            }
            AppRoot = exeDir;
        }
    }
}
