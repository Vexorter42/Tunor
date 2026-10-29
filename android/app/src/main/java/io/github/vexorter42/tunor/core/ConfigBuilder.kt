package io.github.vexorter42.tunor.core

import org.json.JSONArray
import org.json.JSONObject

/** The person's setup cannot be turned into a working configuration; the message says why, in words. */
class ConfigProblem(message: String) : Exception(message)

/**
 * Writes the configuration the engine runs on: one TUN inbound, the tunnel, and the rules that say what
 * goes through it. The same shape as the desktop app's, cut down to what a phone needs: one tunnel in use
 * at a time, one list of sites, and the choice of which apps use the VPN at all (that one is made by the
 * system, in the service, not here).
 */
object ConfigBuilder {
    const val TUNNEL = "tunnel-out"
    const val DIRECT = "direct-out"

    fun activeTunnel(state: State): Tunnel? =
        state.tunnels.firstOrNull { it.id == state.settings.active } ?: state.tunnels.firstOrNull()

    /** Why there is nothing to start, or null when the setup can run. */
    fun problem(state: State): String? {
        val t = activeTunnel(state) ?: return "Сначала добавь туннель: файл .conf, ссылку vless:// или подписку."
        return when (t.kind) {
            Tunnel.WG -> WgConf.parse(t.conf).problem()?.let { "Туннель «${t.title}»: $it" }
            Tunnel.LINK -> VlessLink.parse(t.link).problem?.let { "Туннель «${t.title}»: $it" }
            Tunnel.SUB -> {
                val node = t.nodes.getOrNull(t.chosen) ?: return "В подписке «${t.title}» нет серверов: обнови её."
                VlessLink.parse(node.url).problem?.let { "Сервер «${node.name}»: $it" }
            }
            else -> "Неизвестный вид туннеля «${t.kind}»."
        }
    }

    fun build(state: State, door: Door? = null, logLevel: String = if (io.github.vexorter42.tunor.BuildConfig.DEBUG) "debug" else "warn"): String {
        problem(state)?.let { throw ConfigProblem(it) }
        val tunnel = activeTunnel(state)!!

        val outbounds = JSONArray().put(JSONObject().put("type", "direct").put("tag", DIRECT))
        val endpoints = JSONArray()
        when (tunnel.kind) {
            Tunnel.WG -> endpoints.put(WgConf.parse(tunnel.conf).toEndpoint(TUNNEL))
            Tunnel.LINK -> outbounds.put(VlessLink.parse(tunnel.link).outbound!!.put("tag", TUNNEL))
            Tunnel.SUB -> outbounds.put(VlessLink.parse(tunnel.nodes[tunnel.chosen].url).outbound!!.put("tag", TUNNEL))
        }

        val everything = state.settings.everything == Settings.TUNNEL
        val rules = JSONArray()
        // The doors first: what comes in by one goes where the door leads, whatever else is written below.
        if (door != null) {
            rules.put(JSONObject().put("inbound", "door-tunnel").put("action", "route").put("outbound", TUNNEL))
            rules.put(JSONObject().put("inbound", "door-direct").put("action", "route").put("outbound", DIRECT))
        }
        rules
            .put(JSONObject().put("action", "sniff"))
            .put(JSONObject().put("action", "hijack-dns").put("protocol", "dns"))
            .put(JSONObject().put("action", "route").put("outbound", DIRECT).put("ip_is_private", true))

        // A name covers its subdomains: "youtube.com" in the list sends www., m. and every host under it.
        val domains = JSONArray()
        val seen = HashSet<String>()
        for (d in state.rules.domains) {
            val entry = domainEntry(d)
            if (entry.isNotEmpty() && seen.add(entry)) domains.put(entry)
        }
        if (domains.length() > 0) {
            rules.put(JSONObject().put("action", "route").put("outbound", TUNNEL).put("domain_suffix", domains))
        }

        val dns = JSONObject()
            .put("servers", JSONArray()
                .put(JSONObject().put("type", "udp").put("server", "1.1.1.1").put("server_port", 53).put("tag", "bootstrap-dns"))
                .put(JSONObject().put("type", "https").put("server", "1.1.1.1").put("server_port", 443).put("tag", "main-dns")))
            .put("rules", JSONArray()
                .put(JSONObject().put("action", "reject").put("query_type", "HTTPS"))
                .put(JSONObject().put("action", "reject").put("domain_suffix", "use-application-dns.net")))
            // Answer with IPv4 addresses only. A phone's network often claims IPv6 it cannot use, and an app that
            // is told an IPv6 address goes there and waits for a timeout; with none given it uses the road that works,
            // and nothing slips past the tunnel by an address family the tunnel does not carry.
            .put("strategy", "ipv4_only")
            .put("final", "main-dns")

        val tun = JSONObject()
            .put("type", "tun")
            .put("tag", "tun-in")
            .put("address", JSONArray().put("172.19.0.1/30"))
            .put("auto_route", true)
            .put("stack", "gvisor")
            .put("mtu", 1500)

        val route = JSONObject()
            .put("rules", rules)
            .put("final", if (everything) TUNNEL else DIRECT)
            .put("auto_detect_interface", true)
            .put("find_process", false)
            .put("default_domain_resolver", "main-dns")

        val root = JSONObject()
            // A file of its own in the engine's folder: what the engine says is the first thing to read when something does not open.
            .put("log", JSONObject().put("level", logLevel).put("output", "box.log").put("timestamp", true))
            .put("dns", dns)
            .put("inbounds", JSONArray().put(tun).also { inbounds ->
                if (door != null) {
                    fun mixed(tag: String, port: Int) = JSONObject().put("type", "mixed").put("tag", tag)
                        .put("listen", "127.0.0.1").put("listen_port", port)
                        .put("users", JSONArray().put(JSONObject().put("username", door.user).put("password", door.password)))
                    inbounds.put(mixed("door-tunnel", door.tunnelPort)).put(mixed("door-direct", door.directPort))
                }
            })
            .put("outbounds", outbounds)
            .put("route", route)
            .put("experimental", JSONObject().put("cache_file", JSONObject().put("enabled", true).put("path", "cache.db")))
        if (endpoints.length() > 0) root.put("endpoints", endpoints)
        return root.toString(2)
    }

    /** What a person might paste for a site (an address, "*.site", "site:443") reduced to the name the engine matches. */
    fun domainEntry(item: String): String {
        var s = item.trim().trim('"', '\'')
        if ("://" in s) {
            s = try { java.net.URI(s).host ?: s.substringAfter("://") } catch (_: Exception) { s.substringAfter("://") }
        }
        val cut = s.indexOfAny(charArrayOf('/', '?', '#'))
        if (cut >= 0) s = s.substring(0, cut)
        while (s.startsWith("*.") || s.startsWith(".")) s = s.trimStart('*').trimStart('.')
        if (s.count { it == ':' } == 1) s = s.substringBefore(':')           // host:port, but not IPv6
        return s.trim().trimEnd('.').lowercase()
    }
}
