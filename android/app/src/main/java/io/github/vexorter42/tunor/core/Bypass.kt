package io.github.vexorter42.tunor.core

/**
 * What a phone does when the usual servers of a subscription all go quiet at once.
 *
 * Mobile internet is sometimes cut down to a short list of allowed sites, and every usual server stops answering
 * while the provider's special ones, made for exactly that, still do. Providers say so in the name of those
 * servers: "обход", "глушилка", "LTE". So when not one usual server answers, the subscription's own special ones
 * are timed and the nearest is taken; and when the usual ones answer again, the one that was in use before is taken back.
 *
 * Only a subscription that has both kinds is touched, and only to move between its own servers.
 */
object Bypass {
    // Word starts, not anywhere in a word: "lte" is inside "filter", and "обход" inside "необходимо".
    private val WORDS = Regex("""(?<![\p{L}])(?:обход|глушилк)|(?<![\p{L}])lte(?![\p{L}])""", RegexOption.IGNORE_CASE)

    fun isSpecial(name: String) = WORDS.containsMatchIn(name)

    /** Which server to be on: its place in the list, its answer time, and what to remember about the choice. */
    class Move(val index: Int, val ms: Int, val autoFrom: String, val back: Boolean)

    /**
     * What to do with this subscription, given how its servers answered. Null — leave it as it is.
     * [answers] are by place in the list and must cover every server when the usual ones are to be judged.
     */
    fun decide(t: Tunnel, answers: Map<Int, PingResult>): Move? {
        if (t.kind != Tunnel.SUB) return null
        val special = t.nodes.indices.filter { isSpecial(t.nodes[it].name) }
        val usual = t.nodes.indices.filter { it !in special }
        if (usual.isEmpty() || special.isEmpty()) return null

        fun ms(i: Int) = answers[i]?.ms
        val current = t.nodes.getOrNull(t.chosen)

        if (usual.any { ms(it) != null }) {
            // The usual ones answer. Only a move made by the app itself is undone, and never the person's own choice.
            if (t.autoFrom.isEmpty()) return null
            val before = t.nodes.indexOfFirst { it.url == t.autoFrom }
            val target = when {
                before >= 0 && ms(before) != null -> before
                else -> usual.filter { ms(it) != null }.minByOrNull { ms(it)!! }
            } ?: return null
            return Move(target, ms(target) ?: 0, "", back = true)
        }

        val best = special.filter { ms(it) != null }.minByOrNull { ms(it)!! } ?: return null
        if (best == t.chosen) return null
        // Already on a special one that answers about as well: not worth a switch every few minutes.
        val now = ms(t.chosen)
        if (current != null && isSpecial(current.name) && now != null && now <= ms(best)!! * 1.5 + 30) return null
        val from = when {
            t.autoFrom.isNotEmpty() -> t.autoFrom
            current != null && !isSpecial(current.name) -> current.url
            else -> ""
        }
        return Move(best, ms(best)!!, from, back = false)
    }

    /** The subscription with the move made. */
    fun apply(t: Tunnel, m: Move) = t.copy(chosen = m.index, autoFrom = m.autoFrom)
}
