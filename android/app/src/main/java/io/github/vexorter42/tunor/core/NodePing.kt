package io.github.vexorter42.tunor.core

import io.github.vexorter42.tunor.bg.Vpn
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.async
import kotlinx.coroutines.cancelChildren
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.joinAll
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Semaphore
import kotlinx.coroutines.sync.withPermit
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.Socket
import kotlin.coroutines.coroutineContext

/** How long a server took to answer, or why it is not known. */
class PingResult(val ms: Int?, val note: String = "") {
    val answered get() = ms != null
    val text get() = ms?.let { "$it мс" } ?: note.ifEmpty { "нет ответа" }
}

/**
 * "Which of the servers of this subscription is the near one?" The time it takes to open a TCP connection to the
 * server's own port: the same thing a client does first, so it is the number that matters, where an ICMP ping would
 * be refused by many servers that work fine. Each server is tried three times and the middle result is taken,
 * because the first connection is often slower than the rest and one lost packet should not make a good server
 * look dead.
 *
 * It measures the way to the server from this phone, around the tunnel when one is up (the sockets are kept out
 * of it), not what the server does with the traffic after that. A server that speaks only UDP cannot be measured
 * this way, which is said and not guessed.
 */
object NodePing {
    private const val TIMEOUT_MS = 3_000
    private const val SAMPLES = 3
    private const val IN_PARALLEL = 8

    /** Where the server is: the host and port out of its link; null if the link cannot be read. */
    fun target(url: String): Pair<String, Int>? {
        val o = VlessLink.parse(url).outbound ?: return null
        val host = o.optString("server")
        val port = o.optInt("server_port", 0)
        return if (host.isEmpty() || port !in 1..65535) null else host to port
    }

    /** Why this server cannot be timed by opening a connection; null if it can. */
    fun notMeasurable(url: String): String? {
        val o = VlessLink.parse(url).outbound ?: return "ссылка не читается"
        val transport = o.optJSONObject("transport")?.optString("type").orEmpty()
        return if (transport.equals("quic", true)) "работает по UDP, по TCP не измерить" else null
    }

    private fun connect(address: InetAddress, port: Int): Double? {
        val s = Socket()
        return try {
            // Bound first: a socket that does not exist yet cannot be kept out of the tunnel, and one that is in it
            // "connects" to anything, because the tunnel answers for every address and goes on to the real one by itself.
            s.bind(null)
            if (!Vpn.protect(s)) return null
            s.tcpNoDelay = true
            val start = System.nanoTime()
            s.connect(InetSocketAddress(address, port), TIMEOUT_MS)
            (System.nanoTime() - start) / 1_000_000.0
        } catch (_: Exception) {
            null
        } finally {
            try { s.close() } catch (_: Exception) { }
        }
    }

    /** Host name to an address, IPv4 first: a server that has both answers faster over the one the whole world still mostly uses. */
    private fun resolve(host: String): InetAddress? = try {
        InetAddress.getAllByName(host).let { all -> all.firstOrNull { it.address.size == 4 } ?: all.firstOrNull() }
    } catch (_: Exception) {
        null
    }

    private fun middle(answers: List<Double>): Int? {
        if (answers.isEmpty()) return null
        val s = answers.sorted()
        val mid = if (s.size % 2 == 1) s[s.size / 2] else (s[s.size / 2 - 1] + s[s.size / 2]) / 2
        return maxOf(1, Math.round(mid).toInt())
    }

    private fun measure(url: String, samples: Int, resolved: MutableMap<String, InetAddress?>): PingResult {
        notMeasurable(url)?.let { return PingResult(null, it) }
        val (host, port) = target(url) ?: return PingResult(null, "ссылка не читается")
        // One lookup per host, so that a provider's twenty servers on one name cost one.
        val address = synchronized(resolved) { resolved.getOrPut(host) { resolve(host) } } ?: return PingResult(null, "имя сервера не находится")
        val answers = ArrayList<Double>()
        for (i in 0 until samples) {
            connect(address, port)?.let { answers += it }
            if (i < samples - 1) Thread.sleep(120)
        }
        return middle(answers)?.let { PingResult(it) } ?: PingResult(null, "нет ответа")
    }

    /**
     * Times every server, some at a time. [done] is told after each with how many are done so far.
     * The answers are by the server's place in the list.
     */
    suspend fun measureAll(urls: List<String>, done: (Int, Int) -> Unit = { _, _ -> }): Map<Int, PingResult> = coroutineScope {
        val resolved = HashMap<String, InetAddress?>()
        val gate = Semaphore(IN_PARALLEL)
        val finished = java.util.concurrent.atomic.AtomicInteger()
        val answers = urls.mapIndexed { i, url ->
            async(Dispatchers.IO) {
                val r = gate.withPermit { measure(url, SAMPLES, resolved) }
                done(finished.incrementAndGet(), urls.size)
                i to r
            }
        }
        answers.map { it.await() }.toMap()
    }

    /**
     * Whether any of these servers answers at all, given up as soon as one does: when all is well that is a few
     * milliseconds, and the whole list is only walked through when all is not. Twice each, so that one lost packet is not "no".
     */
    suspend fun anyAnswers(urls: List<String>): Boolean = coroutineScope {
        if (urls.isEmpty()) return@coroutineScope false
        val found = CompletableDeferred<Boolean>()
        val resolved = HashMap<String, InetAddress?>()
        val gate = Semaphore(IN_PARALLEL)
        val jobs = urls.map { url ->
            launch(Dispatchers.IO) {
                gate.withPermit { if (!found.isCompleted && measure(url, 2, resolved).answered) found.complete(true) }
            }
        }
        launch { jobs.joinAll(); found.complete(false) }
        val answer = found.await()
        coroutineContext.cancelChildren()
        answer
    }
}
