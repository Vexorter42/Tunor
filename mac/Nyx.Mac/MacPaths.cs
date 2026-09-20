using System;
using System.IO;

namespace Nyx.Services;

/// <summary>
/// macOS layout. Replaces the Windows Paths.cs for this build — that file is NOT linked
/// in, so the Windows app is untouched and this one owns the whole platform difference.
///
/// A .app bundle is read-only in practice (it lands in /Applications, it gets replaced
/// wholesale on update, and writing inside it breaks the code signature), so nothing the
/// user owns may live there. Configs, rules and the generated config.json go to
/// ~/Library/Application Support/Nyx; only the engine ships inside the bundle.
///
/// The directory *shape* under Application Support matches the Windows install, because
/// rule-set paths inside rules.json are relative to the engine's working directory
/// ("../data/rulesets/x.srs") and must resolve identically on both platforms.
/// </summary>
public static class Paths
{
    /// <summary>~/Library/Application Support/Nyx — everything the user owns.</summary>
    public static string AppRoot { get; }

    /// <summary>Inside the .app bundle: Nyx.app/Contents/Resources.</summary>
    public static string BundleResources { get; }

    public static string BuildDir => Path.Combine(AppRoot, "build");
    public static string DataDir => Path.Combine(AppRoot, "data");
    public static string RulesetsDir => Path.Combine(DataDir, "rulesets");
    public static string LogsDir => Path.Combine(AppRoot, "logs");

    public static string SettingsJson => Path.Combine(AppRoot, "settings.json");
    public static string UpdateJson => Path.Combine(AppRoot, "update.json");
    public static string ConfigJson => Path.Combine(BuildDir, "config.json");
    public static string RulesJson => Path.Combine(DataDir, "rules.json");
    public static string WarpConf => Path.Combine(DataDir, "warp.conf");
    public static string GeoConf => Path.Combine(DataDir, "geo.conf");
    public static string CrashLog => Path.Combine(AppRoot, "crash.log");

    /// <summary>The engine, shipped inside the bundle and never written to.</summary>
    public static string SingBox => Path.Combine(BundleResources, "sing-box");

    static Paths()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        AppRoot = Path.Combine(home, "Library", "Application Support", "Nyx");

        // Running from the bundle the executable sits in Contents/MacOS; during
        // development it is just a build directory, and Resources sits next to it.
        var exeDir = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
        var contents = Path.GetDirectoryName(exeDir);
        var bundled = contents == null ? null : Path.Combine(contents, "Resources");
        BundleResources = bundled != null && Directory.Exists(bundled)
            ? bundled
            : Path.Combine(exeDir, "Resources");
    }

    /// <summary>Creates the user directories and seeds first-run files.</summary>
    public static void EnsureLayout()
    {
        Directory.CreateDirectory(BuildDir);
        Directory.CreateDirectory(RulesetsDir);
        Directory.CreateDirectory(LogsDir);

        if (!File.Exists(SettingsJson))
        {
            // No TUN on macOS yet: that needs root. Proxy mode works with no privileges.
            File.WriteAllText(SettingsJson,
                "{\n  \"accept\": false,\n  \"tun\": false,\n  \"proxy\": true,\n" +
                "  \"final\": \"direct\",\n  \"logging\": true\n}\n");
        }
        if (!File.Exists(RulesJson)) File.WriteAllText(RulesJson, "[]\n");
        if (!File.Exists(WarpConf)) File.WriteAllText(WarpConf, Placeholder("WARP"));
        if (!File.Exists(GeoConf)) File.WriteAllText(GeoConf, Placeholder("GEO"));
    }

    private static string Placeholder(string which) =>
        $"# NYX-PLACEHOLDER - not a working tunnel.\n" +
        $"# Paste a real {which} WireGuard/AmneziaWG config here, or drop a .conf on the window.\n" +
        "[Interface]\nPrivateKey =\nAddress = 172.16.0.2/32\n\n" +
        "[Peer]\nPublicKey =\nAllowedIPs = 0.0.0.0/0, ::/0\nEndpoint = 127.0.0.1:51820\n";
}
