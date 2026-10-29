package io.github.vexorter42.tunor.core

import kotlinx.serialization.Serializable

/** One server out of a subscription: what the provider called it and the share link, kept as it came. */
@Serializable
data class Node(val name: String, val url: String)

/**
 * A way out. Three kinds, as on the desktop: a WireGuard/AmneziaWG config (`wg`), a single share link
 * (`link`) and a subscription (`sub`, an address that answers with many links).
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
) {
    companion object {
        const val WG = "wg"
        const val LINK = "link"
        const val SUB = "sub"
    }
}

/** What goes where. Everything not named here goes the way [Settings.everything] says. */
@Serializable
data class Rules(
    /** Sites that go through the tunnel. A name covers its subdomains too. */
    val domains: List<String> = emptyList(),
    /** Lists from the catalog that are switched on, by their tag. */
    val lists: List<String> = emptyList(),
)

@Serializable
data class Settings(
    /** The tunnel in use; empty — none chosen yet. */
    val active: String = "",
    /** "direct": only what the rules name goes through the tunnel. "tunnel": all of it does. */
    val everything: String = DIRECT,
    /** Only these apps use the VPN at all; empty — all of them do. */
    val apps: List<String> = emptyList(),
    val termsAccepted: Boolean = false,
    val tutorialPassed: Int = 0,
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
)
