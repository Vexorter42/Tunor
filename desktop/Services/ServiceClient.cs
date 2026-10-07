using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Tunor.Services;

namespace Tunor.Desktop.Services;

/// <summary>
/// Talks to the engine's privileged service, so the tunnel can be switched on and off
/// without a password every time.
///
/// The service holds the rights for TUN permanently; what was missing was any way for
/// this app to tell it what to do. It listens on loopback behind mutual TLS: the daemon
/// is its own authority, the app pins it by the fingerprint printed when the service was
/// installed, generates a certificate of its own and registers it once with a one-time
/// code. After that, start and stop are two REST calls and nothing asks for a password.
///
/// The pairing details come from the engine's own specification (SPECS/FEATURES/014 and
/// TASKS/057): the invite is "address#fingerprint#code", enrolment is a POST of that code
/// with the certificate, and the core's life is separate from its config.
/// </summary>
public static class ServiceClient
{
    private const string ClientName = "Tunor";

    private static string KeyFile => Path.Combine(Paths.DataDir, "service-client.pfx");
    private static string PinFile => Path.Combine(Paths.DataDir, "service-pin.json");

    /// <summary>Where the daemon is and what it must prove to be, as recorded at pairing.</summary>
    private sealed record Pin(string Address, string Fingerprint);

    public static bool Paired => File.Exists(KeyFile) && File.Exists(PinFile);

    // ---------------------------------------------------------------- pairing

    /// <summary>
    /// Reads the invite the installer wrote and registers this app with the daemon.
    /// The invite is consumed on success: its code is one-time, so a file left lying
    /// around is only a stale secret.
    /// </summary>
    public static async Task<(bool Ok, string Message)> PairAsync(string invitePath)
    {
        string invite;
        try { invite = File.ReadAllText(invitePath).Trim(); }
        catch (Exception ex) { return (false, "приглашение не прочиталось: " + ex.Message); }

        var parts = invite.Split('#');
        if (parts.Length != 3)
            return (false, "приглашение непонятного вида — ожидалось «адрес#отпечаток#код»");
        var (address, fingerprint, code) = (parts[0].Trim(), parts[1].Trim(), parts[2].Trim());

        try
        {
            var cert = MakeCertificate();
            using var http = Http(fingerprint, null);
            var resp = await http.PostAsJsonAsync($"https://{address}/admin/enroll", new
            {
                code,
                name = ClientName,
                cert_pem = Pem(cert),
            });

            var text = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode) return (false, Error(text, resp.StatusCode.ToString()));

            Directory.CreateDirectory(Paths.DataDir);
            // The private key never leaves this file, which is the user's own.
            File.WriteAllBytes(KeyFile, cert.Export(X509ContentType.Pfx));
            File.WriteAllText(PinFile, JsonSerializer.Serialize(new Pin(address, fingerprint)));
            try { File.Delete(invitePath); } catch { }
            return (true, "приложение сопряжено со службой");
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    public static void Forget()
    {
        foreach (var f in new[] { KeyFile, PinFile })
            try { File.Delete(f); } catch { }
    }

    // ---------------------------------------------------------------- control

    /// <summary>
    /// Hands the daemon the config and brings the core up. Both steps are needed: the
    /// daemon keeps its own copy of the config, separate from the file on disk, and
    /// starting without giving it one answers "no config to start from".
    /// </summary>
    public static async Task<(bool Ok, string Message)> StartAsync()
    {
        string config;
        try { config = Absolutise(File.ReadAllText(Paths.ConfigJson)); }
        catch (Exception ex) { return (false, "конфиг не прочитался: " + ex.Message); }

        var applied = await Send(HttpMethod.Post, "apply", config);
        if (applied == null) return (false, "служба не отвечает");
        if (!applied.Value.Ok) return (false, Error(applied.Value.Body, "служба не приняла конфиг"));

        return await Post("start");
    }

    public static Task<(bool Ok, string Message)> StopAsync() => Post("stop");

    /// <summary>
    /// What the daemon says the core is doing: idle, started or fatal. Null when the
    /// service cannot be reached at all — not paired, not running, gone.
    /// </summary>
    public static async Task<string?> StatusAsync()
    {
        var sent = await Send(HttpMethod.Get, "status");
        if (sent is not { Ok: true }) return null;
        try
        {
            using var doc = JsonDocument.Parse(sent.Value.Body);
            return doc.RootElement.TryGetProperty("status", out var s) ? s.GetString() : null;
        }
        catch { return null; }
    }

    public static async Task<bool> CoreRunning() => await StatusAsync() == "started";

    private static async Task<(bool Ok, string Message)> Post(string route)
    {
        var sent = await Send(HttpMethod.Post, route);
        if (sent == null) return (false, "служба не отвечает");
        return sent.Value.Ok ? (true, "готово") : (false, Error(sent.Value.Body, "служба отказала"));
    }

    private static async Task<(bool Ok, string Body)?> Send(HttpMethod method, string route,
                                                           string? body = null)
    {
        if (!Paired) return null;
        try
        {
            var pin = JsonSerializer.Deserialize<Pin>(File.ReadAllText(PinFile));
            if (pin == null) return null;
            var cert = new X509Certificate2(File.ReadAllBytes(KeyFile));

            using var http = Http(pin.Fingerprint, cert);
            using var req = new HttpRequestMessage(method, $"https://{pin.Address}/admin/{route}");
            // apply takes the config as the body itself, not wrapped in anything.
            if (body != null)
                req.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
            var resp = await http.SendAsync(req);
            return (resp.IsSuccessStatusCode, await resp.Content.ReadAsStringAsync());
        }
        catch { return null; }
    }

    /// <summary>
    /// Rule-set paths in the config are written relative to the engine's own working
    /// directory, build/. The daemon runs from its own state directory and would look for
    /// them somewhere else entirely, so they are made absolute on the way out. The file
    /// on disk is left as it is: it still has to work for an engine started normally.
    /// </summary>
    private static string Absolutise(string json)
    {
        try
        {
            var root = JsonNode.Parse(json)?.AsObject();
            if (root == null) return json;

            foreach (var set in root["route"]?["rule_set"]?.AsArray() ?? new JsonArray())
            {
                var path = set?["path"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path)) continue;
                set!["path"] = Rooted(path);
            }

            // The cache file too, and for the same reason: launchd starts the daemon from
            // "/", which is read-only on macOS, so a relative cache path is where the
            // engine stopped before it had even opened a socket.
            var cache = root["experimental"]?["cache_file"];
            var at = cache?["path"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(at) && !Path.IsPathRooted(at))
                cache!["path"] = Rooted(at);

            return root.ToJsonString();
        }
        catch { return json; }      // unreadable config is the daemon's to complain about
    }

