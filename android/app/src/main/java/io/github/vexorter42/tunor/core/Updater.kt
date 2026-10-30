package io.github.vexorter42.tunor.core

import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.content.pm.PackageInstaller
import android.content.pm.PackageManager
import android.os.Build
import io.github.vexorter42.tunor.BuildConfig
import io.github.vexorter42.tunor.bg.InstallReceiver
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.withContext
import org.json.JSONException
import org.json.JSONObject
import java.io.File
import java.security.MessageDigest

/**
 * The app's own update, over the air: the same `version.json` the desktop app reads (the latest GitHub release carries
 * it), with a section of its own for Android and a file for each processor.
 *
 * What is downloaded is checked three ways before the system is asked to install it: its SHA-256 against the
 * manifest's, the package against this one (same name, newer, signed by the same key), and then the system itself
 * refuses anything not signed by the key the installed app was. The last is what makes it safe: a manifest that
 * somebody changed on a mirror can send the app to a file, but cannot make it install a file that is not ours.
 */
object Updater {
    class Info(val version: String, val code: Int, val notes: String, val url: String, val sha256: String, val size: Long)

    sealed interface State {
        data object Idle : State
        data object Checking : State
        data object UpToDate : State
        data class Available(val info: Info) : State
        data class Downloading(val info: Info, val done: Long, val total: Long) : State
        /** The file is with the system's installer, which asks the person to confirm. */
        data class Installing(val info: Info) : State
        data class Failed(val why: String, val info: Info? = null) : State
    }

    private val flow = MutableStateFlow<State>(State.Idle)
    val state: StateFlow<State> = flow.asStateFlow()

    private const val REPO = "Vexorter42/Tunor"
    private const val MAX_APK = 200L * 1024 * 1024
    private const val MAX_MANIFEST = 64 * 1024

    private fun manifestUrl(ctx: Context): String {
        // A debug build can be pointed at a manifest of its own, to try an update without a release.
        if (BuildConfig.DEBUG) {
            val f = File(ctx.filesDir, "ota-url.txt")
            if (f.isFile) f.readText().trim().takeIf { it.isNotEmpty() }?.let { return it }
        }
        return "https://github.com/$REPO/releases/latest/download/version.json"
    }

    private fun isDebugOverride(ctx: Context) = BuildConfig.DEBUG && File(ctx.filesDir, "ota-url.txt").isFile

    // ------------------------------------------------------------------ the manifest

    /** What a manifest says about this build: null when it has no part for Android. Throws [JSONException] when it is not a manifest. */
    internal fun parse(text: String, abis: List<String>, strict: Boolean): Info? {
        val a = JSONObject(text).optJSONObject("android") ?: return null
        val version = a.optString("version").trim()
        val code = a.optInt("code", 0)
        if (version.isEmpty() || code <= 0) throw JSONException("в манифесте нет версии для Android")
        for (abi in abis) {
            val f = a.optJSONObject(abi) ?: continue
            val url = f.optString("url").trim()
            val sha = f.optString("sha256").trim().lowercase()
            if (!Regex("^[0-9a-f]{64}$").matches(sha)) throw JSONException("в манифесте нет контрольной суммы файла")
            if (strict && !fromOurReleases(url)) throw JSONException("адрес файла не из релизов Tunor")
            return Info(version, code, a.optString("notes").trim(), url, sha, f.optLong("size", 0))
        }
        throw JSONException("в этом релизе нет сборки для процессора этого телефона")
    }

    /** The file has to be one of the repository's own release files, whatever the manifest says. */
    private fun fromOurReleases(url: String): Boolean = try {
        val u = java.net.URI(url)
        u.scheme == "https" && u.host.equals("github.com", true) && u.rawPath.startsWith("/$REPO/releases/download/") &&
            u.rawQuery == null && u.rawUserInfo == null
    } catch (_: Exception) {
        false
    }

    // ------------------------------------------------------------------ looking

    /** Asks for the manifest and says whether there is a newer build. Never throws. */
    suspend fun check(ctx: Context): State {
        if (flow.value is State.Checking || flow.value is State.Downloading || flow.value is State.Installing) return flow.value
        flow.value = State.Checking
        val result: State = try {
            val text = GitHubFetch.text(manifestUrl(ctx), MAX_MANIFEST)
            val info = parse(text, Build.SUPPORTED_ABIS.toList(), strict = !isDebugOverride(ctx))
            if (info != null && info.code > BuildConfig.VERSION_CODE) State.Available(info) else State.UpToDate
        } catch (e: GitHubFetch.Failed) {
            AppLog.w("update", "поиск обновления: не удалось связаться с GitHub и зеркалами")
            State.Failed("не удалось связаться с GitHub и зеркалами")
        } catch (e: Exception) {
            AppLog.w("update", "поиск обновления не удался", e)
            State.Failed(e.message ?: e.javaClass.simpleName)
        }
        if (result is State.Available) AppLog.i("update", "есть версия ${result.info.version}")
        flow.value = result
        return result
    }

    // ------------------------------------------------------------------ installing

    /** Whether the person has let this app install apps. Android asks for it once, per app, in the system's settings. */
    fun mayInstall(ctx: Context) = ctx.packageManager.canRequestPackageInstalls()

    private fun updatesDir(ctx: Context) = File(ctx.cacheDir, "updates").also { it.mkdirs() }

