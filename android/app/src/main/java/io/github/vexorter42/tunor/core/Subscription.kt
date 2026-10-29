package io.github.vexorter42.tunor.core

import android.util.Base64
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.net.HttpURLConnection
import java.net.URL

/**
 * A subscription is one address that answers with many share links, so a provider can change its servers
 * without anyone adding them again by hand.
 *
 * The body is either the links themselves, one per line, or that same text in base64: both are in the
 * wild and which one turns up is not announced. The address is a credential, since whoever holds it holds
 * the keys to every server in it. It is kept on the phone and goes nowhere except to fetch the list.
 */
object Subscription {
    class Result(val nodes: List<Node>, val title: String, val problem: String?) {
        val ok get() = problem == null
    }

    fun looksLike(text: String): Boolean {
        val t = text.trim()
        return (t.startsWith("http://", true) || t.startsWith("https://", true)) && !t.contains(' ')
    }

    suspend fun fetch(url: String): Result = withContext(Dispatchers.IO) {
        if (!looksLike(url)) return@withContext Result(emptyList(), "", "это не похоже на адрес подписки")
        try {
            val c = URL(url.trim()).openConnection() as HttpURLConnection
            c.connectTimeout = 20_000
            c.readTimeout = 40_000
            c.setRequestProperty("User-Agent", "Tunor-Android/1")
            val code = c.responseCode
            if (code != 200) return@withContext Result(emptyList(), "", "сервер ответил $code")
            val body = c.inputStream.bufferedReader().use { it.readText() }
            val title = decodeTitle(c.getHeaderField("profile-title"))
            parse(body, title)
        } catch (e: Exception) {
            Result(emptyList(), "", "не удалось получить список: " + (e.message ?: e.javaClass.simpleName))
        }
    }

    fun parse(body: String, title: String = ""): Result {
        val text = unwrap(body)
        val nodes = ArrayList<Node>()
        var other = 0
        var firstProblem: String? = null
        for (raw in text.lineSequence()) {
            val line = raw.trim()
            if (line.isEmpty()) continue
            if (!VlessLink.looksLike(line)) { other++; continue }
            val parsed = VlessLink.parse(line)
            if (parsed.ok) nodes += Node(parsed.name, line) else if (firstProblem == null) firstProblem = parsed.problem
        }
        if (nodes.isEmpty()) {
            return Result(emptyList(), title, when {
                firstProblem != null -> "ссылки vless:// в списке не разобрать: $firstProblem"
                other > 0 -> "в списке нет серверов, которые поймёт движок (нужны ссылки vless://)"
                else -> "список пуст"
            })
        }
        return Result(nodes, title, null)
    }

    /** Plain links stay as they are; anything else is tried as base64, in either alphabet. */
    private fun unwrap(body: String): String {
        val t = body.trim()
        if (t.contains("://")) return t
        for (flags in intArrayOf(Base64.DEFAULT, Base64.URL_SAFE)) {
            try {
                val decoded = String(Base64.decode(t, flags), Charsets.UTF_8)
                if (decoded.contains("://")) return decoded
            } catch (_: IllegalArgumentException) { }
        }
        return t
    }

    /** Providers write the plan's name into a header, sometimes as "base64:…". */
    private fun decodeTitle(header: String?): String {
        val h = header?.trim().orEmpty()
        if (h.startsWith("base64:")) {
            return try { String(Base64.decode(h.removePrefix("base64:"), Base64.DEFAULT), Charsets.UTF_8) } catch (_: Exception) { "" }
        }
        return h
    }
}
