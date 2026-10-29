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
 * @param current the tunnel's id, or one of the [extra] values; empty with no such value — the first tunnel that works
 * @param extra choices that are not a tunnel, as value to words and shown before the tunnels: "directly" for the traffic
 *              that nothing names, "by the rules" for an app that has no way of its own
 * @param withTunnels whether the tunnels are offered at all
 */
@Composable
fun TunnelPicker(
    current: String,
    tunnels: List<Tunnel>,
    onPick: (String) -> Unit,
    modifier: Modifier = Modifier,
    extra: List<Pair<String, String>> = emptyList(),
    withTunnels: Boolean = true,
) {
    var open by remember { mutableStateOf(false) }
    val ids = if (!withTunnels) emptyList() else ConfigBuilder.ordered(tunnels).map { it.id }.let { have ->
        // What was picked stays in the list even if its tunnel has since gone, so that the button says what it will do.
        if (current.isNotEmpty() && extra.none { it.first == current } && current !in have) listOf(current) + have else have
    }
    val label = extra.firstOrNull { it.first == current }?.second ?: when {
        current.isEmpty() -> ConfigBuilder.live(tunnels).firstOrNull()?.title ?: "—"
        else -> ConfigBuilder.titleOf(current, tunnels)
    }
    Box(modifier) {
        OutlinedButton(onClick = { open = true }, contentPadding = PaddingValues(horizontal = 12.dp, vertical = 2.dp)) {
            Text("$label ▾", fontSize = 13.sp, maxLines = 1)
        }
        DropdownMenu(expanded = open, onDismissRequest = { open = false }) {
            extra.forEach { (value, words) -> DropdownMenuItem(text = { Text(words) }, onClick = { onPick(value); open = false }) }
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
