using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Tunor.Services;

/// <summary>What came of trying a WARP endpoint.</summary>
public sealed class EndpointTry
{
    public string Endpoint { get; init; } = "";
    public bool Started { get; init; }
    public PathResult? Warp { get; init; }
    public SiteResult? Site { get; init; }
    public string SiteHost { get; init; } = "";
    public string Note { get; init; } = "";

    /// <summary>What came of it, in words for the dialog.</summary>
    public string Report()
    {
        if (!Started) return Note;
        if (Warp is not { Status: "ok" } w)
            return $"С адресом {Endpoint} WARP не отвечает" + (Warp is { Note.Length: > 0 } ? $" ({Warp.Note})" : "") + ". Попробуйте следующий.";

        var place = string.Join(", ", new[] { w.Colo.Length > 0 ? "дата-центр " + w.Colo : "", w.Country, w.Ip }.Where(x => x.Length > 0));
        var text = $"С адресом {Endpoint} WARP отвечает за {w.Ms} мс. Выход: {place}.";
        if (Site != null)
            text += Site.Status == "ok"
                ? $"\n{SiteHost} открылся (HTTP {Site.HttpStatus}, {Site.Ms} мс)."
                : $"\n{SiteHost} не открылся: {Site.Note}.";
        return text;
    }
}

/// <summary>
/// The address WARP is reached at, which is the one thing in warp.conf worth changing by hand: it decides
/// which of Cloudflare's data centres the traffic comes out of, and a site that is slow or shut through one
/// of them can be fine through another. (The same key must not be used on two endpoints at once, so the
/// engine is restarted with the new one, not run beside the old.)
/// </summary>
public static class WarpEndpoint
{
    /// <summary>
    /// What to try after the one in the config. None of these is better than another in itself: which one
    /// works depends on the provider, so they are tried one by one and the result is looked at.
    /// </summary>
    public static readonly string[] Candidates =
    {
        "engage.cloudflareclient.com:2408",
        "162.159.192.1:2408",
        "162.159.193.1:2408",
        "162.159.195.1:2408",
        "188.114.96.1:2408",
        "188.114.97.1:2408",
        "188.114.98.1:2408",
        "188.114.99.1:2408",
        "162.159.192.1:500",
        "162.159.192.1:1701",
        "162.159.192.1:4500",
    };

    private static readonly Regex Valid = new(@"^[A-Za-z0-9][A-Za-z0-9.\-]{0,99}:\d{1,5}$", RegexOptions.Compiled);

    public static bool IsValid(string? endpoint)
    {
        var e = endpoint ?? "";
        if (!Valid.IsMatch(e)) return false;
        var port = int.Parse(e[(e.LastIndexOf(':') + 1)..]);
        return port is >= 1 and <= 65535;
    }

