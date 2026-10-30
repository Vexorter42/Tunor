package io.github.vexorter42.tunor.core

import org.json.JSONArray
import org.json.JSONObject

/** The person's setup cannot be turned into a working configuration; the message says why, in words. */
class ConfigProblem(message: String) : Exception(message)

/**
 * Writes the configuration the engine runs on: one TUN inbound, every tunnel that can carry traffic, and the
 * rules that say which one a connection takes. The same shape as the desktop app's, cut down to what a phone
 * needs: no processes to match (the system's own choice of which apps use the VPN stands in for them), no
 * controller, no log page.
 *
 * Like on the desktop, a tunnel that cannot work is left out and the rest still runs, and what was meant
 * for it goes to the tunnel it rides inside, or out directly. One endpoint the engine cannot parse would
 * stop the whole file, and with it every other tunnel.
 */
object ConfigBuilder {
    const val DIRECT = "direct-out"

    /** The engine's name for a tunnel. */
    fun tagOf(t: Tunnel) = "out-" + t.id
    fun doorOf(t: Tunnel) = "door-" + t.id

    /** WARP first, geo second, then the others in the order they were added: the order the rules are tried in. */
    fun ordered(tunnels: List<Tunnel>): List<Tunnel> =
        tunnels.sortedBy { when (it.id) { Tunnel.WARP -> 0; Tunnel.GEO -> 1; else -> 2 } }

    fun titleOf(id: String, tunnels: List<Tunnel>): String =
        tunnels.firstOrNull { it.id == id }?.title ?: when (id) { Tunnel.WARP -> "WARP"; Tunnel.GEO -> "geo"; else -> "—" }

    // ------------------------------------------------------------------ which tunnels can run

    /** The servers the engine chooses among for a subscription left to look after itself: the ones in its pool that can be read. */
    fun poolOf(t: Tunnel): List<String> =
        if (t.kind == Tunnel.SUB && t.auto) t.pool.filter { VlessLink.parse(it).ok }.distinct() else emptyList()

    /** Why this tunnel's own setup cannot run, in words, or null. */
    fun selfProblem(t: Tunnel): String? = when (t.kind) {
        Tunnel.WG -> WgConf.parse(t.conf).problem()
        Tunnel.LINK -> VlessLink.parse(t.link).problem
        // A server that is fine has no problem, which is null too: "no server" has to be told apart before asking about it.
        Tunnel.SUB ->
            if (poolOf(t).isNotEmpty()) null
            else t.nodes.getOrNull(t.chosen).let { node -> if (node == null) "в подписке нет серверов: обнови её" else VlessLink.parse(node.url).problem }
        else -> "неизвестный вид туннеля «${t.kind}»"
    }

    /** Why this tunnel cannot carry traffic: its own setup, or that of the one it rides inside. Null when it can. */
    fun problemOf(t: Tunnel, tunnels: List<Tunnel>, depth: Int = 0): String? {
        if (depth > 8) return "туннели идут друг через друга по кругу"
        selfProblem(t)?.let { return it }
        if (t.via.isEmpty()) return null
        val outer = tunnels.firstOrNull { it.id == t.via }
            ?: return "${ConfigBuilder.titleOf(t.via, tunnels)}, через который он идёт, не добавлен"
        return problemOf(outer, tunnels, depth + 1)?.let { "«${outer.title}», через который он идёт, не готов: $it" }
    }

    /** The tunnels that can carry traffic, in the order the rules are tried in. */
    fun live(tunnels: List<Tunnel>): List<Tunnel> = ordered(tunnels).filter { problemOf(it, tunnels) == null }

    /** Whether [from] ends up riding inside [id], however many tunnels deep: to keep the choice of "rides inside" from going round in a circle. */
    fun rides(from: Tunnel, id: String, tunnels: List<Tunnel>): Boolean {
        var next = from.via
        var steps = 0
        while (next.isNotEmpty() && steps++ < 9) {
            if (next == id) return true
            val here = next
            next = tunnels.firstOrNull { it.id == here }?.via.orEmpty()
        }
        return false
    }

