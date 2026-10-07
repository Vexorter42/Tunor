using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Tunor.Desktop.Services;

/// <summary>One program that can be given a rule.</summary>
/// <param name="Title">What to call it on screen: the app's name, or the file's.</param>
/// <param name="Names">
/// The process names a rule has to list. For a plain program that is one name; for an
/// app bundle it is every executable running inside it, because that is where the
/// traffic actually comes from — Chrome's requests are made by "Google Chrome Helper",
/// not by Chrome itself, so a rule naming only the app would never match a single one.
/// </param>
/// <param name="Path">Where it lives, for the line under the name.</param>
/// <param name="IsApp">An application the user installed, rather than a system process.</param>
public sealed record ProcessEntry(string Title, List<string> Names, string Path, bool IsApp)
{
    /// <summary>The extra executables beyond the first, for the detail line.</summary>
    public string Extra => Names.Count > 1 ? $"и ещё {Names.Count - 1} внутри" : "";
}

/// <summary>
/// The running programs, as the engine will see them.
///
/// A rule matches a process by the name of its executable, and typing that name by hand
/// is how rules end up never matching: on macOS a program is "Telegram", not
/// "Telegram.exe", and what makes Chrome's connections is not called Chrome at all.
/// So the names are read off the running system instead of being guessed.
///
/// .NET cannot help here: it refuses to read the executable path of a process belonging
/// to another user, and the window-related properties it would use to tell a program
/// from a daemon are Windows-only. ps knows all of it and is always present.
/// </summary>
public static class ProcessList
{
    public static Task<List<ProcessEntry>> CollectAsync() => Task.Run(Collect);

    private static List<ProcessEntry> Collect()
    {
        var lines = Run("ps", "-ax -o comm=");
        var own = OwnPath();

        // Everything running inside one .app belongs to that app, however deeply it is
        // nested: Chrome's helpers live in .../Google Chrome.app/Contents/Frameworks/…
        // and have .app bundles of their own, which are not programs in their own right.
        var byBundle = new Dictionary<string, (string Title, string Path, bool IsApp, List<string> Names)>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var raw in lines)
        {
            var path = raw.Trim();
            if (path.Length == 0 || path == own) continue;

            var exe = System.IO.Path.GetFileName(path);
            if (exe.Length == 0) continue;

            var bundle = Bundle(path);
            var key = bundle ?? path;
            var title = bundle != null
                ? System.IO.Path.GetFileNameWithoutExtension(bundle)
                : exe;

            if (!byBundle.TryGetValue(key, out var seen))
                seen = (title, bundle ?? path, bundle != null && !IsSystem(bundle), new List<string>());
            if (!seen.Names.Contains(exe, StringComparer.Ordinal)) seen.Names.Add(exe);
            byBundle[key] = seen;
        }

        return byBundle.Values
            .Select(v => new ProcessEntry(v.Title, Order(v.Names, v.Title), v.Path, v.IsApp))
            // What the user is looking for is almost always an app they installed.
            .OrderByDescending(e => e.IsApp)
            .ThenBy(e => e.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>The name matching the app itself first, so the list reads as the app's own.</summary>
    private static List<string> Order(List<string> names, string title) => names
        .OrderByDescending(n => string.Equals(n, title, StringComparison.OrdinalIgnoreCase))
        .ThenBy(n => n, StringComparer.CurrentCultureIgnoreCase)
        .ToList();

    /// <summary>
    /// The outermost .app in the path, or null when the program is not in a bundle at
    /// all. Outermost, because the bundles inside one belong to it.
    /// </summary>
    private static string? Bundle(string path)
    {
        var at = path.IndexOf(".app/", StringComparison.OrdinalIgnoreCase);
        return at < 0 ? null : path[..(at + 4)];
    }

    /// <summary>
    /// Part of macOS rather than something the user installed. Apple's own apps live
    /// under /System, and the helper bundles of frameworks and services are there too.
    /// </summary>
    private static bool IsSystem(string bundle) =>
        bundle.StartsWith("/System/", StringComparison.OrdinalIgnoreCase) ||
        bundle.StartsWith("/usr/", StringComparison.OrdinalIgnoreCase) ||
        bundle.StartsWith("/Library/Apple/", StringComparison.OrdinalIgnoreCase) ||
        bundle.Contains(".xpc/", StringComparison.OrdinalIgnoreCase) ||
        bundle.Contains(".systemextension/", StringComparison.OrdinalIgnoreCase) ||
        bundle.Contains(".appex/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The executables inside a bundle the user pointed at. Info.plist names the main
    /// one, but a bundle may hold several and the traffic can come from any of them, so
    /// the whole directory is taken.
    /// </summary>
    public static ProcessEntry? FromBundle(string bundlePath)
    {
        try
        {
            var dir = Path.Combine(bundlePath, "Contents", "MacOS");
            var names = Directory.Exists(dir)
                ? Directory.GetFiles(dir).Select(Path.GetFileName)
                    .Where(n => !string.IsNullOrEmpty(n)).Select(n => n!).ToList()
                : new List<string>();
            // Not a bundle: an ordinary executable file will do as itself.
            if (names.Count == 0)
            {
                if (!File.Exists(bundlePath)) return null;
                var one = Path.GetFileName(bundlePath);
                return new ProcessEntry(one, new List<string> { one }, bundlePath, true);
            }
            var title = Path.GetFileNameWithoutExtension(bundlePath);
            return new ProcessEntry(title, Order(names, title), bundlePath, true);
        }
        catch { return null; }
    }

    private static string OwnPath()
    {
        try { return Environment.ProcessPath ?? ""; }
        catch { return ""; }
    }

    private static List<string> Run(string file, string args)
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo(file, args)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (p == null) return new List<string>();
            var text = p.StandardOutput.ReadToEnd();
            p.WaitForExit(8000);
            return text.Split('\n', StringSplitOptions.RemoveEmptyEntries).ToList();
        }
        catch { return new List<string>(); }
    }
}
