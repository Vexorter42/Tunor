package io.github.vexorter42.tunor.core

import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.net.Authenticator
import java.net.HttpURLConnection
import java.net.InetSocketAddress
import java.net.PasswordAuthentication
import java.net.Proxy
import java.net.URL
import java.util.Locale

/**
 * Local entrances to the engine that each lead to one place whatever the rules say: one to every tunnel that
 * is running, one to the direct road. The connection check goes in through them, to ask each road where it
 * comes out. They listen on the phone's own loopback and want a password made fresh each time the engine starts.
 */
class Door(val user: String, val password: String, val direct: Int, val tunnels: Map<String, Int>) {
    companion object {
        /** Doors for these tunnels, by id. */
        fun fresh(ids: List<String>): Door {
            val chars = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789"
            val rnd = java.security.SecureRandom()
            val password = (1..24).map { chars[rnd.nextInt(chars.length)] }.joinToString("")
            return Door("tunor", password, 21081, ids.withIndex().associate { (i, id) -> id to 21082 + i })
        }
    }
}

/** Where a road comes out: what Cloudflare sees of a request that arrives by it. */
class Trace(val ip: String, val country: String, val colo: String) {
    val place get() = listOf(country, colo).filter { it.isNotEmpty() }.joinToString(" · ")
}

object Tracer {
    private const val URL_TRACE = "https://www.cloudflare.com/cdn-cgi/trace"

    /** @param port the loopback port of a door, or null to ask without one — the way an app of the phone would. */
    suspend fun ask(door: Door?, port: Int?): Result<Trace> = withContext(Dispatchers.IO) {
        try {
            val c = if (door != null && port != null) {
                Authenticator.setDefault(object : Authenticator() {
                    override fun getPasswordAuthentication() = PasswordAuthentication(door.user, door.password.toCharArray())
                })
                URL(URL_TRACE).openConnection(Proxy(Proxy.Type.SOCKS, InetSocketAddress("127.0.0.1", port))) as HttpURLConnection
            } else {
                URL(URL_TRACE).openConnection() as HttpURLConnection
            }
            c.connectTimeout = 8_000
            c.readTimeout = 8_000
            if (c.responseCode != 200) return@withContext Result.failure(Exception("ответ ${c.responseCode}"))
            val fields = c.inputStream.bufferedReader().readLines().mapNotNull {
                val i = it.indexOf('=')
                if (i > 0) it.substring(0, i) to it.substring(i + 1) else null
            }.toMap()
            val loc = fields["loc"].orEmpty()
            val name = if (loc.length == 2) Locale("", loc).getDisplayCountry(Locale("ru")).ifEmpty { loc } else loc
            Result.success(Trace(fields["ip"].orEmpty(), name, fields["colo"].orEmpty()))
        } catch (e: Exception) {
            Result.failure(e)
        }
    }
}
