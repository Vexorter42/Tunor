using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Nyx.Services;

public enum ConfKind { Warp, Geo }

public class ConfDetection
{
    public ConfKind Kind { get; init; }
    public bool Confident { get; init; }
    /// <summary>Why we think so — shown to the user.</summary>
    public string Reason { get; init; } = "";
    public string Endpoint { get; init; } = "";
    public string Address { get; init; } = "";
    public bool HasAwg { get; init; }
    public string AwgVersions { get; init; } = "";
}

/// <summary>
/// Recognises a dropped WireGuard/AmneziaWG .conf and installs it as warp.conf
/// or geo.conf, so the user never has to rename "WARPw212412.conf" by hand.
/// </summary>
public static class ConfImporter
{
    /// <summary>Cloudflare WARP's well-known peer public key.</summary>
    private const string WarpPublicKey = "bmXOC+F1FxEMF9dyiK2H5/1SUtzH0JuVo51h2wPfgyo=";

    public static bool LooksLikeConf(string path)
    {
        if (!File.Exists(path)) return false;
        if (!path.EndsWith(".conf", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            var text = File.ReadAllText(path);
            return text.Contains("[Interface]", StringComparison.OrdinalIgnoreCase)
                && text.Contains("PrivateKey", StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public static ConfDetection Detect(string path)
    {
        var (iface, peer) = Parse(path);
        var name = Path.GetFileNameWithoutExtension(path);
        var endpoint = peer.GetValueOrDefault("Endpoint", "");
        var address = iface.GetValueOrDefault("Address", "");
        var pub = peer.GetValueOrDefault("PublicKey", "");

        var awg = new List<string>();
        if (iface.Keys.Any(k => k is "Jc" or "Jmin" or "Jmax" || (k.Length == 2 && (k[0] is 'S' or 'H') && char.IsDigit(k[1]))))
            awg.Add("1.0");
        if (iface.Keys.Any(k => k.Length == 2 && k[0] == 'I' && char.IsDigit(k[1]))) awg.Add("2.0");
        if (iface.ContainsKey("ContentPaddingAddition") || iface.ContainsKey("RekeyAfterTime") ||
            iface.ContainsKey("RejectAfterTime") || iface.ContainsKey("MaxHandshakeAttempts")) awg.Add("3.0");
        if (iface.ContainsKey("RandomTrailers") || iface.ContainsKey("DisableCookies")) awg.Add("3.1");

        ConfKind kind;
        bool confident;
        string reason;

        if (string.Equals(pub, WarpPublicKey, StringComparison.Ordinal))
        {
            kind = ConfKind.Warp; confident = true;
            reason = "публичный ключ Cloudflare WARP";
        }
        else if (endpoint.Contains("cloudflareclient.com", StringComparison.OrdinalIgnoreCase))
        {
            kind = ConfKind.Warp; confident = true;
            reason = "эндпоинт engage.cloudflareclient.com";
        }
        else if (IsWarpIp(endpoint))
        {
            kind = ConfKind.Warp; confident = true;
            reason = "IP эндпоинта из диапазона Cloudflare WARP";
        }
        else if (address.StartsWith("172.16.0.2", StringComparison.Ordinal))
        {
            kind = ConfKind.Warp; confident = false;
            reason = "адрес 172.16.0.2 — типичный для WARP";
        }
        else if (name.Contains("warp", StringComparison.OrdinalIgnoreCase))
        {
            kind = ConfKind.Warp; confident = false;
            reason = "в имени файла есть «warp»";
        }
        else
        {
            kind = ConfKind.Geo; confident = false;
            reason = "не похоже на WARP — сторонний WireGuard-сервер";
        }

        return new ConfDetection
        {
            Kind = kind,
            Confident = confident,
            Reason = reason,
            Endpoint = endpoint,
            Address = address,
            HasAwg = awg.Count > 0,
            AwgVersions = awg.Count > 0 ? string.Join(" + ", awg) : "нет (обычный WireGuard)",
        };
    }

    /// <summary>Copies the file over warp.conf / geo.conf (keeping a .bak) and rebuilds config.json.</summary>
    public static void Apply(string path, ConfKind kind)
    {
        var target = kind == ConfKind.Warp ? Paths.WarpConf : Paths.GeoConf;

        if (File.Exists(target))
        {
            try { File.Copy(target, target + ".bak", overwrite: true); } catch { }
        }

        var text = File.ReadAllText(path);
        File.WriteAllText(target, text);

        ConfigGenerator.Generate();
    }

    private static bool IsWarpIp(string endpoint)
    {
        var host = endpoint.Contains(':') ? endpoint[..endpoint.LastIndexOf(':')] : endpoint;
        // Cloudflare WARP anycast ranges commonly handed out by generators.
        return host.StartsWith("162.159.19", StringComparison.Ordinal)
            || host.StartsWith("188.114.9", StringComparison.Ordinal)
            || host.StartsWith("162.159.20", StringComparison.Ordinal);
    }

    // The generator's parser: it strips the invisible characters and quotes that configs
    // pick up when copied from a messenger. With a separate, plainer parser here, a WARP
    // config whose peer key had picked up quotes would not be recognised as WARP.
    private static (Dictionary<string, string> iface, Dictionary<string, string> peer) Parse(string path)
        => ConfigGenerator.ParseConf(path);
}
