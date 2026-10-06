using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Web;

namespace Tunor.Services;

/// <summary>
/// Turns a share link into a sing-box outbound.
///
/// `vless://uuid@host:port?query#name` is what every panel hands out, but the query is
/// Xray's, not sing-box's, and services differ in what they put there. Everything this
/// understands is listed in <see cref="Parse"/>; anything else is reported in plain words
/// rather than written into the config, because one outbound the engine cannot parse
/// stops the whole file — and with it every other tunnel.
/// </summary>
public static class ProxyLink
{
    /// <summary>What a link turned out to be. Outbound is null when it could not be read.</summary>
    public sealed record Parsed(JsonObject? Outbound, string Name, string Summary, string? Problem)
    {
        public bool Ok => Outbound != null;
    }

    private static Parsed Bad(string why) => new(null, "", "", why);

    public static bool LooksLikeLink(string text) =>
        text.TrimStart().StartsWith("vless://", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Reads one `vless://` link. The tag is filled in later, when the tunnel gets its id.
    ///
    /// Understood: TLS and Reality (`pbk`, `sid`), uTLS fingerprints, Vision flow, VLESS
    /// Encryption, and the tcp, ws, grpc, http, httpupgrade, quic and xhttp transports.
    /// </summary>
    public static Parsed Parse(string link)
    {
        link = link.Trim();
        if (!LooksLikeLink(link)) return Bad("ссылка должна начинаться с vless://");

        Uri uri;
        try { uri = new Uri(link); }
        catch (Exception ex) { return Bad("ссылку не разобрать: " + ex.Message); }

        var uuid = Uri.UnescapeDataString(uri.UserInfo ?? "");
        if (uuid.Length == 0) return Bad("в ссылке нет идентификатора (часть до @)");
        if (!Guid.TryParse(uuid, out _)) return Bad($"идентификатор не похож на UUID: «{Short(uuid)}»");

        var host = uri.IdnHost;
        if (host.Length == 0) return Bad("в ссылке нет адреса сервера");
        var port = uri.Port;
        if (port <= 0 || port > 65535) return Bad("в ссылке нет порта сервера или он неправильный");

        var q = HttpUtility.ParseQueryString(uri.Query);
        string Get(string key) => (q[key] ?? "").Trim();

        var name = Uri.UnescapeDataString(uri.Fragment.TrimStart('#'));
        if (name.Length == 0) name = host;

        var o = new JsonObject
        {
            ["type"] = "vless",
            ["server"] = host,
            ["server_port"] = port,
            ["uuid"] = uuid,
        };

        // Xray writes encryption=none for plain VLESS; anything else is VLESS Encryption,
        // which this engine supports and must be passed through untouched.
        var enc = Get("encryption");
        if (enc.Length > 0 && !enc.Equals("none", StringComparison.OrdinalIgnoreCase)) o["encryption"] = enc;

        var flow = Get("flow");
        if (flow.Length > 0)
        {
            if (!flow.Equals("xtls-rprx-vision", StringComparison.OrdinalIgnoreCase))
                return Bad($"поток «{Short(flow)}» этот движок не поддерживает; работает только xtls-rprx-vision");
            o["flow"] = flow;
        }

        var security = Get("security").ToLowerInvariant();
        if (security.Length == 0) security = Get("pbk").Length > 0 ? "reality" : "none";

        var parts = new List<string>();
        if (security is "tls" or "reality" or "xtls")
        {
            var tls = new JsonObject { ["enabled"] = true };
            var sni = Get("sni");
            if (sni.Length == 0) sni = Get("peer");
            if (sni.Length == 0) sni = Get("host");
            if (sni.Length > 0) tls["server_name"] = sni;

            var alpn = Get("alpn");
            if (alpn.Length > 0)
            {
                var a = new JsonArray();
                foreach (var v in alpn.Split(',', StringSplitOptions.RemoveEmptyEntries)) a.Add(v.Trim());
                if (a.Count > 0) tls["alpn"] = a;
            }

            var fp = Get("fp");
            if (fp.Length > 0) tls["utls"] = new JsonObject { ["enabled"] = true, ["fingerprint"] = fp };

            if (security == "reality")
            {
                var pbk = Get("pbk");
                if (pbk.Length == 0) return Bad("для Reality нужен публичный ключ (pbk), в ссылке его нет");
                var reality = new JsonObject { ["enabled"] = true, ["public_key"] = pbk };
                var sid = Get("sid");
                if (sid.Length > 0) reality["short_id"] = sid;
                tls["reality"] = reality;
                // Reality pins the certificate itself; uTLS is what makes it look ordinary.
                tls["utls"] ??= new JsonObject { ["enabled"] = true, ["fingerprint"] = "chrome" };
                parts.Add("Reality");
            }
            else parts.Add("TLS");

            var insecure = Get("allowInsecure");
            if (insecure.Length == 0) insecure = Get("insecure");
            if (insecure is "1" or "true") tls["insecure"] = true;

            o["tls"] = tls;
        }
        else if (security is not ("" or "none")) return Bad($"защита «{Short(security)}» не поддерживается");

        var type = Get("type").ToLowerInvariant();
        if (type.Length == 0) type = "tcp";
        switch (type)
        {
            case "tcp":
            case "raw":
                break;                                   // no transport section at all
            case "ws":
            case "websocket":
            {
                var tr = new JsonObject { ["type"] = "ws" };
                var path = Get("path");
                if (path.Length > 0) tr["path"] = Uri.UnescapeDataString(path);
                var wsHost = Get("host");
                if (wsHost.Length > 0) tr["headers"] = new JsonObject { ["Host"] = wsHost };
                o["transport"] = tr;
                break;
            }
            case "httpupgrade":
            {
                var tr = new JsonObject { ["type"] = "httpupgrade" };
                var path = Get("path");
                if (path.Length > 0) tr["path"] = Uri.UnescapeDataString(path);
                var huHost = Get("host");
                if (huHost.Length > 0) tr["host"] = huHost;
                o["transport"] = tr;
                break;
            }
            case "grpc":
            {
                var tr = new JsonObject { ["type"] = "grpc" };
                var svc = Get("serviceName");
                if (svc.Length > 0) tr["service_name"] = Uri.UnescapeDataString(svc);
                o["transport"] = tr;
                break;
            }
            case "http":
            case "h2":
            {
                var tr = new JsonObject { ["type"] = "http" };
                var path = Get("path");
                if (path.Length > 0) tr["path"] = Uri.UnescapeDataString(path);
                var hHost = Get("host");
                if (hHost.Length > 0)
                {
                    var hosts = new JsonArray();
                    foreach (var h in hHost.Split(',', StringSplitOptions.RemoveEmptyEntries)) hosts.Add(h.Trim());
                    tr["host"] = hosts;
                }
                o["transport"] = tr;
                break;
            }
            case "quic":
                o["transport"] = new JsonObject { ["type"] = "quic" };
                break;
            case "xhttp":
            case "splithttp":
            {
                var tr = new JsonObject { ["type"] = "xhttp" };
                var path = Get("path");
                if (path.Length > 0) tr["path"] = Uri.UnescapeDataString(path);
                var xHost = Get("host");
                if (xHost.Length > 0) tr["host"] = xHost;
                var mode = Get("mode");
                if (mode.Length > 0 && !mode.Equals("auto", StringComparison.OrdinalIgnoreCase)) tr["mode"] = mode;
                o["transport"] = tr;
                break;
            }
            default:
                return Bad($"транспорт «{Short(type)}» не поддерживается");
        }

        parts.Insert(0, type == "raw" ? "tcp" : type);
        if (flow.Length > 0) parts.Add("Vision");
        if (o.ContainsKey("encryption")) parts.Add("шифрование VLESS");

        return new Parsed(o, name, $"{host}:{port} · {string.Join(" · ", parts)}", null);
    }

    private static string Short(string s) => s.Length <= 24 ? s : s[..24] + "…";
}
