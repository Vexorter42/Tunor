package io.github.vexorter42.tunor.bg

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.pm.PackageInstaller
import io.github.vexorter42.tunor.core.Updater

/**
 * What the system's installer answers once the app has handed it an update: that the person has to confirm (and the
 * confirmation is shown), that it is done (the app is replaced, so nobody is left to hear it), or why it did not take.
 */
class InstallReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        when (intent.getIntExtra(PackageInstaller.EXTRA_STATUS, PackageInstaller.STATUS_FAILURE)) {
            PackageInstaller.STATUS_PENDING_USER_ACTION -> {
                @Suppress("DEPRECATION")
                val confirm = intent.getParcelableExtra<Intent>(Intent.EXTRA_INTENT)
                if (confirm != null) {
                    confirm.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
                    try { context.startActivity(confirm) } catch (e: Exception) { Updater.failed("не удалось показать окно установки: " + (e.message ?: "")) }
                }
            }
            PackageInstaller.STATUS_SUCCESS -> { /* replaced: this process is about to end */ }
            else -> Updater.failed(intent.getStringExtra(PackageInstaller.EXTRA_STATUS_MESSAGE))
        }
    }

    companion object {
        const val ACTION = "io.github.vexorter42.tunor.INSTALL_RESULT"
    }
}
