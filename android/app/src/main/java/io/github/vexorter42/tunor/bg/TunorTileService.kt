package io.github.vexorter42.tunor.bg

import android.app.PendingIntent
import android.content.ComponentName
import android.content.Intent
import android.net.VpnService
import android.os.Build
import android.service.quicksettings.Tile
import android.service.quicksettings.TileService
import io.github.vexorter42.tunor.MainActivity
import io.github.vexorter42.tunor.TunorApp
import io.github.vexorter42.tunor.core.ConfigBuilder
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch

/**
 * The tile in the phone's quick settings: one tap turns the tunnel on or off without opening the app.
 *
 * Turning it on needs the phone's consent to be a VPN, which is asked for once, in the app; if it has not been
 * given yet (or there is nothing to start), the tile opens the app instead, where that can be done.
 */
class TunorTileService : TileService() {
    private val scope = CoroutineScope(Dispatchers.Main)
    private var watching: Job? = null

    override fun onStartListening() {
        watching?.cancel()
        watching = scope.launch { Vpn.phase.collect { render(it) } }
    }

    override fun onStopListening() {
        watching?.cancel()
        watching = null
    }

    override fun onDestroy() {
        scope.cancel()
        super.onDestroy()
    }

    override fun onClick() {
        when (Vpn.phase.value) {
            Phase.Started, Phase.Starting -> Vpn.stop(this)
            else -> {
                if (ConfigBuilder.problem(TunorApp.store.state.value) != null || VpnService.prepare(this) != null) openApp()
                else Vpn.start(this)
            }
        }
    }

    private fun openApp() {
        val intent = Intent(this, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        if (Build.VERSION.SDK_INT >= 34) {
            startActivityAndCollapse(PendingIntent.getActivity(this, 2, intent, PendingIntent.FLAG_IMMUTABLE))
        } else {
            @Suppress("DEPRECATION")
            startActivityAndCollapse(intent)
        }
    }

    private fun render(phase: Phase) {
        val tile = qsTile ?: return
        tile.label = "Tunor"
        tile.state = if (phase == Phase.Started || phase == Phase.Starting) Tile.STATE_ACTIVE else Tile.STATE_INACTIVE
        if (Build.VERSION.SDK_INT >= 29) {
            tile.subtitle = when (phase) {
                Phase.Started -> "Включён"
                Phase.Starting -> "Запускаю…"
                is Phase.Failed -> "Не запустился"
                Phase.Stopped -> "Выключен"
            }
        }
        tile.updateTile()
    }

    companion object {
        /** Asks the system to bring the tile up to date, if the panel is showing it. */
        fun refresh() {
            try {
                val app = TunorApp.instance
                requestListeningState(app, ComponentName(app, TunorTileService::class.java))
            } catch (_: Exception) { }
        }
    }
}
