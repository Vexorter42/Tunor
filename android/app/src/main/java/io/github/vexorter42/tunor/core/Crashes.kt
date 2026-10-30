package io.github.vexorter42.tunor.core

import android.app.ActivityManager
import android.app.ApplicationExitInfo
import android.app.Application
import android.content.Context
import android.os.Build
import java.io.File

/**
 * What the app does about falling over.
 *
 * An error nobody caught is written to a report before the process dies. A death nothing could catch — the engine
 * is machine code, and a fault in it ends the whole process at once; the system also stops apps that use too much
 * memory — leaves nothing behind, so the app notes while the tunnel is up that it is, and the next time it opens and
 * finds the note still standing, it knows the tunnel did not end by itself being switched off, and asks the system
 * how the process ended.
 */
object Crashes {
    private const val PREFS = "crashes"
    private const val RUNNING = "tunnel-up"
    private const val UPDATING = "updating-at"

    /** Ends the system gives a process that are the person's or the installer's doing, not a fault. */
    private val ORDERLY = setOf(
        ApplicationExitInfo.REASON_USER_REQUESTED, ApplicationExitInfo.REASON_USER_STOPPED,
        ApplicationExitInfo.REASON_PACKAGE_UPDATED, ApplicationExitInfo.REASON_PACKAGE_STATE_CHANGE,
    )

    private fun prefs(c: Context) = c.getSharedPreferences(PREFS, Context.MODE_PRIVATE)

    fun install(app: Application) {
        val before = Thread.getDefaultUncaughtExceptionHandler()
        Thread.setDefaultUncaughtExceptionHandler { thread, e ->
            try {
                AppLog.e("crash", "необработанная ошибка в потоке «${thread.name}»", e)
                Reports.create(app, Reports.CRASH, "Приложение закрылось с ошибкой в потоке «${thread.name}»", e)
                // The report says it; the note about the tunnel would only make a second one of the same.
                markTunnel(app, false)
            } catch (_: Throwable) {
            }
            before?.uncaughtException(thread, e)
        }
    }

    /** The tunnel is up ([on]) or has ended in an orderly way. Written at once: the process may be gone a moment later. */
    fun markTunnel(c: Context, on: Boolean) {
        prefs(c).edit().putBoolean(RUNNING, on).commit()
    }

    private fun engineCrash(c: Context) = File(c.filesDir, "engine/previous-crash.log")

    /**
     * The engine writes what it says as it falls (a Go panic, with where it happened) into a file named for the
     * app, and begins that file over at the next start. So before it does, a file with something in it is moved
     * where the report of that fall can find it.
     */
    fun keepEngineCrash(c: Context) {
        try {
            val f = File(c.filesDir, "engine/CrashReport-Tunor.log")
            if (f.length() > 0) {
                val keep = engineCrash(c)
                keep.delete()
                f.renameTo(keep)
            }
        } catch (_: Exception) {
        }
    }

    /** What the engine said when it last fell, its first lines (the cause and the place), or null if it did not. */
    fun engineCrashText(c: Context): String? = try {
        engineCrash(c).takeIf { it.isFile && it.length() > 0 }?.useLines { it.take(120).joinToString("\n") }
    } catch (_: Exception) {
        null
    }

    /** A report has taken it up; it is not told again. */
    fun engineCrashTold(c: Context) {
        try { engineCrash(c).delete() } catch (_: Exception) { }
    }

    /** The app is being replaced by a newer one on the person's say-so: the end that follows is an orderly one. */
    fun noteUpdate(c: Context) {
        prefs(c).edit().putLong(UPDATING, System.currentTimeMillis()).commit()
    }

    /**
     * At start: was the tunnel up when the last process ended, and nobody switched it off? Then it did not end
     * well, and a report is made with what the system says about how. Unless the person ended it, or an update did.
     */
    fun checkLastRun(c: Context) {
        val p = prefs(c)
        if (!p.getBoolean(RUNNING, false)) return
        val updatedLately = System.currentTimeMillis() - p.getLong(UPDATING, 0) < 15 * 60_000L
        p.edit().putBoolean(RUNNING, false).remove(UPDATING).commit()
        var how = "причина неизвестна"
        var orderly = updatedLately
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
            try {
                val last = c.getSystemService(ActivityManager::class.java).getHistoricalProcessExitReasons(c.packageName, 0, 1).firstOrNull()
                if (last != null) {
                    how = Reports.reasonName(last.reason) + (last.description?.takeIf(String::isNotBlank)?.let { " (${Redact.text(it)})" } ?: "")
                    if (last.reason in ORDERLY) orderly = true
                }
            } catch (_: Throwable) {
            }
        }
        if (orderly) {
            AppLog.i("crash", "прошлый раз приложение закрыли при включённом туннеле: $how")
            return
        }
        AppLog.w("crash", "прошлый раз туннель оборвался: $how")
        Reports.create(c, Reports.DIED, "Туннель был включён, и приложение завершилось: $how")
    }
}