    /** Downloads, checks and hands the file to the system's installer. The state says how far it has got. */
    suspend fun install(ctx: Context, info: Info) {
        if (flow.value is State.Downloading || flow.value is State.Installing) return
        val file = File(updatesDir(ctx), "tunor-${info.version}.apk")
        try {
            flow.value = State.Downloading(info, 0, info.size)
            GitHubFetch.download(info.url, file, MAX_APK) { done, total -> flow.value = State.Downloading(info, done, if (total > 0) total else info.size) }

            val actual = withContext(Dispatchers.IO) { sha256(file) }
            if (!actual.equals(info.sha256, true)) {
                AppLog.w("update", "контрольная сумма скачанного файла не совпала: отклонён")
                file.delete()
                flow.value = State.Failed("контрольная сумма не совпала: файл подменён или повреждён, установка отклонена", info)
                return
            }
            packageProblem(ctx, file)?.let {
                AppLog.w("update", "файл не годится: $it")
                file.delete()
                flow.value = State.Failed(it, info)
                return
            }
            flow.value = State.Installing(info)
            AppLog.i("update", "версия ${info.version} передана установщику системы")
            withContext(Dispatchers.IO) { handOff(ctx, file) }
        } catch (e: GitHubFetch.Failed) {
            AppLog.w("update", "не удалось скачать версию ${info.version}", e)
            file.delete()
            flow.value = State.Failed("не удалось скачать: " + (e.message ?: ""), info)
        } catch (e: Exception) {
            AppLog.w("update", "установка версии ${info.version} не удалась", e)
            file.delete()
            flow.value = State.Failed(e.message ?: e.javaClass.simpleName, info)
        }
    }

    /** The system's installer said it did not take it. */
    fun failed(why: String?, info: Info? = null) {
        AppLog.w("update", "система не установила обновление: ${why.orEmpty()}")
        flow.value = State.Failed(why?.takeIf { it.isNotBlank() } ?: "система не установила обновление", (flow.value as? State.Installing)?.info ?: info)
    }

    private fun sha256(f: File): String {
        val md = MessageDigest.getInstance("SHA-256")
        f.inputStream().use { input ->
            val buffer = ByteArray(64 * 1024)
            while (true) {
                val n = input.read(buffer)
                if (n < 0) break
                md.update(buffer, 0, n)
            }
        }
        return md.digest().joinToString("") { "%02x".format(it) }
    }

    /** Why this file is not an update of this app, or null. What cannot be told is left to the system, which checks the signature itself. */
    private fun packageProblem(ctx: Context, apk: File): String? {
        val pm = ctx.packageManager
        val flags = if (Build.VERSION.SDK_INT >= 28) PackageManager.GET_SIGNING_CERTIFICATES else @Suppress("DEPRECATION") PackageManager.GET_SIGNATURES
        val theirs = pm.getPackageArchiveInfo(apk.path, flags) ?: return "скачанный файл не читается как приложение"
        if (theirs.packageName != ctx.packageName) return "скачанный файл — другое приложение"
        val code = if (Build.VERSION.SDK_INT >= 28) theirs.longVersionCode else @Suppress("DEPRECATION") theirs.versionCode.toLong()
        if (code <= BuildConfig.VERSION_CODE) return "скачанная версия не новее установленной"

        val mine = pm.getPackageInfo(ctx.packageName, flags)
        val a: Set<String>
        val b: Set<String>
        if (Build.VERSION.SDK_INT >= 28) {
            a = mine.signingInfo?.apkContentsSigners?.map { it.toCharsString() }?.toSet().orEmpty()
            b = theirs.signingInfo?.apkContentsSigners?.map { it.toCharsString() }?.toSet().orEmpty()
        } else {
            @Suppress("DEPRECATION")
            a = mine.signatures?.map { it.toCharsString() }?.toSet().orEmpty()
            @Suppress("DEPRECATION")
            b = theirs.signatures?.map { it.toCharsString() }?.toSet().orEmpty()
        }
        if (a.isNotEmpty() && b.isNotEmpty() && a != b) return "файл подписан другим ключом, чем установленное приложение: установка отклонена"
        return null
    }

    private fun handOff(ctx: Context, apk: File) {
        val installer = ctx.packageManager.packageInstaller
        val params = PackageInstaller.SessionParams(PackageInstaller.SessionParams.MODE_FULL_INSTALL).apply {
            setAppPackageName(ctx.packageName)
            if (Build.VERSION.SDK_INT >= 31) setRequireUserAction(PackageInstaller.SessionParams.USER_ACTION_NOT_REQUIRED)
        }
        val id = installer.createSession(params)
        installer.openSession(id).use { session ->
            apk.inputStream().use { input ->
                session.openWrite("tunor.apk", 0, apk.length()).use { out ->
                    input.copyTo(out)
                    session.fsync(out)
                }
            }
            val intent = Intent(ctx, InstallReceiver::class.java).setAction(InstallReceiver.ACTION)
            // The installer adds to it what it has to say, so it cannot be an immutable one.
            val pending = PendingIntent.getBroadcast(ctx, id, intent, PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_MUTABLE)
            // The app is about to be replaced under a running tunnel: when it ends, that is no fault and wants no report.
            Crashes.noteUpdate(ctx)
            session.commit(pending.intentSender)
        }
    }
}
