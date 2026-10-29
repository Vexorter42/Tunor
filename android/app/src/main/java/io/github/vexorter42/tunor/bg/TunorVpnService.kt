package io.github.vexorter42.tunor.bg

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Intent
import android.content.pm.PackageManager
import android.content.pm.ServiceInfo
import android.net.IpPrefix
import android.net.ProxyInfo
import android.net.VpnService
import android.os.Build
import android.os.IBinder
import android.os.ParcelFileDescriptor
import android.util.Log
import androidx.core.app.NotificationCompat
import androidx.core.app.ServiceCompat
import io.github.vexorter42.tunor.MainActivity
import io.github.vexorter42.tunor.R
import io.github.vexorter42.tunor.TunorApp
import io.github.vexorter42.tunor.core.ConfigBuilder
import io.github.vexorter42.tunor.core.ConfigProblem
import io.nekohasekai.libbox.BridgeOptions
import io.nekohasekai.libbox.BridgeSession
import io.nekohasekai.libbox.CommandServer
import io.nekohasekai.libbox.CommandServerHandler
import io.nekohasekai.libbox.ConnectionOwner
import io.nekohasekai.libbox.InterfaceUpdateListener
import io.nekohasekai.libbox.Libbox
import io.nekohasekai.libbox.LocalDNSTransport
import io.nekohasekai.libbox.NeighborUpdateListener
import io.nekohasekai.libbox.NetworkInterfaceIterator
import io.nekohasekai.libbox.OverrideOptions
import io.nekohasekai.libbox.PlatformInterface
import io.nekohasekai.libbox.PlatformUser
import io.nekohasekai.libbox.RoutePrefix
import io.nekohasekai.libbox.RoutePrefixIterator
import io.nekohasekai.libbox.ShellSession
import io.nekohasekai.libbox.StringIterator
import io.nekohasekai.libbox.SystemProxyStatus
import io.nekohasekai.libbox.TunOptions
import io.nekohasekai.libbox.WIFIState
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch
import java.net.NetworkInterface
import io.nekohasekai.libbox.NetworkInterface as BoxInterface

/**
 * The tunnel, held by a service the system will not stop while it runs.
 *
 * It is the engine's host: it starts the engine with the configuration built from what the person set up,
 * and answers the engine's questions about the phone — open the TUN interface, which network is the real one,
 * keep this socket out of the tunnel. A good deal of what the engine can ask for (a shell, a bridge, Tailscale)
 * is for other programs and is refused here.
 */
class TunorVpnService : VpnService(), PlatformInterface, CommandServerHandler {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private var server: CommandServer? = null
    private var tun: ParcelFileDescriptor? = null
    @Volatile private var stopping = false

