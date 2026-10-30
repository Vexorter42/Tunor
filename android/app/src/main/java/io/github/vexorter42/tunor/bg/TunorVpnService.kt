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
import io.github.vexorter42.tunor.BuildConfig
import io.github.vexorter42.tunor.MainActivity
import io.github.vexorter42.tunor.R
import io.github.vexorter42.tunor.TunorApp
import io.github.vexorter42.tunor.core.AppLog
import io.github.vexorter42.tunor.core.ConfigBuilder
import io.github.vexorter42.tunor.core.ConfigProblem
import io.github.vexorter42.tunor.core.Crashes
import io.github.vexorter42.tunor.core.Door
import io.github.vexorter42.tunor.core.Reports
import io.github.vexorter42.tunor.core.RuleSets
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
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withTimeoutOrNull
import java.io.File
import java.net.InetAddress
import java.net.InetSocketAddress
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

    /** What the engine was last given, so that a change that comes to the same thing does not restart it. */
    private var lastConfig: String? = null
    private var lastExcluded: List<String> = emptyList()

    private var watch: Job? = null
    private val checkSoon = Channel<Unit>(Channel.CONFLATED)

    override fun onBind(intent: Intent?): IBinder? = super.onBind(intent)

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent?.action == Vpn.ACTION_STOP) {
            stopEngine()
            return START_NOT_STICKY
        }
        if (intent?.action == Vpn.ACTION_RELOAD) {
            scope.launch { reload() }
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
        MobileRescue.reset()
        AppLog.trimEngineLog(this)
        AppLog.i("service", "запуск туннеля, сеть: " + NetMonitor.kind())

        try {
            val state = TunorApp.store.state.value
            val door = Door.fresh(ConfigBuilder.live(state.tunnels).map { it.id })
            val config = ConfigBuilder.build(state, door, have = RuleSets::path, rescue = MobileRescue.plan.value)
            Libbox.checkConfig(config)
            Vpn.door = door
            Vpn.service = this
            lastConfig = config
            lastExcluded = ConfigBuilder.directApps(state)

            NetMonitor.onChanged = { checkSoon.trySend(Unit) }
            NetMonitor.start(this)
            val s = CommandServer(this, this)
            s.start()
            server = s
            s.startOrReloadService(config, overrides())

            Vpn.set(Phase.Started)
            Crashes.markTunnel(this, true)
            AppLog.i("service", "туннель работает")
            foreground("Tunor", statusText())
            startWatch()
        } catch (e: ConfigProblem) {
            fail(e.message ?: "настройки не подходят")
        } catch (e: Exception) {
            AppLog.e("service", "движок не запустился", e)
            fail("движок не запустился: " + (e.message ?: e.javaClass.simpleName))
        }
    }

    /** The apps the person sent straight out are kept out of the VPN altogether: nothing of theirs even reaches the engine. */
    private fun overrides(): OverrideOptions = OverrideOptions().apply {
        val direct = ConfigBuilder.directApps(TunorApp.store.state.value)
        if (direct.isNotEmpty()) excludePackage = Strings(direct.iterator())
    }

    // ------------------------------------------------------------------------ looking after the servers

    /**
     * While the tunnel runs: a look at the subscriptions a few seconds after the start, then every few minutes, and
     * again a little after the phone changes networks, which is when a network that lets only some sites through shows up.
     */
    private fun startWatch() {
        watch?.cancel()
        watch = scope.launch {
            var wait = 8_000L
            var lastNet = ""
            while (isActive) {
                val early = withTimeoutOrNull(wait) { checkSoon.receive() } != null
                if (early) delay(3_000)
                if (BuildConfig.DEBUG) debugCrash()
                val net = NetMonitor.kind()
                if (net != lastNet) {
                    AppLog.i("net", "сеть: $net")
                    lastNet = net
                }
                // On a mobile network that may cut the way out at any moment, the look is every minute instead of every five.
                val quick = TunorApp.store.state.value.settings.mobileRescue && net == "мобильная сеть"
                wait = Health.period(this@TunorVpnService, quick)
                if (Vpn.phase.value != Phase.Started) continue
                // The way round the mobile network first: it is the quick one, and the person is waiting for the tunnel to work.
                val rescue = try { MobileRescue.look(this@TunorVpnService) } catch (e: Exception) { AppLog.e("rescue", "проверка не удалась", e); Health.Outcome() }
                val health = try { Health.run(this@TunorVpnService) } catch (e: Exception) { AppLog.e("health", "проверка не удалась", e); Health.Outcome() }
                val done = rescue + health
                done.said.forEach { Vpn.note(it) }
                if (done.changed) {
                    foreground("Tunor", done.said.lastOrNull() ?: statusText())
                    reload()
                }
            }
        }
    }

    /**
     * Debug builds only, to see what the app does when it falls over: a file `crash-now.txt` in the app's folder, holding
     * "java" or "go", makes it fall over that way at the next look. Like the other files a debug build reads for testing.
     */
    private fun debugCrash() {
        val f = File(filesDir, "crash-now.txt")
        if (!f.isFile) return
        val how = f.readText().trim()
        f.delete()
        AppLog.w("debug", "падение по просьбе: $how")
        when (how) {
            "go" -> Libbox.triggerGoPanic()
            else -> throw IllegalStateException("проверочная ошибка")
        }
    }

    /** What the notification says while all is well. */
    private fun statusText(): String = if (MobileRescue.plan.value != null) "Мобильная сеть: всё идёт через подписку" else "Туннель работает"

    private fun fail(why: String) {
        AppLog.e("service", "туннель не запустился: $why")
        Reports.create(this, Reports.FAILED, "Туннель не запустился: $why")
        Crashes.markTunnel(this, false)
        closeEverything()
        Vpn.set(Phase.Failed(why))
        stopForeground(STOP_FOREGROUND_REMOVE)
        stopSelf()
    }

    private fun stopEngine(quiet: Boolean = false) {
        if (stopping) return
        stopping = true
        AppLog.i("service", "туннель остановлен")
        Crashes.markTunnel(this, false)
        MobileRescue.reset()
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
        Vpn.service = null
        watch?.cancel()
        watch = null
        NetMonitor.onChanged = null
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

    /** Which app a connection belongs to, asked of the system: what lets one app go through one tunnel and another through another. */
    override fun findConnectionOwner(ip: Int, sa: String?, sp: Int, da: String?, dp: Int): ConnectionOwner {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.Q) error("нужен Android 10 или новее")
        val cm = getSystemService(android.net.ConnectivityManager::class.java)
        val uid = cm.getConnectionOwnerUid(ip, InetSocketAddress(InetAddress.getByName(sa), sp), InetSocketAddress(InetAddress.getByName(da), dp))
        if (uid == android.os.Process.INVALID_UID) error("владелец соединения не найден")
        val names = packageManager.getPackagesForUid(uid).orEmpty()
        return ConnectionOwner().also {
            it.userId = uid
            it.setAndroidPackageNames(Strings(names.iterator()))
        }
    }
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
        if (!stopping) {
            AppLog.e("service", "движок остановился сам")
            Reports.create(this, Reports.ENGINE, "Движок остановился сам, когда его никто не просил")
            stopEngine()
        }
    }

    override fun serviceReload() { reload() }

    /**
     * Reads the setup again and gives it to the running engine. A setup that cannot run does not stop the one that
     * does: the engine keeps what it has and the person is told why the change did not take.
     */
    @Synchronized
    private fun reload() {
        val s = server ?: return
        if (stopping || Vpn.phase.value != Phase.Started) return
        try {
            val state = TunorApp.store.state.value
            ConfigBuilder.problem(state)?.let { Vpn.note("Изменения не применились: $it"); return }
            val ids = ConfigBuilder.live(state.tunnels).map { it.id }
            // The same entrances as before when the tunnels are the same, so that a change that comes to nothing is seen to be one.
            val door = Vpn.door?.takeIf { it.tunnels.keys == ids.toSet() } ?: Door.fresh(ids)
            val config = ConfigBuilder.build(state, door, have = RuleSets::path, rescue = MobileRescue.plan.value)
            val excluded = ConfigBuilder.directApps(state)
            if (config == lastConfig && excluded == lastExcluded) return
            Libbox.checkConfig(config)
            AppLog.i("service", "движок читает настройки заново" + if (MobileRescue.plan.value != null) " (запасной путь для мобильной сети)" else "")
            s.startOrReloadService(config, overrides())
            Vpn.door = door
            lastConfig = config
            lastExcluded = excluded
        } catch (e: Exception) {
            AppLog.e("service", "изменения не применились", e)
            Vpn.note("Изменения не применились: " + (e.message ?: e.javaClass.simpleName))
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
