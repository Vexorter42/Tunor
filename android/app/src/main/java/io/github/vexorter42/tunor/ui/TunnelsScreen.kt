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
import io.github.vexorter42.tunor.core.Tunnel

@Composable
fun TunnelsScreen(vm: MainViewModel, padding: PaddingValues) {
    val state by vm.state.collectAsState()
    val busy by vm.busy.collectAsState()
    val ctx = LocalContext.current
    var linkDialog by remember { mutableStateOf(false) }
    var pasteDialog by remember { mutableStateOf(false) }

    val picker = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocument()) { uri ->
        if (uri != null) readConf(ctx, uri, vm)
    }

    Column(
        Modifier.padding(padding).verticalScroll(rememberScrollState()).padding(horizontal = 20.dp, vertical = 16.dp),
        verticalArrangement = Arrangement.spacedBy(14.dp),
    ) {
        Text("Туннели", fontSize = 26.sp, fontWeight = FontWeight.Bold)
        if (busy) LinearProgressIndicator(Modifier.fillMaxWidth())

        Panel(Modifier.fillMaxWidth()) {
            Text("Добавить", fontWeight = FontWeight.SemiBold, fontSize = 16.sp)
            Text(
                "Конфиг WARP или AmneziaWG берётся у бота @warp_generator_bot в Telegram. Подписка VPN — ссылкой.",
                color = Palette.Dim, fontSize = 13.sp, modifier = Modifier.padding(top = 4.dp, bottom = 12.dp),
            )
            Button(onClick = { picker.launch(arrayOf("*/*")) }, modifier = Modifier.fillMaxWidth()) { Text("Выбрать файл .conf") }
            Spacer(Modifier.height(8.dp))
            OutlinedButton(onClick = { pasteDialog = true }, modifier = Modifier.fillMaxWidth()) { Text("Вставить текст конфига") }
            Spacer(Modifier.height(8.dp))
            OutlinedButton(onClick = { linkDialog = true }, modifier = Modifier.fillMaxWidth()) { Text("Ссылка vless:// или подписка") }
        }

        if (state.tunnels.isEmpty()) {
            Text("Пока ни одного туннеля.", color = Palette.Dim, fontSize = 14.sp)
        }
        state.tunnels.forEach { t ->
            val active = t.id == (state.settings.active.ifEmpty { state.tunnels.first().id })
            Panel(Modifier.fillMaxWidth()) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    RadioButton(selected = active, onClick = { vm.use(t.id) })
                    Column(Modifier.weight(1f)) {
                        Text(t.title, fontWeight = FontWeight.SemiBold, fontSize = 16.sp)
                        Text(
                            when (t.kind) {
                                Tunnel.WG -> "WireGuard / AmneziaWG"
                                Tunnel.LINK -> "vless"
                                else -> "подписка · серверов ${t.nodes.size}"
                            },
                            color = Palette.Dim, fontSize = 13.sp,
                        )
                    }
                    if (t.kind == Tunnel.SUB) IconButton(onClick = { vm.refreshSub(t.id) }) { Icon(Icons.Default.Refresh, "Обновить") }
                    IconButton(onClick = { vm.delete(t.id) }) { Icon(Icons.Default.Delete, "Удалить", tint = Palette.Danger) }
                }
                if (t.kind == Tunnel.SUB && active) {
                    Spacer(Modifier.height(6.dp))
                    t.nodes.forEachIndexed { i, node ->
                        Row(
                            Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically,
                        ) {
                            RadioButton(selected = i == t.chosen, onClick = { vm.chooseNode(t.id, i) })
                            Text(node.name, fontSize = 14.sp, modifier = Modifier.weight(1f))
                        }
                    }
                }
            }
        }
    }

    if (pasteDialog) {
        var text by remember { mutableStateOf("") }
        AlertDialog(
            onDismissRequest = { pasteDialog = false },
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
            confirmButton = { Button(onClick = { if (vm.addConf(text, "WARP")) pasteDialog = false }) { Text("Добавить") } },
            dismissButton = { TextButton(onClick = { pasteDialog = false }) { Text("Отмена") } },
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

private fun clipboard(ctx: Context): String {
    val cm = ctx.getSystemService(ClipboardManager::class.java)
    return cm.primaryClip?.takeIf { it.itemCount > 0 }?.getItemAt(0)?.coerceToText(ctx)?.toString().orEmpty()
}

private fun readConf(ctx: Context, uri: Uri, vm: MainViewModel) {
    val name = ctx.contentResolver.query(uri, null, null, null, null)?.use { c ->
        val i = c.getColumnIndex(OpenableColumns.DISPLAY_NAME)
        if (c.moveToFirst() && i >= 0) c.getString(i) else null
    } ?: "WARP"
    val text = try {
        ctx.contentResolver.openInputStream(uri)?.bufferedReader()?.use { it.readText() }.orEmpty()
    } catch (e: Exception) { "" }
    // A file this large is not a .conf; reading it whole would only fill the phone's memory.
    if (text.length > 100_000 || text.isBlank()) { vm.addConf("", name); return }
    vm.addConf(text, name)
}
