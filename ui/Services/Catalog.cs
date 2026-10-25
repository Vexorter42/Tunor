using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Tunor.Services;

public sealed record CatalogEntry(string Id, string Title, string About, string Category, string Url, string Route);

/// <summary>What came of adding a list.</summary>
public sealed record CatalogAdded(CatalogEntry Entry, string Tag, bool Downloaded, string Note);

/// <summary>
/// The catalog of ready rule lists: what each one is for, one tick to add it to the rules.
///
/// It is a file, <c>catalog/catalog.json</c> in the repository: a copy of it is inside the program, so the
/// catalog is there offline and from the first start, and the program asks the repository for a newer one when
/// the window is opened — a list can be added to the catalog without a new version of the program. What was
/// fetched last is kept, so the next start without a network shows that and not only the built-in one.
///
/// A catalog is a file from the internet, and what it adds decides where traffic goes. So it is read
/// strictly: an entry with an odd name, an address that is not a rule list on GitHub, or a size that no
/// real entry has is dropped, not tried; and nothing is added without a person ticking it.
/// </summary>
public static class Catalog
{
    private static readonly Regex Id = new(@"^[a-z0-9][a-z0-9_-]{0,39}$", RegexOptions.Compiled);
    /// <summary>The name a list is saved under comes from its address: plain letters, digits and a few marks, nothing encoded.</summary>
    private static readonly Regex FileName = new(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,99}\.srs$", RegexOptions.Compiled);
    private static readonly string[] Hosts = { "github.com", "raw.githubusercontent.com" };
    private const int MaxEntries = 300;
    private const int MaxBytes = 256 * 1024;

    public static string RemoteUrl => $"https://raw.githubusercontent.com/{UpdateConfig.Load().Repo}/main/catalog/catalog.json";

    // ------------------------------------------------------------------ reading

    /// <summary>The entries of a catalog file that pass the checks; null if there are none, or it is not a catalog.</summary>
    public static List<CatalogEntry>? Parse(string json, out int dropped)
    {
        dropped = 0;
        try
        {
            if (json.Length > MaxBytes) return null;
            if (JsonNode.Parse(json) is not JsonObject root || root["format"]?.GetValue<int>() != 1) return null;
            if (root["lists"] is not JsonArray lists) return null;

            var result = new List<CatalogEntry>();
            var seen = new HashSet<string>();
            foreach (var item in lists.Take(MaxEntries + 1))
            {
                var e = Entry(item as JsonObject);
                if (e == null || result.Count >= MaxEntries || !seen.Add(e.Id) || !seen.Add("url:" + e.Url.ToLowerInvariant())) { dropped++; continue; }
                result.Add(e);
            }
            return result.Count == 0 ? null : result;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) { return null; }
    }

