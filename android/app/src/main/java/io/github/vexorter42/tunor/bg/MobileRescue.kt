package io.github.vexorter42.tunor.bg

import android.content.Context
import io.github.vexorter42.tunor.TunorApp
import io.github.vexorter42.tunor.core.AppLog
import io.github.vexorter42.tunor.core.ConfigBuilder
import io.github.vexorter42.tunor.core.Door
import io.github.vexorter42.tunor.core.NodePing
import io.github.vexorter42.tunor.core.Pool
import io.github.vexorter42.tunor.core.Rescue
import io.github.vexorter42.tunor.core.State
import io.github.vexorter42.tunor.core.Tracer
import io.github.vexorter42.tunor.core.Tunnel
import kotlinx.coroutines.async
import kotlinx.coroutines.awaitAll
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow

/**
 * A way out for the mobile network that lets only some things through.
 *
 * At home, on Wi-Fi, WARP and geo carry what the rules send them, and nothing here does anything. On the
 * mobile network the carrier sometimes cuts the phone down to a short list of allowed addresses, and then neither of
 * them gets through. When that is so for ten seconds, the subscription's own servers for such a network (those
 * named "обход", "глушилка", "LTE", "оператор") are taken and everything goes through the nearest of them, the engine
 * choosing among them by real requests. When WARP and geo answer again, twice running, or the phone is back on
 * Wi-Fi, everything goes back the way the person set it.
 *
 * Nothing the person wrote is changed: this is a plan the config is built with while it lasts, and it is gone
 * with the tunnel.
 */
object MobileRescue {
    private val flow = MutableStateFlow<Rescue?>(null)
    /** The plan in force, or null when everything is as the person set it. */
    val plan: StateFlow<Rescue?> = flow.asStateFlow()

    /** How long the usual ways get to answer before the phone gives up on them. */
    private const val WINDOW_MS = 10_000L

    private var backRun = 0
    private var saidNoServersAt = 0L

    /** A fresh start of the tunnel begins with the usual ways. */
    fun reset() {
        flow.value = null
        backRun = 0
    }

    /** The subscription that would be taken: the one named in the settings, or the first one that has servers for it. */
    fun subOf(state: State): Tunnel? {
        val live = ConfigBuilder.live(state.tunnels).filter { it.kind == Tunnel.SUB && Pool.split(it).first.isNotEmpty() }
        val named = state.settings.mobileRescueSub
        return if (named.isNotEmpty()) live.firstOrNull { it.id == named } else live.firstOrNull()
    }

    /** One look, called while the tunnel runs: when the network changes, and every minute on a mobile one. */
    suspend fun look(context: Context): Health.Outcome {
        val state = TunorApp.store.state.value
        val sub = subOf(state)
        if (!state.settings.mobileRescue || sub == null) return end("")

        when (NetMonitor.kind()) {
            "мобильная сеть" -> Unit
            "Wi-Fi" -> return if (flow.value != null) end("Wi-Fi: всё снова идёт, как ты настроил.") else Health.Outcome()
            // No network for a moment, or one that is neither: nothing to judge, and what is in force stays.
            else -> return Health.Outcome()
        }
        val door = Vpn.door ?: return Health.Outcome()
        // The usual ways: every tunnel that is up except the subscription itself, by the door that leads into each.
        val usual = ConfigBuilder.live(state.tunnels).filter { it.id != sub.id }.mapNotNull { door.tunnels[it.id] }
        if (usual.isEmpty()) return Health.Outcome()

        val plan = flow.value
        if (plan == null) {
            // The servers for this are timed while the last seconds of the wait run, not after them: the wait is long enough already.
            var worked = false
            val servers = coroutineScope {
                val probe = async { works(door, usual, WINDOW_MS) }
                val timing = async { delay(4_000); Pool.measure(sub, special = true) }
                worked = probe.await()
                if (worked) { timing.cancel(); emptyList() } else timing.await()
            }
            if (worked) return Health.Outcome()
            AppLog.w("rescue", "мобильная сеть: ни один из обычных путей не ответил за ${WINDOW_MS / 1000} секунд")
            if (servers.isEmpty()) {
                val now = System.currentTimeMillis()
                AppLog.w("rescue", "серверов для обхода в «${sub.title}», которые отвечали бы, нет")
                if (now - saidNoServersAt > 5 * 60_000L) {
                    saidNoServersAt = now
                    return Health.Outcome(listOf("Мобильная сеть: обычные пути не отвечают, но и серверы для обхода в «${sub.title}» тоже."), false)
                }
                return Health.Outcome()
            }
            backRun = 0
            flow.value = Rescue(sub.id, servers)
            AppLog.i("rescue", "включён: всё идёт через «${sub.title}», серверов для обхода ${servers.size}")
            return Health.Outcome(listOf("Мобильная сеть: обычные пути не отвечают. Всё идёт через «${sub.title}» (серверов для обхода: ${servers.size})."), true)
        }

        // Already on the way round: has the usual one come back? Twice running, so that one lucky answer does not undo it.
        if (works(door, usual, 5_000L)) backRun++ else backRun = 0
        if (backRun >= 2) return end("Обычные пути снова отвечают: всё идёт, как ты настроил.")

        // And are the servers of the plan still there? The engine passes over a dead one by itself; when all are dead, choose again.
        if (!NodePing.anyAnswers(plan.servers)) {
            val fresh = Pool.measure(sub, special = true)
            if (fresh.isNotEmpty() && fresh != plan.servers) {
                flow.value = Rescue(sub.id, fresh)
                AppLog.i("rescue", "серверы для обхода выбраны заново: ${fresh.size}")
                return Health.Outcome(emptyList(), true)
            }
        }
        return Health.Outcome()
    }

    /** Puts everything back; says [text] if the plan was in force. */
    private fun end(text: String): Health.Outcome {
        if (flow.value == null) return Health.Outcome()
        flow.value = null
        backRun = 0
        AppLog.i("rescue", "выключен" + if (text.isNotEmpty()) ": $text" else "")
        return Health.Outcome(listOfNotNull(text.ifEmpty { null }), true)
    }

    /**
     * Whether any of the usual ways gets a request through, in at most [windowMs]: tries again and again while time
     * is left, since a mobile network is often slow to begin and the first try is not the verdict.
     */
    private suspend fun works(door: Door, ports: List<Int>, windowMs: Long): Boolean {
        val start = System.currentTimeMillis()
        while (true) {
            val left = windowMs - (System.currentTimeMillis() - start)
            val timeout = left.coerceIn(2_500L, 5_000L).toInt()
            val ok = coroutineScope { ports.map { async { Tracer.ask(door, it, timeout).isSuccess } }.awaitAll() }.any { it }
            if (ok) return true
            if (System.currentTimeMillis() - start >= windowMs) return false
            delay(500)
        }
    }
}
