package io.github.vexorter42.tunor.ui

import android.app.Application
import android.content.Intent
import android.content.pm.ApplicationInfo
import android.content.pm.PackageManager
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import io.github.vexorter42.tunor.TunorApp
import io.github.vexorter42.tunor.bg.Phase
import io.github.vexorter42.tunor.bg.Vpn
import io.github.vexorter42.tunor.core.ConfigBuilder
import io.github.vexorter42.tunor.core.Node
import io.github.vexorter42.tunor.core.Settings
import io.github.vexorter42.tunor.core.Subscription
import io.github.vexorter42.tunor.core.Trace
import io.github.vexorter42.tunor.core.Tracer
import io.github.vexorter42.tunor.core.Tunnel
import io.github.vexorter42.tunor.core.VlessLink
import io.github.vexorter42.tunor.core.WgConf
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.util.UUID

/** One row of the connection check: a road, and where it came out or why it did not. */
data class Row(val road: String, val trace: Trace?, val problem: String?)

/** An installed app a person can send through the tunnel by itself. */
data class AppItem(val pkg: String, val label: String)

class MainViewModel(app: Application) : AndroidViewModel(app) {
    private val store = TunorApp.store
    val state = store.state

    // ------------------------------------------------------------------ messages

    private val _message = MutableStateFlow<String?>(null)
    /** One line that wants the person's eye: a problem with what they added, or that it was added. */
    val message: StateFlow<String?> = _message.asStateFlow()
    fun clearMessage() { _message.value = null }
    fun say(text: String) { _message.value = text }

    private val _busy = MutableStateFlow(false)
    val busy: StateFlow<Boolean> = _busy.asStateFlow()

    // ------------------------------------------------------------------ tunnels

    /** A .conf given as text. Returns whether it was taken; if not, [message] says why. */
    fun addConf(text: String, name: String): Boolean {
        val problem = WgConf.parse(text).problem()
        if (problem != null) {
            _message.value = "Конфиг не подошёл: $problem."
            return false
        }
        add(Tunnel(UUID.randomUUID().toString(), name.removeSuffix(".conf").ifBlank { "WARP" }, Tunnel.WG, conf = text))
        _message.value = "Конфиг добавлен."
        return true
    }

    /** A `vless://` link or the address of a subscription, whichever it is. */
    fun addLinkOrSub(text: String) {
        val t = text.trim()
        when {
            VlessLink.looksLike(t) -> {
                val p = VlessLink.parse(t)
                if (!p.ok) { _message.value = "Ссылка не подошла: ${p.problem}."; return }
                add(Tunnel(UUID.randomUUID().toString(), p.name, Tunnel.LINK, link = t))
                _message.value = "Ссылка добавлена."
            }
            Subscription.looksLike(t) -> viewModelScope.launch {
                _busy.value = true
                val r = Subscription.fetch(t)
                _busy.value = false
                if (!r.ok) { _message.value = "Подписка не подошла: ${r.problem}."; return@launch }
                add(Tunnel(UUID.randomUUID().toString(), r.title.ifBlank { hostOf(t) }, Tunnel.SUB, sub = t, nodes = r.nodes))
                _message.value = "Подписка добавлена: серверов ${r.nodes.size}."
            }
            else -> _message.value = "Это не конфиг .conf, не ссылка vless:// и не адрес подписки."
        }
    }

    fun refreshSub(id: String) {
        val t = store.state.value.tunnels.firstOrNull { it.id == id && it.kind == Tunnel.SUB } ?: return
        viewModelScope.launch {
            _busy.value = true
            val r = Subscription.fetch(t.sub)
            _busy.value = false
            if (!r.ok) { _message.value = "Подписку не обновить: ${r.problem}."; return@launch }
            val keep = t.nodes.getOrNull(t.chosen)?.url
            val chosen = r.nodes.indexOfFirst { it.url == keep }.takeIf { it >= 0 } ?: 0
            store.update { s -> s.copy(tunnels = s.tunnels.map { if (it.id == id) it.copy(nodes = r.nodes, chosen = chosen) else it }) }
            _message.value = "Подписка обновлена: серверов ${r.nodes.size}."
        }
    }

    fun chooseNode(id: String, index: Int) = store.update { s ->
        s.copy(tunnels = s.tunnels.map { if (it.id == id) it.copy(chosen = index) else it })
    }

    fun use(id: String) = store.update { it.copy(settings = it.settings.copy(active = id)) }

