package io.github.vexorter42.tunor.ui

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Close
import androidx.compose.material3.Button
import androidx.compose.material3.Checkbox
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp

@Composable
fun RulesScreen(vm: MainViewModel, padding: PaddingValues) {
    val state by vm.state.collectAsState()
    val apps by vm.apps.collectAsState()
    var input by remember { mutableStateOf("") }
    var query by remember { mutableStateOf("") }
    LaunchedEffect(Unit) { vm.loadApps() }

    val chosen = state.settings.apps.toSet()
    val shown = apps.filter { query.isBlank() || it.label.contains(query, true) || it.pkg.contains(query, true) }

    LazyColumn(
        Modifier.padding(padding).padding(horizontal = 20.dp),
        verticalArrangement = Arrangement.spacedBy(10.dp),
    ) {
        item {
            Spacer(Modifier.height(16.dp))
            Text("Правила", fontSize = 26.sp, fontWeight = FontWeight.Bold)
        }
        item {
            Panel(Modifier.fillMaxWidth()) {
                Text("Сайты через туннель", fontWeight = FontWeight.SemiBold, fontSize = 16.sp)
                Text(
                    "Адрес покрывает и поддомены: youtube.com включает www., m. и всё под ним. Остальное идёт напрямую.",
                    color = Palette.Dim, fontSize = 13.sp, modifier = Modifier.padding(top = 4.dp, bottom = 10.dp),
                )
                Row(verticalAlignment = Alignment.CenterVertically) {
                    OutlinedTextField(
                        value = input, onValueChange = { input = it }, singleLine = true,
                        label = { Text("youtube.com") }, modifier = Modifier.weight(1f),
                    )
                    Spacer(Modifier.padding(4.dp))
                    Button(onClick = { if (vm.addDomains(input) > 0) input = "" }) { Text("Добавить") }
                }
                Spacer(Modifier.height(8.dp))
                if (state.rules.domains.isEmpty()) {
                    Text("Список пуст.", color = Palette.Dim, fontSize = 13.sp)
                }
                state.rules.domains.forEach { d ->
                    Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                        Text(d, modifier = Modifier.weight(1f), fontSize = 15.sp)
                        IconButton(onClick = { vm.removeDomain(d) }) { Icon(Icons.Default.Close, "Убрать", tint = Palette.Dim) }
                    }
                }
            }
        }
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
