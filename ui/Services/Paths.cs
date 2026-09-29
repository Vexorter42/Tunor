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

    static Paths()
    {
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
