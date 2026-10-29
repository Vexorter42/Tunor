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
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
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
import io.nekohasekai.libbox.Libbox

@Composable
fun MoreScreen(padding: PaddingValues) {
    val ctx = LocalContext.current
    var terms by remember { mutableStateOf(false) }
    var log by remember { mutableStateOf<String?>(null) }
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
