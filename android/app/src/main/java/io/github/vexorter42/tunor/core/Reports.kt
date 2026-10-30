package io.github.vexorter42.tunor.core

import android.app.ActivityManager
import android.app.ApplicationExitInfo
import android.content.Context
import android.os.Build
import android.os.PowerManager
import io.github.vexorter42.tunor.BuildConfig
import io.github.vexorter42.tunor.TunorApp
import io.github.vexorter42.tunor.bg.MobileRescue
import io.github.vexorter42.tunor.bg.NetMonitor
import io.github.vexorter42.tunor.bg.Phase
import io.github.vexorter42.tunor.bg.Vpn
import io.nekohasekai.libbox.Libbox
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import org.json.JSONObject
import java.io.File
import java.io.PrintWriter
import java.io.StringWriter
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

/**
 * Reports of what went wrong: a text file that says what the app is, what it was doing, what it saw and how it
 * ended, ready to be sent to whoever can read it. The app makes one by itself when something falls over, and the
 * person can ask for one at any time.
 *
 * A report holds no key, no address of a subscription, no link, no site the person typed in and no name of an app
 * they have; the numbers of servers and the sites that were opened are taken out of the logs it quotes (see
 * [Redact]). What is left is the shape of the setup and the story of the last minutes, which is what finding
 * a fault needs.
 */
object Reports {
    /** One report on the phone. [auto]: made by the app because something broke, not asked for. */
    class Item(val file: File, val kind: String, val time: Long) {
        val auto get() = kind != MANUAL
        val title get() = when (kind) {
            CRASH -> "Приложение закрылось с ошибкой"
            DIED -> "Туннель оборвался: приложение завершено"
            FAILED -> "Туннель не запустился"
            ENGINE -> "Движок остановился сам"
            else -> "Отчёт по запросу"
        }
    }

    const val MANUAL = "manual"
    const val CRASH = "crash"
    const val DIED = "died"
    const val FAILED = "failed"
    const val ENGINE = "engine"

    private const val KEEP = 12
    private val nameTime = SimpleDateFormat("yyyyMMdd-HHmmss", Locale.US)
    private val shown = SimpleDateFormat("yyyy-MM-dd HH:mm:ss", Locale.US)
    private val lastAuto = HashMap<String, Long>()

    private val freshFlow = MutableStateFlow<Item?>(null)
    /** The newest report the app made by itself that the person has not yet been shown. */
    val fresh: StateFlow<Item?> = freshFlow.asStateFlow()

    private fun dir(c: Context) = File(c.filesDir, "reports").also { it.mkdirs() }
    private fun prefs(c: Context) = c.getSharedPreferences("reports", Context.MODE_PRIVATE)

    fun list(c: Context): List<Item> = (dir(c).listFiles().orEmpty())
        .filter { it.isFile && it.name.startsWith("tunor-") && it.name.endsWith(".txt") }
        .map { Item(it, it.name.removePrefix("tunor-").substringBefore('-'), it.lastModified()) }
        .sortedByDescending { it.time }

    fun delete(c: Context, item: Item) {
        item.file.delete()
        refresh(c)
    }

    fun deleteAll(c: Context) {
        list(c).forEach { it.file.delete() }
        refresh(c)
    }

    /** Looks again at which report is waiting to be shown. */
    fun refresh(c: Context) {
        val seen = prefs(c).getLong("seen", 0)
        freshFlow.value = list(c).firstOrNull { it.auto && it.time > seen }
    }

    /** The person has looked at what is waiting. */
    fun markSeen(c: Context) {
        prefs(c).edit().putLong("seen", System.currentTimeMillis()).apply()
        freshFlow.value = null
    }

