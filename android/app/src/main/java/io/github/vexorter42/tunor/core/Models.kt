package io.github.vexorter42.tunor.core

import kotlinx.serialization.Serializable

/** One server out of a subscription: what the provider called it and the share link, kept as it came. */
@Serializable
data class Node(val name: String, val url: String)

/**
 * A way out. Three kinds, as on the desktop: a WireGuard/AmneziaWG config (`wg`), a single share link
 * (`link`) and a subscription (`sub`, an address that answers with many links).
 *
 * All of them are in the engine's configuration at once and the rules say which one a connection takes.
 * One can ride inside another ([via]): geo goes out through WARP, so that its address is only ever seen
 * from there.
 */
@Serializable
data class Tunnel(
    val id: String,
    val title: String,
    val kind: String,
    /** The text of the .conf, for `wg`. */
    val conf: String = "",
    /** The share link, for `link`. */
    val link: String = "",
    /** The address of the list, for `sub`. It is a credential: it never leaves the phone except to fetch the list. */
    val sub: String = "",
    val nodes: List<Node> = emptyList(),
    /** Which node of a subscription is used. */
    val chosen: Int = 0,
    /** The id of the tunnel this one rides inside; empty — it goes out on its own. */
    val via: String = "",
) {
    companion object {
        const val WG = "wg"
        const val LINK = "link"
        const val SUB = "sub"

        /** The two places whose ids are fixed, as on the desktop: the catalog's lists name them, and geo goes through WARP. */
        const val WARP = "warp"
        const val GEO = "geo"
    }
}

/** A site typed in by hand, and the tunnel it goes through. A name covers its subdomains too. */
@Serializable
data class Site(val domain: String, val tunnel: String = "")

/** A list from the catalog that is switched on: what it is, where it comes from and which tunnel it goes through. */
@Serializable
data class Pick(val id: String, val title: String, val url: String, val tunnel: String)

/** What goes where. Everything not named here goes the way [Settings.rest] says. */
@Serializable
data class Rules(
    val sites: List<Site> = emptyList(),
    val lists: List<Pick> = emptyList(),
    /** Before sites could choose their tunnel, one list for the only tunnel. Read once and moved into [sites]. */
    val domains: List<String> = emptyList(),
)

@Serializable
data class Settings(
    /** Before tunnels were all in use at once, the one that was. Read once, for [rest] and the old [Rules.domains]. */
    val active: String = "",
    /** Before [rest]: "tunnel" sent everything through [active]. */
    val everything: String = DIRECT,
    /** Where traffic that no rule names goes: a tunnel's id, or empty for straight out. */
    val rest: String = "",
    /** Only these apps use the VPN at all; empty — all of them do. */
    val apps: List<String> = emptyList(),
    val termsAccepted: Boolean = false,
    val tutorialPassed: Int = 0,
    /** When the lists were last fetched, in milliseconds since 1970; 0 — never. */
    val listsFetchedAt: Long = 0,
) {
    companion object {
        const val DIRECT = "direct"
        const val TUNNEL = "tunnel"
    }
}

@Serializable
data class State(
    val tunnels: List<Tunnel> = emptyList(),
    val rules: Rules = Rules(),
    val settings: Settings = Settings(),
) {
    /** The same setup in today's shape: an older file's single list of sites and its "everything" switch moved to where they live now. */
    fun migrated(): State {
        var s = this
        val main = settings.active.ifEmpty { tunnels.firstOrNull()?.id.orEmpty() }
        if (rules.domains.isNotEmpty()) {
            s = s.copy(rules = rules.copy(sites = rules.sites + rules.domains.map { Site(it, main) }, domains = emptyList()))
        }
        if (settings.everything == Settings.TUNNEL) {
            s = s.copy(settings = s.settings.copy(rest = if (settings.rest.isEmpty()) main else settings.rest, everything = Settings.DIRECT))
        }
        return s
    }
}
