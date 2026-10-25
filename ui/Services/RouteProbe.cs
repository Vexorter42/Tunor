using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

namespace Tunor.Services;

/// <summary>What one way out made of a request to the site.</summary>
public sealed class SiteResult
{
    public string Name { get; init; } = "";
    /// <summary>ok — the site answered, whatever it said; fail; skip — the tunnel is not there to test.</summary>
    public string Status { get; init; } = "";
    public int HttpStatus { get; init; }
    public int Ms { get; init; }
    public string Note { get; init; } = "";
}

public sealed class SiteCheck
{
    public string Host { get; init; } = "";
    /// <summary>Where the rules send the site, as the Rules page says it; empty if that could not be worked out.</summary>
    public string RuleRoute { get; set; } = "";
    public string RuleWhy { get; set; } = "";
    public List<SiteResult> Routes { get; } = new();
    public string Advice { get; set; } = "";

    /// <summary>The results as lines of text, with the advice after them, for the page that shows them.</summary>
    public string Report()
    {
        var sb = new StringBuilder();
        foreach (var r in Routes)
            sb.AppendLine(r.Status switch
            {
                "ok" => $"{r.Name} — открылся (HTTP {r.HttpStatus}, {r.Ms} мс)",
                "fail" => $"{r.Name} — не открылся: {r.Note}",
                _ => $"{r.Name} — не проверялся: {r.Note}",
            });
        if (Advice.Length > 0) { sb.AppendLine(); sb.Append(Advice); }
        return sb.ToString().TrimEnd();
    }
}

