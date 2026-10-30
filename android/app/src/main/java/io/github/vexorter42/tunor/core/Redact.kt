package io.github.vexorter42.tunor.core

/**
 * Takes out of a piece of text what a report must not carry: share links, keys, passwords, the numbers of other
 * people's servers and the names of the sites that were opened. A report is sent to someone, and "it can be
 * forwarded without reading" is only true if this is done to everything that goes in it.
 *
 * It errs on the side of taking too much: a host name that was the cause of a failure turns into "<домен>", and the
 * words around it ("не удалось найти", "время вышло") are what say what failed.
 */
object Redact {
    private val link = Regex("""[A-Za-z][A-Za-z0-9+.\-]*://[^\s"'<>]+""")
    private val secret = Regex("""(?i)\b(private[_ ]?key|preshared[_ ]?key|public[_ ]?key|password|passwd|secret|token|uuid|short[_ ]?id)\b(\s*[=:]\s*)\S+""")
    private val wgKey = Regex("""\b[A-Za-z0-9+/]{43}=""")
    private val uuid = Regex("""\b[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}\b""")
    private val ipv4 = Regex("""\b(?:\d{1,3}\.){3}\d{1,3}\b""")
    private val ipv6 = Regex("""\b(?:[0-9a-fA-F]{1,4}:){3,7}[0-9a-fA-F]{1,4}\b""")
    private val host = Regex("""\b(?:[A-Za-z0-9\-]+\.)+[A-Za-z]{2,}\b""")

    /** Addresses that name a service of the app's own and say nothing about the person. */
    private val known = listOf("cloudflare.com", "github.com", "githubusercontent.com", "gh-proxy.com", "ghfast.top", "gstatic.com")

    /** A name that is the app's own, or a service of the app's own: it says nothing about the person. */
    private fun keepHost(h: String) = h.startsWith("io.github.vexorter42.tunor") || known.any { h == it || h.endsWith(".$it") }

    private fun keepIp(ip: String): Boolean {
        val p = ip.split('.').mapNotNull { it.toIntOrNull() }
        if (p.size != 4 || p.any { it > 255 }) return true       // not an address after all: a version, a counter
        return p[0] == 10 || p[0] == 127 || p[0] == 0 ||
            (p[0] == 192 && p[1] == 168) || (p[0] == 172 && p[1] in 16..31) || (p[0] == 169 && p[1] == 254) ||
            ip == "1.1.1.1" || ip == "1.0.0.1"
    }

    fun text(s: String): String {
        var t = s
        t = link.replace(t) { m ->
            val h = m.value.substringAfter("://").substringBefore('/').substringBefore('?').substringAfterLast('@').substringBefore(':')
            if (keepHost(h)) m.value.substringBefore('?') else "<ссылка>"
        }
        t = secret.replace(t) { m -> m.groupValues[1] + m.groupValues[2] + "<скрыто>" }
        t = wgKey.replace(t, "<ключ>")
        t = uuid.replace(t, "<uuid>")
        t = ipv4.replace(t) { m -> if (keepIp(m.value)) m.value else "<ip>" }
        t = ipv6.replace(t, "<ip6>")
        t = host.replace(t) { m ->
            if (keepHost(m.value.lowercase())) m.value else "<домен>"
        }
        return t
    }

    /**
     * Only the secrets and the numbers of servers: for a text whose lines are all file names and function names, such as
     * an engine's stack, where taking out what looks like a host name would take out the places it names.
     */
    fun lite(s: String): String {
        var t = link.replace(s, "<ссылка>")
        t = secret.replace(t) { m -> m.groupValues[1] + m.groupValues[2] + "<скрыто>" }
        t = wgKey.replace(t, "<ключ>")
        t = uuid.replace(t, "<uuid>")
        t = ipv4.replace(t) { m -> if (keepIp(m.value)) m.value else "<ip>" }
        return ipv6.replace(t, "<ip6>")
    }

    /**
     * A stack trace keeps its class names and file lines, which say where it broke; only the message of each
     * exception, which can hold an address or a name, goes through [text].
     */
    fun trace(s: String): String = s.lineSequence().joinToString("\n") { line ->
        val head = Regex("""^((?:Caused by: |Suppressed: )?[\w.$]+)(: )(.*)$""").find(line)
        if (head != null && !line.trimStart().startsWith("at ")) head.groupValues[1] + head.groupValues[2] + text(head.groupValues[3]) else line
    }
}
