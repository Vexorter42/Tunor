package io.github.vexorter42.tunor.bg

import android.content.Context
import android.net.ConnectivityManager
import android.net.NetworkCapabilities
import io.github.vexorter42.tunor.BuildConfig
import io.github.vexorter42.tunor.TunorApp
import io.github.vexorter42.tunor.core.AppLog
import io.github.vexorter42.tunor.core.Bypass
import io.github.vexorter42.tunor.core.NodePing
import io.github.vexorter42.tunor.core.Pool
import io.github.vexorter42.tunor.core.Tunnel
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import java.io.File

/**
 * Looks after the servers of the subscriptions while the tunnel runs.
 *
 * A subscription left to look after itself keeps a pool of the nearest servers for the engine to choose among
 * (see [Pool]); this renews the pool when too few of it answer, and takes the servers made for bypassing when no
 * usual one does, and the usual ones back when they return. A subscription the person chose a server in by hand is
 * only moved between its own servers when not one usual server answers (see [Bypass]), and back.
 *
 * It is cheap when all is well: a handful of connections to the pool, a few milliseconds, and nothing else is
 * touched. It is also careful not to act on a phone that has no network at all, where nothing answering says
 * nothing about the servers.
 */
object Health {
    private val one = Mutex()

    /** What a pass did: lines to tell the person, and whether the setup changed, so that the engine must read it again. */
    class Outcome(val said: List<String> = emptyList(), val changed: Boolean = false) {
        operator fun plus(o: Outcome) = Outcome(said + o.said, changed || o.changed)
    }

    /** How many checks in a row the usual servers of a subscription have answered; the way back waits for two. */
    private val upRun = HashMap<String, Int>()

    /**
     * How long between looks. A debug build can be given a shorter time, to try it without waiting.
     * [quick]: a minute and not five, for the time the phone is on a mobile network and the way out may go any moment.
     */
    fun period(context: Context, quick: Boolean = false): Long {
        if (BuildConfig.DEBUG) {
            val f = File(context.filesDir, "health-seconds.txt")
            if (f.isFile) f.readText().trim().toLongOrNull()?.let { return it * 1000 }
        }
        return if (quick) 60_000L else 5 * 60_000L
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

    /** One pass over the subscriptions. */
    suspend fun run(context: Context): Outcome = one.withLock {
        val store = TunorApp.store
        if (!hasNetwork(context)) return Outcome()
        val said = ArrayList<String>()
        var changed = false

        for (t in store.state.value.tunnels.filter { it.kind == Tunnel.SUB }) {
            if (t.auto) {
                if (tend(t, said)) changed = true
            } else if (store.state.value.settings.autoBypass) {
                bypass(t)?.let { said += it; changed = true }
            }
        }
        Outcome(said, changed)
    }

    // ------------------------------------------------------------------ a subscription that looks after itself

    /** Keeps the pool of a subscription good. Says whether it changed. */
    private suspend fun tend(t: Tunnel, said: MutableList<String>): Boolean {
        if (t.nodes.isEmpty()) return false
        val (_, usual) = Pool.split(t)
        val special = Pool.isSpecial(t, t.pool)

        if (t.pool.isNotEmpty()) {
            val alive = NodePing.measureAll(t.pool, samples = 1).values.count { it.answered }
            val enough = alive >= minOf(3, t.pool.size)
            // On servers for bypassing, the usual ones are looked at too, and taken back once they answer twice running.
            val usualBack = if (special && usual.isNotEmpty()) {
                val up = NodePing.anyAnswers(usual.map { t.nodes[it].url })
                upRun[t.id] = if (up) (upRun[t.id] ?: 0) + 1 else 0
                (upRun[t.id] ?: 0) >= 2
            } else {
                upRun[t.id] = 0
                false
            }
            if (enough && !usualBack) return false
        }

        val next = Pool.measure(t)
        if (next.isEmpty()) {
            AppLog.w("health", "«${t.title}»: ни один сервер не ответил, пул оставлен как был")
            return false
        }
        if (next == t.pool) return false
        var done = false
        TunorApp.store.update { s ->
            s.copy(tunnels = s.tunnels.map {
                // Not if the subscription was refreshed or switched to a hand-picked server in the meantime.
                if (it.id == t.id && it.nodes == t.nodes && it.auto) { done = true; it.copy(pool = next) } else it
            })
        }
        if (!done) return false
        val nowSpecial = Pool.isSpecial(t, next)
        AppLog.i("health", "«${t.title}»: пул серверов обновлён, серверов ${next.size}" + if (nowSpecial) ", все для обхода" else "")
        when {
            nowSpecial && !special -> said += "«${t.title}»: обычные серверы не отвечают, взял серверы для обхода (${next.size})."
            special && !nowSpecial -> said += "«${t.title}»: обычные серверы снова отвечают, вернулся к ним."
        }
        return true
    }

    // ------------------------------------------------------------------ a subscription with a server chosen by hand

    /** The old way, for a subscription left on one server: moves it to a server for bypassing when no usual one answers, and back. */
    private suspend fun bypass(t: Tunnel): String? {
        val special = t.nodes.indices.filter { Bypass.isSpecial(t.nodes[it].name) }
        val usual = t.nodes.indices.filter { it !in special }
        if (usual.isEmpty() || special.isEmpty()) return null

        val usualUp = NodePing.anyAnswers(usual.map { t.nodes[it].url })
        upRun[t.id] = if (usualUp) (upRun[t.id] ?: 0) + 1 else 0
        if (usualUp && (t.autoFrom.isEmpty() || (upRun[t.id] ?: 0) < 2)) return null

        // With the usual ones down only the special ones are worth timing; going back, all of them are.
        val which = if (usualUp) t.nodes.indices.toList() else special
        val timed = NodePing.measureAll(which.map { t.nodes[it].url })
        val answers = which.indices.associate { which[it] to timed.getValue(it) }

        val move = Bypass.decide(t, answers) ?: return null
        var done = false
        TunorApp.store.update { s ->
            s.copy(tunnels = s.tunnels.map {
                // Not if the person has moved it in the meantime.
                if (it.id == t.id && it.chosen == t.chosen && it.nodes == t.nodes) { done = true; Bypass.apply(it, move) } else it
            })
        }
        if (!done) return null
        val name = t.nodes[move.index].name
        AppLog.i("health", "«${t.title}»: " + if (move.back) "обычные серверы снова отвечают, вернулся на «$name»" else "обычные серверы не отвечают, переключился на «$name» (${move.ms} мс)")
        return if (move.back) "«${t.title}»: обычные серверы снова отвечают, вернулся на «$name»."
        else "«${t.title}»: обычные серверы не отвечают, переключился на «$name» (${move.ms} мс)."
    }
}
