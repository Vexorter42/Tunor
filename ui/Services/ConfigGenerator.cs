using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Nyx.Services;

/// <summary>
/// Builds build/config.json directly (replaces SSnetCli), in sing-box-lx format:
/// the WARP endpoint is a `wireguard` endpoint carrying AmneziaWG fields
/// (1.0 / 2.0 / 3.x), parsed from data/warp.conf. Routing is rebuilt from
/// data/rules.json, mirroring the previous SSnetCli output.
/// </summary>
public static class ConfigGenerator
{
    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        // Required: JsonNode.ToJsonString(options) throws without a resolver on .NET 9+.
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// One-time migration: older builds produced a Hiddify-style `"type": "awg"`
    /// endpoint, which the current sing-box engine cannot parse. If such a config
    /// (or none at all) is found, rebuild it from the user's own warp.conf /
    /// geo.conf / rules.json. A backup of the old file is kept.
    /// </summary>
    public static void EnsureCompatible()
    {
        try
        {
            if (!File.Exists(Paths.ConfigJson)) { Generate(); return; }

            // Rebuild once per app version. Generator fixes only reach a user when
            // config.json is regenerated, and nothing did that after an update — so a
            // fix shipped in 1.5.2 (placeholder tunnels left out) never arrived on
            // machines that updated but never pressed "Save & apply".
            var oldFormat = File.ReadAllText(Paths.ConfigJson).Contains("\"awg\"");
            var stamp = File.Exists(StampFile) ? File.ReadAllText(StampFile).Trim() : "";
            if (!oldFormat && stamp == GeneratorVersion) return;

            try { File.Copy(Paths.ConfigJson, Paths.ConfigJson + ".bak", overwrite: true); } catch { }
            Generate();
        }
        catch { /* never block startup on this */ }
    }

    /// <summary>
    /// Records which build wrote config.json. Kept beside it rather than inside it:
    /// sing-box rejects unknown fields, so a marker in the config would break it.
    /// </summary>
    private static string StampFile => Paths.ConfigJson + ".version";

    private static string GeneratorVersion =>
        typeof(ConfigGenerator).Assembly.GetName().Version?.ToString() ?? "0";

    /// <summary>True when warp.conf holds a usable tunnel. Re-read on each access.</summary>
    public static bool WarpConfigured => IsUsableConf(Paths.WarpConf);

    /// <summary>True when geo.conf holds a usable tunnel. geo detours through warp.</summary>
    public static bool GeoConfigured => WarpConfigured && IsUsableConf(Paths.GeoConf);

    public static void Generate()
    {
        var settings = SettingsService.Load();

        // An endpoint built from a placeholder .conf never comes up, and the engine then
        // repeats "WireGuard is not ready yet" forever. Leave such endpoints out entirely
        // and route around them instead.
        var warpOk = WarpConfigured;
        var geoOk = warpOk && IsUsableConf(Paths.GeoConf);

        var endpoints = new JsonArray();
        if (warpOk) endpoints.Add(BuildWireguard("warp-out", Paths.WarpConf, null));
        if (geoOk) endpoints.Add(BuildWireguard("geo-out", Paths.GeoConf, "warp-out"));

        var root = new JsonObject
        {
            ["log"] = new JsonObject
            {
                ["disabled"] = !settings.Logging,
                ["level"] = "warn",
            },
            ["dns"] = BuildDns(),
            ["inbounds"] = BuildInbounds(settings),
            ["outbounds"] = new JsonArray(new JsonObject { ["type"] = "direct", ["tag"] = "direct-out" }),
            ["endpoints"] = endpoints,
            ["route"] = BuildRoute(settings, warpOk, geoOk),
        };

        File.WriteAllText(Paths.ConfigJson, root.ToJsonString(Opts));
        try { File.WriteAllText(StampFile, GeneratorVersion); } catch { }
    }

