package io.github.vexorter42.tunor.ui

import android.content.ClipboardManager
import android.content.Context
import android.net.Uri
import android.provider.OpenableColumns
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Delete
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.RadioButton
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import io.github.vexorter42.tunor.core.Bypass
import io.github.vexorter42.tunor.core.ConfigBuilder
import io.github.vexorter42.tunor.core.Tunnel

/** Where a config being added is going: [Tunnel.WARP], [Tunnel.GEO], or empty for one more of the person's own. */
private typealias Slot = String

@Composable
fun TunnelsScreen(vm: MainViewModel, padding: PaddingValues) {
    val state by vm.state.collectAsState()
    val busy by vm.busy.collectAsState()
    val ctx = LocalContext.current
    var linkDialog by remember { mutableStateOf(false) }
    var pasteFor by remember { mutableStateOf<Slot?>(null) }
    var pickFor by remember { mutableStateOf<Slot>("") }

    val picker = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocument()) { uri ->
        if (uri != null) readConf(ctx, uri, vm, pickFor)
    }
    fun chooseFile(slot: Slot) { pickFor = slot; picker.launch(arrayOf("*/*")) }

    val own = state.tunnels.filter { it.id != Tunnel.WARP && it.id != Tunnel.GEO }

    Column(
        Modifier.padding(padding).verticalScroll(rememberScrollState()).padding(horizontal = 20.dp, vertical = 16.dp),
        verticalArrangement = Arrangement.spacedBy(14.dp),
    ) {
        Text("Туннели", fontSize = 26.sp, fontWeight = FontWeight.Bold)
        if (busy) LinearProgressIndicator(Modifier.fillMaxWidth())

        SlotPanel(
            Tunnel.WARP, "WARP",
            "Основной выход. Конфиг берётся у бота @warp_generator_bot в Telegram.",
            state.tunnels, vm, onFile = { chooseFile(Tunnel.WARP) }, onPaste = { pasteFor = Tunnel.WARP },
        )
        SlotPanel(
            Tunnel.GEO, "geo",
            "Второй выход, в другой стране: для сайтов, которые не пускают российские адреса. Идёт внутри WARP, поэтому сначала нужен он.",
            state.tunnels, vm, onFile = { chooseFile(Tunnel.GEO) }, onPaste = { pasteFor = Tunnel.GEO },
        )

        Panel(Modifier.fillMaxWidth()) {
            Text("Свой VPN", fontWeight = FontWeight.SemiBold, fontSize = 16.sp)
            Text(
                "Подписка или ссылка vless://, а также ещё один конфиг .conf. Адрес подписки — как пароль.",
                color = Palette.Dim, fontSize = 13.sp, modifier = Modifier.padding(top = 4.dp, bottom = 12.dp),
            )
            Button(onClick = { linkDialog = true }, modifier = Modifier.fillMaxWidth()) { Text("Ссылка vless:// или подписка") }
            Spacer(Modifier.height(8.dp))
            OutlinedButton(onClick = { chooseFile("") }, modifier = Modifier.fillMaxWidth()) { Text("Файл .conf") }
            Spacer(Modifier.height(8.dp))
            OutlinedButton(onClick = { pasteFor = "" }, modifier = Modifier.fillMaxWidth()) { Text("Текст конфига") }
        }

        own.forEach { t -> OwnTunnel(t, state.tunnels, vm) }
    }

    pasteFor?.let { slot ->
        var text by remember { mutableStateOf("") }
        AlertDialog(
            onDismissRequest = { pasteFor = null },
            title = { Text("Текст конфига") },
            text = {
                Column {
                    OutlinedTextField(
                        value = text, onValueChange = { text = it }, minLines = 6, maxLines = 10,
                        label = { Text("Содержимое .conf") }, modifier = Modifier.fillMaxWidth(),
                    )
                    TextButton(onClick = { text = clipboard(ctx) }) { Text("Взять из буфера обмена") }
                }
            },
            confirmButton = { Button(onClick = { if (vm.addConf(text, slot.ifEmpty { "VPN" }, slot)) pasteFor = null }) { Text("Добавить") } },
            dismissButton = { TextButton(onClick = { pasteFor = null }) { Text("Отмена") } },
        )
    }

    if (linkDialog) {
        var text by remember { mutableStateOf("") }
        AlertDialog(
            onDismissRequest = { linkDialog = false },
            title = { Text("Ссылка или подписка") },
            text = {
                Column {
                    Text(
                        "Адрес подписки — как пароль: по нему кто угодно сможет пользоваться твоей подпиской. Не показывай его никому.",
                        color = Palette.Dim, fontSize = 13.sp, modifier = Modifier.padding(bottom = 10.dp),
                    )
                    OutlinedTextField(value = text, onValueChange = { text = it }, label = { Text("vless://… или https://…") }, modifier = Modifier.fillMaxWidth())
                    TextButton(onClick = { text = clipboard(ctx) }) { Text("Взять из буфера обмена") }
                }
            },
            confirmButton = { Button(onClick = { vm.addLinkOrSub(text); linkDialog = false }) { Text("Добавить") } },
            dismissButton = { TextButton(onClick = { linkDialog = false }) { Text("Отмена") } },
        )
    }
}

