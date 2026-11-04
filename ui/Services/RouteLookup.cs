using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;

namespace Tunor.Services;

public sealed class LookupHit
{
    public string Group { get; init; } = "";
    public string How { get; init; } = "";
    /// <summary>The slot the matching group feeds: a tunnel id, or "direct".</summary>
    public string Slot { get; init; } = "";
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
/// ConfigGenerator writes into config.json: private addresses go direct; then the rule
/// groups, the direct slot before the tunnels and the tunnels in list order, so a direct
/// match wins over any tunnel's list; anything left falls through to the default route.
/// Downloaded .srs lists are asked of the engine itself, so the answer is the engine's,
/// not an imitation.
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

        var tunnels = TunnelService.Load();
        var live = ConfigGenerator.LiveTunnels(tunnels);

        // What a slot means for the user, fallback included: a tunnel that is not set up
        // passes its traffic to what it rides inside, exactly as the generated config does.
        string Label(string slot)
        {
            if (slot == TunnelService.Direct) return "напрямую";
            var t = tunnels.FirstOrDefault(x => string.Equals(x.Id, slot, StringComparison.OrdinalIgnoreCase));
            if (t == null) return "напрямую";
            if (live.Contains(t.Id)) return "через " + t.Title;
            var via = tunnels.FirstOrDefault(x => string.Equals(x.Id, t.Detour, StringComparison.OrdinalIgnoreCase));
            return via != null && live.Contains(via.Id)
                ? $"через {via.Title} — {t.Title} не настроен"
                : $"напрямую — {t.Title} не настроен";
        }

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
            var slot = TunnelService.SlotOf(g.Tag, tunnels) ?? TunnelService.Warp;

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
                            Slot = slot,
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
                            Slot = slot,
                        });
                }));
            }
            else if (g.IsRemote && !isProcess)
            {
                result.Unchecked.Add(g.Tag);
            }
        }
        await Task.WhenAll(checks);

        // Same precedence as the generated route: the direct slot first, then the
        // tunnels in list order.
        int Rank(LookupHit h)
        {
            if (h.Slot == TunnelService.Direct) return 0;
            var at = tunnels.FindIndex(t => string.Equals(t.Id, h.Slot, StringComparison.OrdinalIgnoreCase));
            return at < 0 ? int.MaxValue - 1 : 10 + at;
        }

        var best = result.Hits.OrderBy(Rank).FirstOrDefault();
        if (best != null)
        {
            result.Route = Label(best.Slot);
            result.Why = $"совпало с группой {best.Group} ({best.How})";
            var beaten = result.Hits.Where(h => !ReferenceEquals(h, best)).OrderBy(Rank).ToList();
            if (beaten.Count == 1)
                result.Why += $"; группа {beaten[0].Group} тоже подходит, но {best.Group} проверяется раньше";
            else if (beaten.Count > 1)
                result.Why += $"; ещё подходят {string.Join(", ", beaten.Select(h => h.Group))}, но {best.Group} проверяется раньше";
        }
        else
        {
            var slot = ConfigGenerator.FinalSlot(SettingsService.Load().Final);
            result.Route = slot.Length == 0 ? "напрямую" : Label(slot);
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
