package io.github.vexorter42.tunor.ui

import android.content.Intent
import android.net.Uri
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import io.github.vexorter42.tunor.BuildConfig
import io.github.vexorter42.tunor.bg.MobileRescue
import io.github.vexorter42.tunor.core.Pool
import io.github.vexorter42.tunor.core.Tunnel
import io.github.vexorter42.tunor.core.Updater
import io.nekohasekai.libbox.Libbox

@Composable
fun MoreScreen(vm: MainViewModel, padding: PaddingValues) {
    val ctx = LocalContext.current
    val state by vm.state.collectAsState()
    val update by vm.update.collectAsState()
    var terms by remember { mutableStateOf(false) }
    var log by remember { mutableStateOf<String?>(null) }
    var reports by remember { mutableStateOf(false) }
    fun open(url: String) = ctx.startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(url)).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))

    if (terms) {
        Column(Modifier.padding(padding).padding(20.dp).verticalScroll(rememberScrollState())) {
            Text("Условия использования", fontSize = 22.sp, fontWeight = FontWeight.Bold)
            Spacer(Modifier.height(10.dp))
            Text(TERMS, fontSize = 14.sp, lineHeight = 21.sp)
            Spacer(Modifier.height(14.dp))
            OutlinedButton(onClick = { terms = false }, modifier = Modifier.fillMaxWidth()) { Text("Назад") }
        }
        return
    }

    if (reports) {
        ReportsView(vm, padding, onBack = { reports = false })
        return
    }

    log?.let { text ->
        Column(Modifier.padding(padding).padding(20.dp)) {
            Text("Журнал движка", fontSize = 22.sp, fontWeight = FontWeight.Bold)
            Text("Последние строки. Если что-то не открывается, причина чаще всего написана здесь.", color = Palette.Dim, fontSize = 13.sp, modifier = Modifier.padding(top = 4.dp, bottom = 10.dp))
            Column(Modifier.weight(1f).verticalScroll(rememberScrollState(), reverseScrolling = true)) {
                Text(text.ifBlank { "Журнал пуст: движок ещё не запускался." }, fontSize = 11.sp, lineHeight = 15.sp, fontFamily = androidx.compose.ui.text.font.FontFamily.Monospace)
            }
            Spacer(Modifier.height(10.dp))
            Row {
                OutlinedButton(onClick = { log = null }, modifier = Modifier.weight(1f)) { Text("Назад") }
                Spacer(Modifier.padding(4.dp))
                OutlinedButton(
                    onClick = { ctx.getSystemService(android.content.ClipboardManager::class.java).setPrimaryClip(android.content.ClipData.newPlainText("Tunor", text)) },
                    modifier = Modifier.weight(1f),
                ) { Text("Скопировать") }
            }
        }
        return
    }

    Column(
        Modifier.padding(padding).verticalScroll(rememberScrollState()).padding(horizontal = 20.dp, vertical = 16.dp),
        verticalArrangement = androidx.compose.foundation.layout.Arrangement.spacedBy(14.dp),
    ) {
        Text("Ещё", fontSize = 26.sp, fontWeight = FontWeight.Bold)

        Panel(Modifier.fillMaxWidth()) {
            Text("Обновления", fontWeight = FontWeight.SemiBold, fontSize = 16.sp)
            Text("Сейчас Tunor ${BuildConfig.VERSION_NAME}", color = Palette.Dim, fontSize = 13.sp, modifier = Modifier.padding(top = 4.dp))
            Spacer(Modifier.height(10.dp))
            UpdateBody(update, vm)
            Spacer(Modifier.height(6.dp))
            SwitchRow("Проверять самому", "Раз в полдня, пока приложение открывают.", state.settings.autoUpdateCheck, vm::setAutoUpdateCheck)
        }

        Panel(Modifier.fillMaxWidth()) {
            Text("Автоматика", fontWeight = FontWeight.SemiBold, fontSize = 16.sp)
            SwitchRow(
                "Искать сервер для обхода глушилок",
                "Если ни один обычный сервер подписки не отвечает, взять из неё сервер со словами «обход», «глушилка», «LTE» " +
                    "или «оператор» с самым быстрым откликом, а когда обычные вернутся, вернуться на прежний.",
                state.settings.autoBypass, vm::setAutoBypass,
            )
            SwitchRow(
                "Мобильная сеть: запасной путь",
                "Если в мобильной сети ни WARP, ни geo не отвечают десять секунд, пустить всё через серверы подписки со словами «обход», " +
                    "«глушилка», «LTE» или «оператор». Как только обычные пути ответят снова, или на Wi-Fi, всё вернётся как было. " +
                    "Приложения, которым ты поставил «напрямую», остаются напрямую.",
                state.settings.mobileRescue, vm::setMobileRescue,
            )
            if (state.settings.mobileRescue) {
                val subs = state.tunnels.filter { it.kind == Tunnel.SUB && Pool.split(it).first.isNotEmpty() }
                Row(Modifier.padding(top = 8.dp), verticalAlignment = androidx.compose.ui.Alignment.CenterVertically) {
                    Text("Подписка", fontSize = 14.sp, modifier = Modifier.weight(1f))
                    if (subs.isEmpty()) {
                        Text("нет подписки с такими серверами", color = Palette.Warn, fontSize = 13.sp)
                    } else {
                        TunnelPicker(
                            state.settings.mobileRescueSub, subs, vm::setMobileRescueSub,
                            extra = listOf("" to "Первая подходящая"),
                        )
                    }
                }
            }
            Text(
                "Включать и выключать туннель можно и из шторки быстрых настроек: добавь в неё плитку «Tunor».",
                color = Palette.Dim, fontSize = 13.sp, modifier = Modifier.padding(top = 10.dp),
            )
        }

        Panel(Modifier.fillMaxWidth()) {
            Text("Если что-то не работает", fontWeight = FontWeight.SemiBold, fontSize = 16.sp)
            Text(
                "Когда приложение падает или туннель обрывается, Tunor записывает отчёт сам. Его можно прочитать и отправить: в нём нет ключей, адресов подписок и твоих сайтов.",
                color = Palette.Dim, fontSize = 13.sp, modifier = Modifier.padding(top = 4.dp),
            )
            Spacer(Modifier.height(12.dp))
            androidx.compose.material3.Button(onClick = { vm.makeReport { shareReport(ctx, it) } }, modifier = Modifier.fillMaxWidth()) { Text("Собрать отчёт и поделиться") }
            Spacer(Modifier.height(8.dp))
            OutlinedButton(onClick = { reports = true }, modifier = Modifier.fillMaxWidth()) { Text("Все отчёты") }
        }

        Panel(Modifier.fillMaxWidth()) {
            Text("О программе", fontWeight = FontWeight.SemiBold, fontSize = 16.sp)
            Text("Tunor ${BuildConfig.VERSION_NAME}", modifier = Modifier.padding(top = 8.dp))
            Text("Движок: sing-box-lx ${Libbox.version()}", color = Palette.Dim, fontSize = 13.sp)
            Text(
                "Бесплатная программа с открытым кодом под GNU GPL v3. Своих серверов у неё нет: туннель — это конфиг или подписка, которые добавляешь ты.",
                color = Palette.Dim, fontSize = 13.sp, modifier = Modifier.padding(top = 8.dp),
            )
            Spacer(Modifier.height(12.dp))
            OutlinedButton(onClick = { open("https://github.com/Vexorter42/Tunor") }, modifier = Modifier.fillMaxWidth()) { Text("Исходный код и релизы") }
            Spacer(Modifier.height(8.dp))
            OutlinedButton(onClick = { log = readLog(ctx) }, modifier = Modifier.fillMaxWidth()) { Text("Журнал движка") }
            Spacer(Modifier.height(8.dp))
            OutlinedButton(onClick = { terms = true }, modifier = Modifier.fillMaxWidth()) { Text("Условия использования") }
            Spacer(Modifier.height(8.dp))
            OutlinedButton(onClick = { open("https://t.me/warp_generator_bot") }, modifier = Modifier.fillMaxWidth()) { Text("Бот для конфига WARP") }
        }
    }
}