/** WARP and geo: two places that are always on the page, filled or not, because the catalog's lists name them. */
@Composable
private fun SlotPanel(
    slot: Slot, title: String, hint: String, tunnels: List<Tunnel>, vm: MainViewModel,
    onFile: () -> Unit, onPaste: () -> Unit,
) {
    val t = tunnels.firstOrNull { it.id == slot }
    Panel(Modifier.fillMaxWidth()) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Column(Modifier.weight(1f)) {
                Text(title, fontWeight = FontWeight.SemiBold, fontSize = 16.sp)
                Status(t, tunnels)
            }
            if (t != null) IconButton(onClick = { vm.delete(t.id) }) { Icon(Icons.Default.Delete, "Удалить", tint = Palette.Danger) }
        }
        Text(hint, color = Palette.Dim, fontSize = 13.sp, modifier = Modifier.padding(top = 6.dp, bottom = 10.dp))
        if (t != null && slot == Tunnel.GEO) {
            Via(t, tunnels, vm)
            Spacer(Modifier.height(10.dp))
        }
        Row {
            Button(onClick = onFile, modifier = Modifier.weight(1f)) { Text("Файл .conf") }
            Spacer(Modifier.padding(4.dp))
            OutlinedButton(onClick = onPaste, modifier = Modifier.weight(1f)) { Text("Вставить текст") }
        }
    }
}

/** One of the person's own tunnels: a config, a link or a subscription. */
@Composable
private fun OwnTunnel(t: Tunnel, tunnels: List<Tunnel>, vm: MainViewModel) {
    Panel(Modifier.fillMaxWidth()) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Column(Modifier.weight(1f)) {
                Text(t.title, fontWeight = FontWeight.SemiBold, fontSize = 16.sp)
                Status(t, tunnels)
            }
            if (t.kind == Tunnel.SUB) IconButton(onClick = { vm.refreshSub(t.id) }) { Icon(Icons.Default.Refresh, "Обновить") }
            IconButton(onClick = { vm.delete(t.id) }) { Icon(Icons.Default.Delete, "Удалить", tint = Palette.Danger) }
        }
        Spacer(Modifier.height(6.dp))
        Via(t, tunnels, vm)
        if (t.kind == Tunnel.SUB) Servers(t, vm)
    }
}

/** The servers of a subscription: which one is in use, how fast each answers, and the buttons to time them and take the fastest. */
@Composable
private fun Servers(t: Tunnel, vm: MainViewModel) {
    val pings by vm.pings.collectAsState()
    val pinging by vm.pinging.collectAsState()
    val answers = pings[t.id].orEmpty()
    val progress = pinging[t.id]

    Spacer(Modifier.height(10.dp))
    Row(verticalAlignment = Alignment.CenterVertically) {
        Column(Modifier.weight(1f).padding(end = 12.dp)) {
            Text("Выбирать сервер самому", fontSize = 14.sp)
            Text(
                if (t.auto) "Tunor меряет серверы и отдаёт движку ближайшие: он проверяет их настоящим запросом и обходит те, что не работают. Сейчас в работе: ${t.pool.size}."
                else "Используется сервер, который выбрал ты.",
                color = Palette.Dim, fontSize = 12.sp, modifier = Modifier.padding(top = 2.dp),
            )
        }
        androidx.compose.material3.Switch(checked = t.auto, onCheckedChange = { vm.setAuto(t.id, it) })
    }
    Spacer(Modifier.height(10.dp))
    Row(verticalAlignment = Alignment.CenterVertically) {
        Text(if (t.auto) "Серверы" else "Сервер", color = Palette.Dim, fontSize = 13.sp, modifier = Modifier.weight(1f))
        OutlinedButton(onClick = { vm.ping(t.id) }, enabled = progress == null) {
            if (progress != null) {
                androidx.compose.material3.CircularProgressIndicator(Modifier.size(14.dp), strokeWidth = 2.dp)
                Spacer(Modifier.width(8.dp))
                Text("${progress.first}/${progress.second}", fontSize = 13.sp)
            } else Text("Проверить пинг", fontSize = 13.sp)
        }
        if (answers.isNotEmpty() && !t.auto) {
            Spacer(Modifier.width(8.dp))
            Button(onClick = { vm.chooseFastest(t.id) }, enabled = answers.values.any { it.answered }) { Text("Быстрейший", fontSize = 13.sp) }
        }
    }
    if (t.autoFrom.isNotEmpty() && !t.auto) {
        Text(
            "Сервер выбран приложением: обычные не отвечали. Как только они ответят, оно вернётся на прежний.",
            color = Palette.Warn, fontSize = 12.sp, modifier = Modifier.padding(top = 6.dp),
        )
    }
    // The nearest first once they have been timed; the ones that did not answer last, in the provider's order.
    val order = t.nodes.indices.sortedWith(compareBy({ answers[it]?.ms ?: Int.MAX_VALUE }, { it }))
    order.forEach { i ->
        val node = t.nodes[i]
        val r = answers[i]
        Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
            RadioButton(selected = !t.auto && i == t.chosen, onClick = { vm.chooseNode(t.id, i) })
            Column(Modifier.weight(1f)) {
                Text(node.name, fontSize = 14.sp)
                val tags = listOfNotNull(
                    if (Bypass.isSpecial(node.name)) "для обхода" else null,
                    if (t.auto && node.url in t.pool) "в работе" else null,
                )
                if (tags.isNotEmpty()) Text(tags.joinToString(" · "), color = Palette.Dim, fontSize = 11.sp)
            }
            if (r != null) Text(r.text, color = if (r.answered) Palette.Accent else Palette.Dim, fontSize = 13.sp)
        }
    }
}

