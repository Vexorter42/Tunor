package io.github.vexorter42.tunor.core

import android.content.Context
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import org.json.JSONException
import org.json.JSONObject
import java.io.File

/** One ready-made list: what it is for and where to get it. [route] is the tunnel it is meant for, "warp" or "geo". */
data class CatalogEntry(val id: String, val title: String, val about: String, val category: String, val url: String, val route: String)

/**
 * The catalog of ready rule lists, the same file the desktop app reads: `catalog/catalog.json` in the repository.
 * A copy of it is inside the app, so it is there offline and from the first start, and the app asks the repository
 * for a newer one when the page is opened, so a list can be added without a new version. What was fetched last is
 * kept for the next start without a network.
 *
 * A catalog is a file from the internet, and what it adds decides where traffic goes. So it is read strictly: an
 * entry with an odd name, an address that is not a rule list on GitHub, or a size no real entry has is dropped, not
 * tried; and nothing is added without a person ticking it.
 */
object Catalog {
    private val ID = Regex("""^[a-z0-9][a-z0-9_-]{0,39}$""")
    private val FILE = Regex("""^[A-Za-z0-9][A-Za-z0-9._-]{0,99}\.srs$""")
    private val HOSTS = setOf("github.com", "raw.githubusercontent.com")
    private const val MAX_ENTRIES = 300
    private const val MAX_BYTES = 256 * 1024

    const val REMOTE = "https://raw.githubusercontent.com/Vexorter42/Tunor/main/catalog/catalog.json"

    /** Where the entries on the screen came from, in words. */
    enum class From(val words: String) {
        Repository("список из интернета"),
        Saved("список, сохранённый в прошлый раз"),
        BuiltIn("список из самого приложения"),
    }

    /** The entries of a catalog file that pass the checks; null if there are none, or it is not a catalog. */
    fun parse(json: String): List<CatalogEntry>? {
        try {
            if (json.length > MAX_BYTES) return null
            val root = JSONObject(json)
            if (root.optInt("format", -1) != 1) return null
            val lists = root.optJSONArray("lists") ?: return null

            val result = ArrayList<CatalogEntry>()
            val seen = HashSet<String>()
            for (i in 0 until minOf(lists.length(), MAX_ENTRIES + 1)) {
                val e = entry(lists.optJSONObject(i)) ?: continue
                if (result.size >= MAX_ENTRIES || !seen.add(e.id) || !seen.add("url:" + e.url.lowercase())) continue
                result += e
            }
            return result.ifEmpty { null }
        } catch (_: JSONException) {
            return null
        }
    }

    private fun entry(o: JSONObject?): CatalogEntry? {
        if (o == null) return null
        fun get(key: String) = (o.opt(key) as? String)?.trim().orEmpty()

        val id = get("id"); val title = get("title"); val about = get("about")
        val category = get("category"); val url = get("url")
        val route = get("route").ifEmpty { Tunnel.WARP }

        if (!ID.matches(id) || title.length !in 1..60 || about.length > 200 || category.length !in 1..40) return null
        if (route != Tunnel.WARP && route != Tunnel.GEO) return null
        val uri = try { java.net.URI(url) } catch (_: Exception) { return null }
        if (uri.scheme != "https" || uri.host?.lowercase() !in HOSTS) return null
        if (!uri.rawQuery.isNullOrEmpty() || !uri.rawUserInfo.isNullOrEmpty() || !uri.rawFragment.isNullOrEmpty()) return null
        val path = uri.rawPath ?: return null
        if (!path.endsWith(".srs", ignoreCase = true) || !FILE.matches(path.substringAfterLast('/'))) return null
        return CatalogEntry(id, title, about, category, url, route)
    }

    fun builtIn(ctx: Context): List<CatalogEntry> {
        val text = ctx.assets.open("catalog.json").bufferedReader().use { it.readText() }
        return parse(text) ?: throw IllegalStateException("встроенный каталог не читается")
    }

    private fun cache(ctx: Context) = File(ctx.filesDir, "catalog.json")

    /**
     * The newest catalog there is: the repository's if it can be reached and is valid, else the one saved from
     * last time, else the built-in. Never throws: the page has to open without a network.
     */
    suspend fun load(ctx: Context, askRepository: Boolean): Pair<List<CatalogEntry>, From> = withContext(Dispatchers.IO) {
        if (askRepository) {
            try {
                val text = GitHubFetch.text(REMOTE, MAX_BYTES)
                val fresh = parse(text)
                if (fresh != null) {
                    try { cache(ctx).writeText(text) } catch (_: Exception) { /* it only saves a request next time */ }
                    return@withContext fresh to From.Repository
                }
            } catch (_: Exception) { /* offline, or GitHub and every mirror are down: what is on the phone will do */ }
        }
        try {
            val f = cache(ctx)
            if (f.exists()) parse(f.readText())?.let { return@withContext it to From.Saved }
        } catch (_: Exception) { }
        builtIn(ctx) to From.BuiltIn
    }
}
