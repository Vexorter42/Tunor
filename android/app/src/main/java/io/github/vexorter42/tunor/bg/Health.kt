package io.github.vexorter42.tunor.bg

import android.content.Context
import android.net.ConnectivityManager
import android.net.NetworkCapabilities
import io.github.vexorter42.tunor.BuildConfig
import io.github.vexorter42.tunor.TunorApp
import io.github.vexorter42.tunor.core.Bypass
import io.github.vexorter42.tunor.core.NodePing
import io.github.vexorter42.tunor.core.Tunnel
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import java.io.File

/**
 * Looks after the servers of the subscriptions while the tunnel runs: when not one usual server of a subscription
 * answers, moves it to its own server meant for that (see [Bypass]), and back again when the usual ones are back.
 *
 * It is cheap when all is well: the usual servers are tried only until one answers, a few milliseconds, and nothing
 * else is touched. It is also careful not to act on a phone that has no network at all, where nothing answering
 * says nothing about the servers.
 */
object Health {
    private val one = Mutex()

    /** How many checks in a row the usual servers of a subscription have answered; the way back waits for two. */
    private val upRun = HashMap<String, Int>()

    /** How long between looks. A debug build can be given a shorter time, to try it without waiting. */
    fun period(context: Context): Long {
        if (BuildConfig.DEBUG) {
            val f = File(context.filesDir, "health-seconds.txt")
            if (f.isFile) f.readText().trim().toLongOrNull()?.let { return it * 1000 }
        }
        return 5 * 60_000L
    }

    /** Whether the phone has a real network to be on, not counting the tunnel itself. */
    fun hasNetwork(context: Context): Boolean {
        val cm = context.getSystemService(ConnectivityManager::class.java)
        @Suppress("DEPRECATION")
        return cm.allNetworks.any { n ->
            val c = cm.getNetworkCapabilities(n)
            c != null && c.hasCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET) && c.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_VPN)
        }
    }

    /** One pass over the subscriptions. Returns a line for each move it made, to tell the person. */
    suspend fun run(context: Context): List<String> = one.withLock {
        val store = TunorApp.store
        if (!store.state.value.settings.autoBypass || !hasNetwork(context)) return emptyList()
        val said = ArrayList<String>()

        for (t in store.state.value.tunnels.filter { it.kind == Tunnel.SUB }) {
            val special = t.nodes.indices.filter { Bypass.isSpecial(t.nodes[it].name) }
            val usual = t.nodes.indices.filter { it !in special }
            if (usual.isEmpty() || special.isEmpty()) continue

            val usualUp = NodePing.anyAnswers(usual.map { t.nodes[it].url })
            upRun[t.id] = if (usualUp) (upRun[t.id] ?: 0) + 1 else 0
            if (usualUp && (t.autoFrom.isEmpty() || (upRun[t.id] ?: 0) < 2)) continue

            // With the usual ones down only the special ones are worth timing; going back, all of them are.
            val which = if (usualUp) t.nodes.indices.toList() else special
            val timed = NodePing.measureAll(which.map { t.nodes[it].url })
            val answers = which.indices.associate { which[it] to timed.getValue(it) }

            val move = Bypass.decide(t, answers) ?: continue
            var done = false
            store.update { s ->
                s.copy(tunnels = s.tunnels.map {
                    // Not if the person has moved it in the meantime.
                    if (it.id == t.id && it.chosen == t.chosen && it.nodes == t.nodes) { done = true; Bypass.apply(it, move) } else it
                })
            }
            if (!done) continue
            val name = t.nodes[move.index].name
            said += if (move.back) "«${t.title}»: обычные серверы снова отвечают, вернулся на «$name»."
            else "«${t.title}»: обычные серверы не отвечают, переключился на «$name» (${move.ms} мс)."
        }
        said
    }
}