    override fun onBind(intent: Intent?): IBinder? = super.onBind(intent)

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent?.action == Vpn.ACTION_STOP) {
            stopEngine()
            return START_NOT_STICKY
        }
        foreground("Tunor", "Запускаю…")
        scope.launch { startEngine() }
        return START_NOT_STICKY
    }

    override fun onDestroy() {
        stopEngine(quiet = true)
        scope.cancel()
        super.onDestroy()
    }

    /** The person took the VPN away in system settings, or another VPN replaced this one. */
    override fun onRevoke() {
        stopEngine()
    }

    // ------------------------------------------------------------------------ starting and stopping

    private fun startEngine() {
        if (Vpn.phase.value is Phase.Starting || Vpn.phase.value is Phase.Started) return
        stopping = false
        Vpn.set(Phase.Starting)

        try {
            val door = io.github.vexorter42.tunor.core.Door.fresh()
            val config = ConfigBuilder.build(TunorApp.store.state.value, door)
            Libbox.checkConfig(config)
            Vpn.door = door

            NetMonitor.start(this)
            val s = CommandServer(this, this)
            s.start()
            server = s
            s.startOrReloadService(config, overrides())

            Vpn.set(Phase.Started)
            foreground("Tunor", "Туннель работает")
        } catch (e: ConfigProblem) {
            fail(e.message ?: "настройки не подходят")
        } catch (e: Exception) {
            Log.e(TAG, "start", e)
            fail("движок не запустился: " + (e.message ?: e.javaClass.simpleName))
        }
    }

    /** Which apps use the VPN: all, or only the ones the person chose (and this app, so that its own check goes the same way). */
    private fun overrides(): OverrideOptions = OverrideOptions().apply {
        val apps = TunorApp.store.state.value.settings.apps
        if (apps.isNotEmpty()) includePackage = Strings((apps + packageName).distinct().iterator())
    }

    private fun fail(why: String) {
        closeEverything()
        Vpn.set(Phase.Failed(why))
        stopForeground(STOP_FOREGROUND_REMOVE)
        stopSelf()
    }

    private fun stopEngine(quiet: Boolean = false) {
        if (stopping) return
        stopping = true
        closeEverything()
        if (!quiet) Vpn.set(Phase.Stopped)
        stopForeground(STOP_FOREGROUND_REMOVE)
        stopSelf()
    }

    private fun closeEverything() {
        try { server?.closeService() } catch (e: Exception) { Log.w(TAG, "closeService", e) }
        try { server?.close() } catch (e: Exception) { Log.w(TAG, "close", e) }
        server = null
        Vpn.door = null
        NetMonitor.stop()
        try { tun?.close() } catch (_: Exception) { }
        tun = null
    }

    // ------------------------------------------------------------------------ the notification

    private fun foreground(title: String, text: String) {
        val nm = getSystemService(NotificationManager::class.java)
        if (nm.getNotificationChannel(CHANNEL) == null) {
            nm.createNotificationChannel(NotificationChannel(CHANNEL, "Туннель", NotificationManager.IMPORTANCE_LOW))
        }
        val open = PendingIntent.getActivity(this, 0, Intent(this, MainActivity::class.java), PendingIntent.FLAG_IMMUTABLE)
        val stop = PendingIntent.getService(
            this, 1, Intent(this, TunorVpnService::class.java).setAction(Vpn.ACTION_STOP), PendingIntent.FLAG_IMMUTABLE,
        )
        val n: Notification = NotificationCompat.Builder(this, CHANNEL)
            .setSmallIcon(R.drawable.ic_notification)
            .setContentTitle(title)
            .setContentText(text)
            .setContentIntent(open)
            .setOngoing(true)
            .setOnlyAlertOnce(true)
            .addAction(0, "Остановить", stop)
            .build()
        val type = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE else 0
        ServiceCompat.startForeground(this, NOTIFICATION_ID, n, type)
    }

    // ------------------------------------------------------------------------ what the engine asks for

    override fun usePlatformAutoDetectInterfaceControl(): Boolean = true

    /** The engine opens a socket for its own traffic and asks for it to be kept out of the tunnel. */
    override fun autoDetectInterfaceControl(fd: Int) {
        protect(fd)
    }

    override fun openTun(options: TunOptions): Int {
        if (prepare(this) != null) error("нет разрешения на VPN")

        val b = Builder().setSession("Tunor").setMtu(options.mtu)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) b.setMetered(false)

        for (it in options.inet4Address.all()) b.addAddress(it.address(), it.prefix())
        for (it in options.inet6Address.all()) b.addAddress(it.address(), it.prefix())

        if (options.autoRoute) {
            if (options.dnsMode.value != Libbox.DNSModeDisabled) {
                val dns = options.dnsServerAddress
                while (dns.hasNext()) b.addDnsServer(dns.next())
            }

            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
                val v4 = options.inet4RouteAddress.all()
                if (v4.isNotEmpty()) v4.forEach { b.addRoute(it.toIpPrefix()) } else if (options.inet4Address.all().isNotEmpty()) b.addRoute("0.0.0.0", 0)
                val v6 = options.inet6RouteAddress.all()
                if (v6.isNotEmpty()) v6.forEach { b.addRoute(it.toIpPrefix()) } else if (options.inet6Address.all().isNotEmpty()) b.addRoute("::", 0)
                options.inet4RouteExcludeAddress.all().forEach { b.excludeRoute(it.toIpPrefix()) }
                options.inet6RouteExcludeAddress.all().forEach { b.excludeRoute(it.toIpPrefix()) }
            } else {
                val v4 = options.inet4RouteRange.all()
                if (v4.isEmpty()) b.addRoute("0.0.0.0", 0) else v4.forEach { b.addRoute(it.address(), it.prefix()) }
                val v6 = options.inet6RouteRange.all()
                if (v6.isEmpty()) b.addRoute("::", 0) else v6.forEach { b.addRoute(it.address(), it.prefix()) }
            }

            val include = options.includePackage
            while (include.hasNext()) {
                val p = include.next()
                try { b.addAllowedApplication(p) } catch (e: PackageManager.NameNotFoundException) { Log.w(TAG, "приложения нет: $p") }
            }
            val exclude = options.excludePackage
            while (exclude.hasNext()) {
                val p = exclude.next()
                try { b.addDisallowedApplication(p) } catch (e: PackageManager.NameNotFoundException) { Log.w(TAG, "приложения нет: $p") }
            }
        }

        if (options.isHTTPProxyEnabled && Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            b.setHttpProxy(ProxyInfo.buildDirectProxy(options.httpProxyServer, options.httpProxyServerPort, options.httpProxyBypassDomain.toList()))
        }

        val pfd = b.establish() ?: error("не удалось открыть VPN: приложение не подготовлено или отозвано")
        tun = pfd
        NetMonitor.network?.let { setUnderlyingNetworks(arrayOf(it)) }
        return pfd.fd
    }

    override fun startDefaultInterfaceMonitor(listener: InterfaceUpdateListener) { NetMonitor.setListener(listener) }
    override fun closeDefaultInterfaceMonitor(listener: InterfaceUpdateListener) { NetMonitor.setListener(null) }

    /** The phone's network interfaces, as the engine wants to see them. */
    override fun getInterfaces(): NetworkInterfaceIterator {
        val cm = getSystemService(android.net.ConnectivityManager::class.java)
        val systemInterfaces = NetworkInterface.getNetworkInterfaces()?.toList().orEmpty()
        val out = ArrayList<BoxInterface>()
        for (network in cm.allNetworks) {
            val props = cm.getLinkProperties(network) ?: continue
            val caps = cm.getNetworkCapabilities(network) ?: continue
            val sys = systemInterfaces.find { it.name == props.interfaceName } ?: continue
            out += BoxInterface().apply {
                name = props.interfaceName
                index = sys.index
                runCatching { mtu = sys.mtu }
                dnsServer = Strings(props.dnsServers.mapNotNull { it.hostAddress }.iterator())
                gateway = Strings(props.routes.filter { it.destination.prefixLength == 0 }.mapNotNull { it.gateway }.filterNot { it.isAnyLocalAddress }.mapNotNull { it.hostAddress }.iterator())
                addresses = Strings(sys.interfaceAddresses.map { "${it.address.hostAddress?.substringBefore('%')}/${it.networkPrefixLength}" }.iterator())
                type = when {
                    caps.hasTransport(android.net.NetworkCapabilities.TRANSPORT_WIFI) -> Libbox.InterfaceTypeWIFI
                    caps.hasTransport(android.net.NetworkCapabilities.TRANSPORT_CELLULAR) -> Libbox.InterfaceTypeCellular
                    caps.hasTransport(android.net.NetworkCapabilities.TRANSPORT_ETHERNET) -> Libbox.InterfaceTypeEthernet
                    else -> Libbox.InterfaceTypeOther
                }
                var f = 0
                if (caps.hasCapability(android.net.NetworkCapabilities.NET_CAPABILITY_INTERNET)) f = android.system.OsConstants.IFF_UP or android.system.OsConstants.IFF_RUNNING
                if (sys.isLoopback) f = f or android.system.OsConstants.IFF_LOOPBACK
                if (sys.isPointToPoint) f = f or android.system.OsConstants.IFF_POINTOPOINT
                if (sys.supportsMulticast()) f = f or android.system.OsConstants.IFF_MULTICAST
                flags = f
                metered = !caps.hasCapability(android.net.NetworkCapabilities.NET_CAPABILITY_NOT_METERED)
            }
        }
        return object : NetworkInterfaceIterator {
            private val it = out.iterator()
            override fun hasNext() = it.hasNext()
            override fun next(): BoxInterface = it.next()
        }
    }

    // What a phone has no use for, or no way to give: refused.
    override fun underNetworkExtension() = false
    override fun includeAllNetworks() = false
    override fun clearDNSCache() {}
    override fun readWIFIState(): WIFIState? = null
    override fun localDNSTransport(): LocalDNSTransport? = null
    override fun registerMyInterface(name: String?) {}
    override fun startNeighborMonitor(listener: NeighborUpdateListener?) {}
    override fun closeNeighborMonitor(listener: NeighborUpdateListener?) {}
    override fun findConnectionOwner(ip: Int, sa: String?, sp: Int, da: String?, dp: Int): ConnectionOwner = error("не поддерживается")
    override fun usePlatformShell() = false
    override fun checkPlatformShell() { error("не поддерживается") }
    override fun lookupUser(name: String?): PlatformUser = error("не поддерживается")
    override fun openShellSession(user: PlatformUser?, command: String?, environ: StringIterator?, term: String?, rows: Int, cols: Int): ShellSession = error("не поддерживается")
    override fun readSystemSSHHostKey(): String = error("не поддерживается")
    override fun lookupSFTPServer(): String = error("не поддерживается")
    override fun tailscaleHostname(): String = "tunor"
    override fun usePlatformBridge() = false
    override fun createBridge(options: BridgeOptions?): BridgeSession = error("не поддерживается")
    override fun sendNotification(notification: io.nekohasekai.libbox.Notification) {}
    override fun cancelNotification(identifier: String?, typeID: Int) {}

    // ------------------------------------------------------------------------ what the engine tells us

    /** The engine closed itself. */
    override fun serviceStop() {
        if (!stopping) stopEngine()
    }

    override fun serviceReload() {
        val s = server ?: return
        try {
            s.startOrReloadService(ConfigBuilder.build(TunorApp.store.state.value, Vpn.door), overrides())
        } catch (e: Exception) {
            Log.e(TAG, "reload", e)
            fail("настройки не применились: " + (e.message ?: ""))
        }
    }

    override fun getSystemProxyStatus(): SystemProxyStatus = SystemProxyStatus()
    override fun setSystemProxyEnabled(enabled: Boolean) {}
    override fun connectSSHAgent(): Int = error("не поддерживается")
    override fun triggerNativeCrash() {}
    override fun writeDebugMessage(message: String?) { Log.d(TAG, message ?: "") }

    companion object {
        private const val TAG = "TunorVpn"
        private const val CHANNEL = "tunnel"
        private const val NOTIFICATION_ID = 1
    }
}

/** A list of strings, in the shape the engine takes. */
class Strings(source: Iterator<String>) : StringIterator {
    private val items = source.asSequence().toList()
    private var at = 0
    override fun hasNext() = at < items.size
    override fun len() = items.size
    override fun next(): String = items[at++]
}

private fun RoutePrefixIterator.all(): List<RoutePrefix> {
    val out = ArrayList<RoutePrefix>()
    while (hasNext()) out += next()
    return out
}

private fun StringIterator.toList(): List<String> {
    val out = ArrayList<String>()
    while (hasNext()) out += next()
    return out
}

private fun RoutePrefix.toIpPrefix(): IpPrefix = IpPrefix(java.net.InetAddress.getByName(address()), prefix())
