package io.github.vexorter42.tunor.core

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.net.HttpURLConnection
import java.net.URL

/**
 * Fetches something that lives on GitHub: from GitHub itself first, and through a mirror if GitHub does not
 * answer. The same order as the desktop app's, for the same reasons: a mirror is a way round a block, not the
 * way to the files, and a mirror that has been the answer once is the one most likely to be gone next time.
 *
 * Nothing waits long for a dead address. What worked last is tried first on the next file of this run, so
 * where GitHub is blocked it costs one wait and not one per file.
 */
object GitHubFetch {
    /** Nothing worked. [unreachable] is true when no place even answered, as opposed to saying "no such file". */
    class Failed(message: String, val unreachable: Boolean) : Exception(message)

    private class Refused(code: Int) : Exception("ответ $code")

    private val mirrors = listOf("https://gh-proxy.com/", "https://ghfast.top/")
    private const val CONNECT_MS = 12_000
    private const val READ_MS = 20_000
    private const val DIRECT = "GitHub"

    @Volatile private var lastGood = DIRECT

    private fun ways(url: String): List<Pair<String, String>> {
        val all = listOf(DIRECT to url) + mirrors.map { ("зеркало " + it.removePrefix("https://").trimEnd('/')) to (it + url) }
        val first = all.indexOfFirst { it.first == lastGood }
        return if (first > 0) listOf(all[first]) + all.filterIndexed { i, _ -> i != first } else all
    }

    /** A file, whole. Throws [Failed] when no place had it, and when it is bigger than [maxBytes]. */
    suspend fun bytes(url: String, maxBytes: Int): ByteArray = withContext(Dispatchers.IO) {
        val why = ArrayList<String>()
        var answered = false
        for ((name, address) in ways(url)) {
            try {
                val got = once(address, maxBytes)
                lastGood = name
                return@withContext got
            } catch (e: Refused) {
                answered = true
                why += "$name: ${e.message}"
            } catch (e: Exception) {
                why += "$name: ${reason(e)}"
            }
        }
        throw Failed(why.joinToString("; "), unreachable = !answered)
    }

    suspend fun text(url: String, maxBytes: Int): String = bytes(url, maxBytes).toString(Charsets.UTF_8).trimStart('\uFEFF')

    private fun once(address: String, maxBytes: Int): ByteArray {
        val c = URL(address).openConnection() as HttpURLConnection
        try {
            c.connectTimeout = CONNECT_MS
            c.readTimeout = READ_MS
            c.setRequestProperty("User-Agent", "Tunor-Android/1")
            if (c.responseCode != 200) throw Refused(c.responseCode)
            val out = java.io.ByteArrayOutputStream()
            val buffer = ByteArray(16 * 1024)
            c.inputStream.use { body ->
                while (true) {
                    val n = body.read(buffer)
                    if (n < 0) break
                    out.write(buffer, 0, n)
                    if (out.size() > maxBytes) throw java.io.IOException("файл больше $maxBytes байт")
                }
            }
            return out.toByteArray()
        } finally {
            c.disconnect()
        }
    }

    private fun reason(e: Exception) = when (e) {
        is java.net.SocketTimeoutException -> "нет ответа"
        is java.net.UnknownHostException -> "адрес не находится"
        else -> e.message ?: e.javaClass.simpleName
    }
}