/** Whether the tunnel can carry traffic, and if not, what is wrong, in words. */
@Composable
private fun Status(t: Tunnel?, tunnels: List<Tunnel>) {
    if (t == null) {
        Text("не добавлен", color = Palette.Dim, fontSize = 13.sp)
        return
    }
    val problem = ConfigBuilder.problemOf(t, tunnels)
    if (problem == null) {
        Text("готов · " + kindName(t), color = Palette.Accent, fontSize = 13.sp)
    } else {
        Text(problem, color = Palette.Danger, fontSize = 13.sp)
    }
}

private fun kindName(t: Tunnel) = when (t.kind) {
    Tunnel.WG -> "WireGuard / AmneziaWG"
    Tunnel.LINK -> "vless"
    else -> "подписка, серверов ${t.nodes.size}"
}

/** "Goes through": the tunnel this one rides inside, if any. */
@Composable
private fun Via(t: Tunnel, tunnels: List<Tunnel>, vm: MainViewModel) {
    // Not itself, and not one that already rides inside this one: that would go round in a circle.
    val options = tunnels.filter { it.id != t.id && !ConfigBuilder.rides(it, t.id, tunnels) }
    var open by remember { mutableStateOf(false) }
    Row(verticalAlignment = Alignment.CenterVertically) {
        Text("Идёт через", color = Palette.Dim, fontSize = 13.sp, modifier = Modifier.weight(1f))
        androidx.compose.foundation.layout.Box {
            OutlinedButton(onClick = { open = true }, contentPadding = PaddingValues(horizontal = 12.dp, vertical = 2.dp)) {
                Text((if (t.via.isEmpty()) "ни через что" else ConfigBuilder.titleOf(t.via, tunnels)) + " ▾", fontSize = 13.sp, maxLines = 1)
            }
            androidx.compose.material3.DropdownMenu(expanded = open, onDismissRequest = { open = false }) {
                androidx.compose.material3.DropdownMenuItem(text = { Text("ни через что: сам по себе") }, onClick = { vm.setVia(t.id, ""); open = false })
                ConfigBuilder.ordered(options).forEach { o ->
                    androidx.compose.material3.DropdownMenuItem(text = { Text(o.title) }, onClick = { vm.setVia(t.id, o.id); open = false })
                }
            }
        }
    }
}

private fun clipboard(ctx: Context): String {
    val cm = ctx.getSystemService(ClipboardManager::class.java)
    return cm.primaryClip?.takeIf { it.itemCount > 0 }?.getItemAt(0)?.coerceToText(ctx)?.toString().orEmpty()
}

private fun readConf(ctx: Context, uri: Uri, vm: MainViewModel, slot: Slot) {
    val name = ctx.contentResolver.query(uri, null, null, null, null)?.use { c ->
        val i = c.getColumnIndex(OpenableColumns.DISPLAY_NAME)
        if (c.moveToFirst() && i >= 0) c.getString(i) else null
    } ?: "VPN"
    val text = try {
        ctx.contentResolver.openInputStream(uri)?.bufferedReader()?.use { it.readText() }.orEmpty()
    } catch (e: Exception) { "" }
    // A file this large is not a .conf; reading it whole would only fill the phone's memory.
    if (text.length > 100_000 || text.isBlank()) { vm.addConf("", name, slot); return }
    vm.addConf(text, name, slot)
}