    /// <summary>A path the config gives relative to build/, as the daemon must see it.</summary>
    private static string Rooted(string relative) =>
        Path.GetFullPath(Path.Combine(Paths.BuildDir, relative));

    // ---------------------------------------------------------------- plumbing

    /// <summary>
    /// A client that trusts exactly one server — the one whose certificate hashes to the
    /// fingerprint from the invite. The daemon signs its own certificate, so the usual
    /// chain of authorities says nothing about it; the fingerprint is the whole trust.
    /// </summary>
    private static HttpClient Http(string fingerprint, X509Certificate2? client)
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, cert, _, _) =>
                cert != null && Fingerprint(cert).Equals(fingerprint.Replace(":", ""),
                    StringComparison.OrdinalIgnoreCase),
        };
        if (client != null) handler.ClientCertificates.Add(client);
        return new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
    }

    private static string Fingerprint(X509Certificate2 cert) =>
        Convert.ToHexString(SHA256.HashData(cert.RawData));

    /// <summary>This app's own certificate. Self-signed: the daemon pins it, nothing else
    /// has to believe it.</summary>
    private static X509Certificate2 MakeCertificate()
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest($"CN={ClientName}", rsa,
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(
            new X509EnhancedKeyUsageExtension(
                new OidCollection { new("1.3.6.1.5.5.7.3.2") }, true));   // client auth
        return req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1),
                                    DateTimeOffset.UtcNow.AddYears(10));
    }

    private static string Pem(X509Certificate2 cert) =>
        "-----BEGIN CERTIFICATE-----\n"
        + Convert.ToBase64String(cert.RawData, Base64FormattingOptions.InsertLineBreaks)
        + "\n-----END CERTIFICATE-----\n";

    /// <summary>The daemon answers failures as {"error": "..."}; anything else is shown raw.</summary>
    private static string Error(string body, string fallback)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var e))
                return e.GetString() ?? fallback;
        }
        catch { }
        return string.IsNullOrWhiteSpace(body) ? fallback : body.Trim();
    }
}