    /** Why there is nothing to start, or null when the setup can run. */
    fun problem(state: State): String? {
        if (state.tunnels.isEmpty()) return "Сначала добавь туннель: файл .conf, ссылку vless:// или подписку."
        if (live(state.tunnels).isEmpty()) {
            val t = ordered(state.tunnels).first()
            return "Туннель «${t.title}»: ${problemOf(t, state.tunnels)}."
        }
        return null
    }

    /**
     * Where traffic aimed at a tunnel actually goes: into it when it is up, otherwise into whatever it rides
     * inside, and failing that out directly. Naming an outbound the configuration never made would stop the engine.
     */
    private fun targets(tunnels: List<Tunnel>, live: Set<String>): Map<String, String> {
        val byId = tunnels.associateBy { it.id }
        fun resolve(t: Tunnel, depth: Int): String = when {
            depth > 8 -> DIRECT
            t.id in live -> tagOf(t)
            else -> byId[t.via]?.let { resolve(it, depth + 1) } ?: DIRECT
        }
        return tunnels.associate { it.id to resolve(it, 0) }
    }

    // ------------------------------------------------------------------ the file

    /**
     * @param door the loopback entrances for the connection check, or null when no check is wanted
     * @param have where the file of a catalog list is on the phone, or null when it has not been fetched:
     *             the engine refuses to start over a list it cannot find, so such a list is left out
     * @param rescue a way out that takes everything while the mobile network will not carry the usual ones; the
     *               tunnels stay in the file, so that the doors still lead to each and the usual ones can be looked at
     */
    fun build(
        state: State,
        door: Door? = null,
        have: (Pick) -> String? = { null },
        logLevel: String = if (io.github.vexorter42.tunor.BuildConfig.DEBUG) "debug" else "warn",
        rescue: Rescue? = null,
    ): String {
        problem(state)?.let { throw ConfigProblem(it) }
        val liveList = live(state.tunnels)
        val liveIds = liveList.map { it.id }.toSet()
        val byId = state.tunnels.associateBy { it.id }
        val target = targets(state.tunnels, liveIds)
        // A name that is no tunnel (the catalog says "geo" and there is none) goes to the first one that works.
        val fallback = target[liveList.first().id]!!

        // A rescue for a tunnel that is not there, or with no server that can be read, is no rescue: the usual setup stands.
        val rescueServers = rescue?.servers.orEmpty().filter { VlessLink.parse(it).ok }.distinct()
        val rescueTunnel = rescue?.takeIf { rescueServers.isNotEmpty() }?.let { r -> liveList.firstOrNull { it.id == r.tunnelId && it.kind == Tunnel.SUB } }
        val rescueTag = rescueTunnel?.let { tagOf(it) }
        fun dest(id: String) = rescueTag ?: target[id] ?: fallback
        // With everything going one way the rules say nothing more; what the person named goes the same way as the rest.
        val rules0 = if (rescueTag != null) Rules() else state.rules

        val outbounds = JSONArray().put(JSONObject().put("type", "direct").put("tag", DIRECT))
        val endpoints = ArrayList<JSONObject>()
        for (t in liveList) {
            val detour = t.via.takeIf { it.isNotEmpty() }?.let { tagOf(byId.getValue(it)) }
            when (t.kind) {
                Tunnel.WG -> endpoints += WgConf.parse(t.conf).toEndpoint(tagOf(t), detour)
                else -> {
                    val links = when {
                        t === rescueTunnel -> rescueServers
                        t.kind == Tunnel.LINK -> listOf(t.link)
                        else -> poolOf(t).ifEmpty { listOf(t.nodes[t.chosen].url) }
                    }
                    addServers(outbounds, tagOf(t), links, detour)
                }
            }
        }
        fitNested(endpoints)

        val finalTag = rescueTag ?: state.settings.rest.takeIf { it.isNotEmpty() }?.let { target[it] } ?: DIRECT

        val rules = JSONArray()
        // The doors first: what comes in by one goes where the door leads, whatever else is written below.
        if (door != null) {
            rules.put(JSONObject().put("inbound", "door-direct").put("action", "route").put("outbound", DIRECT))
            for (t in liveList) if (door.tunnels.containsKey(t.id)) {
                rules.put(JSONObject().put("inbound", doorOf(t)).put("action", "route").put("outbound", tagOf(t)))
            }
        }
        rules
            .put(JSONObject().put("action", "sniff"))
            .put(JSONObject().put("action", "hijack-dns").put("protocol", "dns"))
            .put(JSONObject().put("action", "route").put("outbound", DIRECT).put("ip_is_private", true))

        // Grouped by where they lead, the tunnels in their order: when two lists name the same site the first wins.
        val order = liveList.map { tagOf(it) } + DIRECT
        // Going direct only means something when the rest does not.
        val worthIt = { tag: String -> tag != DIRECT || finalTag != DIRECT }

        // An app named by hand goes where it was told, before any list or site: "this program goes there" beats what it connects to.
        // Which app a connection belongs to is asked of the system by the service (see findConnectionOwner), so the engine is
        // told to look only when there is a rule that needs it.
        val apps = LinkedHashMap<String, ArrayList<String>>().also { m -> order.forEach { m[it] = ArrayList() } }
        for (a in rules0.apps) {
            if (a.tunnel.isEmpty() || a.tunnel == AppRule.DIRECT) continue
            apps.getValue(dest(a.tunnel)).add(a.pkg)
        }
        var findProcess = false
        for ((tag, packages) in apps) {
            if (packages.isEmpty() || !worthIt(tag)) continue
            findProcess = true
            rules.put(JSONObject().put("action", "route").put("outbound", tag).put("package_name", JSONArray(packages.distinct())))
        }

        // Sites typed by hand come before the lists: a name written down on purpose beats a big list that happens to hold it.
        val sites = LinkedHashMap<String, LinkedHashSet<String>>().also { m -> order.forEach { m[it] = LinkedHashSet() } }
        for (s in rules0.sites) {
            val entry = domainEntry(s.domain)
            if (entry.isNotEmpty()) sites.getValue(dest(s.tunnel)).add(entry)
        }
        for ((tag, names) in sites) {
            if (names.isEmpty() || !worthIt(tag)) continue
            rules.put(JSONObject().put("action", "route").put("outbound", tag).put("domain_suffix", JSONArray(names.toList())))
        }

        val ruleSets = JSONArray()
        val lists = LinkedHashMap<String, ArrayList<String>>().also { m -> order.forEach { m[it] = ArrayList() } }
        val seen = HashSet<String>()
        for (p in rules0.lists) {
            val tag = dest(p.tunnel)
            if (!worthIt(tag) || !seen.add(p.id)) continue
            val path = have(p) ?: continue
            ruleSets.put(JSONObject().put("type", "local").put("tag", listTag(p)).put("format", "binary").put("path", path))
            lists.getValue(tag).add(listTag(p))
        }
        for ((tag, tags) in lists) {
            if (tags.isEmpty()) continue
            rules.put(JSONObject().put("action", "route").put("outbound", tag).put("rule_set", JSONArray(tags)))
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
            .put("final", finalTag)
            .put("auto_detect_interface", true)
            .put("find_process", findProcess)
            .put("default_domain_resolver", "main-dns")
        if (ruleSets.length() > 0) route.put("rule_set", ruleSets)

        val root = JSONObject()
            // A file of its own in the engine's folder: what the engine says is the first thing to read when something does not open.
            .put("log", JSONObject().put("level", logLevel).put("output", "box.log").put("timestamp", true))
            .put("dns", dns)
            .put("inbounds", JSONArray().put(tun).also { inbounds ->
                if (door != null) {
                    fun mixed(tag: String, port: Int) = JSONObject().put("type", "mixed").put("tag", tag)
                        .put("listen", "127.0.0.1").put("listen_port", port)
                        .put("users", JSONArray().put(JSONObject().put("username", door.user).put("password", door.password)))
                    inbounds.put(mixed("door-direct", door.direct))
                    for (t in liveList) door.tunnels[t.id]?.let { inbounds.put(mixed(doorOf(t), it)) }
                }
            })
            .put("outbounds", outbounds)
            .put("route", route)
            .put("experimental", JSONObject().put("cache_file", JSONObject().put("enabled", true).put("path", "cache.db")))
        if (endpoints.isNotEmpty()) root.put("endpoints", JSONArray(endpoints))
        return root.toString(2)
    }

    private fun listTag(p: Pick) = "list-" + p.id

    /** What the engine asks to see to call a server working: Cloudflare's own address for this, which answers with nothing and is up. */
    private const val TEST_URL = "https://cp.cloudflare.com/generate_204"

    /**
     * Puts the servers into the outbounds under the tunnel's name. One server is just that; several are a group the
     * engine tests with real requests and picks among, so that a server that has stopped working is passed over by the
     * engine itself, a minute after, and taken back when it works again.
     */
    private fun addServers(outbounds: JSONArray, tag: String, links: List<String>, detour: String?) {
        fun one(link: String, name: String): JSONObject {
            val o = VlessLink.parse(link).outbound!!.put("tag", name)
            if (detour != null) o.put("detour", detour)
            return o
        }
        if (links.size == 1) {
            outbounds.put(one(links[0], tag))
            return
        }
        val members = JSONArray()
        links.forEachIndexed { i, link ->
            val name = "$tag-$i"
            outbounds.put(one(link, name))
            members.put(name)
        }
        outbounds.put(
            JSONObject().put("type", "urltest").put("tag", tag).put("outbounds", members)
                .put("url", TEST_URL).put("interval", "1m").put("tolerance", 100).put("idle_timeout", "10m"),
        )
    }

    /** The apps that go straight out: the system keeps them out of the VPN altogether, so nothing of theirs even reaches the engine. */
    fun directApps(state: State): List<String> = state.rules.apps.filter { it.tunnel == AppRule.DIRECT }.map { it.pkg }.distinct()

    // ------------------------------------------------------------------ one tunnel inside another

    /** What the engine uses when a config names no MTU of its own. */
    private const val DEFAULT_MTU = 1408

    /** What one layer of WireGuard costs: its own header, the nonce and the tag. Eighty is WireGuard's own figure and covers IPv6, the worse case. */
    private const val WIREGUARD_OVERHEAD = 80

    /** Below this a tunnel is not worth having; something else is wrong. */
    private const val MIN_MTU = 1000

    /**
     * Shrinks every tunnel that rides inside another so its packets fit in the one carrying them.
     *
     * A nested tunnel left at the default inside a smaller carrier looks perfectly healthy — the handshake is small
     * enough to pass, the tunnel comes up and the connection check is green — and then silently drops anything
     * full-sized. From the outside that reads as "it worked and then just stopped".
     *
     * An MTU the person wrote in their own config is left alone: they may know something about their path.
     */
    private fun fitNested(endpoints: List<JSONObject>) {
        val byTag = endpoints.associateBy { it.optString("tag") }

        // What this tunnel's own packets may be, resolving outwards: a carrier with no MTU of its own is itself measured against whatever carries it.
        fun mtuOf(e: JSONObject, depth: Int): Int {
            if (e.has("mtu")) return e.getInt("mtu")
            val outer = e.optString("detour").takeIf { it.isNotEmpty() && depth < 8 }?.let { byTag[it] }
            return if (outer != null) maxOf(MIN_MTU, mtuOf(outer, depth + 1) - WIREGUARD_OVERHEAD) else DEFAULT_MTU
        }

        for (e in endpoints) {
            if (e.has("mtu")) continue
            val carrier = e.optString("detour").takeIf { it.isNotEmpty() }?.let { byTag[it] } ?: continue
            val fits = maxOf(MIN_MTU, mtuOf(carrier, 0) - WIREGUARD_OVERHEAD)
            if (fits < DEFAULT_MTU) e.put("mtu", fits)
        }
    }

    // ------------------------------------------------------------------ sites

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
