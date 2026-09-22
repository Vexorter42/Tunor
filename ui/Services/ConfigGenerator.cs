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
    /// Candidate ports for the stats API. Deliberately not 9090, the usual Clash port:
    /// a bind failure there would stop the whole engine from starting, and people who
    /// need Nyx have often tried Clash first.
    /// </summary>
    private static readonly int[] ControllerPorts = { 29090, 29091, 29092, 29093, 29094, 29095 };

    /// <summary>
    /// Makes sure the stats API has a port and a secret, persisting both. The port is
    /// probed only when none is stored yet, so it stays stable while the engine is
    /// running (and holding it). Returns false when no candidate port is free.
    /// </summary>
    private static bool EnsureController(AppSettings s)
    {
        var changed = false;
        if (string.IsNullOrEmpty(s.ControllerSecret))
        {
            s.ControllerSecret = Convert.ToHexString(
                System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
            changed = true;
        }
        if (s.ControllerPort == 0)
        {
            var free = ControllerPorts.FirstOrDefault(IsPortFree);
            if (free == 0) return false;   // leave the API out rather than risk the engine
            s.ControllerPort = free;
            changed = true;
        }
        if (changed)
            try { SettingsService.Save(s); } catch { }
        return true;
    }

    /// <summary>Forgets the stats port so the next build probes for a free one.</summary>
    public static void ResetControllerPort()
    {
        try
        {
            var s = SettingsService.Load();
            s.ControllerPort = 0;
            SettingsService.Save(s);
        }
        catch { }
    }

    private static bool IsPortFree(int port)
    {
        try
        {
            var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, port);
            l.Start();
            l.Stop();
            return true;
        }
        catch { return false; }
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

        // Stats API for the Connections page. Windows only for now: the macOS engine
        // build has not been checked for it, and a missing feature would stop it starting.
        if (OperatingSystem.IsWindows() && EnsureController(settings))
        {
            root["experimental"] = new JsonObject
            {
                ["clash_api"] = new JsonObject
                {
                    // Loopback only, with a secret: nothing outside this machine can
                    // reach it, and nothing on it can without the token.
                    ["external_controller"] = $"127.0.0.1:{settings.ControllerPort}",
                    ["secret"] = settings.ControllerSecret,
                },
            };
        }

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

    private static bool IsUsableConf(string path) => Inspect(path).Usable;

    /// <summary>What state a tunnel file is in.</summary>
    /// <param name="Placeholder">Not filled in yet — the normal state before setup.</param>
    /// <param name="Problem">Filled in, but broken, in plain words; null otherwise.</param>
    public sealed record ConfState(bool Usable, bool Placeholder, string? Problem);

    public static ConfState WarpState => Inspect(Paths.WarpConf);
    public static ConfState GeoState => Inspect(Paths.GeoConf);

    /// <summary>
    /// Decides whether a tunnel file may become an endpoint. Everything the engine would
    /// reject is caught here instead: one bad endpoint fails the whole config, so a
    /// broken geo.conf used to take WARP down with it. A broken tunnel is left out and
    /// the reason is shown; the rest keeps working.
    /// </summary>
    public static ConfState Inspect(string path)
    {
        var placeholder = new ConfState(false, true, null);
        ConfState Broken(string why) => new(false, false, why);

        if (!File.Exists(path)) return placeholder;

        // The shipped warp.conf points at the real Cloudflare endpoint and carries a real
        // peer key — only the private key is random — so it is indistinguishable from a
        // working config by its fields alone. The templates carry an explicit marker.
        try
        {
            var text = File.ReadAllText(path);
            if (text.Contains(PlaceholderMarker, StringComparison.OrdinalIgnoreCase)) return placeholder;
            if (text.Contains(LegacyWarpComment, StringComparison.OrdinalIgnoreCase) &&
                text.Contains(LegacyWarpAddress, StringComparison.OrdinalIgnoreCase)) return placeholder;
            if (text.Trim().Length == 0) return placeholder;
        }
        catch (Exception ex) { return Broken("файл не читается: " + ex.Message); }

        var (iface, peer) = ParseConf(path);
        if (iface.Count == 0 && peer.Count == 0) return Broken("это не похоже на конфиг WireGuard");

        var problem = KeyProblem(iface.GetValueOrDefault("PrivateKey"), "PrivateKey")
                   ?? KeyProblem(peer.GetValueOrDefault("PublicKey"), "PublicKey (в [Peer])")
                   ?? (peer.TryGetValue("PresharedKey", out var psk) ? KeyProblem(psk, "PresharedKey") : null);
        if (problem != null) return Broken(problem);

        var endpoint = peer.GetValueOrDefault("Endpoint", "");
        var idx = endpoint.LastIndexOf(':');
        if (idx <= 0) return Broken("нет адреса сервера (Endpoint в [Peer])");

        var host = endpoint[..idx].Trim('[', ']');
        if (!int.TryParse(endpoint[(idx + 1)..], out var port) || port <= 0 || port > 65535)
            return Broken($"в Endpoint неправильный порт: «{endpoint[(idx + 1)..]}»");

        // A loopback endpoint is what the old geo template carried.
        if (host.Length == 0 || host is "127.0.0.1" or "::1" or "0.0.0.0" or "localhost")
            return placeholder;

        return new ConfState(true, false, null);
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
        // Process groups get rules of their own, ahead of the domain lists: "send this
        // app to geo" must win even when one of its domains sits in a WARP list.
        var warpProcTags = new List<string>();
        var geoProcTags = new List<string>();

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
                var isProcess = g.ItemKind == RuleItemKind.ProcessName;
                var items = new JsonArray();
                foreach (var it in g.Items)
                {
                    if (string.IsNullOrWhiteSpace(it)) continue;
                    items.Add(isProcess ? ProcessPattern(it) : it.Trim());
                }
                if (items.Count == 0) continue;   // an empty rule would match nothing anyway

                var ruleObj = new JsonObject();
                // process_name is case-sensitive: "telegram.exe" silently never matched a
                // "Telegram.exe" on disk (checked against the engine). A case-insensitive
                // regex on the image path fixes that for rules already written by hand.
                ruleObj[isProcess ? "process_path_regex" : "domain"] = items;
                ruleSet.Add(new JsonObject
                {
                    ["type"] = "inline", ["rules"] = new JsonArray(ruleObj), ["tag"] = g.Tag,
                });

                if (isProcess)
                {
                    (IsGeoTag(g.Tag) ? geoProcTags : warpProcTags).Add(g.Tag);
                    continue;
                }
            }

            (IsGeoTag(g.Tag) ? geoTags : warpTags).Add(g.Tag);
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
        // Order is precedence: apps first (an explicit "this program goes there" beats a
        // domain list), then WARP lists before geo lists, as before.
        void Route(List<string> tags, string outbound)
        {
            if (tags.Count > 0)
                rules.Add(new JsonObject { ["action"] = "route", ["outbound"] = outbound, ["rule_set"] = ToArray(tags) });
        }
        Route(warpProcTags, warpTarget);
        Route(geoProcTags, geoTarget);
        Route(warpTags, warpTarget);
        Route(geoTags, geoTarget);

        return new JsonObject
        {
            ["rules"] = rules,
            ["rule_set"] = ruleSet,
            // geoTarget/warpTarget already fall back (geo → warp → direct) when a tunnel
            // is not configured, so an unset geo never strands the default route.
            ["final"] = s.Final switch
            {
                "geo" => geoTarget,
                "proxy" => warpTarget,
                _ => "direct-out",
            },
            ["auto_detect_interface"] = true,
            ["default_domain_resolver"] = "main-dns",
            // Tag every connection with its process, not just those a process rule looks
            // at: the Apps page lists what each program connects to.
            ["find_process"] = OperatingSystem.IsWindows(),
        };

        static JsonArray ToArray(IEnumerable<string> xs)
        {
            var a = new JsonArray();
            foreach (var x in xs) a.Add(x);
            return a;
        }
    }

    public static bool IsGeoTag(string tag) => tag.StartsWith("geo-", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Rule-list entry → case-insensitive regex on the process image path.
    /// "Discord.exe" → <c>(?i)(?:^|[\\/])Discord\.exe$</c>; a bare "discord" also accepts
    /// the ".exe" people leave off; a pasted full path still means just the file. The
    /// separator anchor keeps "curl.exe" from matching "xcurl.exe".
    /// </summary>
    public static string ProcessPattern(string item)
    {
        var name = Path.GetFileName(item.Trim().Trim('"'));
        var hasExe = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
        var stem = hasExe ? name[..^4] : name;
        return @"(?i)(?:^|[\\/])" + Re2Escape(stem) + (hasExe ? @"\.exe" : @"(?:\.exe)?") + "$";
    }

    /// <summary>
    /// What <see cref="ProcessPattern"/> will match, in plain C#: does a rule-list entry
    /// cover this executable? Kept next to it so the two cannot drift apart.
    /// </summary>
    public static bool ProcessMatches(string item, string exe)
    {
        var name = Path.GetFileName(item.Trim().Trim('"'));
        var file = Path.GetFileName(exe.Trim().Trim('"'));
        if (name.Length == 0 || file.Length == 0) return false;
        return string.Equals(name, file, StringComparison.OrdinalIgnoreCase)
            || (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                && string.Equals(name + ".exe", file, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Escapes exactly RE2's metacharacters — the regex dialect the engine (Go) runs.
    /// Regex.Escape targets .NET's own dialect; its output happens to be accepted by RE2
    /// today, but the rule text is for the engine, so it is escaped for the engine.
    /// </summary>
    private static string Re2Escape(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length + 8);
        foreach (var c in s)
        {
            if (@"\.+*?()|[]{}^$".IndexOf(c) >= 0) sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
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

    /// <summary>
    /// Reads a WireGuard .conf into its [Interface] and [Peer] values. The one parser
    /// for the whole app — the importer uses it too.
    ///
    /// Configs are usually copied out of Telegram or a web page, and arrive with
    /// invisible characters (zero-width spaces, a BOM, soft hyphens) or with the key in
    /// quotes. The engine rejects all of those with "illegal base64 data at input byte 0"
    /// and, since one bad endpoint fails the whole config, takes WARP down with it. So
    /// invisible characters are dropped, non-breaking spaces become spaces, and a value
    /// wrapped in quotes is unwrapped.
    /// </summary>
    public static (Dictionary<string, string> iface, Dictionary<string, string> peer) ParseConf(string path)
    {
        var iface = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var peer = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return (iface, peer);

        var section = "";
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = CleanLine(raw);
            if (line.Length == 0 || line.StartsWith("#")) continue;
            if (line.StartsWith("["))
            {
                section = line.Trim('[', ']').ToLowerInvariant();
                continue;
            }
            var eq = line.IndexOf('=');
            if (eq < 0) continue;
            var key = line[..eq].Trim();
            var val = Unquote(line[(eq + 1)..].Trim());
            (section == "interface" ? iface : peer)[key] = val;
        }
        return (iface, peer);
    }

    private static string CleanLine(string raw)
    {
        var sb = new System.Text.StringBuilder(raw.Length);
        foreach (var c in raw)
        {
            switch (c)
            {
                case '﻿': case '​': case '‌': case '‍': case '⁠': case '­':
                    continue;   // invisible: BOM, zero-width space/joiners, word joiner, soft hyphen
                case ' ': case ' ': case ' ':
                    sb.Append(' '); break;   // non-breaking spaces
                default:
                    sb.Append(c); break;
            }
        }
        return sb.ToString().Trim();
    }

    private static string Unquote(string v)
    {
        foreach (var (open, close) in new[] { ('"', '"'), ('\'', '\''), ('«', '»'), ('“', '”'), ('„', '“'), ('`', '`') })
            if (v.Length >= 2 && v[0] == open && v[^1] == close)
                return v[1..^1].Trim();
        return v;
    }

    /// <summary>
    /// Why a WireGuard key is unusable, in words a user can act on — or null if it is a
    /// proper 32-byte key. Standard and URL-safe base64 are both accepted, as by the engine.
    /// </summary>
    public static string? KeyProblem(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value)) return $"нет ключа {name}";
        var v = value.Trim();

        var b64 = v.Replace('-', '+').Replace('_', '/');
        if (b64.Length % 4 != 0) b64 = b64.PadRight(b64.Length + (4 - b64.Length % 4), '=');
        var buf = new byte[b64.Length];
        if (Convert.TryFromBase64String(b64, buf, out var n))
            return n == 32 ? null : $"ключ {name} неправильной длины ({n} байт вместо 32)";

        var first = v[0];
        if (!char.IsAsciiLetterOrDigit(first) && first is not ('+' or '/' or '-' or '_'))
        {
            if (first == '<' || first == '[' || first == '(')
                return $"вместо ключа {name} стоит заглушка вида «{Short(v)}» — вставь настоящий конфиг";
            return char.IsControl(first) || char.GetUnicodeCategory(first) == System.Globalization.UnicodeCategory.Format
                ? $"ключ {name} начинается с невидимого символа U+{(int)first:X4} — скопируй конфиг заново"
                : $"ключ {name} начинается с лишнего символа «{first}»";
        }
        return v.Length < 44
            ? $"ключ {name} обрезан: {v.Length} символов вместо 44"
            : $"ключ {name} повреждён — в нём есть символы, которых не бывает в ключах";

        static string Short(string s) => s.Length > 16 ? s[..16] + "…" : s;
    }

    private static IEnumerable<string> Split(string s)
        => s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
