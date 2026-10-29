package io.github.vexorter42.tunor.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import io.github.vexorter42.tunor.bg.Phase
import io.github.vexorter42.tunor.bg.Vpn
import io.github.vexorter42.tunor.core.ConfigBuilder
import io.github.vexorter42.tunor.core.Updater

@Composable
fun HomeScreen(vm: MainViewModel, padding: PaddingValues, onToggle: () -> Unit) {
    val state by vm.state.collectAsState()
    val phase by Vpn.phase.collectAsState()
    val check by vm.check.collectAsState()
    val checking by vm.checking.collectAsState()

    val working = ConfigBuilder.live(state.tunnels)
    val running = phase == Phase.Started
    val busy = phase == Phase.Starting

    Column(
        Modifier.padding(padding).verticalScroll(rememberScrollState()).padding(horizontal = 20.dp, vertical = 16.dp),
        verticalArrangement = Arrangement.spacedBy(16.dp),
    ) {
        Text("Главная", fontSize = 26.sp, fontWeight = FontWeight.Bold)

        Panel(Modifier.fillMaxWidth()) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Box(
                    Modifier.size(12.dp).clip(CircleShape).background(
                        when {
                            running -> Palette.Accent
                            phase is Phase.Failed -> Palette.Danger
                            else -> Palette.Dim
                        },
                    ),
                )
                Spacer(Modifier.size(10.dp))
                Text(
                    when (phase) {
                        Phase.Started -> "Подключено"
                        Phase.Starting -> "Запускаю…"
                        Phase.Stopped -> "Остановлено"
                        is Phase.Failed -> "Не запустилось"
                    },
                    fontSize = 18.sp,
                    fontWeight = FontWeight.SemiBold,
                )
            }
            val failed = phase as? Phase.Failed
            if (failed != null) {
                Text(failed.why, color = Palette.Danger, fontSize = 13.sp, modifier = Modifier.padding(top = 8.dp))
            } else if (state.tunnels.isEmpty()) {
                Text("Добавь туннель во вкладке «Туннели».", color = Palette.Dim, fontSize = 13.sp, modifier = Modifier.padding(top = 8.dp))
            } else if (working.isEmpty()) {
                Text("Ни один туннель не готов: причина написана во вкладке «Туннели».", color = Palette.Danger, fontSize = 13.sp, modifier = Modifier.padding(top = 8.dp))
            } else {
                val left = state.tunnels.size - working.size
                Text(
                    "Туннели: " + working.joinToString(", ") { it.title } + if (left > 0) " (не готовы: $left)" else "",
                    color = Palette.Dim, fontSize = 13.sp, modifier = Modifier.padding(top = 8.dp),
                )
            }
            Spacer(Modifier.height(14.dp))
            Button(
                onClick = onToggle,
                enabled = !busy,
                modifier = Modifier.fillMaxWidth().height(52.dp),
                colors = if (running) ButtonDefaults.buttonColors(containerColor = Palette.Danger, contentColor = Palette.OnAccent) else ButtonDefaults.buttonColors(),
            ) {
                if (busy) CircularProgressIndicator(Modifier.size(20.dp), strokeWidth = 2.dp, color = Palette.OnAccent)
                else Text(if (running) "Остановить" else "Запустить", fontWeight = FontWeight.SemiBold, fontSize = 16.sp)
            }
        }

        val update by vm.update.collectAsState()
        (update as? Updater.State.Available)?.let { a ->
            Panel(Modifier.fillMaxWidth()) {
                Text("Доступно обновление ${a.info.version}", fontWeight = FontWeight.SemiBold, color = Palette.Accent)
                if (a.info.notes.isNotEmpty()) Text(a.info.notes, color = Palette.Dim, fontSize = 13.sp, modifier = Modifier.padding(top = 4.dp))
                Spacer(Modifier.height(10.dp))
                OutlinedButton(onClick = vm::installUpdate) { Text("Скачать и установить") }
            }
        }

        Panel(Modifier.fillMaxWidth()) {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Column(Modifier.weight(1f)) {
                    Text("Всё остальное", fontWeight = FontWeight.SemiBold)
                    Text(
                        if (state.settings.rest.isEmpty()) "Идёт напрямую: через туннели только то, что названо в правилах. Скорость не страдает."
                        else "Идёт через ${ConfigBuilder.titleOf(state.settings.rest, state.tunnels)}, кроме адресов в локальной сети.",
                        color = Palette.Dim, fontSize = 13.sp, modifier = Modifier.padding(top = 4.dp),
                    )
                }
                Spacer(Modifier.size(8.dp))
                TunnelPicker(state.settings.rest, state.tunnels, vm::setRest, extra = listOf("" to "Напрямую"))
            }
        }

        Panel(Modifier.fillMaxWidth()) {
            Text("Проверка соединения", fontWeight = FontWeight.SemiBold, fontSize = 16.sp)
            Text(
                "Спрашивает у Cloudflare, откуда виден запрос: через каждый туннель и напрямую. Если страна не твоя, туннель работает.",
                color = Palette.Dim, fontSize = 13.sp, modifier = Modifier.padding(top = 4.dp),
            )
            Spacer(Modifier.height(12.dp))
            OutlinedButton(onClick = vm::runCheck, enabled = running && !checking) {
                if (checking) CircularProgressIndicator(Modifier.size(16.dp), strokeWidth = 2.dp) else Text("Проверить")
            }
            check?.let { rows ->
                Spacer(Modifier.height(12.dp))
                rows.forEach { r ->
                    Row(Modifier.fillMaxWidth().padding(vertical = 6.dp), verticalAlignment = Alignment.CenterVertically) {
                        Text(r.road, fontWeight = FontWeight.SemiBold, color = if (r.direct) Palette.Blue else Palette.Accent, modifier = Modifier.weight(0.5f).padding(end = 8.dp))
                        if (r.trace != null) {
                            Column(Modifier.weight(0.5f)) {
                                Text(r.trace.place, fontSize = 14.sp)
                                Text(r.trace.ip, color = Palette.Dim, fontSize = 12.sp)
                            }
                        } else {
                            Text(r.problem ?: "нет ответа", color = Palette.Danger, fontSize = 13.sp, modifier = Modifier.weight(0.5f))
                        }
                    }
                }
            }
        }
    }
}
