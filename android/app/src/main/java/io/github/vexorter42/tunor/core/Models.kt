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
    /**
     * For a subscription whose server the app changed by itself (the usual ones were not answering): the link of the
     * server that was in use before, so that it can go back to it when it answers again. Empty — the person's own choice stands.
     */
    val autoFrom: String = "",
    /**
     * For a subscription: the app times the servers itself and hands the engine the fastest few to choose among
     * (see [Pool]), instead of the one server in [chosen]. The engine tries each with a real request and passes over
     * the ones that do not work, which a server's answer to a connection alone does not tell.
     */
    val auto: Boolean = true,
    /** The links of the servers the engine chooses among when [auto] is on, fastest first; empty until the app has timed them. */
    val pool: List<String> = emptyList(),
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

/**
 * An app and where its traffic goes: a tunnel's id, or [DIRECT] for straight out without ever entering the VPN.
 * An app that is not here goes by the rules like everything else.
 */
@Serializable
data class AppRule(val pkg: String, val tunnel: String) {
    companion object {
        const val DIRECT = "direct"
    }
}

/** What goes where. Everything not named here goes the way [Settings.rest] says. */
@Serializable
data class Rules(
    val sites: List<Site> = emptyList(),
    val lists: List<Pick> = emptyList(),
    val apps: List<AppRule> = emptyList(),
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
    /** Before apps could be sent each its own way: the ones that alone used the VPN. No longer read; kept so that an old file still loads. */
    val apps: List<String> = emptyList(),
    val termsAccepted: Boolean = false,
    val tutorialPassed: Int = 0,
    /** When the lists were last fetched, in milliseconds since 1970; 0 — never. */
    val listsFetchedAt: Long = 0,
    /** When none of a subscription's usual servers answers, move to one of its servers meant for that (see [Bypass]). */
    val autoBypass: Boolean = true,
    /** On the mobile network, when no tunnel answers, send everything through a subscription's servers made for that (see MobileRescue). */
    val mobileRescue: Boolean = false,
    /** The subscription for that: its id; empty — the first one that has such servers. */
    val mobileRescueSub: String = "",
    /** Look for a newer version of the app now and then. */
    val autoUpdateCheck: Boolean = true,
    /** When that was last done, in milliseconds since 1970; 0 — never. */
    val updateCheckedAt: Long = 0,
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

/**
 * A way out put in place of the usual ones while the mobile network will not carry them: everything goes through
 * the subscription [tunnelId], and the engine chooses among [servers], links, the nearest first.
 * It lives only while it is needed and is never written down.
 */
data class Rescue(val tunnelId: String, val servers: List<String>)