/// <summary>
/// "Does this site open, and through which way?" One request to the site through each way out of
/// the engine — straight, and through each tunnel — so the answer is not the browser's guess but
/// what actually happens on each. A site that opens on one way and not on another says where the
/// fault is: the provider's block (the direct way fails), the tunnel's exit (one tunnel fails), or the
/// site itself (all fail).
///
/// An answer of any kind counts: a 403 or a redirect means the connection, the TLS handshake and the
/// request all went through, which is the thing being asked.
/// </summary>
public static class RouteProbe
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    internal readonly record struct Reply(bool Ok, int Status, int Ms, string Note);

    /// <summary>One request to <paramref name="url"/> through the proxy on <paramref name="port"/>. A test puts its own here.</summary>
    internal static Func<int, string, TimeSpan, Task<Reply>> Fetch { get; set; } = FetchViaProxy;

    /// <summary>
    /// What the person typed, as an address to request: a bare name or a link; null for what cannot be
    /// requested (a program's name, an empty line).
    /// </summary>
    public static string? UrlOf(string raw)
    {
        var s = (raw ?? "").Trim();
        if (s.Length == 0 || s.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return null;
        if (!s.Contains("://")) s = "https://" + s;
        if (!Uri.TryCreate(s, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host)) return null;
        if (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) return null;
        return uri.GetLeftPart(UriPartial.Authority) + (uri.AbsolutePath.Length > 1 ? uri.PathAndQuery : "/");
    }

    /// <param name="lookup">Also ask where the rules send the site (it starts the engine's own tool for the downloaded lists).</param>
    public static async Task<SiteCheck> CheckSiteAsync(string raw, bool lookup = true)
    {
        var url = UrlOf(raw) ?? throw new ArgumentException("Нужен сайт или адрес — программу так не проверить.");
        var host = new Uri(url).Host;
        var check = new SiteCheck { Host = host };

        var doors = ConnectionCheck.Doors();
        var tasks = doors.Select(d => d.Skipped != null
            ? Task.FromResult(new SiteResult { Name = d.Name, Status = "skip", Note = d.Skipped.Note })
            : Probe(d, url)).ToList();

        if (lookup)
        {
            try
            {
                var r = await RouteLookup.CheckAsync(host);
                check.RuleRoute = r.Route;
                check.RuleWhy = r.Why;
            }
            catch { /* the answer is only the extra */ }
        }

        check.Routes.AddRange(await Task.WhenAll(tasks));
        check.Advice = Advise(check.Routes, check.RuleRoute);

        foreach (var r in check.Routes.Where(r => r.Status != "skip"))
            EngineState.Note($"сайт {host} через {r.Name}: " + (r.Status == "ok" ? $"ответил {r.HttpStatus} за {r.Ms} мс" : "не открылся — " + r.Note), r.Status == "fail");
        return check;
    }

    /// <summary>A second try for a tunnel that rides inside another: the first one can fail on a cold start while nothing is wrong.</summary>
    private static async Task<SiteResult> Probe(Door d, string url)
    {
        var reply = await Fetch(d.Port, url, Timeout);
        if (!reply.Ok && d.Chained)
        {
            await Task.Delay(TimeSpan.FromSeconds(2));
            reply = await Fetch(d.Port, url, Timeout);
        }
        return new SiteResult
        {
            Name = d.Name, Status = reply.Ok ? "ok" : "fail", HttpStatus = reply.Status, Ms = reply.Ms, Note = reply.Note,
        };
    }

    internal static async Task<Reply> FetchViaProxy(int port, string url, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var handler = new HttpClientHandler
            {
                Proxy = new WebProxy($"socks5://127.0.0.1:{port}"),
                UseProxy = true,
                AllowAutoRedirect = false,
            };
            using var http = new HttpClient(handler) { Timeout = timeout };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (compatible; Tunor)");
            using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            return new Reply(true, (int)resp.StatusCode, (int)sw.ElapsedMilliseconds, "");
        }
        catch (Exception ex)
        {
            return new Reply(false, 0, (int)sw.ElapsedMilliseconds, Explain(ex, timeout));
        }
    }

    /// <summary>What went wrong, in a few words; a reset in the middle of a handshake is what a block looks like.</summary>
    internal static string Explain(Exception ex, TimeSpan timeout)
    {
        if (ex is TaskCanceledException or OperationCanceledException) return $"нет ответа за {timeout.TotalSeconds:0} с";
        var text = ex.InnerException?.Message ?? ex.Message;
        var low = text.ToLowerInvariant();
        if (low.Contains("reset") || low.Contains("forcibly") || low.Contains("разорвал") || low.Contains("сброс"))
            return "соединение сбросили на полпути (так выглядит блокировка)";
        if (low.Contains("refused") || low.Contains("отклонил")) return "отказ в соединении";
        return text.Length > 120 ? text[..120] + "…" : text;
    }

    /// <summary>
    /// What the results come to, in a sentence a person can act on. Pure: the same results always
    /// give the same advice, so it can be tested without a network.
    /// </summary>
    internal static string Advise(IReadOnlyList<SiteResult> routes, string ruleRoute)
    {
        var tested = routes.Where(r => r.Status != "skip").ToList();
        var direct = tested.FirstOrDefault(r => r.Name == ConnectionCheck.DirectName);
        var tunnels = tested.Where(r => r.Name != ConnectionCheck.DirectName).ToList();
        var good = tunnels.Where(r => r.Status == "ok").ToList();
        var bad = tunnels.Where(r => r.Status == "fail").ToList();

        if (tested.Count == 0) return "Нечем проверить: туннелей, которые можно проверить, нет.";
        if (tested.All(r => r.Status == "fail"))
            return "Не открылся ни одним путём. Либо сайт сейчас недоступен сам по себе, либо его не пускают и напрямую, и через туннели — тогда нужен другой туннель (geo или свой VPN).";

        // what the rules would do, and whether that way works
        var ruled = tested.FirstOrDefault(r => RuleSends(ruleRoute, r.Name));
        if (ruled is { Status: "fail" })
        {
            var other = tested.FirstOrDefault(r => r.Status == "ok");
            return $"Правила отправляют сайт «{ruleRoute}», а там он не открывается" +
                   (other == null ? "." : $", хотя «{other.Name}» его открывает. Это и есть причина: поменяйте группу правил для сайта или почините этот путь.") +
                   (ruled.Name != ConnectionCheck.DirectName && ruled.Name.Equals("WARP", StringComparison.OrdinalIgnoreCase)
                       ? " С WARP помогает другая точка выхода: Главная → «Точка выхода WARP»."
                       : "");
        }

        if (direct is { Status: "fail" } && good.Count > 0)
            return $"Напрямую не открывается — похоже, провайдер ограничивает; через «{string.Join("», «", good.Select(g => g.Name))}» открывается." +
                   (ruleRoute.Contains("напрямую") ? " Сейчас правила пускают сайт напрямую: добавьте его в список для туннеля." : "");

        if (direct is { Status: "ok" } && bad.Count > 0)
            return $"Через «{string.Join("», «", bad.Select(b => b.Name))}» не открывается, хотя напрямую открывается. " +
                   "Если сайт идёт по правилам через этот туннель, смените точку выхода или уберите сайт из его правил.";

        if (bad.Count > 0)
            return $"Через «{string.Join("», «", bad.Select(b => b.Name))}» не открывается, через остальные пути — да.";

        return "Открывается на всех путях. Если в браузере не открывается, дело не в маршруте: кэш, расширение, DNS самого браузера.";
    }

    /// <summary>Whether the route a rule gives ("через WARP", "напрямую", "через geo — geo не настроен"...) is this way out.</summary>
    internal static bool RuleSends(string ruleRoute, string name)
    {
        if (string.IsNullOrEmpty(ruleRoute)) return false;
        // "через WARP — geo не настроен" is WARP; cut at the dash before looking for a name
        var head = ruleRoute.Split(" — ")[0];
        if (name == ConnectionCheck.DirectName) return head.StartsWith("напрямую", StringComparison.OrdinalIgnoreCase);
        return head.Equals("через " + name, StringComparison.OrdinalIgnoreCase);
    }
}
