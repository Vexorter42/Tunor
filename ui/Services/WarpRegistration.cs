using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Tunor.Services;

public sealed class WarpRegistrationException : Exception
{
    public WarpRegistrationException(string message) : base(message) { }
}

/// <summary>What Cloudflare handed out for a new device.</summary>
public sealed class WarpAccount
{
    public string PrivateKey { get; init; } = "";
    public string PeerPublicKey { get; init; } = "";
    public string Endpoint { get; init; } = "";
    public string AddressV4 { get; init; } = "";
    public string AddressV6 { get; init; } = "";
    /// <summary>Which way the request got there: "напрямую", "через WARP"...</summary>
    public string Way { get; init; } = "";
}

/// <summary>
/// A WARP config made here, without a bot: a new device is registered with Cloudflare the way its own app
/// (1.1.1.1) does it, and the answer is written out as a WireGuard (AmneziaWG) config.
///
/// What it takes from the user: the Cloudflare terms of service are accepted on their behalf in the request —
/// so the window that starts this asks first, and says so. What it does not do: keep anything. The private key
/// goes into warp.conf and nowhere else, and the account token that Cloudflare returns is dropped.
///
/// api.cloudflareclient.com is not reachable from every network (from some it is cut off at the TLS
/// handshake), so the request goes out directly first and then through each tunnel that is already up.
/// </summary>
public static class WarpRegistration
{
    internal const string Api = "https://api.cloudflareclient.com";
    // The client the API expects to hear from; it changes with the app, and so does this.
    internal const string Version = "v0a5641";
    internal const string ClientVersion = "a-6.38.9-5641";
    internal const string UserAgent = "1.1.1.1/6.38.9-5641 (Android 16.0.0)";
    public const string TermsUrl = "https://www.cloudflare.com/application/terms/";

    /// <summary>
    /// The window that makes a config here is shown only when TUNOR_EXPERIMENTAL=1 is set or a file experimental.flag lies in the program's folder: it talks to Cloudflare's
    /// registration, which cannot be tried against from where the program is written, so a build for trying it
    /// carries the switch, and the release does not until someone has seen it work.
    /// </summary>
    public static bool Available =>
        Environment.GetEnvironmentVariable("TUNOR_EXPERIMENTAL") == "1" || System.IO.File.Exists(System.IO.Path.Combine(Paths.AppRoot, "experimental.flag"));

    // ------------------------------------------------------------------ the keys

    /// <summary>A WireGuard private key: 32 random bytes, clamped, in the form WireGuard stores them.</summary>
    public static byte[] NewPrivateKey()
    {
        var k = RandomNumberGenerator.GetBytes(32);
        k[0] &= 248; k[31] &= 127; k[31] |= 64;
        return k;
    }

    public static string PublicKey(string privateKeyBase64) => Convert.ToBase64String(X25519(Convert.FromBase64String(privateKeyBase64), Basepoint));

    private static readonly byte[] Basepoint = Enumerable.Range(0, 32).Select(i => (byte)(i == 0 ? 9 : 0)).ToArray();

    /// <summary>
    /// Curve25519 as RFC 7748 writes it. The framework has no X25519, and the key is made once, on the
    /// person's own computer, so a plain big-integer ladder is enough — it is checked against the RFC's vectors.
    /// </summary>
    internal static byte[] X25519(byte[] scalar, byte[] u)
    {
        if (scalar.Length != 32 || u.Length != 32) throw new ArgumentException("ключ должен быть из 32 байт");
        var p = BigInteger.Pow(2, 255) - 19;
        var a24 = new BigInteger(121665);

        var k = (byte[])scalar.Clone();
        k[0] &= 248; k[31] &= 127; k[31] |= 64;
        var kn = new BigInteger(k, isUnsigned: true, isBigEndian: false);

        var ub = (byte[])u.Clone();
        ub[31] &= 127;
        var x1 = new BigInteger(ub, isUnsigned: true, isBigEndian: false) % p;

        BigInteger x2 = 1, z2 = 0, x3 = x1, z3 = 1;
        var swap = 0;
        for (var t = 254; t >= 0; t--)
        {
            var kt = (int)((kn >> t) & 1);
            swap ^= kt;
            if (swap == 1) { (x2, x3) = (x3, x2); (z2, z3) = (z3, z2); }
            swap = kt;

            var a = (x2 + z2) % p;
            var aa = a * a % p;
            var b = (x2 - z2 + p) % p;
            var bb = b * b % p;
            var e = (aa - bb + p) % p;
            var c = (x3 + z3) % p;
            var d = (x3 - z3 + p) % p;
            var da = d * a % p;
            var cb = c * b % p;
            x3 = BigInteger.ModPow((da + cb) % p, 2, p);
            z3 = x1 * BigInteger.ModPow((da - cb + p) % p, 2, p) % p;
            x2 = aa * bb % p;
            z2 = e * ((aa + a24 * e) % p) % p;
        }
        if (swap == 1) { (x2, x3) = (x3, x2); (z2, z3) = (z3, z2); }

        var result = x2 * BigInteger.ModPow(z2, p - 2, p) % p;
        var bytes = result.ToByteArray(isUnsigned: true, isBigEndian: false);
        var padded = new byte[32];
        Buffer.BlockCopy(bytes, 0, padded, 0, Math.Min(32, bytes.Length));
        return padded;
    }