    private static CatalogEntry? Entry(JsonObject? o)
    {
        if (o == null) return null;
        string Get(string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : "";

        var id = Get("id"); var title = Get("title"); var about = Get("about");
        var category = Get("category"); var url = Get("url"); var route = Get("route");
        if (route.Length == 0) route = "warp";

        if (!Id.IsMatch(id) || title.Length is 0 or > 60 || about.Length > 200 || category.Length is 0 or > 40) return null;
        if (route is not ("warp" or "geo")) return null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps || !Hosts.Contains(uri.Host)) return null;
        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.UserInfo) || !uri.AbsolutePath.EndsWith(".srs", StringComparison.OrdinalIgnoreCase)) return null;
        if (!FileName.IsMatch(uri.AbsolutePath.Split('/').Last())) return null;
        return new CatalogEntry(id, title, about, category, url, route);
    }

    /// <summary>The catalog that came with this version of the program.</summary>
    public static List<CatalogEntry> BuiltIn()
    {
        var asm = typeof(Catalog).Assembly;
        var name = asm.GetManifestResourceNames().FirstOrDefault(n => n.EndsWith("catalog.json", StringComparison.OrdinalIgnoreCase))
                   ?? throw new InvalidOperationException("в программе нет встроенного каталога");
        using var s = asm.GetManifestResourceStream(name)!;
        using var r = new StreamReader(s);
        return Parse(r.ReadToEnd(), out _) ?? throw new InvalidOperationException("встроенный каталог не читается");
    }

    // ------------------------------------------------------------------ getting it

    internal static Func<Task<string>> FetchRemote { get; set; } =
        () => GitHubFetch.GetStringAsync(RemoteUrl, UpdateConfig.Load().Mirror);
    internal static Func<string> CacheFile { get; set; } = () => Path.Combine(Paths.DataDir, "catalog.json");

    /// <summary>Where the entries on the screen came from, in words.</summary>
    public enum From { Repository, Saved, BuiltIn }

    /// <summary>
    /// The newest catalog there is: the repository's if it can be reached and is valid, else the one saved
    /// from last time, else the built-in. Never throws — a catalog window must open without a network.
    /// </summary>
    public static async Task<(List<CatalogEntry> Entries, From Source)> LoadAsync(bool askRepository)
    {
        if (askRepository)
        {
            try
            {
                var text = await FetchRemote();
                if (Parse(text, out _) is { } fresh)
                {
                    try
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(CacheFile())!);
                        File.WriteAllText(CacheFile(), text);
                    }
                    catch { /* it only saves a request next time */ }
                    return (fresh, From.Repository);
                }
            }
            catch { /* offline, or GitHub and every mirror are down: what is on the disk will do */ }
        }

        try
        {
            if (File.Exists(CacheFile()) && Parse(File.ReadAllText(CacheFile()), out _) is { } saved)
                return (saved, From.Saved);
        }
        catch { }
        return (BuiltIn(), From.BuiltIn);
    }

    // ------------------------------------------------------------------ adding

    /// <summary>Whether the rules already have this list, under whatever name.</summary>
    public static bool IsAdded(CatalogEntry e, IEnumerable<RuleGroup> groups)
        => groups.Any(g => Same(g.SourceUrl, e.Url) || Same(g.Url, e.Url));

    private static bool Same(string a, string b) => a.Length > 0 && string.Equals(a.Trim(), b, StringComparison.OrdinalIgnoreCase);

    /// <summary>"warp-youtube", or "warp-youtube-2" if some other group already has that name.</summary>
    internal static string TagFor(CatalogEntry e, string route, IEnumerable<string> taken)
    {
        var used = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);
        var tag = route + "-" + e.Id;
        for (var i = 2; used.Contains(tag); i++) tag = $"{route}-{e.Id}-{i}";
        return tag;
    }

    /// <summary>
    /// Adds the picked lists to <paramref name="groups"/>, skipping those it already has. <paramref name="route"/> is
    /// "warp" or "geo" for all of them, or null for each as the catalog suggests. Returns the groups it added.
    /// </summary>
    public static List<(CatalogEntry Entry, RuleGroup Group)> Plan(IEnumerable<CatalogEntry> picks, string? route, IList<RuleGroup> groups)
    {
        var added = new List<(CatalogEntry, RuleGroup)>();
        foreach (var e in picks)
        {
            if (IsAdded(e, groups)) continue;
            var way = route is "warp" or "geo" ? route : e.Route;
            var file = new Uri(e.Url).AbsolutePath.Split('/').Last();
            var g = new RuleGroup
            {
                Tag = TagFor(e, way, groups.Select(x => x.Tag)),
                Type = "local",
                Format = "binary",
                SourceUrl = e.Url,
                Path = "../data/rulesets/" + file,
            };
            groups.Add(g);
            added.Add((e, g));
        }
        return added;
    }

    internal static Func<List<RuleGroup>> LoadGroups { get; set; } = RulesService.Load;
    internal static Action<List<RuleGroup>> SaveGroups { get; set; } = RulesService.Save;
    internal static Func<IEnumerable<RuleGroup>, Task<List<RulesetDownloadResult>>> Download { get; set; } =
        g => RulesetDownloader.DownloadAllAsync(g, UpdateConfig.Load().Mirror);
    internal static Action Rebuild { get; set; } = () => ConfigGenerator.Generate();

    /// <summary>
    /// Puts the lists into the rules on disk: added, downloaded (only the new ones — the others are not touched),
    /// saved, and the config rebuilt. A list that could not be downloaded stays in the rules and is fetched
    /// by "Обновить списки" later, so a bad network does not lose the choice.
    /// </summary>
    public static async Task<List<CatalogAdded>> AddAsync(IEnumerable<CatalogEntry> picks, string? route)
    {
        var groups = LoadGroups();
        var plan = Plan(picks, route, groups);
        if (plan.Count == 0) return new List<CatalogAdded>();

        var results = await Download(plan.Select(p => p.Group).ToList());
        SaveGroups(groups);
        Rebuild();

        return plan.Select(p =>
        {
            var r = results.FirstOrDefault(x => x.Tag == p.Group.Tag);
            return new CatalogAdded(p.Entry, p.Group.Tag, r?.Ok == true, r?.Ok == true ? "" : r?.Message ?? "не скачался");
        }).ToList();
    }
}
