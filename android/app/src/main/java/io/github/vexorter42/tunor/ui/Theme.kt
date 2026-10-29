package io.github.vexorter42.tunor.ui

import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Shapes
import androidx.compose.material3.Surface
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.unit.dp

/** The palette of the desktop app, so that the two look like one program. */
object Palette {
    val Bg = Color(0xFF1A1E25)
    val Panel = Color(0xFF242932)
    val Input = Color(0xFF1F242B)
    val Hover = Color(0xFF2E343E)
    val Border = Color(0xFF3A404A)
    val Text = Color(0xFFF4F6F7)
    val Dim = Color(0xFF959DA9)
    val Accent = Color(0xFF3DDC5C)
    val OnAccent = Color(0xFF06120A)
    val AccentSoft = Color(0x383DDC5C)
    val Danger = Color(0xFFFF6B6B)
    val Blue = Color(0xFF59A5FF)
    val Warn = Color(0xFFFFB454)
}

private val Scheme = darkColorScheme(
    primary = Palette.Accent,
    onPrimary = Palette.OnAccent,
    primaryContainer = Palette.AccentSoft,
    onPrimaryContainer = Palette.Text,
    secondary = Palette.Blue,
    background = Palette.Bg,
    onBackground = Palette.Text,
    surface = Palette.Bg,
    onSurface = Palette.Text,
    surfaceVariant = Palette.Input,
    onSurfaceVariant = Palette.Dim,
    surfaceContainer = Palette.Panel,
    surfaceContainerHigh = Palette.Hover,
    surfaceContainerHighest = Palette.Hover,
    surfaceContainerLow = Palette.Input,
    outline = Palette.Border,
    outlineVariant = Palette.Border,
    error = Palette.Danger,
)

@Composable
fun TunorTheme(content: @Composable () -> Unit) {
    MaterialTheme(
        colorScheme = Scheme,
        shapes = Shapes(
            small = RoundedCornerShape(10.dp),
            medium = RoundedCornerShape(14.dp),
            large = RoundedCornerShape(18.dp),
        ),
    ) {
        // Without a surface under it, text takes the default colour, which is black.
        Surface(Modifier.fillMaxSize(), color = Palette.Bg, contentColor = Palette.Text, content = content)
    }
}

/** A card the way the desktop app has them: a panel with a hairline border. */
@Composable
fun Panel(modifier: Modifier = Modifier, content: @Composable () -> Unit) {
    Card(
        modifier = modifier,
        shape = RoundedCornerShape(16.dp),
        colors = CardDefaults.cardColors(containerColor = Palette.Panel),
        border = androidx.compose.foundation.BorderStroke(1.dp, Palette.Border),
    ) {
        Column(Modifier.padding(18.dp)) { content() }
    }
}