    // ------------------------------------------------------------------ the request

    /// <summary>What goes to Cloudflare: the public key and the same fields its Android app sends, with the terms accepted as of now.</summary>
    internal static string RequestBody(string publicKey, DateTimeOffset now)
        => new JsonObject
        {
            ["fcm_token"] = "",
            ["install_id"] = "",
            ["key"] = publicKey,
            ["locale"] = "en_US",
            ["model"] = "PC",
            ["tos"] = now.ToString("yyyy-MM-dd'T'HH:mm:ss.fffK"),
            ["serial_number"] = "",
            ["os_version"] = "16.0.0",
            ["key_type"] = "curve25519",
            ["tunnel_type"] = "wireguard",
        }.ToJsonString();

    /// <summary>The client for one way out: straight, or through the local proxy port of a tunnel that is up. A test puts its own.</summary>
    internal static Func<int?, HttpMessageHandler> Handler { get; set; } = port =>
        port is { } p
            ? new HttpClientHandler { Proxy = new WebProxy($"socks5://127.0.0.1:{p}"), UseProxy = true }
            : new HttpClientHandler { UseProxy = false };

    internal static TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// Registers a new device. <paramref name="ways"/> are tried in order — (name, null) is straight out, (name, port)
    /// is through the proxy on that port; the first that gets an answer wins, the failures are told together if none does.
    /// </summary>
    public static async Task<WarpAccount> RegisterAsync(IEnumerable<(string Name, int? Port)> ways, CancellationToken ct = default)
    {
        var failures = new List<string>();
        foreach (var (name, port) in ways)
        {
            try
            {
                return await RegisterVia(name, port, ct);
            }
            catch (WarpRegistrationException ex)
            {
                // an answer that is a "no" can come from the way out and not from Cloudflare (a throttled exit, a block page)
                failures.Add($"{name}: {ex.Message}");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or System.IO.IOException)
            {
                if (ct.IsCancellationRequested) throw;
                failures.Add($"{name}: " + RouteProbe.Explain(ex, Timeout));
            }
        }
        throw new WarpRegistrationException(failures.Count == 0
            ? "Нет ни одного пути, по которому можно достучаться до Cloudflare."
            : "До Cloudflare не достучаться. " + string.Join("; ", failures) + ".");
    }

    private static async Task<WarpAccount> RegisterVia(string way, int? port, CancellationToken ct)
    {
        var privateKey = Convert.ToBase64String(NewPrivateKey());
        var body = RequestBody(PublicKey(privateKey), DateTimeOffset.Now);

        using var http = new HttpClient(Handler(port), disposeHandler: true) { Timeout = Timeout };
        using var post = new HttpRequestMessage(HttpMethod.Post, $"{Api}/{Version}/reg") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        Headers(post);
        using var response = await http.SendAsync(post, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new WarpRegistrationException($"Cloudflare ответил {(int)response.StatusCode}: {Excerpt(text)}");

        var account = Parse(text, privateKey, way);
        if (account != null) return account;

        // The answer to the registration does not always carry the config; the device's own record does.
        var root = JsonNode.Parse(text) as JsonObject;
        var id = root?["id"]?.GetValue<string>();
        var token = root?["token"]?.GetValue<string>();
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(token))
            throw new WarpRegistrationException("Cloudflare ответил, но в ответе нет данных устройства: " + Excerpt(text));

        using var get = new HttpRequestMessage(HttpMethod.Get, $"{Api}/{Version}/reg/{Uri.EscapeDataString(id)}");
        Headers(get);
        get.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
        using var second = await http.SendAsync(get, ct);
        var secondText = await second.Content.ReadAsStringAsync(ct);
        if (!second.IsSuccessStatusCode)
            throw new WarpRegistrationException($"Cloudflare ответил {(int)second.StatusCode} на запрос настроек: {Excerpt(secondText)}");
        return Parse(secondText, privateKey, way)
               ?? throw new WarpRegistrationException("Cloudflare не выдал настройки туннеля: " + Excerpt(secondText));
    }

    private static void Headers(HttpRequestMessage r)
    {
        r.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        r.Headers.TryAddWithoutValidation("CF-Client-Version", ClientVersion);
    }

    /// <summary>A few words of what the server said, with nothing in them that could break a line or carry a token.</summary>
    internal static string Excerpt(string text)
    {
        var one = Regex.Replace(text ?? "", @"\s+", " ").Trim();
        one = Regex.Replace(one, @"""token""\s*:\s*""[^""]*""", "\"token\":\"…\"");
        return one.Length > 160 ? one[..160] + "…" : one;
    }

