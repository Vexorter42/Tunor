using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Tasks;

namespace Nyx.Services;

public sealed class ConnInfo
{
    public string Id { get; init; } = "";
    /// <summary>For display: domain (or IP) with the port.</summary>
    public string Host { get; init; } = "";
    /// <summary>Domain when the engine sniffed one, otherwise the IP — no port.</summary>
    public string Target { get; init; } = "";
    public bool TargetIsIp { get; init; }
    public string ProcessPath { get; init; } = "";
    public string Outbound { get; init; } = "";
    public string Rule { get; init; } = "";
    public string Process { get; init; } = "";
    public string Network { get; init; } = "";
    public long Upload { get; init; }
    public long Download { get; init; }
    public DateTime Start { get; init; }
}

public sealed class ConnSnapshot
{
    public List<ConnInfo> Connections { get; } = new();
    public long UploadTotal { get; init; }
    public long DownloadTotal { get; init; }
}

/// <summary>
/// Reads live connections from the engine's stats API (the Clash-compatible one built
/// into sing-box), which config.json enables on a loopback port with a secret.
/// </summary>
public static class ConnectionsService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(3) };

    /// <summary>Null when the engine is not running or the API is unreachable.</summary>
    public static async Task<ConnSnapshot?> FetchAsync()
    {
        AppSettings s;
        try { s = SettingsService.Load(); } catch { return null; }
        if (s.ControllerPort == 0 || string.IsNullOrEmpty(s.ControllerSecret)) return null;

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get,
                $"http://127.0.0.1:{s.ControllerPort}/connections");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", s.ControllerSecret);
            using var resp = await Http.SendAsync(req);
            if (!resp.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStreamAsync());
            var root = doc.RootElement;
            var snap = new ConnSnapshot
            {
                UploadTotal = Long(root, "uploadTotal"),
                DownloadTotal = Long(root, "downloadTotal"),
            };

            if (root.TryGetProperty("connections", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var c in list.EnumerateArray())
                {
                    var m = c.TryGetProperty("metadata", out var md) ? md : default;
                    var host = Str(m, "host");
                    var ip = Str(m, "destinationIP");
                    var port = Str(m, "destinationPort");
                    var target = host.Length > 0 ? host : ip;

                    var outbound = "";
                    if (c.TryGetProperty("chains", out var chains) && chains.ValueKind == JsonValueKind.Array)
                        foreach (var ch in chains.EnumerateArray())
                        {
                            outbound = ch.GetString() ?? "";
                            break;   // the first link is the outbound the rule picked
                        }

                    var path = Str(m, "processPath");
                    snap.Connections.Add(new ConnInfo
                    {
                        Id = Str(c, "id"),
                        Host = port.Length > 0 && port != "0" ? $"{target}:{port}" : target,
                        Target = target,
                        TargetIsIp = host.Length == 0,
                        ProcessPath = path,
                        Outbound = outbound,
                        Rule = Str(c, "rule"),
                        Process = System.IO.Path.GetFileName(path),
                        Network = Str(m, "network"),
                        Upload = Long(c, "upload"),
                        Download = Long(c, "download"),
                        Start = DateTime.TryParse(Str(c, "start"), out var t) ? t : DateTime.Now,
                    });
                }
            }
            return snap;
        }
        catch { return null; }
    }

    /// <summary>Human label for an outbound tag.</summary>
    public static string RouteLabel(string outbound) => outbound switch
    {
        "warp-out" => "WARP",
        "geo-out" => "geo",
        "direct-out" => "напрямую",
        "" => "—",
        _ => outbound,
    };

    private static string Str(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind == JsonValueKind.String ? v.GetString() ?? ""
            : v.ValueKind == JsonValueKind.Number ? v.GetRawText() : ""
            : "";

    private static long Long(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
           && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0;
}
