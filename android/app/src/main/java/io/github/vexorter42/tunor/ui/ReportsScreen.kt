package io.github.vexorter42.tunor.ui

import android.content.ClipData
import android.content.Context
import android.content.Intent
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
import androidx.compose.material3.Button
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.core.content.FileProvider
import io.github.vexorter42.tunor.core.Reports
import java.io.File
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

/** Hands a report to the system's "send" menu: a messenger, mail, a cloud folder, whatever the person has. */
fun shareReport(ctx: Context, file: File) {
    val uri = FileProvider.getUriForFile(ctx, ctx.packageName + ".reports", file)
    val send = Intent(Intent.ACTION_SEND)
        .setType("text/plain")
        .putExtra(Intent.EXTRA_STREAM, uri)
        .putExtra(Intent.EXTRA_SUBJECT, "Отчёт Tunor")
        .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
    send.clipData = ClipData.newRawUri("Отчёт Tunor", uri)
    ctx.startActivity(Intent.createChooser(send, "Отправить отчёт").addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
}

/** The reports on the phone: the ones the app made when something broke, and the ones the person asked for. */
@Composable
fun ReportsView(vm: MainViewModel, padding: PaddingValues, onBack: () -> Unit) {
    val ctx = LocalContext.current
    val items by vm.reports.collectAsState()
    var open by remember { mutableStateOf<Reports.Item?>(null) }
    LaunchedEffect(Unit) {
        vm.loadReports()
        vm.reportSeen()
    }
    val day = remember { SimpleDateFormat("d MMM, HH:mm", Locale("ru")) }

    val item = open
    if (item != null) {
        val text = remember(item) { runCatching { item.file.readText() }.getOrDefault("Не удалось прочитать файл.") }
        Column(Modifier.padding(padding).padding(20.dp)) {
            Text(item.title, fontSize = 20.sp, fontWeight = FontWeight.Bold)
            Text(
                day.format(Date(item.time)) + ". В отчёте нет ключей, адресов подписок и твоих сайтов; адреса серверов и открытых сайтов в журналах скрыты. Если сомневаешься, прочитай перед отправкой.",
                color = Palette.Dim, fontSize = 12.sp, modifier = Modifier.padding(top = 4.dp, bottom = 10.dp),
            )
            Column(Modifier.weight(1f).verticalScroll(rememberScrollState())) {
                Text(text, fontSize = 11.sp, lineHeight = 15.sp, fontFamily = FontFamily.Monospace)
            }
            Spacer(Modifier.height(10.dp))
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                OutlinedButton(onClick = { open = null }, modifier = Modifier.weight(1f)) { Text("Назад") }
                Button(onClick = { shareReport(ctx, item.file) }, modifier = Modifier.weight(1f)) { Text("Поделиться") }
            }
        }
        return
    }

    Column(
        Modifier.padding(padding).verticalScroll(rememberScrollState()).padding(horizontal = 20.dp, vertical = 16.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        Text("Отчёты", fontSize = 26.sp, fontWeight = FontWeight.Bold)
        Text(
            "Когда что-то падает или туннель обрывается, Tunor сам записывает отчёт: что за программа, какие туннели, что делало приложение и что говорил движок. Сюда же попадают отчёты, которые ты собрал сам. Хранятся 30 дней, последние двенадцать.",
            color = Palette.Dim, fontSize = 13.sp,
        )
        Button(onClick = { vm.makeReport { shareReport(ctx, it) } }, modifier = Modifier.fillMaxWidth()) { Text("Собрать отчёт сейчас и поделиться") }
        if (items.isEmpty()) {
            Panel(Modifier.fillMaxWidth()) { Text("Отчётов нет. Это хорошо.", color = Palette.Dim) }
        }
        items.forEach { r ->
            Panel(Modifier.fillMaxWidth()) {
                Text(r.title, fontWeight = FontWeight.SemiBold)
                Text(day.format(Date(r.time)) + if (r.auto) " · записан самим приложением" else "", color = Palette.Dim, fontSize = 12.sp, modifier = Modifier.padding(top = 2.dp))
                Spacer(Modifier.height(8.dp))
                val tight = PaddingValues(horizontal = 8.dp, vertical = 8.dp)
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    OutlinedButton(onClick = { open = r }, modifier = Modifier.weight(1f), contentPadding = tight) { Text("Открыть", fontSize = 13.sp, maxLines = 1) }
                    OutlinedButton(onClick = { shareReport(ctx, r.file) }, modifier = Modifier.weight(1f), contentPadding = tight) { Text("Отправить", fontSize = 13.sp, maxLines = 1) }
                    OutlinedButton(onClick = { vm.deleteReport(r) }, modifier = Modifier.weight(1f), contentPadding = tight) { Text("Удалить", color = Palette.Danger, fontSize = 13.sp, maxLines = 1) }
                }
            }
        }
        if (items.isNotEmpty()) OutlinedButton(onClick = vm::deleteReports, modifier = Modifier.fillMaxWidth()) { Text("Удалить все") }
        OutlinedButton(onClick = onBack, modifier = Modifier.fillMaxWidth()) { Text("Назад") }
    }
}
