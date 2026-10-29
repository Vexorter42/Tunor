package io.github.vexorter42.tunor.bg

import android.content.Context
import android.net.ConnectivityManager
import android.net.LinkProperties
import android.net.Network
import android.net.NetworkCapabilities
import android.net.NetworkRequest
import android.os.Build
import android.os.Handler
import android.os.HandlerThread
import io.nekohasekai.libbox.InterfaceUpdateListener
import java.net.NetworkInterface

/**
 * Follows the physical network the phone is really on — wifi or mobile, never the tunnel itself — and tells
 * the engine which interface that is, so that what it sends to the tunnel's server does not go back into it.
 *
 * "The default network" cannot be asked for plainly: since Android 9 it answers with the VPN's own interface
 * while one is up. So the request names what a real network has (internet, not restricted) and the system is
 * asked for the best match: directly on Android 12 and later, through a request on the older ones.
 */
object NetMonitor {
    private lateinit var cm: ConnectivityManager
    private val thread by lazy { HandlerThread("tunor-network").apply { start() } }
    private val handler by lazy { Handler(thread.looper) }

    @Volatile var network: Network? = null
        private set
    private var listener: InterfaceUpdateListener? = null
    private var running = false

    private val request: NetworkRequest = NetworkRequest.Builder()
        .addCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET)
        .addCapability(NetworkCapabilities.NET_CAPABILITY_NOT_RESTRICTED)
        .build()

    private val callback = object : ConnectivityManager.NetworkCallback() {
        override fun onAvailable(n: Network) { network = n; report() }
        override fun onCapabilitiesChanged(n: Network, c: NetworkCapabilities) { if (n == network) report() }
        override fun onLinkPropertiesChanged(n: Network, p: LinkProperties) { if (n == network) report() }
        override fun onLost(n: Network) {
            if (n == network) { network = null; report() }
        }
    }

    @Synchronized
    fun start(context: Context) {
        if (running) return
        cm = context.getSystemService(ConnectivityManager::class.java)
        network = cm.activeNetwork?.takeIf { cm.getNetworkCapabilities(it)?.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_VPN) == true }
        try {
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
                cm.registerBestMatchingNetworkCallback(request, callback, handler)
            } else {
                cm.requestNetwork(request, callback, handler)
            }
            running = true
        } catch (e: Exception) {
            android.util.Log.e("Tunor", "network monitor", e)
        }
    }

    @Synchronized
    fun stop() {
        if (!running) return
        running = false
        try { cm.unregisterNetworkCallback(callback) } catch (_: Exception) { }
        network = null
        listener = null
    }

    fun setListener(l: InterfaceUpdateListener?) {
        listener = l
        report()
    }

    /** Says to the engine what the physical interface is now, retrying a little: it is not always ready the instant it appears. */
    private fun report() {
        val l = listener ?: return
        val n = network
        if (n == null) {
            l.updateDefaultInterface("", -1, false, false)
            return
        }
        for (attempt in 0 until 10) {
            val props = cm.getLinkProperties(n)
            val name = props?.interfaceName
            val index = try { name?.let { NetworkInterface.getByName(it)?.index } } catch (_: Exception) { null }
            if (name != null && index != null) {
                val caps = cm.getNetworkCapabilities(n)
                val metered = caps?.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_METERED) == false
                l.updateDefaultInterface(name, index, metered, false)
                return
            }
            Thread.sleep(100)
        }
    }
}
