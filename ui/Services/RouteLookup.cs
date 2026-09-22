using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;

namespace Nyx.Services;

public sealed class LookupHit
{
    public string Group { get; init; } = "";
    public string How { get; init; } = "";
    public bool IsGeo { get; init; }
}

public sealed class LookupResult
{
    public string Input { get; init; } = "";
    public string Route { get; set; } = "";
    public string Why { get; set; } = "";
    public List<LookupHit> Hits { get; } = new();
    public List<string> Unchecked { get; } = new();
}

/// <summary>
/// "Where will this go?" Answers with the same rules, in the same order, that
/// ConfigGenerator writes into config.json: private addresses go direct; then every
/// warp group is one rule and every geo group the next, so a warp match wins over a
/// geo match; anything left falls through to the default route. Downloaded .srs lists
/// are asked of the engine itself, so the answer is the engine's, not an imitation.
/// </summary>
public static class RouteLookup
{
    public static async Task<LookupResult> CheckAsync(string raw)
    {
        var input = Normalize(raw);
        var result = new LookupResult { Input = input };
        if (input.Length == 0) return result;

        var isProcess = input.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
        var isIp = IPAddress.TryParse(input, out var ip);

        var warpOk = ConfigGenerator.WarpConfigured;
        var geoOk = ConfigGenerator.GeoConfigured;
        var warpRoute = warpOk ? "через WARP" : "напрямую — WARP не настроен";
        var geoRoute = geoOk ? "через geo" : warpOk ? "через WARP — geo не настроен" : warpRoute;

        if (isIp && IsPrivate(ip!))
        {
            result.Route = "напрямую";
            result.Why = "адрес локальной сети — такой трафик в туннель не идёт никогда";
            return result;
        }

        var checks = new List<Task>();
        foreach (var g in RulesService.Load())
        {
            if (string.IsNullOrWhiteSpace(g.Tag)) continue;
            var geo = g.Tag.StartsWith("geo-", StringComparison.OrdinalIgnoreCase);

            if (g.IsInline)
            {
                var wantProcess = g.ItemKind == RuleItemKind.ProcessName;
                if (wantProcess != isProcess) continue;
                // Same rules as the generator: a domain entry covers its subdomains
                // (domain_suffix); a process entry matches in any case, ".exe" optional.
                var matched = wantProcess
                    ? g.Items.Any(i => ConfigGenerator.ProcessMatches(i, input))
                    : g.Items.Any(i => ConfigGenerator.DomainMatches(i, input));
                if (matched)
                    lock (result.Hits)
                        result.Hits.Add(new LookupHit
                        {
                            Group = g.Tag,
                            How = wantProcess ? "имя программы" : "домен в списке",
                            IsGeo = geo,
                        });
            }
            else if (g.IsLocal && !isProcess)
            {
                var file = Path.GetFullPath(Path.Combine(Paths.BuildDir, g.Path));
                if (!File.Exists(file)) continue;   // the generator leaves such groups out too
                var group = g;
                checks.Add(Task.Run(() =>
                {
                    if (!MatchesRuleSet(file, input)) return;
                    lock (result.Hits)
                        result.Hits.Add(new LookupHit
                        {
                            Group = group.Tag,
                            How = "список " + Path.GetFileName(file),
                            IsGeo = geo,
                        });
                }));
            }
            else if (g.IsRemote && !isProcess)
            {
                result.Unchecked.Add(g.Tag);
            }
        }
        await Task.WhenAll(checks);

        // Same precedence as the generated route: all warp groups, then all geo groups.
        var warpHit = result.Hits.FirstOrDefault(h => !h.IsGeo);
        var geoHit = result.Hits.FirstOrDefault(h => h.IsGeo);
        if (warpHit != null)
        {
            result.Route = warpRoute;
            result.Why = $"совпало с группой {warpHit.Group} ({warpHit.How})";
            if (geoHit != null)
                result.Why += $"; группа {geoHit.Group} тоже подходит, но правила WARP проверяются раньше";
        }
        else if (geoHit != null)
        {
            result.Route = geoRoute;
            result.Why = $"совпало с группой {geoHit.Group} ({geoHit.How})";
        }
        else
        {
            var final = SettingsService.Load().Final;
            result.Route = final switch
            {
                "geo" => geoRoute,
                "proxy" => warpRoute,
                _ => "напрямую",
            };
            result.Why = "ни одно правило не подошло — действует маршрут по умолчанию";
        }
        return result;
    }

    /// <summary>
    /// Accepts what people actually paste — a full URL, a host with a port, a trailing
    /// slash — and reduces it to the host the engine would see.
    /// </summary>
    public static string Normalize(string raw)
    {
        var s = (raw ?? "").Trim();
        if (s.Length == 0) return s;
        if (s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return Path.GetFileName(s);

        if (Uri.TryCreate(s.Contains("://") ? s : "http://" + s, UriKind.Absolute, out var uri)
            && !string.IsNullOrEmpty(uri.Host))
            s = uri.Host;

        return s.Trim('.', '/').ToLowerInvariant();
    }

    private static bool MatchesRuleSet(string file, string input)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = Paths.SingBoxExe,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in new[] { "rule-set", "match", "-f", "binary", file, input })
                psi.ArgumentList.Add(a);

            using var p = Process.Start(psi);
            if (p == null) return false;
            // The engine prints "match rules.[N]: ..." on a hit and nothing otherwise — on
            // STDERR, through its logger. Read both streams concurrently: reading one to
            // the end while the other fills its pipe buffer would deadlock.
            var err = p.StandardError.ReadToEndAsync();
            var outp = p.StandardOutput.ReadToEndAsync();
            p.WaitForExit(5000);
            var text = err.Result + "\n" + outp.Result;
            return text.Split('\n').Any(l => l.TrimStart().StartsWith("match ", StringComparison.OrdinalIgnoreCase));
        }
        catch { return false; }
    }

    private static bool IsPrivate(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal) return true;
        var b = ip.GetAddressBytes();
        if (b.Length != 4) return false;
        return b[0] == 10
            || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
            || (b[0] == 192 && b[1] == 168)
            || (b[0] == 169 && b[1] == 254)
            || (b[0] == 100 && b[1] >= 64 && b[1] <= 127);   // CGNAT, used by WARP itself
    }
}
