"""Builds the tree the installer packs, out of a working copy of Tunor.

Nothing here is specific to one machine: paths come from the environment.

    TUNOR_APP     installed (or working) copy of Tunor, the source of the engine,
                  the helper scripts and the settings/rules used as a template.
                  Default: C:/Program Files/ssnet
    TUNOR_OUT     where the staged tree and the finished installer go.
                  Default: <repo>/build-out
    TUNOR_ENGINE  sing-box.exe to ship. Default: the one in TUNOR_APP/build.

What leaves this script is deliberately impersonal: keys, personal rule lists and
per-machine state are replaced or stripped below, because the result is published.

    python packaging/stage.py
    ISCC packaging/tunor.iss
"""
import base64
import hashlib
import json
import os
import secrets
import shutil
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.dirname(HERE)

APP = os.environ.get("TUNOR_APP", "C:/Program Files/ssnet").replace("\\", "/")
OUT = os.environ.get("TUNOR_OUT", os.path.join(REPO, "build-out")).replace("\\", "/")
PUB = APP + "/ui/bin/Release/net8.0-windows/win-x64/publish"
STAGE = OUT + "/dist"

# The engine ships byte for byte as published upstream — this says which build it is.
# Bumping it: download the release, verify it against its SHA256SUMS, point TUNOR_ENGINE
# at the extracted sing-box.exe and change the two lines below. The installer keeps an
# engine that is already installed, so a bump also means flipping that flag in tunor.iss.
ENGINE_VERSION = "1.14.1-lx.8"
ENGINE_SOURCE = "https://github.com/Leadaxe/sing-box-lx/releases/tag/v" + ENGINE_VERSION
SINGBOX_LX = os.environ.get("TUNOR_ENGINE", APP + "/build/sing-box.exe").replace("\\", "/")

sys.path.insert(0, HERE)
from gen_config import build as gen_build


def rnd_key():
    return base64.b64encode(secrets.token_bytes(32)).decode()


def sha256(path):
    with open(path, "rb") as fh:
        return hashlib.sha256(fh.read()).hexdigest()


if os.path.exists(STAGE):
    shutil.rmtree(STAGE)
for d in ("", "/ui", "/build", "/data/rulesets"):
    os.makedirs(STAGE + d, exist_ok=True)

# 1. Published UI (Tunor.exe + runtime)
if not os.path.isdir(PUB):
    sys.exit(f"no published build at {PUB}\n"
             f"run first:  dotnet publish -c Release -r win-x64 --self-contained true")
for name in os.listdir(PUB):
    s, d = os.path.join(PUB, name), os.path.join(STAGE + "/ui", name)
    shutil.copy2(s, d) if os.path.isfile(s) else shutil.copytree(s, d)
print("copied UI publish:", len(os.listdir(STAGE + "/ui")), "entries")

# 2. settings.json + update.json
_st = json.load(open(APP + "/settings.json", encoding="utf-8"))
_st["accept"] = False          # show the licence + tutorial on first launch
# Per-machine state, not defaults: the stats API key must be each install's own
# (Tunor generates one when missing), and a fresh install has downloaded no lists yet.
for _k in ("controllerSecret", "controllerPort", "listsUpdatedAt"):
    _st.pop(_k, None)
json.dump(_st, open(STAGE + "/settings.json", "w", encoding="utf-8"),
          indent=2, ensure_ascii=False)
json.dump({"repo": "Vexorter42/Tunor", "mirror": "https://ghproxy.net/"},
          open(STAGE + "/update.json", "w", encoding="utf-8"), indent=2, ensure_ascii=False)

# 3. sing-box = sing-box-lx (AmneziaWG 1.0/2.0/3.x) + helper scripts
shutil.copy2(SINGBOX_LX, STAGE + "/build/sing-box.exe")
engine_sha = sha256(STAGE + "/build/sing-box.exe")
# Which engine this is, without running it: the app reads this in its problem report,
# and compares the hash with the file it actually has.
open(STAGE + "/build/sing-box.version", "w", encoding="utf-8").write(
    f"version = {ENGINE_VERSION}\nsha256 = {engine_sha}\nsource = {ENGINE_SOURCE}\n")
# install.bat / delete.bat are gone as of 1.10.1 — they built a scheduled task under an
# obsolete name that pointed at a file no longer shipped.
for name in ("run.bat", "stop.bat", "restart-headless.vbs"):
    p = os.path.join(APP + "/build", name)
    if os.path.exists(p):
        shutil.copy2(p, STAGE + "/build/" + name)
print(f"copied sing-box-lx {ENGINE_VERSION} ({engine_sha[:16]}…) + scripts")

# 4. rule-set files (.srs) are deliberately NOT shipped.
# itdoginfo/allow-domains has no licence, so Tunor downloads them from the
# original source on first run instead of redistributing them.
print("rulesets: not bundled (downloaded on first run)")

# 4b. GPL compliance: licence texts + third-party notices
shutil.copytree(REPO + "/licenses", STAGE + "/licenses",
                ignore=shutil.ignore_patterns("THIRD-PARTY-NOTICES.md"))
shutil.copy2(REPO + "/THIRD-PARTY-NOTICES.md", STAGE + "/THIRD-PARTY-NOTICES.md")
print("copied licences:", os.listdir(STAGE + "/licenses"))

