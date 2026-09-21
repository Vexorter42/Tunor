using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Nyx.Services;

/// <summary>
/// Named copies of warp.conf / geo.conf, for switching between several tunnels (two
/// WARP accounts, geo exits in different countries) without pasting configs back and
/// forth. A profile is just a file in data/profiles/&lt;kind&gt;/; the active tunnel is still
/// warp.conf / geo.conf, which a switch overwrites (keeping a .bak).
/// </summary>
public static class ProfileService
{
    public const string Warp = "warp";
    public const string Geo = "geo";

    private static string Dir(string kind) => Path.Combine(Paths.DataDir, "profiles", kind);
    private static string Target(string kind) => kind == Geo ? Paths.GeoConf : Paths.WarpConf;
    private static string FileOf(string kind, string name) => Path.Combine(Dir(kind), name + ".conf");

    public static List<string> List(string kind)
    {
        var dir = Dir(kind);
        if (!Directory.Exists(dir)) return new List<string>();
        return Directory.EnumerateFiles(dir, "*.conf")
            .Select(f => Path.GetFileNameWithoutExtension(f))
            .OrderBy(n => n, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>The profile whose contents match the active file, if any.</summary>
    public static string? ActiveName(string kind)
    {
        var target = Target(kind);
        if (!File.Exists(target)) return null;
        var current = Normalize(File.ReadAllText(target));
        return List(kind).FirstOrDefault(n => Normalize(File.ReadAllText(FileOf(kind, n))) == current);
    }

    /// <summary>Saves the active file under a name. Returns the name actually used.</summary>
    public static string SaveCurrentAs(string kind, string rawName)
    {
        var name = Sanitize(rawName);
        if (name.Length == 0) throw new ArgumentException("Нужно имя профиля.");
        var target = Target(kind);
        if (!File.Exists(target)) throw new FileNotFoundException("Нечего сохранять: файл пуст.");

        Directory.CreateDirectory(Dir(kind));
        File.Copy(target, FileOf(kind, name), overwrite: true);
        return name;
    }

    /// <summary>Makes a profile the active tunnel and rebuilds config.json.</summary>
    public static void Activate(string kind, string name)
    {
        var source = FileOf(kind, name);
        if (!File.Exists(source)) throw new FileNotFoundException("Профиль не найден: " + name);

        var target = Target(kind);
        if (File.Exists(target))
            try { File.Copy(target, target + ".bak", overwrite: true); } catch { }
        File.Copy(source, target, overwrite: true);
        ConfigGenerator.Generate();
    }

    public static void Delete(string kind, string name)
    {
        var f = FileOf(kind, name);
        if (File.Exists(f)) File.Delete(f);
    }

    private static string Sanitize(string raw)
    {
        var bad = Path.GetInvalidFileNameChars();
        var s = new string((raw ?? "").Trim().Where(c => !bad.Contains(c)).ToArray()).Trim('.', ' ');
        return s.Length > 60 ? s[..60] : s;
    }

    // Line endings and trailing blanks differ between a pasted and a copied file.
    private static string Normalize(string s)
        => string.Join("\n", s.Replace("\r", "").Split('\n').Select(l => l.TrimEnd())).Trim();
}
