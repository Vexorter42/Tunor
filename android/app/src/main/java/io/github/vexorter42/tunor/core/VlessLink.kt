package io.github.vexorter42.tunor.core

import org.json.JSONArray
import org.json.JSONObject
import java.net.IDN
import java.net.URI
import java.net.URLDecoder

/**
 * Turns a `vless://` share link into an outbound of the engine's configuration.
 *
 * `vless://uuid@host:port?query#name` is what every panel hands out, but the query is Xray's, not the
 * engine's, and services differ in what they put there. What this understands is listed in [parse];
 * anything else is reported in plain words rather than written into the configuration, because one
 * outbound the engine cannot parse stops the whole file, and with it every other tunnel.
 */
object VlessLink {
    class Parsed(val outbound: JSONObject?, val name: String, val summary: String, val problem: String?) {
        val ok get() = outbound != null
    }

    private fun bad(why: String) = Parsed(null, "", "", why)

    fun looksLike(text: String) = text.trimStart().startsWith("vless://", ignoreCase = true)

    fun parse(link: String): Parsed {
        val text = link.trim()
        if (!looksLike(text)) return bad("ссылка должна начинаться с vless://")

        val uri = try { URI(text) } catch (e: Exception) { return bad("ссылку не разобрать: " + (e.message ?: "")) }
        val uuid = decode(uri.rawUserInfo.orEmpty())
        if (uuid.isEmpty()) return bad("в ссылке нет идентификатора (часть до @)")
        if (!Regex("^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$").matches(uuid)) {
            return bad("идентификатор не похож на UUID: «${short(uuid)}»")
        }
        val rawHost = uri.host.orEmpty().trim('[', ']')
        if (rawHost.isEmpty()) return bad("в ссылке нет адреса сервера")
        val host = try { IDN.toASCII(rawHost) } catch (e: Exception) { rawHost }
        val port = uri.port
        if (port <= 0 || port > 65535) return bad("в ссылке нет порта сервера или он неправильный")

        val q = HashMap<String, String>()
        for (pair in uri.rawQuery.orEmpty().split('&')) {
            if (pair.isEmpty()) continue
            val eq = pair.indexOf('=')
            val key = decode(if (eq < 0) pair else pair.substring(0, eq))
            q[key] = decode(if (eq < 0) "" else pair.substring(eq + 1)).trim()
        }
        fun get(key: String) = q[key].orEmpty()

        val name = decode(uri.rawFragment.orEmpty()).ifEmpty { host }
        val o = JSONObject().put("type", "vless").put("server", host).put("server_port", port).put("uuid", uuid)

        // Xray writes encryption=none for plain VLESS; anything else is VLESS Encryption, which the engine supports.
        val enc = get("encryption")
        if (enc.isNotEmpty() && !enc.equals("none", true)) o.put("encryption", enc)

        val flow = get("flow")
        if (flow.isNotEmpty()) {
            if (!flow.equals("xtls-rprx-vision", true)) return bad("поток «${short(flow)}» этот движок не поддерживает; работает только xtls-rprx-vision")
            o.put("flow", flow)
        }

        var security = get("security").lowercase()
        if (security.isEmpty()) security = if (get("pbk").isNotEmpty()) "reality" else "none"

        val parts = ArrayList<String>()
        if (security == "tls" || security == "reality" || security == "xtls") {
            val tls = JSONObject().put("enabled", true)
            val sni = get("sni").ifEmpty { get("peer") }.ifEmpty { get("host") }
            if (sni.isNotEmpty()) tls.put("server_name", sni)

            val alpn = get("alpn")
            if (alpn.isNotEmpty()) {
                val a = JSONArray()
                alpn.split(',').map { it.trim() }.filter { it.isNotEmpty() }.forEach { a.put(it) }
                if (a.length() > 0) tls.put("alpn", a)
            }
            val fp = get("fp")
            if (fp.isNotEmpty()) tls.put("utls", JSONObject().put("enabled", true).put("fingerprint", fp))

            if (security == "reality") {
                val pbk = get("pbk")
                if (pbk.isEmpty()) return bad("для Reality нужен публичный ключ (pbk), в ссылке его нет")
                val reality = JSONObject().put("enabled", true).put("public_key", pbk)
                get("sid").takeIf { it.isNotEmpty() }?.let { reality.put("short_id", it) }
                tls.put("reality", reality)
                // Reality pins the certificate itself; uTLS is what makes it look ordinary.
                if (!tls.has("utls")) tls.put("utls", JSONObject().put("enabled", true).put("fingerprint", "chrome"))
                parts += "Reality"
            } else parts += "TLS"

            val insecure = get("allowInsecure").ifEmpty { get("insecure") }
            if (insecure == "1" || insecure == "true") tls.put("insecure", true)
            o.put("tls", tls)
        } else if (security != "" && security != "none") return bad("защита «${short(security)}» не поддерживается")

        val type = get("type").lowercase().ifEmpty { "tcp" }
        when (type) {
            "tcp", "raw" -> {}
            "ws", "websocket" -> {
                val tr = JSONObject().put("type", "ws")
                get("path").takeIf { it.isNotEmpty() }?.let { tr.put("path", it) }
                get("host").takeIf { it.isNotEmpty() }?.let { tr.put("headers", JSONObject().put("Host", it)) }
                o.put("transport", tr)
            }
            "httpupgrade" -> {
                val tr = JSONObject().put("type", "httpupgrade")
                get("path").takeIf { it.isNotEmpty() }?.let { tr.put("path", it) }
                get("host").takeIf { it.isNotEmpty() }?.let { tr.put("host", it) }
                o.put("transport", tr)
            }
            "grpc" -> {
                val tr = JSONObject().put("type", "grpc")
                get("serviceName").takeIf { it.isNotEmpty() }?.let { tr.put("service_name", it) }
                o.put("transport", tr)
            }
            "http", "h2" -> {
                val tr = JSONObject().put("type", "http")
                get("path").takeIf { it.isNotEmpty() }?.let { tr.put("path", it) }
                get("host").takeIf { it.isNotEmpty() }?.let { h ->
                    tr.put("host", JSONArray().also { a -> h.split(',').map { it.trim() }.filter { it.isNotEmpty() }.forEach(a::put) })
                }
                o.put("transport", tr)
            }
            "quic" -> o.put("transport", JSONObject().put("type", "quic"))
            "xhttp", "splithttp" -> {
                val tr = JSONObject().put("type", "xhttp")
                get("path").takeIf { it.isNotEmpty() }?.let { tr.put("path", it) }
                get("host").takeIf { it.isNotEmpty() }?.let { tr.put("host", it) }
                get("mode").takeIf { it.isNotEmpty() && !it.equals("auto", true) }?.let { tr.put("mode", it) }
                o.put("transport", tr)
            }
            else -> return bad("транспорт «${short(type)}» не поддерживается")
        }

        parts.add(0, if (type == "raw") "tcp" else type)
        if (flow.isNotEmpty()) parts += "Vision"
        if (o.has("encryption")) parts += "шифрование VLESS"
        return Parsed(o, name, "$host:$port · ${parts.joinToString(" · ")}", null)
    }

    private fun decode(s: String) = try { URLDecoder.decode(s.replace("+", "%2B"), "UTF-8") } catch (e: Exception) { s }
    private fun short(s: String) = if (s.length <= 24) s else s.take(24) + "…"
}