    /**
     * Writes a report and says where it is; null if it could not be written. It never throws, because it is called
     * from places that are already in trouble. [e]: the error that caused it, if there is one.
     */
    fun create(c: Context, kind: String, why: String, e: Throwable? = null): File? {
        try {
            if (kind != CRASH && kind != MANUAL) {
                // The same trouble can repeat every few seconds; one report of it is enough for now.
                val now = System.currentTimeMillis()
                synchronized(lastAuto) {
                    if (now - (lastAuto[kind] ?: 0) < 60_000) return null
                    lastAuto[kind] = now
                }
            }
            val f = File(dir(c), "tunor-$kind-${nameTime.format(Date())}.txt")
            f.writeText(build(c, kind, why, e))
            // What the engine said when it fell is in this report now; it is not told again in the next.
            if (kind != MANUAL) Crashes.engineCrashTold(c)
            prune(c)
            if (kind != MANUAL) {
                // A crash is the one case where the person is not looking: it is shown at the next start, from the file.
                freshFlow.value = Item(f, kind, f.lastModified())
            }
            AppLog.i("report", "отчёт «$kind» записан: ${f.name}")
            return f
        } catch (t: Throwable) {
            try { AppLog.e("report", "отчёт не записался", t) } catch (_: Throwable) { }
            return null
        }
    }

    private fun prune(c: Context) {
        val all = list(c)
        all.drop(KEEP).forEach { it.file.delete() }
        val old = System.currentTimeMillis() - 30L * 24 * 3600 * 1000
        all.filter { it.time < old }.forEach { it.file.delete() }
    }

    // ------------------------------------------------------------------ the text

    fun build(c: Context, kind: String, why: String, e: Throwable?): String {
        val sb = StringBuilder()
        fun section(title: String) { sb.append("\n== ").append(title).append(" ==\n") }
        fun line(text: String) { sb.append(text).append('\n') }

        line("Отчёт Tunor")
        line("Составлен: " + shown.format(Date()))
        line("Что случилось: " + Redact.text(why))
        line("Вид: $kind")

        section("Приложение")
        line("Версия: ${BuildConfig.VERSION_NAME} (${BuildConfig.VERSION_CODE}), сборка ${if (BuildConfig.DEBUG) "отладочная" else "выпуск"}")
        line("Движок: sing-box-lx " + safe { Libbox.version() })
        line("Android ${Build.VERSION.RELEASE} (API ${Build.VERSION.SDK_INT}), ${Build.MANUFACTURER} ${Build.MODEL}, ${Build.SUPPORTED_ABIS.firstOrNull().orEmpty()}")
        line("Экономия батареи не мешает приложению: " + safe {
            if (c.getSystemService(PowerManager::class.java).isIgnoringBatteryOptimizations(c.packageName)) "да" else "нет, система вправе закрыть его в фоне"
        })

        section("Сейчас")
        line("Туннель: " + when (val p = Vpn.phase.value) {
            Phase.Started -> "работает"
            Phase.Starting -> "запускается"
            Phase.Stopped -> "остановлен"
            is Phase.Failed -> "не запустился: ${Redact.text(p.why)}"
        })
        val up = Vpn.phase.value == Phase.Started || Vpn.phase.value == Phase.Starting
        line("Сеть: " + if (up) safe { NetMonitor.kind() } else "не определяется, пока туннель не запущен")
        val rescue = MobileRescue.plan.value
        line("Запасной путь для мобильной сети: " + when {
            !(TunorApp.storeOrNull()?.state?.value?.settings?.mobileRescue ?: false) -> "выключен"
            rescue != null -> "включён, идёт через подписку (серверов для обхода: ${rescue.servers.size})"
            !up -> "включён, смотрит за сетью, пока туннель работает"
            else -> "включён, ждёт: обычные пути работают"
        })

        val state = TunorApp.storeOrNull()?.state?.value
        if (state != null) {
            section("Туннели")
            line(Redact.text(describeTunnels(state)).trimEnd())
            section("Правила и настройки")
            line(describeRules(state))
            section("Что получилось в конфиге движка (без адресов и ключей)")
            line(safe { configShape(state, rescue) })
        }

        if (e != null) {
            section("Ошибка")
            if (!BuildConfig.DEBUG) line("(в выпуске имена классов сокращены; прочитать их можно файлом mapping.txt той же версии)")
            line(Redact.trace(StringWriter().also { e.printStackTrace(PrintWriter(it)) }.toString()).trimEnd())
        }

        section("Завершения приложения, которые запомнила система")
        line(exits(c))

        Crashes.engineCrashText(c)?.let {
            section("Что движок написал, когда упал в прошлый раз (начало)")
            line(Redact.lite(it))
        }

        section("Что делало приложение (последние строки журнала)")
        line(Redact.text(AppLog.tail(300)).ifBlank { "журнал пуст" })

        section("Что говорил движок (последние строки, адреса скрыты)")
        line(Redact.text(engineLog(c, 200)).ifBlank { "журнал пуст: движок ещё не запускался" })
        return sb.toString()
    }

