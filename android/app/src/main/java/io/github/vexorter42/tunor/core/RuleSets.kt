package io.github.vexorter42.tunor.core

import io.github.vexorter42.tunor.TunorApp
import java.io.File

/**
 * The files of the catalog's lists on the phone. They are downloaded, not bundled: a list is big and changes
 * more often than the app does.
 */
object RuleSets {
    /** How a fetch went. [unreachable]: no place answered at all, so the next list will fare no better. */
    class Outcome(val problem: String?, val unreachable: Boolean = false) {
        val ok get() = problem == null
    }

    /** The biggest list there is is a few megabytes; this is only a stop for something that is not one. */
    private const val MAX_BYTES = 40 * 1024 * 1024

    private val MAGIC = byteArrayOf(0x53, 0x52, 0x53)      // "SRS": how every compiled rule list begins

    private fun dir(): File = File(TunorApp.instance.filesDir, "engine/rulesets").also { it.mkdirs() }

    /** Named by the list's id, which the catalog has already limited to letters, digits, "-" and "_": nothing in it leads out of the folder. */
    private fun fileOf(p: Pick) = File(dir(), p.id.filter { it.isLetterOrDigit() || it == '-' || it == '_' } + ".srs")

    /** Where the list is on the phone, or null if it has not been fetched. */
    fun path(p: Pick): String? = fileOf(p).takeIf { it.isFile && it.length() > MAGIC.size }?.path

    /** When the file was fetched, in milliseconds since 1970; 0 — not there. */
    fun stamp(p: Pick): Long = fileOf(p).takeIf { it.isFile }?.lastModified() ?: 0

    /** Fetches the list and puts it where [path] will find it. */
    suspend fun download(p: Pick): Outcome {
        val bytes = try {
            GitHubFetch.bytes(p.url, MAX_BYTES)
        } catch (e: GitHubFetch.Failed) {
            return Outcome(e.message ?: "не скачался", e.unreachable)
        } catch (e: Exception) {
            return Outcome(e.message ?: "не скачался")
        }
        // A mirror that is down sometimes answers with a page of its own, and a page is not a list.
        if (bytes.size <= MAGIC.size || !bytes.copyOf(MAGIC.size).contentEquals(MAGIC)) return Outcome("пришёл не список правил")
        return try {
            val target = fileOf(p)
            val tmp = File(target.parentFile, target.name + ".tmp")
            tmp.writeBytes(bytes)
            if (!tmp.renameTo(target)) { target.delete(); tmp.renameTo(target) }
            Outcome(null)
        } catch (e: Exception) {
            Outcome("не удалось сохранить: " + (e.message ?: e.javaClass.simpleName))
        }
    }
}
