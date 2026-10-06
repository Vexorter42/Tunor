using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace Tunor.Services;

/// <summary>One server out of a subscription.</summary>
public class SubNode
{
    /// <summary>What the provider called it — usually a country and a number.</summary>
    [JsonPropertyName("name")] public string Name { get; set; } = "";

    /// <summary>The share link, kept as it came so it is parsed the same way every time.</summary>
    [JsonPropertyName("url")] public string Url { get; set; } = "";

    /// <summary>"tcp · Reality · Vision" — what the link turned out to be.</summary>
    [JsonIgnore] public string Summary => ProxyLink.Parse(Url).Summary;
}

/// <summary>
/// A subscription is one address that answers with many share links, so a provider can
/// change its servers without anyone re-adding them by hand.
///
/// The body is either the links themselves, one per line, or that same text in base64 —
/// both are in the wild, and which one shows up is not advertised. Providers also put
/// the plan's name, its traffic allowance and how often to look again in headers of
/// their own; those are read when present and ignored when not.
///
/// The address is a credential: anyone holding it gets the keys to every server in it.
/// It is kept in data/tunnels.json like the rest of the tunnel settings and never
/// leaves the machine except to fetch the list itself.
/// </summary>
public static class Subscription
{
    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        AutomaticDecompression = System.Net.DecompressionMethods.All,
    })
    { Timeout = TimeSpan.FromSeconds(40) };

    public sealed record Result(List<SubNode> Nodes, string Title, string Traffic, string? Problem)
    {
        public bool Ok => Problem == null;
    }

    private static Result Bad(string why) => new(new List<SubNode>(), "", "", why);

    /// <summary>Read straight off the assembly, so this file depends on nothing else.</summary>
    private static string Version =>
        System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "1";

    public static bool LooksLikeSubscription(string text)
    {
        text = text.Trim();
        return (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
               && Uri.TryCreate(text, UriKind.Absolute, out _);
    }

    /// <summary>Downloads the list and reads every link in it that this engine can use.</summary>
    public static async Task<Result> FetchAsync(string url)
    {
        if (!LooksLikeSubscription(url)) return Bad("это не похоже на адрес подписки");

        HttpResponseMessage resp;
        string body;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url.Trim());
            // Some providers answer differently, or not at all, without a client name.
            req.Headers.TryAddWithoutValidation("User-Agent", "Tunor/" + Version);
            resp = await Http.SendAsync(req);
            body = await resp.Content.ReadAsStringAsync();
        }
        catch (Exception ex)
        {
            return Bad("не удалось скачать список: " + ex.Message);
        }

        if (!resp.IsSuccessStatusCode)
            return Bad($"сервис ответил {(int)resp.StatusCode} {resp.ReasonPhrase}. "
                       + "Проверь адрес подписки — возможно, он устарел.");

        var text = Decode(body);
        var nodes = new List<SubNode>();
        var refused = 0;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var p = ProxyLink.Parse(line);
            if (!p.Ok) { refused++; continue; }
            nodes.Add(new SubNode { Name = p.Name, Url = line });
        }

        if (nodes.Count == 0)
            return Bad(refused > 0
                ? $"в списке {refused} серверов, но ни одного подходящего: это другие протоколы"
                : "по этому адресу нет списка серверов");

        return new Result(nodes, ReadTitle(resp), ReadTraffic(resp), null)
        {
        };
    }

    /// <summary>The body as text, whether it arrived as links or as base64 of the same.</summary>
    private static string Decode(string body)
    {
        var t = body.Trim();
        if (t.Contains("://")) return t;                 // already the links themselves
        try
        {
            var padded = t.Replace('-', '+').Replace('_', '/');
            padded += new string('=', (4 - padded.Length % 4) % 4);
            var text = Encoding.UTF8.GetString(Convert.FromBase64String(padded));
            return text.Contains("://") ? text : t;
        }
        catch { return t; }                              // not base64 after all
    }

    private static string ReadTitle(HttpResponseMessage resp)
    {
        var raw = Header(resp, "profile-title");
        if (raw.Length == 0) return "";
        // Providers send this as "base64:<...>" when it is not plain ASCII.
        if (raw.StartsWith("base64:", StringComparison.OrdinalIgnoreCase))
        {
            try { return Encoding.UTF8.GetString(Convert.FromBase64String(raw[7..])); }
            catch { return ""; }
        }
        return raw;
    }

    /// <summary>"осталось 48 ГБ · до 3 марта", as much of it as the provider said.</summary>
    private static string ReadTraffic(HttpResponseMessage resp)
    {
        var raw = Header(resp, "subscription-userinfo");
        if (raw.Length == 0) return "";

        var f = raw.Split(';')
            .Select(p => p.Split('=', 2))
            .Where(p => p.Length == 2)
            .ToDictionary(p => p[0].Trim().ToLowerInvariant(), p => p[1].Trim());

        long Num(string k) => long.TryParse(f.GetValueOrDefault(k), out var v) ? v : 0;

        var used = Num("upload") + Num("download");
        var total = Num("total");
        var parts = new List<string>();
        // total=0 means unmetered, which is not the same as "nothing left".
        if (total > 0) parts.Add($"осталось {Size(Math.Max(0, total - used))} из {Size(total)}");
        else if (used > 0) parts.Add($"израсходовано {Size(used)}");

        var expire = Num("expire");
        if (expire > 0)
            parts.Add("до " + DateTimeOffset.FromUnixTimeSeconds(expire).ToLocalTime().ToString("d MMMM yyyy"));

        return string.Join(" · ", parts);
    }

    private static string Header(HttpResponseMessage resp, string name)
    {
        if (resp.Headers.TryGetValues(name, out var v)) return v.FirstOrDefault()?.Trim() ?? "";
        if (resp.Content.Headers.TryGetValues(name, out var c)) return c.FirstOrDefault()?.Trim() ?? "";
        return "";
    }

    private static string Size(long b) => b switch
    {
        >= 1L << 40 => $"{b / (double)(1L << 40):0.#} ТБ",
        >= 1L << 30 => $"{b / (double)(1L << 30):0.#} ГБ",
        >= 1L << 20 => $"{b / (double)(1L << 20):0.#} МБ",
        _ => $"{b / 1024.0:0.#} КБ",
    };
}
