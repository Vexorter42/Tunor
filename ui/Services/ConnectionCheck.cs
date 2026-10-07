using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace Tunor.Services;

public sealed class PathResult
{
    public string Name { get; init; } = "";
    /// <summary>ok, fail, or skip (tunnel not configured — nothing to test).</summary>
    public string Status { get; init; } = "";
    public string Ip { get; init; } = "";
    public string Country { get; init; } = "";
    /// <summary>Cloudflare's own verdict that the request came through WARP.</summary>
    public bool ViaWarp { get; init; }
    public int Ms { get; init; }
    public string Note { get; init; } = "";
}

/// <summary>
/// Sends one request through each path of the running engine — its direct, WARP and geo
/// proxy ports — and reports the exit IP, country and response time. Answers "is it
/// actually working?" in one look, instead of by reading logs. Going through the live
/// engine means no second WireGuard session is opened with the same key.
/// </summary>
public static class ConnectionCheck
{
    // Cloudflare's trace page: plain key=value lines — ip, loc (country), warp=on/off.
    private const string TraceUrl = "https://www.cloudflare.com/cdn-cgi/trace";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>Why the check cannot run right now, or null if it can.</summary>
    public static string? Unavailable()
    {
        if (!EngineState.IsRunning) return "Туннель не запущен — запусти его и проверь снова.";
        try
        {
            if (!SettingsService.Load().Proxy)
                return "Проверка идёт через локальный прокси движка — включи «Proxy» в «Настройках».";
        }
        catch { }
        return null;
    }

    public static async Task<List<PathResult>> RunAsync()
    {
        var tunnels = TunnelService.Load();
        var live = ConfigGenerator.LiveTunnels(tunnels);

        // Every tunnel in the list, not just the two that came with the app: a VPN the
        // user added themselves is the one they most want to see an answer from.
        var tasks = new List<Task<PathResult>> { Probe("Напрямую", 1081, chained: false) };
        foreach (var t in tunnels)
        {
            var port = ConfigGenerator.DoorPort(t.Id, tunnels);
            tasks.Add(live.Contains(t.Id) && port is > 0 and <= 65535
                ? Probe(t.Title, port, chained: t.Detour.Length > 0)
                : Task.FromResult(Skipped(t, tunnels)));
        }
        var results = (await Task.WhenAll(tasks)).ToList();

        // Into the log as well, so a saved log shows the state of every tunnel.
        foreach (var r in results)
            EngineState.Note($"проверка {r.Name}: " + (r.Status switch
            {
                "ok" => $"{r.Ip} {r.Country}{(r.ViaWarp ? " warp=on" : "")} · {r.Ms} мс",
                "skip" => r.Note,
                _ => "не отвечает — " + r.Note,
            }), r.Status == "fail");
        return results;
    }

    /// <summary>
    /// Why a tunnel is not being tested, in words the user can act on. The reasons follow
    /// what decides a tunnel is live: its own settings first, then the one it rides
    /// inside — geo without WARP is a dead end, not a slow path.
    /// </summary>
    private static PathResult Skipped(Tunnel t, List<Tunnel> tunnels)
    {
        string note;
        if (!t.Enabled) note = "выключен";
        else if (t.IsWireguard)
        {
            var state = ConfigGenerator.Inspect(ConfigGenerator.ConfPath(t));
            note = state.Problem != null ? "конфиг не подходит: " + state.Problem
                 : !state.Usable ? "не настроен"
                 : Via(t, tunnels);
        }
        else
        {
            var problem = t.IsAuto
                ? t.Nodes.Count == 0 ? "в подписке нет серверов — обнови её"
                  : t.Nodes.All(n => !ProxyLink.Parse(n.Url).Ok)
                      ? "ни один сервер подписки не разобрался" : null
                : ProxyLink.Parse(t.ActiveUrl).Problem;
            note = problem ?? Via(t, tunnels);
        }
        return new PathResult { Name = t.Title, Status = "skip", Note = note };
    }

    /// <summary>A tunnel that is fine in itself, but has nothing to ride inside.</summary>
    private static string Via(Tunnel t, List<Tunnel> tunnels)
    {
        var via = tunnels.FirstOrDefault(
            x => string.Equals(x.Id, t.Detour, StringComparison.OrdinalIgnoreCase));
        return via == null ? "не настроен" : $"идёт через «{via.Title}», а он не готов";
    }

    /// <summary>
    /// One request through one door, with a second attempt for a tunnel that rides inside
    /// another. Its own handshake cannot even begin until the outer tunnel is up, so the
    /// first try just after a start can fail while nothing at all is wrong — which is
    /// exactly how geo behaves on a cold start.
    /// </summary>
    private static async Task<PathResult> Probe(string name, int port, bool chained)
    {
        var first = await Attempt(name, port);
        if (first.Status == "ok" || !chained) return first;
        await Task.Delay(TimeSpan.FromSeconds(2));
        return await Attempt(name, port);
    }

    private static async Task<PathResult> Attempt(string name, int port)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var handler = new HttpClientHandler
            {
                Proxy = new WebProxy($"socks5://127.0.0.1:{port}"),
                UseProxy = true,
            };
            using var http = new HttpClient(handler) { Timeout = Timeout };
            var text = await http.GetStringAsync(TraceUrl);
            sw.Stop();

            var kv = text.Split('\n')
                .Select(l => l.Split('=', 2))
                .Where(p => p.Length == 2)
                .GroupBy(p => p[0].Trim())
                .ToDictionary(g => g.Key, g => g.First()[1].Trim());

            return new PathResult
            {
                Name = name,
                Status = "ok",
                Ip = kv.GetValueOrDefault("ip", "?"),
                Country = kv.GetValueOrDefault("loc", ""),
                ViaWarp = kv.GetValueOrDefault("warp", "off") is "on" or "plus",
                Ms = (int)sw.ElapsedMilliseconds,
            };
        }
        catch (Exception ex)
        {
            return new PathResult
            {
                Name = name,
                Status = "fail",
                Ms = (int)sw.ElapsedMilliseconds,
                Note = ex is TaskCanceledException ? $"нет ответа за {Timeout.TotalSeconds:0} с"
                     : ex.InnerException?.Message ?? ex.Message,
            };
        }
    }
}
