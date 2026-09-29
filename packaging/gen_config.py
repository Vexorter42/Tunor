#!/usr/bin/env python3
"""
Build a sing-box-lx config.json from Tunor data files (no SSnetCli).
WARP endpoint is `type: wireguard` with AmneziaWG fields (1.0 / 2.0 / 3.x).

Usage:
  gen_config.py --root <dir> --out <config.json> [--test]
  gen_config.py --out <f> --settings <f> --rules <f> --warp <f> --geo <f> [--test]
"""
import sys, os, json, argparse


def read_conf(path):
    iface, peer, section = {}, {}, None
    if not os.path.exists(path):
        return iface, peer
    for line in open(path, encoding="utf-8").read().splitlines():
        line = line.strip()
        if not line or line.startswith("#"):
            continue
        if line.startswith("["):
            section = line.strip("[]").lower()
            continue
        if "=" not in line:
            continue
        k, v = line.split("=", 1)
        (iface if section == "interface" else peer)[k.strip()] = v.strip()
    return iface, peer


def wg_endpoint(tag, path, detour=None):
    iface, peer = read_conf(path)
    addrs = []
    for a in [x.strip() for x in iface.get("Address", "").split(",") if x.strip()]:
        if "/" not in a:
            a += "/128" if ":" in a else "/32"
        addrs.append(a)

    ep = {"type": "wireguard", "tag": tag}
    if "MTU" in iface:
        ep["mtu"] = int(iface["MTU"])
    ep["address"] = addrs
    ep["private_key"] = iface.get("PrivateKey", "")

    # AmneziaWG 1.0 / 2.0
    for k in ("Jc", "Jmin", "Jmax", "S1", "S2", "S3", "S4"):
        if k in iface:
            ep[k.lower()] = int(iface[k])
    for k in ("H1", "H2", "H3", "H4"):
        if k in iface:
            ep[k.lower()] = str(iface[k])
    for k in ("I1", "I2", "I3", "I4", "I5"):
        if k in iface:
            ep[k.lower()] = iface[k]
    # AmneziaWG 3.x
    for ck, jk in (("ContentPaddingAddition", "content_padding_addition"),
                   ("RekeyAfterTime", "rekey_after_time"),
                   ("RekeyTimeout", "rekey_timeout"),
                   ("RejectAfterTime", "reject_after_time"),
                   ("KeepaliveTimeout", "keepalive_timeout"),
                   ("MaxHandshakeAttempts", "max_handshake_attempts")):
        if ck in iface:
            ep[jk] = iface[ck]
    if "HeaderProtectionKey" in iface:
        ep["header_protection_key"] = iface["HeaderProtectionKey"]
    if iface.get("RandomTrailers", "").lower() == "on":
        ep["random_trailers"] = True
    if iface.get("DisableCookies", "").lower() == "on":
        ep["disable_cookies"] = True

    endpoint = peer.get("Endpoint", "")
    host, _, port = endpoint.rpartition(":")
    p = {"address": host or endpoint, "port": int(port or 0),
         "public_key": peer.get("PublicKey", ""),
         "allowed_ips": [x.strip() for x in peer.get("AllowedIPs", "0.0.0.0/0, ::/0").split(",")]}
    if "PresharedKey" in peer:
        p["pre_shared_key"] = peer["PresharedKey"]
    ep["peers"] = [p]
    if detour:
        ep["detour"] = detour
    return ep


