package io.github.vexorter42.tunor.bg

import android.content.Context
import android.content.Intent
import androidx.core.content.ContextCompat
import io.github.vexorter42.tunor.core.Door
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.flow.asStateFlow

/** What the tunnel is doing, as the screens see it. */
sealed interface Phase {
    data object Stopped : Phase
    data object Starting : Phase
    data object Started : Phase
    data class Failed(val why: String) : Phase
}

/** The one place the service says how it is, and the app asks it to start or stop. */
object Vpn {
    private val flow = MutableStateFlow<Phase>(Phase.Stopped)
    val phase: StateFlow<Phase> = flow.asStateFlow()

    internal fun set(p: Phase) { flow.value = p }

    /** The doors of the running engine, with this run's password; null while it is not running. */
    @Volatile var door: Door? = null
        internal set

    const val ACTION_STOP = "io.github.vexorter42.tunor.STOP"
    const val ACTION_RELOAD = "io.github.vexorter42.tunor.RELOAD"

    private val noteFlow = MutableSharedFlow<String>(extraBufferCapacity = 4)
    /** Things the service wants the person to know that are not a change of phase: a reload that did not take, for one. */
    val notes: SharedFlow<String> = noteFlow.asSharedFlow()
    internal fun note(text: String) { noteFlow.tryEmit(text) }

    /** Starts the service. The system's consent to be a VPN has to have been asked for already. */
    fun start(context: Context) {
        ContextCompat.startForegroundService(context, Intent(context, TunorVpnService::class.java))
    }

    fun stop(context: Context) {
        context.startService(Intent(context, TunorVpnService::class.java).setAction(ACTION_STOP))
    }

    /** Tells a running engine that the setup has changed. It reads it again and carries on with the new one. */
    fun reload(context: Context) {
        if (phase.value != Phase.Started) return
        try {
            context.startService(Intent(context, TunorVpnService::class.java).setAction(ACTION_RELOAD))
        } catch (_: Exception) {
            // Only an app that is in the foreground may start a service; the next start reads the setup anyway.
        }
    }
}
