using System;
using System.Collections.Generic;
using System.Linq;

namespace Nyx.Services;

/// <summary>
/// Edits rules.json on the user's behalf: "send this program to WARP", "send this
/// domain to geo". Writes into the custom groups the user already has when it can find
/// them, and creates them otherwise. Every change rebuilds config.json.
/// </summary>
public static class RouteEditor
{
    public const string Warp = "warp";
    public const string Geo = "geo";

    /// <summary>Where a process rule currently sends this program: warp, geo or null.</summary>
    public static string? ProcessRoute(string exe) => ProcessRoute(exe, RulesService.Load());

    /// <summary>Same, against rules already loaded — for checking many programs at once.</summary>
    public static string? ProcessRoute(string exe, IEnumerable<RuleGroup> groups)
    {
        // Same precedence as the generated config: WARP app rules are checked first.
        string? found = null;
        foreach (var g in groups.Where(IsProcessGroup))
        {
            if (!g.Items.Any(i => ConfigGenerator.ProcessMatches(i, exe))) continue;
            if (!ConfigGenerator.IsGeoTag(g.Tag)) return Warp;
            found ??= Geo;
        }
        return found;
    }

    /// <summary>
    /// Sends all of a program's traffic to WARP or geo, or (null) removes its app rule
    /// so the domain lists decide again. The program is taken out of every other process
    /// group first, so it never ends up in two places — which also clears duplicates
    /// that differ only in case ("telegram.exe" next to "Telegram.exe").
    /// </summary>
    public static void SetProcessRoute(string exe, string? route)
    {
        var name = System.IO.Path.GetFileName(exe.Trim());
        if (name.Length == 0) return;

        var groups = RulesService.Load();
        foreach (var g in groups.Where(IsProcessGroup))
            foreach (var item in g.Items.Where(i => ConfigGenerator.ProcessMatches(i, name)).ToList())
                g.Items.Remove(item);

        if (route != null)
            Target(groups, route, RuleItemKind.ProcessName, "processes").Items.Add(name);

        RulesService.Save(groups);
        ConfigGenerator.Generate();
    }

    /// <summary>Adds a domain to the WARP or geo custom list (once).</summary>
    public static void AddDomain(string domain, string route)
    {
        var d = ConfigGenerator.DomainEntry(domain);
        if (d.Length == 0) return;

        var groups = RulesService.Load();
        // Leave the other side's custom list, or the WARP one would always win. Compared
        // in normalised form, so "*.site.com" and "site.com" count as the same entry.
        foreach (var g in groups.Where(g => g.IsInline && g.ItemKind == RuleItemKind.Domain))
            foreach (var item in g.Items.Where(i => ConfigGenerator.DomainEntry(i) == d).ToList())
                g.Items.Remove(item);

        Target(groups, route, RuleItemKind.Domain, "domains").Items.Add(d);
        RulesService.Save(groups);
        ConfigGenerator.Generate();
    }

    private static bool IsProcessGroup(RuleGroup g)
        => g.IsInline && g.ItemKind == RuleItemKind.ProcessName;

    /// <summary>
    /// The group to write into: "&lt;side&gt;-custom-&lt;kind&gt;" if it exists, then the shipped
    /// "&lt;side&gt;-custom-sample" for domains, then any inline group of that side and kind,
    /// else a new "&lt;side&gt;-custom-&lt;kind&gt;".
    /// </summary>
    private static RuleGroup Target(List<RuleGroup> groups, string route, RuleItemKind kind, string suffix)
    {
        var side = route == Geo ? "geo-" : "warp-";
        bool Fits(RuleGroup g) => g.IsInline && g.ItemKind == kind
                                  && g.Tag.StartsWith(side, StringComparison.OrdinalIgnoreCase);

        var preferred = new List<string> { side + "custom-" + suffix };
        if (kind == RuleItemKind.Domain) preferred.Add(side + "custom-sample");

        foreach (var tag in preferred)
        {
            var hit = groups.FirstOrDefault(g => Fits(g) && string.Equals(g.Tag, tag, StringComparison.OrdinalIgnoreCase));
            if (hit != null) return hit;
        }
        var any = groups.FirstOrDefault(Fits);
        if (any != null) return any;

        var created = new RuleGroup { Tag = side + "custom-" + suffix, Type = "inline", ItemKind = kind };
        groups.Add(created);
        return created;
    }
}
