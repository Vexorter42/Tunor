package io.github.vexorter42.tunor.core

/**
 * The few servers of a subscription the engine chooses among, when the app is left to look after the choice.
 *
 * A server that answers a connection can still be a server that does not work: its port is open and what sits
 * behind it is gone, or it is full, or the provider has cut it off. Opening a connection cannot tell. So the app
 * only narrows the list to the nearest few, by that, and the engine does the rest: it sends a real request through
 * each (a `urltest` group), takes the one that works best, and goes on testing, so a server that dies is left
 * and one that returns is taken back with nobody pressing anything.
 *
 * The usual servers are preferred; the ones named for getting round a cut-down mobile network (see [Bypass]) are
 * taken when not one usual server answers, which is the moment they are for.
 */
object Pool {
    /** How many servers the engine is given: enough that a few can be dead, few enough that testing them is cheap. */
    const val SIZE = 8

    /** The places in the list of the servers named for bypassing, and of the others. */
    fun split(t: Tunnel): Pair<List<Int>, List<Int>> {
        val special = t.nodes.indices.filter { Bypass.isSpecial(t.nodes[it].name) }
        val usual = t.nodes.indices.filter { it !in special }
        return special to usual
    }

    private fun nearest(t: Tunnel, which: List<Int>, answers: Map<Int, PingResult>): List<String> =
        which.filter { answers[it]?.ms != null }.sortedBy { answers.getValue(it).ms }.take(SIZE).map { t.nodes[it].url }

    /** For every day: the nearest usual servers that answer; when none does, the nearest of the special ones. */
    fun daily(t: Tunnel, answers: Map<Int, PingResult>): List<String> {
        val (special, usual) = split(t)
        return nearest(t, usual, answers).ifEmpty { nearest(t, special, answers) }
    }

    /** For the mobile network that lets only some through: the nearest special servers, whatever the usual ones do. */
    fun rescue(t: Tunnel, answers: Map<Int, PingResult>): List<String> = nearest(t, split(t).first, answers)

    /** Whether these links are all of servers named for bypassing: it says which kind a pool is made of. */
    fun isSpecial(t: Tunnel, links: List<String>): Boolean {
        if (links.isEmpty()) return false
        val names = t.nodes.associate { it.url to it.name }
        return links.all { names[it]?.let(Bypass::isSpecial) == true }
    }

    /** Times the servers (every one, twice) and takes the pool out of the answers. Empty when none answers. */
    suspend fun measure(t: Tunnel, special: Boolean = false): List<String> {
        val which = if (special) split(t).first else t.nodes.indices.toList()
        if (which.isEmpty()) return emptyList()
        val timed = NodePing.measureAll(which.map { t.nodes[it].url }, samples = 2)
        val answers = which.indices.associate { which[it] to timed.getValue(it) }
        return if (special) rescue(t, answers) else daily(t, answers)
    }
}
