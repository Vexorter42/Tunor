package io.github.vexorter42.tunor.ui

import android.app.Application
import android.content.Intent
import android.content.pm.ApplicationInfo
import android.content.pm.PackageManager
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import io.github.vexorter42.tunor.TunorApp
import io.github.vexorter42.tunor.bg.Health
import io.github.vexorter42.tunor.bg.Phase
import io.github.vexorter42.tunor.bg.Vpn
import io.github.vexorter42.tunor.core.AppLog
import io.github.vexorter42.tunor.core.AppRule
import io.github.vexorter42.tunor.core.Bypass
import io.github.vexorter42.tunor.core.Catalog
import io.github.vexorter42.tunor.core.CatalogEntry
import io.github.vexorter42.tunor.core.ConfigBuilder
import io.github.vexorter42.tunor.core.NodePing
import io.github.vexorter42.tunor.core.Pick
import io.github.vexorter42.tunor.core.PingResult
import io.github.vexorter42.tunor.core.Pool
import io.github.vexorter42.tunor.core.Reports
import io.github.vexorter42.tunor.core.RuleSets
import io.github.vexorter42.tunor.core.Site
import io.github.vexorter42.tunor.core.Subscription
import io.github.vexorter42.tunor.core.Trace
import io.github.vexorter42.tunor.core.Tracer
import io.github.vexorter42.tunor.core.Tunnel
import io.github.vexorter42.tunor.core.Updater
import io.github.vexorter42.tunor.core.VlessLink
import io.github.vexorter42.tunor.core.WgConf
import kotlinx.coroutines.Deferred
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.FlowPreview
import kotlinx.coroutines.async
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.debounce
import kotlinx.coroutines.flow.distinctUntilChanged
import kotlinx.coroutines.flow.drop
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.util.UUID

/** One row of the connection check: a road, and where it came out or why it did not. */
data class Row(val road: String, val trace: Trace?, val problem: String?, val direct: Boolean = false)

/** An installed app a person can send through the tunnel by itself. */
data class AppItem(val pkg: String, val label: String)

/** The catalog on the screen and where it came from. */
data class CatalogView(val entries: List<CatalogEntry>, val from: Catalog.From)