    /// <summary>The peer, the endpoint and the addresses out of Cloudflare's answer; null if they are not there.</summary>
    internal static WarpAccount? Parse(string json, string privateKey, string way)
    {
        JsonObject? root;
        try { root = JsonNode.Parse(json) as JsonObject; }
        catch (JsonException) { throw new WarpRegistrationException("Cloudflare прислал не JSON: " + Excerpt(json)); }

        var config = root?["config"] as JsonObject;
        var peer = (config?["peers"] as JsonArray)?.FirstOrDefault() as JsonObject;
        var addresses = (config?["interface"] as JsonObject)?["addresses"] as JsonObject;
        var key = peer?["public_key"]?.GetValue<string>();
        var v4 = addresses?["v4"]?.GetValue<string>();
        if (peer == null || string.IsNullOrEmpty(key) || string.IsNullOrEmpty(v4)) return null;

        var endpoint = peer["endpoint"] as JsonObject;
        var host = endpoint?["host"]?.GetValue<string>();
        if (string.IsNullOrEmpty(host))
        {
            var ip = endpoint?["v4"]?.GetValue<string>() ?? "";
            var colon = ip.LastIndexOf(':');
            if (colon > 0) ip = ip[..colon];
            var port = (endpoint?["ports"] as JsonArray)?.FirstOrDefault()?.GetValue<int>() ?? 2408;
            host = ip.Length > 0 ? $"{ip}:{port}" : "engage.cloudflareclient.com:2408";
        }
        if (!WarpEndpoint.IsValid(host)) throw new WarpRegistrationException("Cloudflare назвал адрес, который не похож на адрес: " + Excerpt(host));
        if (!Regex.IsMatch(key, @"^[A-Za-z0-9+/]{43}=$")) throw new WarpRegistrationException("Cloudflare прислал ключ не того вида.");

        return new WarpAccount
        {
            PrivateKey = privateKey,
            PeerPublicKey = key,
            Endpoint = host,
            AddressV4 = StripMask(v4),
            AddressV6 = StripMask(addresses?["v6"]?.GetValue<string>() ?? ""),
            Way = way,
        };
    }

    private static string StripMask(string address) => address.Contains('/') ? address[..address.IndexOf('/')] : address;

    // ------------------------------------------------------------------ the config

    /// <summary>
    /// The obfuscation lines a config can carry (AmneziaWG), as they are in <paramref name="current"/> if it has them,
    /// else the ones that do no harm against Cloudflare: a few junk packets before the handshake.
    /// </summary>
    internal static List<string> AwgLines(string? current)
    {
        var keep = new[] { "Jc", "Jmin", "Jmax", "S1", "S2", "S3", "S4", "H1", "H2", "H3", "H4", "I1", "I2", "I3", "I4", "I5" };
        var found = new List<string>();
        if (current != null)
        {
            var inInterface = false;
            foreach (var raw in current.Split('\n'))
            {
                var line = raw.Trim();
                if (line.StartsWith('[')) { inInterface = line.Equals("[Interface]", StringComparison.OrdinalIgnoreCase); continue; }
                if (!inInterface || line.Length == 0) continue;
                var eq = line.IndexOf('=');
                if (eq <= 0) continue;
                var name = line[..eq].Trim();
                var value = line[(eq + 1)..].Trim();
                if (keep.Contains(name, StringComparer.OrdinalIgnoreCase) && value.Length > 0 && !value.Contains('\r'))
                    found.Add($"{name} = {value}");
            }
        }
        return found.Count > 0
            ? found
            : new List<string> { "S1 = 0", "S2 = 0", "S3 = 0", "S4 = 0", "Jc = 4", "Jmin = 40", "Jmax = 70", "H1 = 1", "H2 = 2", "H3 = 3", "H4 = 4" };
    }

    /// <summary>The config in the form the program keeps its WARP in: what the registration gave, and the obfuscation if wanted.</summary>
    public static string BuildConf(WarpAccount a, bool awg, string? previousConf = null, string? endpoint = null)
    {
        var sb = new StringBuilder();
        sb.Append("[Interface]\n");
        sb.Append("PrivateKey = ").Append(a.PrivateKey).Append('\n');
        sb.Append("Address = ").Append(a.AddressV4);
        if (a.AddressV6.Length > 0) sb.Append(", ").Append(a.AddressV6);
        sb.Append('\n');
        sb.Append("DNS = 1.1.1.1, 1.0.0.1, 2606:4700:4700::1111, 2606:4700:4700::1001\n");
        sb.Append("MTU = 1280\n");
        if (awg) foreach (var line in AwgLines(previousConf)) sb.Append(line).Append('\n');
        sb.Append("\n[Peer]\n");
        sb.Append("PublicKey = ").Append(a.PeerPublicKey).Append('\n');
        sb.Append("AllowedIPs = 0.0.0.0/0, ::/0\n");
        sb.Append("Endpoint = ").Append(endpoint is { Length: > 0 } && WarpEndpoint.IsValid(endpoint) ? endpoint : a.Endpoint).Append('\n');
        return sb.ToString();
    }
}
