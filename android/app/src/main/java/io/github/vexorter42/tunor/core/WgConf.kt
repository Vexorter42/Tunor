package io.github.vexorter42.tunor.core

import org.json.JSONArray
import org.json.JSONObject

/**
 * A WireGuard / AmneziaWG `.conf`: what it says and whether it can be used.
 *
 * Configs are copied out of Telegram or a web page and arrive with invisible characters (a byte-order
 * mark, zero-width spaces, soft hyphens) or with the key in quotes. The engine rejects all of those with
 * "illegal base64 data" and, since one bad endpoint fails the whole configuration, takes everything down
 * with it. So the invisible is dropped, non-breaking spaces become spaces and a quoted value is unwrapped.
 */
class WgConf private constructor(val iface: Map<String, String>, val peer: Map<String, String>) {

    /** Why this cannot be used, in words, or null when it can. */
    fun problem(): String? {
        val key = iface["PrivateKey"].orEmpty()
        if (key.isEmpty()) return "в конфиге нет PrivateKey"
        keyProblem(key, "PrivateKey")?.let { return it }
        if (iface["Address"].isNullOrBlank()) return "в конфиге нет Address"
        val pub = peer["PublicKey"].orEmpty()
        if (pub.isEmpty()) return "в конфиге нет PublicKey сервера"
        keyProblem(pub, "PublicKey")?.let { return it }
        val endpoint = peer["Endpoint"].orEmpty()
        if (endpoint.isEmpty() || !endpoint.contains(':')) return "в конфиге нет Endpoint (адрес:порт)"
        if (endpoint.startsWith("127.")) return "это шаблон, а не настоящий конфиг: Endpoint указывает на этот же телефон"
        return null
    }

    /** The endpoint of the engine's configuration; [detour] is the tag of what it is carried by, if it rides inside another tunnel. */
    fun toEndpoint(tag: String, detour: String? = null): JSONObject {
        val ep = JSONObject().put("type", "wireguard").put("tag", tag)
        if (detour != null) ep.put("detour", detour)
        iface["MTU"]?.toIntOrNull()?.let { ep.put("mtu", it) }

        val addresses = JSONArray()
        for (a in split(iface["Address"].orEmpty())) {
            addresses.put(if ('/' in a) a else if (':' in a) "$a/128" else "$a/32")
        }
        ep.put("address", addresses)
        ep.put("private_key", iface["PrivateKey"].orEmpty())

        // AmneziaWG 1.0 / 2.0
        for (k in listOf("Jc", "Jmin", "Jmax", "S1", "S2", "S3", "S4")) {
            iface[k]?.toIntOrNull()?.let { ep.put(k.lowercase(), it) }
        }
        for (k in listOf("H1", "H2", "H3", "H4", "I1", "I2", "I3", "I4", "I5")) {
            iface[k]?.let { ep.put(k.lowercase(), it) }
        }

        // AmneziaWG 3.x: ranged "min-max" strings
        for ((conf, json) in listOf(
            "ContentPaddingAddition" to "content_padding_addition",
            "RekeyAfterTime" to "rekey_after_time",
            "RekeyTimeout" to "rekey_timeout",
            "RejectAfterTime" to "reject_after_time",
            "KeepaliveTimeout" to "keepalive_timeout",
            "MaxHandshakeAttempts" to "max_handshake_attempts",
        )) {
            iface[conf]?.let { ep.put(json, it) }
        }
        iface["HeaderProtectionKey"]?.let { ep.put("header_protection_key", it) }
        if (iface["RandomTrailers"].equals("on", true)) ep.put("random_trailers", true)
        if (iface["DisableCookies"].equals("on", true)) ep.put("disable_cookies", true)

        val endpoint = peer["Endpoint"].orEmpty()
        val cut = endpoint.lastIndexOf(':')
        val host = if (cut > 0) endpoint.substring(0, cut).trim('[', ']') else endpoint
        val port = if (cut > 0) endpoint.substring(cut + 1).toIntOrNull() ?: 0 else 0

        val allowed = JSONArray()
        for (a in split(peer["AllowedIPs"] ?: "0.0.0.0/0, ::/0")) allowed.put(a)
        val p = JSONObject()
            .put("address", host)
            .put("port", port)
            .put("public_key", peer["PublicKey"].orEmpty())
            .put("allowed_ips", allowed)
        peer["PresharedKey"]?.let { p.put("pre_shared_key", it) }

        // Without it the session goes quiet, the far side forgets the way back, and the next packet waits
        // on a fresh handshake. A provider writes it into the config for exactly that reason.
        peer["PersistentKeepalive"]?.let { keepalive(it) }?.let { p.put("persistent_keepalive_interval", it) }
        ep.put("peers", JSONArray().put(p))
        return ep
    }

    companion object {
        fun parse(text: String): WgConf {
            // Key names are not case-sensitive in WireGuard: "JC" and "Jc" are the same parameter.
            val iface = java.util.TreeMap<String, String>(String.CASE_INSENSITIVE_ORDER)
            val peer = java.util.TreeMap<String, String>(String.CASE_INSENSITIVE_ORDER)
            var section: MutableMap<String, String>? = null
            for (raw in text.lineSequence()) {
                val line = clean(raw)
                if (line.isEmpty() || line.startsWith("#") || line.startsWith(";")) continue
                if (line.startsWith("[")) {
                    section = when (line.trim('[', ']', ' ').lowercase()) {
                        "interface" -> iface
                        "peer" -> if (peer.isEmpty()) peer else null       // one peer: a second would overwrite the first
                        else -> null
                    }
                    continue
                }
                val eq = line.indexOf('=')
                if (eq <= 0 || section == null) continue
                section[line.substring(0, eq).trim()] = unquote(line.substring(eq + 1).trim())
            }
            return WgConf(iface, peer)
        }

        private fun clean(raw: String): String {
            val sb = StringBuilder(raw.length)
            for (c in raw) {
                when {
                    c == ' ' -> sb.append(' ')
                    c == '﻿' || c == '​' || c == '‌' || c == '‍' || c == '­' || c == '⁠' -> {}
                    else -> sb.append(c)
                }
            }
            return sb.toString().trim()
        }

        private fun unquote(v: String): String =
            if (v.length >= 2 && ((v.first() == '"' && v.last() == '"') || (v.first() == '\'' && v.last() == '\''))) v.substring(1, v.length - 1) else v

        private fun split(s: String) = s.split(',', ' ', ';').map { it.trim() }.filter { it.isNotEmpty() }

        /** A WireGuard key is 32 bytes in base64: 44 characters, the last one "=". */
        private fun keyProblem(value: String, name: String): String? {
            val b64 = Regex("^[A-Za-z0-9+/]{43}=$")
            if (!b64.matches(value)) {
                val short = if (value.length > 16) value.take(16) + "…" else value
                return "$name не похож на ключ WireGuard («$short»): должно быть 44 символа base64"
            }
            return null
        }

        /**
         * The keepalive as the engine takes it: seconds, or in an AmneziaWG 3.x config a range such as
         * "25-35", which it picks from again every time. Anything else, and 0, means none.
         */
        private fun keepalive(value: String): Any? {
            val v = value.trim()
            v.toIntOrNull()?.let { return if (it > 0) it else null }
            val dash = v.indexOf('-')
            if (dash > 0) {
                val lo = v.substring(0, dash).trim().toIntOrNull()
                val hi = v.substring(dash + 1).trim().toIntOrNull()
                if (lo != null && hi != null && lo >= 0 && hi >= lo && hi > 0) return "$lo-$hi"
            }
            return null
        }
    }
}