    fun delete(id: String) = store.update { s ->
        val left = s.tunnels.filterNot { it.id == id }
        s.copy(tunnels = left, settings = s.settings.copy(active = if (s.settings.active == id) left.firstOrNull()?.id.orEmpty() else s.settings.active))
    }

    private fun add(t: Tunnel) = store.update { s ->
        s.copy(tunnels = s.tunnels + t, settings = if (s.settings.active.isEmpty() || s.tunnels.isEmpty()) s.settings.copy(active = t.id) else s.settings)
    }

    private fun hostOf(url: String) = try { java.net.URI(url).host ?: "Подписка" } catch (_: Exception) { "Подписка" }

    // ------------------------------------------------------------------ rules

    fun addDomains(text: String): Int {
        val entries = text.split(',', ' ', '\n', ';').map { ConfigBuilder.domainEntry(it) }.filter { it.isNotEmpty() && '.' in it }
        if (entries.isEmpty()) { _message.value = "В этом нет адреса сайта."; return 0 }
        var added = 0
        store.update { s ->
            val fresh = entries.filter { it !in s.rules.domains }.distinct()
            added = fresh.size
            s.copy(rules = s.rules.copy(domains = s.rules.domains + fresh))
        }
        return added
    }

    fun removeDomain(d: String) = store.update { it.copy(rules = it.rules.copy(domains = it.rules.domains - d)) }

    fun setEverything(all: Boolean) = store.update {
        it.copy(settings = it.settings.copy(everything = if (all) Settings.TUNNEL else Settings.DIRECT))
    }

    // ------------------------------------------------------------------ apps

    fun setApp(pkg: String, on: Boolean) = store.update {
        val list = if (on) (it.settings.apps + pkg).distinct() else it.settings.apps - pkg
        it.copy(settings = it.settings.copy(apps = list))
    }

    fun clearApps() = store.update { it.copy(settings = it.settings.copy(apps = emptyList())) }

    private val _apps = MutableStateFlow<List<AppItem>>(emptyList())
    val apps: StateFlow<List<AppItem>> = _apps.asStateFlow()

    /** The apps with a launcher icon: what a person thinks of as "my apps". */
    fun loadApps() {
        if (_apps.value.isNotEmpty()) return
        viewModelScope.launch {
            val list = withContext(Dispatchers.IO) {
                val pm = getApplication<Application>().packageManager
                pm.queryIntentActivities(Intent(Intent.ACTION_MAIN).addCategory(Intent.CATEGORY_LAUNCHER), PackageManager.MATCH_ALL)
                    .map { it.activityInfo.applicationInfo }
                    .filter { it.packageName != getApplication<Application>().packageName }
                    .distinctBy(ApplicationInfo::packageName)
                    .map { AppItem(it.packageName, pm.getApplicationLabel(it).toString()) }
                    .sortedBy { it.label.lowercase() }
            }
            _apps.value = list
        }
    }

    // ------------------------------------------------------------------ the check

    private val _check = MutableStateFlow<List<Row>?>(null)
    val check: StateFlow<List<Row>?> = _check.asStateFlow()
    private val _checking = MutableStateFlow(false)
    val checking: StateFlow<Boolean> = _checking.asStateFlow()

    /** Asks each road where it comes out: through the tunnel, and directly. */
    fun runCheck() {
        val door = Vpn.door
        if (door == null || Vpn.phase.value != Phase.Started) {
            _message.value = "Сначала запусти туннель."
            return
        }
        viewModelScope.launch {
            _checking.value = true
            val tunnel = Tracer.ask(door, door.tunnelPort)
            val direct = Tracer.ask(door, door.directPort)
            _check.value = listOf(
                Row("Через туннель", tunnel.getOrNull(), tunnel.exceptionOrNull()?.let(::describe)),
                Row("Напрямую", direct.getOrNull(), direct.exceptionOrNull()?.let(::describe)),
            )
            _checking.value = false
        }
    }

    fun forgetCheck() { _check.value = null }

    private fun describe(e: Throwable) = when (e) {
        is java.net.SocketTimeoutException -> "не ответил за 8 секунд"
        is java.net.ConnectException -> "нет соединения"
        else -> e.message ?: e.javaClass.simpleName
    }

    // ------------------------------------------------------------------ first run

    fun acceptTerms() = store.update { it.copy(settings = it.settings.copy(termsAccepted = true)) }
}