    private static readonly Regex Line = new(@"^(?<pre>\s*Endpoint\s*=\s*)(?<value>[^\r\n#;]*?)(?<post>\s*)$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>The endpoint of the first peer in a config, or null if it has none.</summary>
    public static string? Read(string conf)
    {
        var peer = false;
        foreach (var raw in conf.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var t = line.Trim();
            if (t.StartsWith('[')) { peer = t.Equals("[Peer]", StringComparison.OrdinalIgnoreCase); continue; }
            if (!peer) continue;
            var m = Line.Match(line);
            if (m.Success) return m.Groups["value"].Value.Trim();
        }
        return null;
    }

    /// <summary>The config with its endpoint replaced and everything else — keys, obfuscation parameters, line endings — as it was.</summary>
    public static string Replace(string conf, string endpoint)
    {
        if (!IsValid(endpoint)) throw new ArgumentException("Адрес должен быть вида хост:порт.");
        var lines = conf.Split('\n');
        var peer = false;
        for (var i = 0; i < lines.Length; i++)
        {
            var cr = lines[i].EndsWith('\r') ? "\r" : "";
            var line = lines[i].TrimEnd('\r');
            var t = line.Trim();
            if (t.StartsWith('[')) { peer = t.Equals("[Peer]", StringComparison.OrdinalIgnoreCase); continue; }
            if (!peer) continue;
            var m = Line.Match(line);
            if (!m.Success) continue;
            lines[i] = m.Groups["pre"].Value + endpoint + m.Groups["post"].Value + cr;
            return string.Join("\n", lines);
        }
        throw new InvalidOperationException("В warp.conf нет строки Endpoint.");
    }

    /// <summary>The places the tests put their own: where warp.conf is, what rebuilds the config, whether the engine is up, how long to wait.</summary>
    internal static Func<string> ConfFile { get; set; } = () => Paths.WarpConf;
    internal static Action Rebuild { get; set; } = () => ConfigGenerator.Generate();
    internal static Func<bool> Running { get; set; } = () => EngineState.IsRunning;
    internal static TimeSpan Pause { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>Where warp.conf points now; null if it is missing or has none.</summary>
    public static string? Current()
    {
        try { return File.Exists(ConfFile()) ? Read(File.ReadAllText(ConfFile())) : null; }
        catch { return null; }
    }

    /// <summary>The choices to offer: the one now in the config first, then the others.</summary>
    public static List<string> Options(string? current)
    {
        var list = new List<string>();
        if (!string.IsNullOrEmpty(current)) list.Add(current);
        list.AddRange(Candidates.Where(c => !c.Equals(current, StringComparison.OrdinalIgnoreCase)));
        return list;
    }

    /// <summary>The one after <paramref name="current"/> in <paramref name="options"/>, wrapping round.</summary>
    public static string Next(IReadOnlyList<string> options, string? current)
    {
        var i = options.ToList().FindIndex(o => o.Equals(current, StringComparison.OrdinalIgnoreCase));
        return options[(i + 1) % options.Count];
    }

    /// <summary>Writes the endpoint into warp.conf, keeping the file as it was in warp.conf.bak.</summary>
    public static void Apply(string endpoint)
    {
        var path = ConfFile();
        var text = File.ReadAllText(path);
        var changed = Replace(text, endpoint);
        if (changed == text) return;
        try { File.Copy(path, path + ".bak", overwrite: true); } catch { }
        File.WriteAllText(path, changed);
        Rebuild();
    }

    /// <summary>The way the engine is restarted, and how the engine is waited for. A test puts its own.</summary>
    internal static Func<Task> Restart { get; set; } = () => EngineState.RestartAsync();
    internal static Func<Task<List<PathResult>>> Check { get; set; } = () => ConnectionCheck.RunAsync(log: false);

    /// <summary>
    /// Switches to <paramref name="endpoint"/>, restarts the engine on it and looks at what comes out: does
    /// WARP answer, from which data centre, and does the site open. Nothing is undone if it does not — the
    /// caller decides, and puts the old one back with <see cref="Apply"/>.
    /// </summary>
    public static async Task<EndpointTry> TryAsync(string endpoint, string? site)
    {
        if (await SwitchAsync(endpoint) is { } problem) return new EndpointTry { Endpoint = endpoint, Note = problem };

        // The engine takes a moment to bring the tunnel up: look until WARP answers or the time is out.
        PathResult? warp = null;
        for (var i = 0; i < 4; i++)
        {
            warp = (await Check()).FirstOrDefault(r => r.Name.Equals("WARP", StringComparison.OrdinalIgnoreCase));
            if (warp is { Status: "ok" }) break;
            await Task.Delay(Pause);
        }

        SiteResult? result = null;
        var host = "";
        if (warp is { Status: "ok" } && !string.IsNullOrWhiteSpace(site))
        {
            var url = RouteProbe.UrlOf(site);
            if (url != null)
            {
                host = new Uri(url).Host;
                var door = ConnectionCheck.Doors().FirstOrDefault(d => d.Name.Equals("WARP", StringComparison.OrdinalIgnoreCase));
                if (door is { Skipped: null })
                {
                    var reply = await RouteProbe.Fetch(door.Port, url, TimeSpan.FromSeconds(10));
                    result = new SiteResult { Name = "WARP", Status = reply.Ok ? "ok" : "fail", HttpStatus = reply.Status, Ms = reply.Ms, Note = reply.Note };
                }
            }
        }
        return new EndpointTry { Endpoint = endpoint, Started = true, Warp = warp, Site = result, SiteHost = host };
    }

    /// <summary>
    /// Writes the endpoint and restarts the engine on it. Returns what went wrong, in words, or null.
    /// This is also how an endpoint is put back: nothing here knows whether it is a try or a return.
    /// </summary>
    public static async Task<string?> SwitchAsync(string endpoint)
    {
        if (!IsValid(endpoint)) return "Адрес должен быть вида хост:порт.";
        try { Apply(endpoint); }
        catch (Exception ex) { return ex.Message; }

        try { await Restart(); }
        catch (Exception ex) { return "Движок не перезапустился: " + ex.Message; }
        if (!Running())
            return "Движок не запустился с этой точкой" + (EngineState.LastFailure is { } f ? ": " + f : ".");
        return null;
    }
}