    /// <summary>
    /// A .conf is usable only if it carries real key material and a reachable peer. The
    /// installer ships templates with random keys and Endpoint 127.0.0.1 so the app has
    /// something valid to parse — those must not become live endpoints.
    /// </summary>
    /// <summary>Marks every template shipped from 1.5.2 on.</summary>
    private const string PlaceholderMarker = "NYX-PLACEHOLDER";

    /// <summary>
    /// Older installers wrote a warp.conf template with no marker: real Cloudflare
    /// endpoint, real peer key, random private key — it passes every field check and
    /// becomes an endpoint that never handshakes. Its fingerprint is the zero-padded
    /// address below, which no real WARP account is ever given. The comment alone is not
    /// enough: someone may have pasted a real config under it without deleting it.
    /// (The old geo template is already caught by its 127.0.0.1 endpoint.)
    /// </summary>
    private const string LegacyWarpComment = "generate via @warp_generator_bot";
    private const string LegacyWarpAddress = "2606:4700:110:0000:0000:0000:0000:0001";

    private static bool IsUsableConf(string path)
    {
        if (!File.Exists(path)) return false;

        // The shipped warp.conf points at the real Cloudflare endpoint and carries a real
        // peer key — only the private key is random — so it is indistinguishable from a
        // working config by its fields alone. The templates carry an explicit marker.
        try
        {
            var text = File.ReadAllText(path);
            if (text.Contains(PlaceholderMarker, StringComparison.OrdinalIgnoreCase))
                return false;
            if (text.Contains(LegacyWarpComment, StringComparison.OrdinalIgnoreCase) &&
                text.Contains(LegacyWarpAddress, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        catch { return false; }

        var (iface, peer) = ParseConf(path);
        if (string.IsNullOrWhiteSpace(iface.GetValueOrDefault("PrivateKey"))) return false;
        if (string.IsNullOrWhiteSpace(peer.GetValueOrDefault("PublicKey"))) return false;

        var endpoint = peer.GetValueOrDefault("Endpoint", "");
        var idx = endpoint.LastIndexOf(':');
        if (idx <= 0) return false;

        var host = endpoint[..idx].Trim('[', ']');
        if (!int.TryParse(endpoint[(idx + 1)..], out var port) || port <= 0) return false;

        return host.Length > 0
            && host != "127.0.0.1" && host != "::1"
            && host != "0.0.0.0" && host != "localhost";
    }

    private static JsonObject BuildDns() => new()
    {
        ["servers"] = new JsonArray(
            new JsonObject { ["type"] = "udp", ["server"] = "1.1.1.1", ["server_port"] = 53, ["tag"] = "bootstrap-dns" },
            new JsonObject { ["type"] = "https", ["server"] = "1.1.1.1", ["server_port"] = 443, ["tag"] = "main-dns" }),
        ["rules"] = new JsonArray(
            new JsonObject { ["action"] = "reject", ["query_type"] = "HTTPS" },
            new JsonObject { ["action"] = "reject", ["domain_suffix"] = "use-application-dns.net" }),
        ["final"] = "main-dns",
    };

    private static JsonArray BuildInbounds(AppSettings s)
    {
        var arr = new JsonArray();
        if (s.Tun)
            arr.Add(new JsonObject
            {
                ["type"] = "tun", ["address"] = "172.18.0.1/30",
                ["auto_route"] = true, ["stack"] = "gvisor", ["tag"] = "main-in",
            });
        if (s.Proxy)
        {
            arr.Add(Mixed("proxy-in", 1080));
            arr.Add(Mixed("direct-in", 1081));
            arr.Add(Mixed("warp-in", 1082));
            arr.Add(Mixed("geo-in", 1083));
        }
        return arr;

        static JsonObject Mixed(string tag, int port) => new()
        {
            ["type"] = "mixed", ["tag"] = tag, ["listen"] = "0.0.0.0", ["listen_port"] = port,
        };
    }

    private static JsonObject BuildRoute(AppSettings s, bool warpOk, bool geoOk)
    {
        var groups = RulesService.Load();
        var ruleSet = new JsonArray();
        var warpTags = new List<string>();
        var geoTags = new List<string>();

        foreach (var g in groups)
        {
            if (string.IsNullOrWhiteSpace(g.Tag)) continue;
            if (g.IsRemote)
            {
                ruleSet.Add(new JsonObject
                {
                    ["type"] = "remote", ["tag"] = g.Tag, ["format"] = string.IsNullOrEmpty(g.Format) ? "binary" : g.Format,
                    ["url"] = g.Url, ["update_interval"] = string.IsNullOrEmpty(g.UpdateInterval) ? "1d" : g.UpdateInterval,
                });
            }
            else if (g.IsLocal)
            {
                // Rule-set files are downloaded, not bundled. Skip any that are not on
                // disk yet — the engine refuses to start if a local rule-set is missing.
                if (!LocalRuleSetExists(g.Path)) continue;

                ruleSet.Add(new JsonObject
                {
                    ["type"] = "local", ["path"] = g.Path,
                    ["format"] = string.IsNullOrEmpty(g.Format) ? "binary" : g.Format, ["tag"] = g.Tag,
                });
            }
            else // inline
            {
                var items = new JsonArray();
                foreach (var it in g.Items) items.Add(it);
                var ruleObj = new JsonObject();
                ruleObj[g.ItemKind == RuleItemKind.ProcessName ? "process_name" : "domain"] = items;
                ruleSet.Add(new JsonObject
                {
                    ["type"] = "inline", ["rules"] = new JsonArray(ruleObj), ["tag"] = g.Tag,
                });
            }

            if (g.Tag.StartsWith("geo-", StringComparison.OrdinalIgnoreCase)) geoTags.Add(g.Tag);
            else warpTags.Add(g.Tag);
        }

        // Never name an outbound that was not created — the engine refuses such a config.
        // Without a geo tunnel its traffic falls back to warp (tunnelled) rather than
        // direct (where it was blocked in the first place).
        var warpTarget = warpOk ? "warp-out" : "direct-out";
        var geoTarget = geoOk ? "geo-out" : warpTarget;

        var rules = new JsonArray
        {
            new JsonObject { ["action"] = "sniff" },
            new JsonObject { ["action"] = "hijack-dns", ["protocol"] = "dns" },
            new JsonObject { ["action"] = "route", ["outbound"] = "direct-out", ["ip_is_private"] = true },
            new JsonObject { ["action"] = "route", ["outbound"] = "direct-out", ["inbound"] = "direct-in" },
            new JsonObject { ["action"] = "route", ["outbound"] = warpTarget, ["inbound"] = "warp-in" },
            new JsonObject { ["action"] = "route", ["outbound"] = geoTarget, ["inbound"] = "geo-in" },
        };
        if (warpTags.Count > 0)
            rules.Add(new JsonObject { ["action"] = "route", ["outbound"] = warpTarget, ["rule_set"] = ToArray(warpTags) });
        if (geoTags.Count > 0)
            rules.Add(new JsonObject { ["action"] = "route", ["outbound"] = geoTarget, ["rule_set"] = ToArray(geoTags) });

        return new JsonObject
        {
            ["rules"] = rules,
            ["rule_set"] = ruleSet,
            ["final"] = s.Final == "proxy" ? warpTarget : "direct-out",
            ["auto_detect_interface"] = true,
            ["default_domain_resolver"] = "main-dns",
        };

        static JsonArray ToArray(IEnumerable<string> xs)
        {
            var a = new JsonArray();
            foreach (var x in xs) a.Add(x);
            return a;
        }
    }

    /// <summary>Rule-set paths are relative to the engine's working directory (build/).</summary>
    private static bool LocalRuleSetExists(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return false;
        try
        {
            var full = Path.GetFullPath(Path.Combine(Paths.BuildDir, relativePath));
            return File.Exists(full);
        }
        catch { return false; }
    }

    // --- .conf parsing -> wireguard endpoint (with AmneziaWG fields) ---

    private static JsonObject BuildWireguard(string tag, string confPath, string? detour)
    {
        var (iface, peer) = ParseConf(confPath);
        var ep = new JsonObject { ["type"] = "wireguard", ["tag"] = tag };

        if (iface.TryGetValue("MTU", out var mtu) && int.TryParse(mtu, out var mtuI)) ep["mtu"] = mtuI;

        var addrs = new JsonArray();
        foreach (var a in Split(iface.GetValueOrDefault("Address", "")))
        {
            var v = a.Contains('/') ? a : (a.Contains(':') ? a + "/128" : a + "/32");
            addrs.Add(v);
        }
        ep["address"] = addrs;
        ep["private_key"] = iface.GetValueOrDefault("PrivateKey", "");

        // AmneziaWG 1.0 / 2.0
        foreach (var k in new[] { "Jc", "Jmin", "Jmax", "S1", "S2", "S3", "S4" })
            if (iface.TryGetValue(k, out var v) && int.TryParse(v, out var iv)) ep[k.ToLowerInvariant()] = iv;
        foreach (var k in new[] { "H1", "H2", "H3", "H4" })
            if (iface.TryGetValue(k, out var v)) ep[k.ToLowerInvariant()] = v; // strings
        foreach (var k in new[] { "I1", "I2", "I3", "I4", "I5" })
            if (iface.TryGetValue(k, out var v)) ep[k.ToLowerInvariant()] = v;

        // AmneziaWG 3.x
        var map = new (string conf, string json)[]
        {
            ("ContentPaddingAddition", "content_padding_addition"),
            ("RekeyAfterTime", "rekey_after_time"),
            ("RekeyTimeout", "rekey_timeout"),
            ("RejectAfterTime", "reject_after_time"),
            ("KeepaliveTimeout", "keepalive_timeout"),
            ("MaxHandshakeAttempts", "max_handshake_attempts"),
        };
        foreach (var (c, j) in map)
            if (iface.TryGetValue(c, out var v)) ep[j] = v; // ranged "min-max" strings
        if (iface.TryGetValue("HeaderProtectionKey", out var hpk)) ep["header_protection_key"] = hpk;
        if (string.Equals(iface.GetValueOrDefault("RandomTrailers"), "on", StringComparison.OrdinalIgnoreCase))
            ep["random_trailers"] = true;
        if (string.Equals(iface.GetValueOrDefault("DisableCookies"), "on", StringComparison.OrdinalIgnoreCase))
            ep["disable_cookies"] = true;

        var endpoint = peer.GetValueOrDefault("Endpoint", "");
        var idx = endpoint.LastIndexOf(':');
        var host = idx > 0 ? endpoint[..idx] : endpoint;
        var portStr = idx > 0 ? endpoint[(idx + 1)..] : "0";
        int.TryParse(portStr, out var port);

        var allowed = new JsonArray();
        foreach (var a in Split(peer.GetValueOrDefault("AllowedIPs", "0.0.0.0/0, ::/0"))) allowed.Add(a);

        var p = new JsonObject
        {
            ["address"] = host, ["port"] = port,
            ["public_key"] = peer.GetValueOrDefault("PublicKey", ""),
            ["allowed_ips"] = allowed,
        };
        if (peer.TryGetValue("PresharedKey", out var psk)) p["pre_shared_key"] = psk;
        ep["peers"] = new JsonArray(p);

        if (detour != null) ep["detour"] = detour;
        return ep;
    }

    private static (Dictionary<string, string> iface, Dictionary<string, string> peer) ParseConf(string path)
    {
        var iface = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var peer = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return (iface, peer);

        var section = "";
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("#")) continue;
            if (line.StartsWith("["))
            {
                section = line.Trim('[', ']').ToLowerInvariant();
                continue;
            }
            var eq = line.IndexOf('=');
            if (eq < 0) continue;
            var key = line[..eq].Trim();
            var val = line[(eq + 1)..].Trim();
            (section == "interface" ? iface : peer)[key] = val;
        }
        return (iface, peer);
    }

    private static IEnumerable<string> Split(string s)
        => s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