@OptIn(FlowPreview::class)
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

    private fun newId() = "t" + UUID.randomUUID().toString().replace("-", "").take(6)

    /**
     * A .conf given as text. [slot] is [Tunnel.WARP] or [Tunnel.GEO] for those two places, or empty for one more of the
     * person's own. Returns whether it was taken; if not, [message] says why.
     */
    fun addConf(text: String, name: String, slot: String = ""): Boolean {
        val problem = WgConf.parse(text).problem()
        if (problem != null) {
            _message.value = "Конфиг не подошёл: $problem."
            return false
        }
        val id = slot.ifEmpty { newId() }
        val title = when (slot) {
            Tunnel.WARP -> "WARP"
            Tunnel.GEO -> "geo"
            else -> name.removeSuffix(".conf").ifBlank { "VPN" }
        }
        store.update { s ->
            val old = s.tunnels.firstOrNull { it.id == id }
            // geo goes out through WARP unless the person says otherwise; a config put in its place keeps what they said.
            val via = old?.via ?: if (slot == Tunnel.GEO) Tunnel.WARP else ""
            val t = Tunnel(id, title, Tunnel.WG, conf = text, via = via)
            s.copy(tunnels = if (old != null) s.tunnels.map { if (it.id == id) t else it } else s.tunnels + t)
        }
        _message.value = when (slot) {
            Tunnel.WARP -> "Конфиг WARP добавлен."
            Tunnel.GEO -> "Конфиг geo добавлен."
            else -> "Конфиг добавлен."
        }
        return true
    }

    /** A `vless://` link or the address of a subscription, whichever it is. */
    fun addLinkOrSub(text: String) {
        val t = text.trim()
        when {
            VlessLink.looksLike(t) -> {
                val p = VlessLink.parse(t)
                if (!p.ok) { _message.value = "Ссылка не подошла: ${p.problem}."; return }
                add(Tunnel(newId(), p.name, Tunnel.LINK, link = t))
                _message.value = "Ссылка добавлена."
            }
            Subscription.looksLike(t) -> viewModelScope.launch {
                _busy.value = true
                val r = Subscription.fetch(t)
                _busy.value = false
                if (!r.ok) { AppLog.w("sub", "подписка не подошла: ${r.problem}"); _message.value = "Подписка не подошла: ${r.problem}."; return@launch }
                val fresh = Tunnel(newId(), r.title.ifBlank { hostOf(t) }, Tunnel.SUB, sub = t, nodes = r.nodes)
                add(fresh)
                _message.value = "Подписка добавлена: серверов ${r.nodes.size}."
                fillPool(fresh.id)
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
            if (!r.ok) { AppLog.w("sub", "«${t.title}»: подписку не обновить: ${r.problem}"); _message.value = "Подписку не обновить: ${r.problem}."; return@launch }
            val keep = t.nodes.getOrNull(t.chosen)?.url
            val chosen = r.nodes.indexOfFirst { it.url == keep }.takeIf { it >= 0 } ?: 0
            store.update { s ->
                s.copy(tunnels = s.tunnels.map {
                    if (it.id == id) it.copy(nodes = r.nodes, chosen = chosen, pool = it.pool.filter { link -> r.nodes.any { n -> n.url == link } }) else it
                })
            }
            AppLog.i("sub", "«${t.title}»: подписка обновлена, серверов ${r.nodes.size}")
            _message.value = "Подписка обновлена: серверов ${r.nodes.size}."
            if (store.state.value.tunnels.firstOrNull { it.id == id }?.let { it.auto && it.pool.isEmpty() } == true) fillPool(id)
        }
    }

    /** A server taken by hand: it is the person's choice, so the app stops choosing for this subscription. */
    fun chooseNode(id: String, index: Int) {
        val was = store.state.value.tunnels.firstOrNull { it.id == id }?.auto == true
        store.update { s -> s.copy(tunnels = s.tunnels.map { if (it.id == id) it.copy(chosen = index, auto = false, autoFrom = "") else it }) }
        if (was) _message.value = "Автовыбор выключен: сервер выбран вручную."
    }

    /** Lets the app look after the servers of a subscription, or leaves it on the one chosen. */
    fun setAuto(id: String, on: Boolean) {
        store.update { s -> s.copy(tunnels = s.tunnels.map { if (it.id == id) it.copy(auto = on, autoFrom = if (on) "" else it.autoFrom) else it }) }
        if (on) fillPool(id)
    }

    /** Times the servers of a subscription and gives the engine the nearest few to choose among. */
    private fun fillPool(id: String) {
        val t = store.state.value.tunnels.firstOrNull { it.id == id && it.kind == Tunnel.SUB } ?: return
        viewModelScope.launch {
            val next = Pool.measure(t)
            if (next.isEmpty()) { AppLog.w("pool", "«${t.title}»: ни один сервер не ответил"); return@launch }
            store.update { s -> s.copy(tunnels = s.tunnels.map { if (it.id == id && it.nodes == t.nodes && it.auto) it.copy(pool = next) else it }) }
            AppLog.i("pool", "«${t.title}»: выбрано серверов ${next.size}")
        }
    }

    /** Sets which tunnel [id] rides inside; empty — none. */
    fun setVia(id: String, via: String) = store.update { s ->
        s.copy(tunnels = s.tunnels.map { if (it.id == id) it.copy(via = via) else it })
    }

    fun delete(id: String) = store.update { s ->
        s.copy(
            tunnels = s.tunnels.filterNot { it.id == id },
            settings = s.settings.copy(
                rest = if (s.settings.rest == id) "" else s.settings.rest,
                mobileRescueSub = if (s.settings.mobileRescueSub == id) "" else s.settings.mobileRescueSub,
            ),
        )
    }

    private fun add(t: Tunnel) = store.update { s -> s.copy(tunnels = s.tunnels + t) }

    private fun hostOf(url: String) = try { java.net.URI(url).host ?: "Подписка" } catch (_: Exception) { "Подписка" }

    // ------------------------------------------------------------------ sites

    /** Adds the sites in [text] to go through [tunnel]. Returns how many were new. */
    fun addSites(text: String, tunnel: String): Int {
        val entries = text.split(',', ' ', '\n', ';').map { ConfigBuilder.domainEntry(it) }.filter { it.isNotEmpty() && '.' in it }.distinct()
        if (entries.isEmpty()) { _message.value = "В этом нет адреса сайта."; return 0 }
        var added = 0
        store.update { s ->
            // Written down by name, not as "whichever works first": the site stays with the tunnel it was given.
            val to = tunnel.ifEmpty { ConfigBuilder.live(s.tunnels).firstOrNull()?.id ?: ConfigBuilder.ordered(s.tunnels).firstOrNull()?.id.orEmpty() }
            val have = s.rules.sites.map { it.domain }.toSet()
            val fresh = entries.filter { it !in have }
            added = fresh.size
            s.copy(rules = s.rules.copy(sites = s.rules.sites + fresh.map { Site(it, to) }))
        }
        if (added == 0) _message.value = "Уже есть в списке."
        return added
    }

    fun removeSite(domain: String) = store.update { s ->
        s.copy(rules = s.rules.copy(sites = s.rules.sites.filterNot { it.domain == domain }))
    }

    fun setSiteTunnel(domain: String, tunnel: String) = store.update { s ->
        s.copy(rules = s.rules.copy(sites = s.rules.sites.map { if (it.domain == domain) it.copy(tunnel = tunnel) else it }))
    }

    /** Where traffic that no rule names goes: a tunnel's id, or empty for straight out. */
    fun setRest(id: String) = store.update { it.copy(settings = it.settings.copy(rest = id)) }

    // ------------------------------------------------------------------ the catalog's lists

    private val _catalog = MutableStateFlow<CatalogView?>(null)
    val catalog: StateFlow<CatalogView?> = _catalog.asStateFlow()
    private var catalogAsked = false

    /** Shows the catalog that is on the phone at once, then asks the repository for a newer one. */
    fun loadCatalog() {
        if (catalogAsked) return
        catalogAsked = true
        val ctx = getApplication<Application>()
        viewModelScope.launch {
            Catalog.load(ctx, askRepository = false).let { (entries, from) -> _catalog.value = CatalogView(entries, from) }
            Catalog.load(ctx, askRepository = true).let { (entries, from) ->
                if (from == Catalog.From.Repository) _catalog.value = CatalogView(entries, from)
            }
        }
    }

    /** Which lists are being fetched right now. */
    private val _downloading = MutableStateFlow<Set<String>>(emptySet())
    val downloading: StateFlow<Set<String>> = _downloading.asStateFlow()

    /** What went wrong with a list the last time it was fetched, by id. */
    private val _listProblems = MutableStateFlow<Map<String, String>>(emptyMap())
    val listProblems: StateFlow<Map<String, String>> = _listProblems.asStateFlow()

    /** When each switched-on list's file was fetched (0 — it is not on the phone), by id. */
    private val _files = MutableStateFlow<Map<String, Long>>(emptyMap())
    val files: StateFlow<Map<String, Long>> = _files.asStateFlow()

    private fun refreshFiles() {
        _files.value = store.state.value.rules.lists.associate { it.id to RuleSets.stamp(it) }
    }

    private fun working(id: String, on: Boolean) {
        _downloading.value = if (on) _downloading.value + id else _downloading.value - id
    }

    private fun result(id: String, problem: String?) {
        _listProblems.value = if (problem == null) _listProblems.value - id else _listProblems.value + (id to problem)
    }

    /** Switches a list on, fetching it first, or off. A list that could not be fetched stays chosen: the choice is not lost to a bad network. */
    fun setList(e: CatalogEntry, on: Boolean) {
        if (!on) {
            store.update { s -> s.copy(rules = s.rules.copy(lists = s.rules.lists.filterNot { it.id == e.id })) }
            result(e.id, null)
            refreshFiles()
            return
        }
        if (e.id in _downloading.value) return
        val pick = Pick(e.id, e.title, e.url, e.route)
        viewModelScope.launch {
            working(e.id, true)
            val got = RuleSets.download(pick)
            working(e.id, false)
            result(e.id, got.problem)
            store.update { s ->
                if (s.rules.lists.any { it.id == e.id }) s
                else s.copy(rules = s.rules.copy(lists = s.rules.lists + pick), settings = s.settings.copy(listsFetchedAt = if (got.ok) System.currentTimeMillis() else s.settings.listsFetchedAt))
            }
            refreshFiles()
            if (!got.ok) _message.value = "«${e.title}» не скачался: ${got.problem}. Он остался в выбранных; нажми «Обновить списки», когда будет сеть."
        }
    }

    fun setListTunnel(id: String, tunnel: String) = store.update { s ->
        s.copy(rules = s.rules.copy(lists = s.rules.lists.map { if (it.id == id) it.copy(tunnel = tunnel) else it }))
    }

    /** Fetches every switched-on list again. [quiet]: the app's own errand at start, which says nothing unless it changed something. */
    fun updateLists(quiet: Boolean = false) {
        val picks = store.state.value.rules.lists
        if (picks.isEmpty()) {
            if (!quiet) _message.value = "Ни один список не выбран."
            return
        }
        if (_busy.value) return
        viewModelScope.launch {
            _busy.value = true
            var ok = 0
            var failed = 0
            var unreachable = 0
            for (p in picks) {
                // With nobody answering twice in a row the rest would fail the same way: they are not waited for.
                if (unreachable >= 2) { failed++; result(p.id, "пропущено: ни GitHub, ни зеркала не отвечают"); continue }
                working(p.id, true)
                val got = RuleSets.download(p)
                working(p.id, false)
                result(p.id, got.problem)
                if (got.ok) { ok++; unreachable = 0 } else { failed++; if (got.unreachable) unreachable++ }
            }
            if (ok > 0) store.update { it.copy(settings = it.settings.copy(listsFetchedAt = System.currentTimeMillis())) }
            refreshFiles()
            _busy.value = false
            if (ok > 0 && Vpn.phase.value == Phase.Started) Vpn.reload(getApplication())
            if (!quiet) _message.value = when {
                failed == 0 -> "Списки обновлены: $ok."
                ok == 0 -> "Списки не обновились: нет связи с GitHub и зеркалами."
                else -> "Обновлено списков: $ok, не удалось: $failed."
            }
        }
    }

    /** Lists the phone has not seen for a few days, or has lost, are fetched again when the app opens. */
    private fun refreshStale() {
        val s = store.state.value
        if (s.rules.lists.isEmpty()) return
        val old = System.currentTimeMillis() - s.settings.listsFetchedAt > 3L * 24 * 3600 * 1000
        val lost = s.rules.lists.any { RuleSets.path(it) == null }
        if (old || lost) updateLists(quiet = true)
    }

    // ------------------------------------------------------------------ apps

    /** Where an app's traffic goes: a tunnel's id, [AppRule.DIRECT] for around the VPN altogether, or empty to go by the rules like everything else. */
    fun setAppRoute(pkg: String, tunnel: String) = store.update { s ->
        val others = s.rules.apps.filterNot { it.pkg == pkg }
        s.copy(rules = s.rules.copy(apps = if (tunnel.isEmpty()) others else others + AppRule(pkg, tunnel)))
    }

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

    // ------------------------------------------------------------------ ping

    private val _pings = MutableStateFlow<Map<String, Map<Int, PingResult>>>(emptyMap())
    /** How the servers of each subscription answered the last time they were timed, by the tunnel's id and the server's place in its list. */
    val pings: StateFlow<Map<String, Map<Int, PingResult>>> = _pings.asStateFlow()

    private val _pinging = MutableStateFlow<Map<String, Pair<Int, Int>>>(emptyMap())
    /** The subscriptions being timed now, with how many servers are done of how many. */
    val pinging: StateFlow<Map<String, Pair<Int, Int>>> = _pinging.asStateFlow()

    /** Times every server of a subscription. If none of the usual ones answers, moves to the nearest of its own for that, as the app does by itself. */
    fun ping(id: String) {
        val t = store.state.value.tunnels.firstOrNull { it.id == id && it.kind == Tunnel.SUB } ?: return
        if (id in _pinging.value) return
        viewModelScope.launch {
            _pinging.value = _pinging.value + (id to (0 to t.nodes.size))
            val answers = NodePing.measureAll(t.nodes.map { it.url }) { done, all -> _pinging.value = _pinging.value + (id to (done to all)) }
            _pinging.value = _pinging.value - id
            _pings.value = _pings.value + (id to answers)

            if (answers.values.none { it.answered }) {
                _message.value = "Ни один сервер не отвечает: " +
                    if (Health.hasNetwork(getApplication())) "проверь адрес подписки или попробуй позже." else "нет сети."
            }
            if (t.auto) {
                val next = Pool.daily(t, answers)
                if (next.isNotEmpty() && next != t.pool) {
                    store.update { s -> s.copy(tunnels = s.tunnels.map { if (it.id == id && it.nodes == t.nodes && it.auto) it.copy(pool = next) else it }) }
                    _message.value = "Для движка выбрано серверов: ${next.size}."
                }
                return@launch
            }
            if (!store.state.value.settings.autoBypass) return@launch
            val move = Bypass.decide(t, answers) ?: return@launch
            var done = false
            store.update { s ->
                s.copy(tunnels = s.tunnels.map { if (it.id == id && it.chosen == t.chosen && it.nodes == t.nodes) { done = true; Bypass.apply(it, move) } else it })
            }
            if (done) {
                val name = t.nodes[move.index].name
                _message.value = if (move.back) "Обычные серверы снова отвечают: вернулся на «$name»."
                else "Обычные серверы не отвечают: переключился на «$name» (${move.ms} мс)."
            }
        }
    }

    /** Takes the server that answered fastest. */
    fun chooseFastest(id: String) {
        val answers = _pings.value[id] ?: return
        val best = answers.filter { it.value.answered }.minByOrNull { it.value.ms!! }?.key
        if (best == null) { _message.value = "Ни один сервер не ответил."; return }
        // The person's own choice: the app will not take it back on its own.
        store.update { s -> s.copy(tunnels = s.tunnels.map { if (it.id == id) it.copy(chosen = best, autoFrom = "") else it }) }
        _message.value = "Выбран самый быстрый: ${store.state.value.tunnels.first { it.id == id }.nodes[best].name} (${answers.getValue(best).ms} мс)."
    }

    fun setAutoBypass(on: Boolean) = store.update { it.copy(settings = it.settings.copy(autoBypass = on)) }

    fun setMobileRescue(on: Boolean) = store.update { it.copy(settings = it.settings.copy(mobileRescue = on)) }

    fun setMobileRescueSub(id: String) = store.update { it.copy(settings = it.settings.copy(mobileRescueSub = id)) }

    // ------------------------------------------------------------------ reports

    private val _reports = MutableStateFlow<List<Reports.Item>>(emptyList())
    /** The reports on the phone, the newest first. */
    val reports: StateFlow<List<Reports.Item>> = _reports.asStateFlow()

    fun loadReports() {
        val ctx = getApplication<Application>()
        viewModelScope.launch { _reports.value = withContext(Dispatchers.IO) { Reports.list(ctx) } }
    }

    /** Collects a report now, at the person's asking. Says the file to [then], or tells why it could not. */
    fun makeReport(then: (java.io.File) -> Unit) {
        val ctx = getApplication<Application>()
        viewModelScope.launch {
            val f = withContext(Dispatchers.IO) { Reports.create(ctx, Reports.MANUAL, "Отчёт по просьбе пользователя") }
            loadReports()
            if (f == null) _message.value = "Отчёт не удалось записать." else then(f)
        }
    }

    fun deleteReport(item: Reports.Item) {
        val ctx = getApplication<Application>()
        viewModelScope.launch { withContext(Dispatchers.IO) { Reports.delete(ctx, item) }; loadReports() }
    }

    fun deleteReports() {
        val ctx = getApplication<Application>()
        viewModelScope.launch { withContext(Dispatchers.IO) { Reports.deleteAll(ctx) }; loadReports() }
    }

    /** The waiting report has been shown. */
    fun reportSeen() = Reports.markSeen(getApplication())

    // ------------------------------------------------------------------ updates

    val update: StateFlow<Updater.State> = Updater.state

    fun setAutoUpdateCheck(on: Boolean) = store.update { it.copy(settings = it.settings.copy(autoUpdateCheck = on)) }

    fun checkUpdate() {
        viewModelScope.launch {
            Updater.check(getApplication())
            store.update { it.copy(settings = it.settings.copy(updateCheckedAt = System.currentTimeMillis())) }
        }
    }

    /** Downloads the new version and hands it to the system's installer, which asks the person to confirm. */
    fun installUpdate() {
        val info = (Updater.state.value as? Updater.State.Available)?.info ?: (Updater.state.value as? Updater.State.Failed)?.info ?: return
        val ctx = getApplication<Application>()
        if (!Updater.mayInstall(ctx)) {
            _message.value = "Разреши Tunor устанавливать приложения: включи переключатель на открывшемся экране и вернись сюда."
            ctx.startActivity(
                Intent(android.provider.Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES, android.net.Uri.parse("package:" + ctx.packageName))
                    .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK),
            )
            return
        }
        viewModelScope.launch { Updater.install(ctx, info) }
    }

    /** Looks for a new version when the app opens, at most twice a day. */
    private fun checkUpdateAtStart() {
        val s = store.state.value.settings
        if (!s.autoUpdateCheck || System.currentTimeMillis() - s.updateCheckedAt < 12L * 3600 * 1000) return
        checkUpdate()
    }

    // ------------------------------------------------------------------ the check

    private val _check = MutableStateFlow<List<Row>?>(null)
    val check: StateFlow<List<Row>?> = _check.asStateFlow()
    private val _checking = MutableStateFlow(false)
    val checking: StateFlow<Boolean> = _checking.asStateFlow()

    /** Asks each road where it comes out: through every tunnel that is running, and directly. */
    fun runCheck() {
        val door = Vpn.door
        if (door == null || Vpn.phase.value != Phase.Started) {
            _message.value = "Сначала запусти туннель."
            return
        }
        viewModelScope.launch {
            _checking.value = true
            val tunnels = store.state.value.tunnels
            _check.value = coroutineScope {
                val asked = ArrayList<Triple<String, Boolean, Deferred<Result<Trace>>>>()
                for (t in ConfigBuilder.live(tunnels)) door.tunnels[t.id]?.let { port ->
                    val road = if (t.via.isEmpty()) t.title else "${t.title} · через ${ConfigBuilder.titleOf(t.via, tunnels)}"
                    asked += Triple(road, false, async { Tracer.ask(door, port) })
                }
                asked += Triple("Напрямую", true, async { Tracer.ask(door, door.direct) })
                asked.map { (road, direct, job) ->
                    val r = job.await()
                    Row(road, r.getOrNull(), r.exceptionOrNull()?.let(::describe), direct)
                }
            }
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

    // ------------------------------------------------------------------ at start

    // Last in the class: it uses the state above, and a block that runs before the fields it uses are made finds them empty.
    init {
        // A running engine follows the setup: a tick on a list, a site, a tunnel's server reaches it without a restart by hand.
        viewModelScope.launch {
            store.state
                .map { Triple(it.tunnels, it.rules, it.settings.rest) }
                .distinctUntilChanged()
                .drop(1)
                .debounce(700)
                .collect { Vpn.reload(getApplication()) }
        }
        viewModelScope.launch { Vpn.notes.collect { say(it) } }
        refreshFiles()
        refreshStale()
        checkUpdateAtStart()
    }
}
