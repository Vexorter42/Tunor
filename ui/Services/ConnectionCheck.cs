using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;

namespace Nyx.Services;

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
        if (!ProcessService.IsRunning) return "Туннель не запущен — запусти его и проверь снова.";
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
        var warp = ConfigGenerator.WarpState;
        var geo = ConfigGenerator.GeoState;

        var tasks = new List<Task<PathResult>>
        {
            Probe("Напрямую", 1081),
            warp.Usable ? Probe("WARP", 1082) : Task.FromResult(Skipped("WARP", warp)),
            geo.Usable ? Probe("geo", 1083) : Task.FromResult(Skipped("geo", geo)),
        };
        var results = (await Task.WhenAll(tasks)).ToList();

        // Into the log as well, so a saved log shows the state of every tunnel.
        foreach (var r in results)
            ProcessService.Note($"проверка {r.Name}: " + (r.Status switch
            {
                "ok" => $"{r.Ip} {r.Country}{(r.ViaWarp ? " warp=on" : "")} · {r.Ms} мс",
                "skip" => r.Note,
                _ => "не отвечает — " + r.Note,
            }), r.Status == "fail");
        return results;
    }

    private static PathResult Skipped(string name, ConfigGenerator.ConfState state) => new()
    {
        Name = name,
        Status = "skip",
        Note = state.Problem != null ? "конфиг не подходит: " + state.Problem : "не настроен",
    };

    private static async Task<PathResult> Probe(string name, int port)
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
