package io.github.vexorter42.tunor.ui

import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import io.github.vexorter42.tunor.core.ConfigBuilder
import io.github.vexorter42.tunor.core.Tunnel

/**
 * A button that says which tunnel something goes through and lets the person pick another.
 *
 * @param current the tunnel's id; empty — [allowDirect] says "directly", otherwise the first that works
 * @param allowDirect offer "directly" as well: for the traffic that nothing names
 */
@Composable
fun TunnelPicker(
    current: String,
    tunnels: List<Tunnel>,
    onPick: (String) -> Unit,
    modifier: Modifier = Modifier,
    allowDirect: Boolean = false,
) {
    var open by remember { mutableStateOf(false) }
    val ids = ConfigBuilder.ordered(tunnels).map { it.id }.let { have ->
        // What was picked stays in the list even if its tunnel has since gone, so that the button says what it will do.
        if (current.isNotEmpty() && current !in have) listOf(current) + have else have
    }
    val label = when {
        current.isEmpty() && allowDirect -> "Напрямую"
        current.isEmpty() -> ConfigBuilder.live(tunnels).firstOrNull()?.title ?: "—"
        else -> ConfigBuilder.titleOf(current, tunnels)
    }
    Box(modifier) {
        OutlinedButton(onClick = { open = true }, contentPadding = PaddingValues(horizontal = 12.dp, vertical = 2.dp)) {
            Text("$label ▾", fontSize = 13.sp, maxLines = 1)
        }
        DropdownMenu(expanded = open, onDismissRequest = { open = false }) {
            if (allowDirect) DropdownMenuItem(text = { Text("Напрямую") }, onClick = { onPick(""); open = false })
            ids.forEach { id ->
                val missing = tunnels.none { it.id == id }
                DropdownMenuItem(
                    text = { Text(ConfigBuilder.titleOf(id, tunnels) + if (missing) " (не добавлен)" else "") },
                    onClick = { onPick(id); open = false },
                )
            }
        }
    }
}
