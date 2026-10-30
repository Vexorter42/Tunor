package io.github.vexorter42.tunor.core

import android.content.Context
import android.util.Log
import java.io.File
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

/**
 * What the app itself did and why, in a file that outlives it: when the tunnel started and stopped, which network
 * the phone was on, what the checks found, which server was taken and why, what failed. The engine's own log says
 * what the engine saw; this one says what the app decided, and with the two side by side a report can answer
 * "what happened" instead of only "what broke".
 *
 * A line holds no key, no address of a subscription and no link: whoever writes one is to leave them out, and the
 * report goes through [Redact] besides. Two files, the older one thrown away when the newer one is full, so the log
 * never takes more than about half a megabyte of the phone.
 */
object AppLog {
    private const val MAX_BYTES = 256 * 1024L
    private val lock = Any()
    private var dir: File? = null
    private val stamp = SimpleDateFormat("yyyy-MM-dd HH:mm:ss.SSS", Locale.US)

    fun init(context: Context) {
        dir = File(context.filesDir, "logs").also { it.mkdirs() }
    }

    fun i(tag: String, text: String) = write("I", tag, text, null)
    fun w(tag: String, text: String, e: Throwable? = null) = write("W", tag, text, e)
    fun e(tag: String, text: String, e: Throwable? = null) = write("E", tag, text, e)

    private fun write(level: String, tag: String, text: String, e: Throwable?) {
        when (level) {
            "E" -> Log.e("Tunor", "$tag: $text", e)
            "W" -> Log.w("Tunor", "$tag: $text", e)
            else -> Log.i("Tunor", "$tag: $text")
        }
        val d = dir ?: return
        try {
            synchronized(lock) {
                val f = File(d, "tunor.log")
                if (f.length() > MAX_BYTES) {
                    val old = File(d, "tunor.log.1")
                    old.delete()
                    f.renameTo(old)
                }
                val line = StringBuilder().append(stamp.format(Date())).append(' ').append(level).append(' ').append(tag).append(": ").append(text)
                if (e != null) line.append(" — ").append(e.javaClass.simpleName).append(": ").append(e.message.orEmpty())
                File(d, "tunor.log").appendText(line.append('\n').toString())
            }
        } catch (_: Exception) {
            // A log that cannot be written must not be the reason for anything else to fail.
        }
    }

    /** The last [lines] lines, oldest first, across both files. */
    fun tail(lines: Int = 300): String {
        val d = dir ?: return ""
        return try {
            synchronized(lock) {
                val all = ArrayList<String>()
                for (name in listOf("tunor.log.1", "tunor.log")) File(d, name).takeIf { it.isFile }?.let { all += it.readLines() }
                all.takeLast(lines).joinToString("\n")
            }
        } catch (_: Exception) {
            ""
        }
    }

    /** The engine keeps one file and never trims it; before it starts again, one that has grown large is begun afresh. */
    fun trimEngineLog(context: Context) {
        val f = File(context.filesDir, "engine/box.log")
        try {
            if (f.isFile && f.length() > 2 * 1024 * 1024) {
                val keep = f.readBytes().let { it.copyOfRange(it.size - 256 * 1024, it.size) }
                f.writeBytes(keep)
            }
        } catch (_: Exception) { }
    }
}