/** The last of the engine's log: enough to see why something did not open, not enough to fill the phone's memory. */
private fun readLog(ctx: android.content.Context): String {
    val f = java.io.File(ctx.filesDir, "engine/box.log")
    if (!f.exists()) return ""
    java.io.RandomAccessFile(f, "r").use { r ->
        val take = minOf(r.length(), 48_000L)
        r.seek(r.length() - take)
        val bytes = ByteArray(take.toInt())
        r.readFully(bytes)
        // Cut at a line start, and keep the engine's own colour codes out of the text.
        val text = String(bytes, Charsets.UTF_8).substringAfter('\n', "").replace(Regex("\u001B\\[[0-9;]*m"), "")
        return text.lines().filterNot { "failed to search process" in it }.takeLast(400).joinToString("\n")
    }
}

@Composable
private fun SwitchRow(title: String, about: String, on: Boolean, onChange: (Boolean) -> Unit) {
    Row(Modifier.padding(top = 8.dp), verticalAlignment = androidx.compose.ui.Alignment.CenterVertically) {
        Column(Modifier.weight(1f).padding(end = 12.dp)) {
            Text(title, fontSize = 15.sp)
            Text(about, color = Palette.Dim, fontSize = 12.sp, modifier = Modifier.padding(top = 2.dp))
        }
        androidx.compose.material3.Switch(checked = on, onCheckedChange = onChange)
    }
}