    private fun <T> safe(block: () -> T): String = try { block().toString() } catch (t: Throwable) { "не удалось узнать (${t.javaClass.simpleName})" }

    private fun describeTunnels(s: State): String = buildString {
        if (s.tunnels.isEmpty()) append("нет ни одного\n")
        for (t in ConfigBuilder.ordered(s.tunnels)) {
            val problem = ConfigBuilder.problemOf(t, s.tunnels)
            append("- ").append(t.title).append(" [").append(t.kind).append("] ")
            append(if (problem == null) "готов" else "не готов: $problem")
            if (t.via.isNotEmpty()) append(", идёт через ").append(ConfigBuilder.titleOf(t.via, s.tunnels))
            if (t.kind == Tunnel.SUB) {
                append(", серверов ").append(t.nodes.size).append(" (для обхода: ").append(Pool.split(t).first.size).append("), ")
                append(if (t.auto) "автовыбор, в пуле ${t.pool.size}" + if (Pool.isSpecial(t, t.pool)) " (серверы для обхода)" else "" else "выбран вручную")
            }
            append('\n')
        }
    }

    private fun describeRules(s: State): String {
        val apps = s.rules.apps
        return listOf(
            "Свои сайты: ${s.rules.sites.size}",
            "Списки из каталога: " + (s.rules.lists.joinToString(", ") { it.id }.ifEmpty { "нет" }),
            "Приложения: ${apps.size} (напрямую ${apps.count { it.tunnel == AppRule.DIRECT }}, через туннель ${apps.count { it.tunnel != AppRule.DIRECT }})",
            "Всё остальное: " + (if (s.settings.rest.isEmpty()) "напрямую" else "через " + Redact.text(ConfigBuilder.titleOf(s.settings.rest, s.tunnels))),
            "Поиск серверов для обхода: " + (if (s.settings.autoBypass) "включён" else "выключен"),
            "Запасной путь для мобильной сети: " + (if (s.settings.mobileRescue) "включён" else "выключен"),
            "Проверять обновления: " + (if (s.settings.autoUpdateCheck) "да" else "нет"),
        ).joinToString("\n")
    }

    /** What the engine's file would be like, said without any of what is in it: which parts, how many, how joined. */
    private fun configShape(s: State, rescue: Rescue?): String {
        val j = try {
            JSONObject(ConfigBuilder.build(s, null, RuleSets::path, rescue = rescue))
        } catch (e: ConfigProblem) {
            return "конфиг не собирается: " + Redact.text(e.message.orEmpty())
        }
        val out = StringBuilder()
        j.optJSONArray("outbounds")?.let { a ->
            for (i in 0 until a.length()) {
                val o = a.getJSONObject(i)
                out.append("выход ").append(o.optString("tag")).append(": ").append(o.optString("type"))
                o.optJSONObject("transport")?.let { out.append(", ").append(it.optString("type")) }
                if (o.optJSONObject("tls")?.optBoolean("enabled") == true) out.append(", tls")
                if (o.has("detour")) out.append(", внутри ").append(o.optString("detour"))
                o.optJSONArray("outbounds")?.let { out.append(", серверов ").append(it.length()) }
                out.append('\n')
            }
        }
        j.optJSONArray("endpoints")?.let { a ->
            for (i in 0 until a.length()) {
                val o = a.getJSONObject(i)
                out.append("туннель ").append(o.optString("tag")).append(": ").append(o.optString("type"))
                if (o.has("detour")) out.append(", внутри ").append(o.optString("detour"))
                if (o.has("mtu")) out.append(", mtu ").append(o.optInt("mtu"))
                out.append('\n')
            }
        }
        val r = j.optJSONObject("route")
        out.append("правил маршрута: ").append(r?.optJSONArray("rules")?.length() ?: 0)
        out.append(", списков: ").append(r?.optJSONArray("rule_set")?.length() ?: 0)
        out.append(", остальное идёт в ").append(r?.optString("final").orEmpty())
        return out.toString()
    }