def build(out, settings_p, rules_p, warp_p, geo_p, test=False):
    settings = json.load(open(settings_p, encoding="utf-8")) if os.path.exists(settings_p) else {}
    rules = json.load(open(rules_p, encoding="utf-8")) if os.path.exists(rules_p) else []

    inbounds = []
    if test:
        for tag, port in (("proxy-in", 12080), ("direct-in", 12081), ("warp-in", 12082), ("geo-in", 12083)):
            inbounds.append({"type": "mixed", "tag": tag, "listen": "127.0.0.1", "listen_port": port})
    else:
        if settings.get("tun", True):
            inbounds.append({"type": "tun", "address": "172.18.0.1/30", "auto_route": True,
                             "stack": "gvisor", "tag": "main-in"})
        if settings.get("proxy", True):
            for tag, port in (("proxy-in", 1080), ("direct-in", 1081), ("warp-in", 1082), ("geo-in", 1083)):
                inbounds.append({"type": "mixed", "tag": tag, "listen": "0.0.0.0", "listen_port": port})

    rule_set, warp_tags, geo_tags = [], [], []
    for g in rules:
        tag = g.get("tag", "")
        if not tag:
            continue
        t = g.get("type", "inline")
        if t == "remote":
            rule_set.append({"type": "remote", "tag": tag, "format": g.get("format", "binary"),
                             "url": g.get("url", ""), "update_interval": g.get("update_interval", "1d")})
        elif t == "local":
            rule_set.append({"type": "local", "path": g.get("path", ""),
                             "format": g.get("format", "binary"), "tag": tag})
        else:
            rule_set.append({"type": "inline", "rules": g.get("rules", [{"domain": []}]), "tag": tag})
        (geo_tags if tag.startswith("geo-") else warp_tags).append(tag)

    route_rules = [
        {"action": "sniff"},
        {"action": "hijack-dns", "protocol": "dns"},
        {"action": "route", "outbound": "direct-out", "ip_is_private": True},
        {"action": "route", "outbound": "direct-out", "inbound": "direct-in"},
        {"action": "route", "outbound": "warp-out", "inbound": "warp-in"},
        {"action": "route", "outbound": "geo-out", "inbound": "geo-in"},
    ]
    if warp_tags:
        route_rules.append({"action": "route", "outbound": "warp-out", "rule_set": warp_tags})
    if geo_tags:
        route_rules.append({"action": "route", "outbound": "geo-out", "rule_set": geo_tags})

    cfg = {
        "log": {"disabled": not settings.get("logging", True), "level": "warn"},
        "dns": {
            "servers": [
                {"type": "udp", "server": "1.1.1.1", "server_port": 53, "tag": "bootstrap-dns"},
                {"type": "https", "server": "1.1.1.1", "server_port": 443, "tag": "main-dns"},
            ],
            "rules": [
                {"action": "reject", "query_type": "HTTPS"},
                {"action": "reject", "domain_suffix": "use-application-dns.net"},
            ],
            "final": "main-dns",
        },
        "inbounds": inbounds,
        "outbounds": [{"type": "direct", "tag": "direct-out"}],
        "endpoints": [wg_endpoint("warp-out", warp_p), wg_endpoint("geo-out", geo_p, "warp-out")],
        "route": {
            "rules": route_rules,
            "rule_set": rule_set,
            "final": "warp-out" if settings.get("final") == "proxy" else "direct-out",
            "auto_detect_interface": True,
            "default_domain_resolver": "main-dns",
        },
    }
    json.dump(cfg, open(out, "w", encoding="utf-8"), indent=2, ensure_ascii=False)
    awg3 = "content_padding_addition" in cfg["endpoints"][0]
    print(f"wrote {out} | endpoints: warp-out, geo-out | awg3: {awg3} | rule_set: {len(rule_set)}")


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--root")
    ap.add_argument("--out", required=True)
    ap.add_argument("--settings"); ap.add_argument("--rules")
    ap.add_argument("--warp"); ap.add_argument("--geo")
    ap.add_argument("--test", action="store_true")
    a = ap.parse_args()
    r = a.root or "C:/Program Files/ssnet"
    build(a.out,
          a.settings or os.path.join(r, "settings.json"),
          a.rules or os.path.join(r, "data/rules.json"),
          a.warp or os.path.join(r, "data/warp.conf"),
          a.geo or os.path.join(r, "data/geo.conf"),
          test=a.test)