/** What the update is doing, and the one button that moves it on. */
@Composable
private fun UpdateBody(update: Updater.State, vm: MainViewModel) {
    when (update) {
        Updater.State.Idle, Updater.State.UpToDate -> {
            if (update == Updater.State.UpToDate) Text("Установлена последняя версия.", color = Palette.Accent, fontSize = 13.sp, modifier = Modifier.padding(bottom = 8.dp))
            OutlinedButton(onClick = vm::checkUpdate, modifier = Modifier.fillMaxWidth()) { Text("Проверить обновления") }
        }
        Updater.State.Checking -> Row(verticalAlignment = androidx.compose.ui.Alignment.CenterVertically) {
            androidx.compose.material3.CircularProgressIndicator(Modifier.size(18.dp), strokeWidth = 2.dp)
            Text("  Спрашиваю у GitHub…", color = Palette.Dim, fontSize = 13.sp)
        }
        is Updater.State.Available -> {
            Text("Доступна версия ${update.info.version}", fontWeight = FontWeight.SemiBold, color = Palette.Accent)
            if (update.info.notes.isNotEmpty()) Text(update.info.notes, color = Palette.Dim, fontSize = 13.sp, modifier = Modifier.padding(top = 4.dp, bottom = 8.dp))
            androidx.compose.material3.Button(onClick = vm::installUpdate, modifier = Modifier.fillMaxWidth()) { Text("Скачать и установить") }
        }
        is Updater.State.Downloading -> {
            val part = if (update.total > 0) update.done.toFloat() / update.total else 0f
            Text(
                "Скачиваю ${update.info.version}: ${update.done / 1_048_576} из ${if (update.total > 0) (update.total / 1_048_576).toString() else "?"} МБ",
                fontSize = 13.sp,
            )
            Spacer(Modifier.height(6.dp))
            if (update.total > 0) androidx.compose.material3.LinearProgressIndicator(progress = { part }, modifier = Modifier.fillMaxWidth())
            else androidx.compose.material3.LinearProgressIndicator(Modifier.fillMaxWidth())
        }
        is Updater.State.Installing -> Text(
            "Версия ${update.info.version} передана системе: подтверди установку в открывшемся окне. После неё Tunor закроется, открой его снова.",
            fontSize = 13.sp,
        )
        is Updater.State.Failed -> {
            Text(update.why, color = Palette.Danger, fontSize = 13.sp, modifier = Modifier.padding(bottom = 8.dp))
            Row(horizontalArrangement = androidx.compose.foundation.layout.Arrangement.spacedBy(8.dp)) {
                // With a file in question: try it again, or ask afresh what the newest is; without one, only the second.
                if (update.info != null) OutlinedButton(onClick = vm::installUpdate, modifier = Modifier.weight(1f)) { Text("Повторить") }
                OutlinedButton(onClick = vm::checkUpdate, modifier = Modifier.weight(1f)) { Text("Проверить заново") }
            }
        }
    }
}