    /** What the system remembers of how the app's processes ended: a crash, an out-of-memory kill, a stop by the person. */
    private fun exits(c: Context): String {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.R) return "Android старше 11 их не хранит"
        return try {
            val list = c.getSystemService(ActivityManager::class.java).getHistoricalProcessExitReasons(c.packageName, 0, 5)
            if (list.isEmpty()) "нет записей" else list.joinToString("\n") {
                shown.format(Date(it.timestamp)) + " — " + reasonName(it.reason) + (it.description?.takeIf(String::isNotBlank)?.let { d -> " (${Redact.text(d)})" } ?: "")
            }
        } catch (t: Throwable) {
            "не удалось получить (${t.javaClass.simpleName})"
        }
    }

    fun reasonName(reason: Int): String = when (reason) {
        ApplicationExitInfo.REASON_ANR -> "приложение не отвечало (ANR)"
        ApplicationExitInfo.REASON_CRASH -> "ошибка в программе"
        ApplicationExitInfo.REASON_CRASH_NATIVE -> "сбой в машинном коде (движок)"
        ApplicationExitInfo.REASON_LOW_MEMORY -> "системе не хватило памяти"
        ApplicationExitInfo.REASON_SIGNALED -> "процесс завершён сигналом"
        ApplicationExitInfo.REASON_USER_REQUESTED -> "закрыто пользователем"
        ApplicationExitInfo.REASON_USER_STOPPED -> "остановлено пользователем"
        ApplicationExitInfo.REASON_EXCESSIVE_RESOURCE_USAGE -> "система закрыла: слишком много ресурсов"
        ApplicationExitInfo.REASON_INITIALIZATION_FAILURE -> "не удалось запуститься"
        ApplicationExitInfo.REASON_PERMISSION_CHANGE -> "изменено разрешение"
        ApplicationExitInfo.REASON_DEPENDENCY_DIED -> "завершилось то, от чего зависело приложение"
        ApplicationExitInfo.REASON_EXIT_SELF -> "приложение закрылось само"
        ApplicationExitInfo.REASON_FREEZER -> "система заморозила приложение"
        ApplicationExitInfo.REASON_PACKAGE_STATE_CHANGE -> "изменилось состояние пакета"
        ApplicationExitInfo.REASON_PACKAGE_UPDATED -> "приложение обновлено или переустановлено"
        else -> "причина $reason"
    }

    /** The last lines of the engine's own log, without the noise about looking for the owner of a connection. */
    fun engineLog(c: Context, lines: Int): String {
        val f = File(c.filesDir, "engine/box.log")
        if (!f.isFile) return ""
        return try {
            java.io.RandomAccessFile(f, "r").use { r ->
                val take = minOf(r.length(), 64_000L)
                r.seek(r.length() - take)
                val bytes = ByteArray(take.toInt())
                r.readFully(bytes)
                String(bytes, Charsets.UTF_8).substringAfter('\n', "")
                    .replace(Regex("\u001B\\[[0-9;]*m"), "")
                    .lines().filterNot { "failed to search process" in it }
                    // A debug build writes a line for every step; they bury the few that say what failed.
                    .let { all -> all.filterNot { " DEBUG " in it }.takeIf { it.size >= 20 } ?: all }
                    .takeLast(lines).joinToString("\n")
            }
        } catch (_: Exception) {
            ""
        }
    }
}
