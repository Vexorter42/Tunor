package io.github.vexorter42.tunor.ui

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Close
import androidx.compose.material3.Button
import androidx.compose.material3.Checkbox
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.FilterChip
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import io.github.vexorter42.tunor.core.CatalogEntry
import io.github.vexorter42.tunor.core.ConfigBuilder
import io.github.vexorter42.tunor.core.Pick
import io.github.vexorter42.tunor.core.Tunnel

@Composable
fun RulesScreen(vm: MainViewModel, padding: PaddingValues) {
    var section by rememberSaveable { mutableIntStateOf(0) }
    LaunchedEffect(Unit) { vm.loadApps(); vm.loadCatalog() }

    Column(Modifier.padding(padding).padding(horizontal = 20.dp)) {
        Spacer(Modifier.height(16.dp))
        Text("Правила", fontSize = 26.sp, fontWeight = FontWeight.Bold)
        Row(Modifier.padding(vertical = 10.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            FilterChip(section == 0, { section = 0 }, { Text("Списки") })
            FilterChip(section == 1, { section = 1 }, { Text("Сайты") })
            FilterChip(section == 2, { section = 2 }, { Text("Приложения") })
        }
        when (section) {
            0 -> Lists(vm)
            1 -> Sites(vm)
            else -> Apps(vm)
        }
    }
}

// ---------------------------------------------------------------------- the catalog's lists

@Composable
private fun Lists(vm: MainViewModel) {
    val state by vm.state.collectAsState()
    val catalog by vm.catalog.collectAsState()
    val downloading by vm.downloading.collectAsState()
    val problems by vm.listProblems.collectAsState()
    val files by vm.files.collectAsState()
    val picks = state.rules.lists.associateBy { it.id }

    LazyColumn(verticalArrangement = Arrangement.spacedBy(6.dp)) {
        item {
            Panel(Modifier.fillMaxWidth()) {
                Text("Готовые списки сайтов", fontWeight = FontWeight.SemiBold, fontSize = 16.sp)
                Text(
                    "Отметь нужное: список скачается, и эти сайты пойдут через выбранный туннель. " +
                        "Обычно это WARP, а для сервисов, которые не пускают российские адреса, — geo.",
                    color = Palette.Dim, fontSize = 13.sp, modifier = Modifier.padding(top = 4.dp, bottom = 10.dp),
                )
                Row(verticalAlignment = Alignment.CenterVertically) {
                    OutlinedButton(onClick = { vm.updateLists() }, enabled = picks.isNotEmpty()) { Text("Обновить списки") }
                    Spacer(Modifier.padding(4.dp))
                    Text(
                        catalog?.from?.words?.replaceFirstChar { it.uppercase() } ?: "Загружаю каталог…",
                        color = Palette.Dim, fontSize = 12.sp, modifier = Modifier.weight(1f),
                    )
                }
            }
        }
        val view = catalog
        if (view != null) {
            view.entries.groupBy { it.category }.forEach { (category, entries) ->
                item(key = "category:$category") {
                    Text(category, color = Palette.Accent, fontWeight = FontWeight.SemiBold, fontSize = 14.sp, modifier = Modifier.padding(top = 12.dp, bottom = 2.dp))
                }
                items(entries, key = { "list:" + it.id }) { e ->
                    ListRow(e, picks[e.id], state.tunnels, e.id in downloading, files[e.id] ?: 0, problems[e.id], vm)
                }
            }
        }
        item { Spacer(Modifier.height(24.dp)) }
    }
}

@Composable
private fun ListRow(
    e: CatalogEntry, pick: Pick?, tunnels: List<Tunnel>, busy: Boolean, fetchedAt: Long, problem: String?, vm: MainViewModel,
) {
    Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
        if (busy) {
            CircularProgressIndicator(Modifier.padding(13.dp).size(18.dp), strokeWidth = 2.dp)
        } else {
            Checkbox(checked = pick != null, onCheckedChange = { vm.setList(e, it) })
        }
        Column(Modifier.weight(1f)) {
            Text(e.title, fontSize = 15.sp)
            Text(e.about, color = Palette.Dim, fontSize = 12.sp)
            if (pick != null && !busy) {
                when {
                    problem != null -> Text(problem, color = Palette.Danger, fontSize = 12.sp, maxLines = 2)
                    fetchedAt == 0L -> Text("не скачан", color = Palette.Danger, fontSize = 12.sp)
                    else -> Text("скачан", color = Palette.Accent, fontSize = 12.sp)
                }
            }
        }
        if (pick != null) TunnelPicker(pick.tunnel, tunnels, { vm.setListTunnel(pick.id, it) })
    }
}

// ---------------------------------------------------------------------- sites by hand

