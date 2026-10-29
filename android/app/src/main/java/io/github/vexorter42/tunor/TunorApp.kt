package io.github.vexorter42.tunor

import android.app.Application
import io.github.vexorter42.tunor.core.Store
import io.nekohasekai.libbox.Libbox
import io.nekohasekai.libbox.SetupOptions
import java.io.File

class TunorApp : Application() {
    override fun onCreate() {
        super.onCreate()
        instance = this
        store = Store(this)

        val base = filesDir
        val work = File(base, "engine").also { it.mkdirs() }
        val temp = cacheDir.also { it.mkdirs() }
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
            },
        )
    }

    companion object {
        lateinit var instance: TunorApp
            private set
        lateinit var store: Store
            private set
    }
}