# 5. rules.json — SANITIZED (strip personal inline lists, keep structure)
rules = json.load(open(APP + "/data/rules.json", encoding="utf-8"))
for g in rules:
    for rule in g.get("rules", []):
        # Every list in an inline rule is personal (domain, domain_suffix, ip_cidr,
        # process_*...), not just the two keys the app writes today.
        for k in list(rule):
            if isinstance(rule[k], list):
                rule[k] = (["example.exe"] if k.startswith("process")
                           else ["example.com"] if k.startswith("domain")
                           else [])
json.dump(rules, open(STAGE + "/data/rules.json", "w", encoding="utf-8"), indent=2, ensure_ascii=False)
print("sanitized rules.json")

# 6. warp.conf / geo.conf templates (valid placeholders, no real keys)
warp_tmpl = "\n".join([
    "[Interface]",
    "# TUNOR-PLACEHOLDER - not a working tunnel. Replace this file with a real config:",
    "# generate one via @warp_generator_bot (AmneziaWG, AWG 1.0 / 2.0 / 3.x) and paste it here.",
    "PrivateKey = " + rnd_key(),
    "Address = 172.16.0.2, 2606:4700:110:0000:0000:0000:0000:0001",
    "DNS = 1.1.1.1, 1.0.0.1, 2606:4700:4700::1111, 2606:4700:4700::1001",
    "MTU = 1280", "S1 = 0", "S2 = 0", "Jc = 4", "Jmin = 40", "Jmax = 70",
    "H1 = 1", "H2 = 2", "H3 = 3", "H4 = 4", "",
    "[Peer]",
    "PublicKey = bmXOC+F1FxEMF9dyiK2H5/1SUtzH0JuVo51h2wPfgyo=",
    "AllowedIPs = 0.0.0.0/0, ::/0",
    "Endpoint = engage.cloudflareclient.com:2408",
    "PersistentKeepalive = 25", "",
])
open(STAGE + "/data/warp.conf", "w", encoding="utf-8").write(warp_tmpl)

geo_tmpl = "\n".join([
    "[Interface]",
    "# TUNOR-PLACEHOLDER - not a working tunnel. Paste any WireGuard config here",
    "# (e.g. ProtonVPN), then Save & apply.",
    "# These placeholders keep the app running; geo sites won't work until replaced.",
    "PrivateKey = " + rnd_key(),
    "Address = 10.2.0.2/32, 2a07:b944::2:2/128",
    "DNS = 10.2.0.1, 2a07:b944::2:1", "",
    "[Peer]",
    "PublicKey = " + rnd_key(),
    "AllowedIPs = 0.0.0.0/0, ::/0",
    "Endpoint = 127.0.0.1:51820",
    "PersistentKeepalive = 25", "",
])
open(STAGE + "/data/geo.conf", "w", encoding="utf-8").write(geo_tmpl)
print("wrote warp.conf + geo.conf templates")

# 7. Starting config.json generated from the staged templates (sing-box-lx format)
gen_build(STAGE + "/build/config.json",
          STAGE + "/settings.json", STAGE + "/data/rules.json",
          STAGE + "/data/warp.conf", STAGE + "/data/geo.conf")

# 7b. Make that config true on a machine that has just installed Tunor and nothing else.
# The tunnels staged above are placeholders and no .srs file ships (they are downloaded
# on first run), so anything built from them must go: a config that names a tunnel key
# which is not real, or a rule-set file that is not there, fails to load. Tunor rebuilds
# this file at its first launch anyway — this copy only has to be valid until then.
cfg = json.load(open(STAGE + "/build/config.json", encoding="utf-8"))
route = cfg.setdefault("route", {})

dead_outbounds = {e.get("tag") for e in cfg.get("endpoints", []) if e.get("tag")}
cfg["endpoints"] = []

kept_sets, dead_sets = [], set()
for rs in route.get("rule_set", []):
    path = rs.get("path") or ""
    if rs.get("type") == "local" and not os.path.exists(os.path.join(STAGE, path)):
        dead_sets.add(rs.get("tag"))
    else:
        kept_sets.append(rs)
route["rule_set"] = kept_sets

kept_rules = []
for rule in route.get("rules", []):
    if rule.get("outbound") in dead_outbounds:
        continue
    if "rule_set" in rule:
        left = [t for t in rule["rule_set"] if t not in dead_sets]
        if not left:
            continue
        rule["rule_set"] = left
    kept_rules.append(rule)
route["rules"] = kept_rules

if route.get("final") in dead_outbounds:
    route["final"] = "direct"

# Nothing may point at a tag that no longer exists.
known = {o.get("tag") for o in cfg.get("outbounds", [])} | {"direct", "block", "dns-out"}
known_sets = {rs.get("tag") for rs in route["rule_set"]}
for rule in route["rules"]:
    assert rule.get("outbound", "direct") in known, f"rule points at unknown outbound: {rule}"
    for t in rule.get("rule_set", []):
        assert t in known_sets, f"rule points at unknown rule-set: {t}"
assert route.get("final", "direct") in known, "final points at an outbound that is gone"

json.dump(cfg, open(STAGE + "/build/config.json", "w", encoding="utf-8"),
          indent=2, ensure_ascii=False)
print(f"trimmed starting config: endpoints {sorted(dead_outbounds)} dropped, "
      f"rule_set {len(kept_sets)} kept / {len(dead_sets)} dropped, "
      f"rules {len(kept_rules)}, final {route.get('final')}")

print("\nSTAGE ready at", STAGE)
