package io.github.vexorter42.tunor

import android.app.Application
import io.github.vexorter42.tunor.core.AppLog
import io.github.vexorter42.tunor.core.Crashes
import io.github.vexorter42.tunor.core.Reports
import io.github.vexorter42.tunor.core.Store
import io.nekohasekai.libbox.Libbox
import io.nekohasekai.libbox.SetupOptions
import java.io.File

class TunorApp : Application() {
    override fun onCreate() {
        super.onCreate()
        instance = this
        // The log and the net under falling over come first: if anything below fails, it is written down.
        AppLog.init(this)
        Crashes.install(this)
        store = Store(this)
        AppLog.i("app", "запуск ${BuildConfig.VERSION_NAME} (${BuildConfig.VERSION_CODE})")

        val base = filesDir
        val work = File(base, "engine").also { it.mkdirs() }
        val temp = cacheDir.also { it.mkdirs() }
        // The engine starts its crash file afresh each time it is set up; what the last run left in it is put aside first.
        Crashes.keepEngineCrash(this)
        Libbox.setup(
            SetupOptions().apply {
                basePath = base.path
                workingPath = work.path
                tempPath = temp.path
                fixAndroidStack = true
                logMaxLines = 3000
                debug = false
                appVersion = BuildConfig.VERSION_NAME
                appMarketingVersion = BuildConfig.VERSION_NAME
                // The engine writes down a fault of its own, with its stack, where a report can find it.
                crashReportSource = "Tunor"
            },
        )

        Crashes.checkLastRun(this)
        Reports.refresh(this)
    }

    companion object {
        lateinit var instance: TunorApp
            private set
        lateinit var store: Store
            private set

        /** The store, or null before it exists: for what runs while the app is still starting or already falling. */
        fun storeOrNull(): Store? = if (::store.isInitialized) store else null
    }
}
