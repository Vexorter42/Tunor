using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tunor.Services;

/// <summary>
/// One tunnel the user can send traffic into. WARP and geo were the only two for a long
/// time and were written into the code by name; they are now the first two entries of a
/// list the user can add to.
///
/// The id is the whole identity: rule groups are tagged "&lt;id&gt;-...", the engine gets an
/// outbound named "&lt;id&gt;-out" and a local door named "&lt;id&gt;-in". Keeping "warp" and "geo"
/// as the ids of the first two means every rule written before this change still points
/// where it did, with nothing to migrate.
/// </summary>
public class Tunnel
{
    /// <summary>Lower-case slug: the tag prefix, the outbound name and the door name.</summary>
    [JsonPropertyName("id")] public string Id { get; set; } = "";

    /// <summary>What the user sees. Free text.</summary>
    [JsonPropertyName("title")] public string Title { get; set; } = "";

    /// <summary>"wireguard" (a .conf file) or a proxy protocol such as "vless" (a link).</summary>
    [JsonPropertyName("kind")] public string Kind { get; set; } = Kinds.Wireguard;

    /// <summary>For a wireguard tunnel: the .conf path, relative to the app root.</summary>
    [JsonPropertyName("file")] public string File { get; set; } = "";

    /// <summary>For a proxy tunnel: the link the user pasted, parsed on each build.</summary>
    [JsonPropertyName("url")] public string Url { get; set; } = "";

    /// <summary>
    /// Id of the tunnel this one is reached through, or empty for straight out. geo has
    /// always gone through warp; a user's own VPN can do the same, which is what keeps it
    /// reachable when its address is blocked.
    /// </summary>
    [JsonPropertyName("detour")] public string Detour { get; set; } = "";

    /// <summary>Off means: keep the entry and its rules, but route as if it were absent.</summary>
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;

    [JsonIgnore] public bool IsWireguard => Kind.Equals(Kinds.Wireguard, StringComparison.OrdinalIgnoreCase);

    /// <summary>The engine's name for this tunnel's outbound.</summary>
    [JsonIgnore] public string OutboundTag => Id + "-out";

    /// <summary>The engine's name for the local door that always enters this tunnel.</summary>
    [JsonIgnore] public string InboundTag => Id + "-in";

    public static class Kinds
    {
        public const string Wireguard = "wireguard";
        public const string Vless = "vless";
    }
}

public static class TunnelService
{
    /// <summary>The two built-in ids. They are ordinary entries; only their ids are fixed,
    /// because rules written before tunnels were a list carry these prefixes.</summary>
    public const string Warp = "warp";
    public const string Geo = "geo";

    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string TunnelsJson => Path.Combine(Paths.DataDir, "tunnels.json");

    /// <summary>
    /// The tunnels, in the order they are offered. A missing or unreadable file gives the
    /// two that were hard-coded before, so an install that predates this list behaves
    /// exactly as it did.
    /// </summary>
    public static List<Tunnel> Load()
    {
        try
        {
            if (System.IO.File.Exists(TunnelsJson))
            {
                var list = JsonSerializer.Deserialize<List<Tunnel>>(
                    System.IO.File.ReadAllText(TunnelsJson), Opts);
                if (list is { Count: > 0 })
                    return Repair(list);
            }
        }
        catch { /* a corrupt list must not stop the app: fall through to the defaults */ }
        return Defaults();
    }

    public static void Save(IEnumerable<Tunnel> tunnels)
    {
        Directory.CreateDirectory(Paths.DataDir);
        System.IO.File.WriteAllText(TunnelsJson,
            JsonSerializer.Serialize(Repair(tunnels.ToList()), Opts));
    }

    /// <summary>What WARP and geo were before this list existed.</summary>
    public static List<Tunnel> Defaults() => new()
    {
        new Tunnel { Id = Warp, Title = "WARP", Kind = Tunnel.Kinds.Wireguard, File = "data/warp.conf" },
        new Tunnel { Id = Geo, Title = "geo", Kind = Tunnel.Kinds.Wireguard, File = "data/geo.conf", Detour = Warp },
    };

    /// <summary>
    /// Makes a loaded list safe to build a config from: ids present, unique and in slug
    /// form, the two built-ins present, and no detour that points at a missing tunnel or
    /// round a loop — the engine would refuse such a config, and one bad entry would take
    /// every tunnel down with it.
    /// </summary>
    private static List<Tunnel> Repair(List<Tunnel> list)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ok = new List<Tunnel>();
        foreach (var t in list)
        {
            t.Id = Slug(t.Id);
            if (t.Id.Length == 0 || !seen.Add(t.Id)) continue;
            if (t.Title.Length == 0) t.Title = t.Id;
            ok.Add(t);
        }
        foreach (var id in new[] { Warp, Geo })
            if (!seen.Contains(id))
                ok.Insert(Math.Min(ok.Count, id == Warp ? 0 : 1), Defaults().First(d => d.Id == id));

        var byId = ok.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
        foreach (var t in ok)
        {
            t.Detour = Slug(t.Detour);
            if (t.Detour.Length == 0) continue;
            if (!byId.ContainsKey(t.Detour) || Loops(t, byId)) t.Detour = "";
        }
        return ok;
    }

    private static bool Loops(Tunnel start, Dictionary<string, Tunnel> byId)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { start.Id };
        var at = start;
        while (at.Detour.Length > 0 && byId.TryGetValue(at.Detour, out var next))
        {
            if (!seen.Add(next.Id)) return true;
            at = next;
        }
        return false;
    }

    /// <summary>A free-typed name as an id: lower case, letters, digits and dashes.</summary>
    public static string Slug(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        var chars = s.Trim().ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) && c < 128 ? c : '-')
            .ToArray();
        return string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>An id not yet taken, built from what the user called the tunnel.</summary>
    public static string FreeId(string wanted, IEnumerable<Tunnel> existing)
    {
        var taken = new HashSet<string>(existing.Select(t => t.Id), StringComparer.OrdinalIgnoreCase);
        var baseId = Slug(wanted);
        if (baseId.Length == 0) baseId = "vpn";
        // "direct" and "main" are the engine's own names in the generated config.
        if (baseId is "direct" or "main") baseId += "-vpn";
        if (!taken.Contains(baseId)) return baseId;
        for (var n = 2; ; n++)
            if (!taken.Contains($"{baseId}-{n}")) return $"{baseId}-{n}";
    }

    /// <summary>
    /// Which tunnel a rule group belongs to, by its tag prefix: "geo-custom-domains" is
    /// geo's. Longest id first, so an id that starts with another id still resolves to
    /// itself. Null when the tag names no tunnel.
    /// </summary>
    public static string? SlotOf(string tag, IEnumerable<Tunnel> tunnels)
    {
        foreach (var t in tunnels.OrderByDescending(t => t.Id.Length))
            if (tag.StartsWith(t.Id + "-", StringComparison.OrdinalIgnoreCase))
                return t.Id;
        return null;
    }
}