@Composable
private fun Sites(vm: MainViewModel) {
    val state by vm.state.collectAsState()
    var input by remember { mutableStateOf("") }
    var tunnel by remember { mutableStateOf("") }

    LazyColumn(verticalArrangement = Arrangement.spacedBy(10.dp)) {
        item {
            Panel(Modifier.fillMaxWidth()) {
                Text("Свои сайты", fontWeight = FontWeight.SemiBold, fontSize = 16.sp)
                Text(
                    "Адрес покрывает и поддомены: youtube.com включает www., m. и всё под ним. Эти сайты важнее готовых списков.",
                    color = Palette.Dim, fontSize = 13.sp, modifier = Modifier.padding(top = 4.dp, bottom = 10.dp),
                )
                OutlinedTextField(
                    value = input, onValueChange = { input = it }, singleLine = true,
                    label = { Text("youtube.com") }, modifier = Modifier.fillMaxWidth(),
                )
                Spacer(Modifier.height(8.dp))
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Text("Через", color = Palette.Dim, fontSize = 13.sp)
                    Spacer(Modifier.padding(4.dp))
                    TunnelPicker(tunnel, state.tunnels, { tunnel = it })
                    Spacer(Modifier.weight(1f))
                    Button(onClick = { if (vm.addSites(input, tunnel) > 0) input = "" }) { Text("Добавить") }
                }
                if (state.rules.sites.isEmpty()) {
                    Text("Список пуст.", color = Palette.Dim, fontSize = 13.sp, modifier = Modifier.padding(top = 10.dp))
                }
            }
        }
        items(state.rules.sites, key = { "site:" + it.domain }) { s ->
            Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                Text(s.domain, modifier = Modifier.weight(1f), fontSize = 15.sp)
                TunnelPicker(s.tunnel, state.tunnels, { vm.setSiteTunnel(s.domain, it) })
                IconButton(onClick = { vm.removeSite(s.domain) }) { Icon(Icons.Default.Close, "Убрать", tint = Palette.Dim) }
            }
        }
        item {
            Panel(Modifier.fillMaxWidth()) {
                Text("Всё остальное", fontWeight = FontWeight.SemiBold, fontSize = 16.sp)
                Text(
                    if (state.settings.rest.isEmpty()) "Что не названо в списках и сайтах, идёт напрямую: скорость не страдает."
                    else "Что не названо в списках и сайтах, идёт через ${ConfigBuilder.titleOf(state.settings.rest, state.tunnels)}. Адреса в локальной сети всегда идут напрямую.",
                    color = Palette.Dim, fontSize = 13.sp, modifier = Modifier.padding(top = 4.dp, bottom = 10.dp),
                )
                TunnelPicker(state.settings.rest, state.tunnels, vm::setRest, allowDirect = true)
            }
        }
        item { Spacer(Modifier.height(24.dp)) }
    }
}

// ---------------------------------------------------------------------- apps

@Composable
private fun Apps(vm: MainViewModel) {
    val state by vm.state.collectAsState()
    val apps by vm.apps.collectAsState()
    var query by remember { mutableStateOf("") }

    val chosen = state.settings.apps.toSet()
    val shown = apps.filter { query.isBlank() || it.label.contains(query, true) || it.pkg.contains(query, true) }

    LazyColumn(verticalArrangement = Arrangement.spacedBy(10.dp)) {
        item {
            Panel(Modifier.fillMaxWidth()) {
                Text("Приложения", fontWeight = FontWeight.SemiBold, fontSize = 16.sp)
                Text(
                    if (chosen.isEmpty()) "Сейчас VPN используют все приложения. Отметь нужные, и остальные будут ходить мимо него."
                    else "VPN используют только отмеченные приложения (${chosen.size}). Остальные ходят мимо него, напрямую.",
                    color = Palette.Dim, fontSize = 13.sp, modifier = Modifier.padding(top = 4.dp, bottom = 8.dp),
                )
                if (chosen.isNotEmpty()) TextButton(onClick = vm::clearApps) { Text("Снять все отметки") }
                OutlinedTextField(
                    value = query, onValueChange = { query = it }, singleLine = true,
                    label = { Text("Найти приложение") }, modifier = Modifier.fillMaxWidth(),
                )
            }
        }
        items(shown, key = { it.pkg }) { app ->
            Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                Checkbox(checked = app.pkg in chosen, onCheckedChange = { vm.setApp(app.pkg, it) })
                Column(Modifier.weight(1f)) {
                    Text(app.label, fontSize = 15.sp)
                    Text(app.pkg, color = Palette.Dim, fontSize = 11.sp)
                }
            }
        }
        item { Spacer(Modifier.height(24.dp)) }
    }
}
