package io.github.vexorter42.tunor.ui

import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.Checkbox
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp

const val TERMS = "Tunor — бесплатная программа с открытым исходным кодом, которая управляет туннелем " +
    "sing-box. Устанавливая и используя её, ты соглашаешься со следующим.\n\n" +

    "1. БЕЗ ГАРАНТИЙ\n" +
    "Программа предоставляется «как есть», без каких-либо гарантий — явных или подразумеваемых, включая " +
    "гарантии работоспособности, пригодности для конкретных целей и отсутствия ошибок.\n\n" +

    "2. БЕЗ ОТВЕТСТВЕННОСТИ\n" +
    "Автор Tunor не несёт никакой ответственности за любой прямой или косвенный ущерб, связанный с " +
    "использованием программы: потерю или утечку данных, нарушение работы сети, блокировку аккаунтов в " +
    "сторонних сервисах, упущенную выгоду и любые иные последствия. Вся ответственность лежит на тебе.\n\n" +

    "3. ЗАКОННОСТЬ ИСПОЛЬЗОВАНИЯ\n" +
    "Ты сам отвечаешь за соблюдение законов своей страны и условий использования сервисов, к которым " +
    "подключаешься. Tunor — это лишь интерфейс; решение о том, как его применять, принимаешь ты.\n\n" +

    "4. TUNOR НЕ ЯВЛЯЕТСЯ VPN-СЕРВИСОМ\n" +
    "Программа не предоставляет серверов и не передаёт твой трафик через инфраструктуру автора. Туннели ты " +
    "настраиваешь сам своими конфигурациями от сторонних поставщиков (Cloudflare WARP, ProtonVPN и т. п.). " +
    "Их доступность, скорость и политика конфиденциальности — зона ответственности этих поставщиков.\n\n" +

    "5. КЛЮЧИ И КОНФИГИ\n" +
    "Приватные ключи туннелей хранятся только на этом телефоне и никуда не отправляются. Tunor не собирает " +
    "телеметрию.\n\n" +

    "6. СТОРОННИЕ КОМПОНЕНТЫ И ЛИЦЕНЗИЯ\n" +
    "В приложение входит движок sing-box (сборка sing-box-lx) под лицензией GNU GPL v3. Поэтому и это " +
    "Android-приложение распространяется под GNU GPL v3; исходный код открыт: github.com/Vexorter42/Tunor " +
    "(папка android).\n\n" +

    "7. ОТСУТСТВИЕ АФФИЛИАЦИИ\n" +
    "Tunor не аффилирован с проектами sing-box, AmneziaWG, Cloudflare, ProtonVPN и не одобрен ими. Их " +
    "названия используются только для описания совместимости."

/** The first thing a new install shows: the terms, and nothing else until they are accepted. */
@Composable
fun TermsScreen(onAccept: () -> Unit) {
    var ticked by remember { mutableStateOf(false) }
    Column(Modifier.fillMaxSize().safeDrawingPadding().padding(20.dp)) {
        Text("Условия использования", fontSize = 24.sp, fontWeight = FontWeight.Bold)
        Text("Прочитай и прими, чтобы продолжить", color = Palette.Dim, fontSize = 13.sp, modifier = Modifier.padding(top = 4.dp, bottom = 14.dp))
        Column(Modifier.weight(1f).verticalScroll(rememberScrollState())) {
            Text(TERMS, fontSize = 14.sp, lineHeight = 21.sp)
        }
        Spacer(Modifier.height(10.dp))
        Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
            Checkbox(checked = ticked, onCheckedChange = { ticked = it })
            Text("Я прочитал и принимаю условия", fontSize = 14.sp)
        }
        Button(onClick = onAccept, enabled = ticked, modifier = Modifier.fillMaxWidth().height(50.dp)) { Text("Продолжить") }
    }
}
