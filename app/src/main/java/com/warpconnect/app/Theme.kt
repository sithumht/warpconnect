package com.warpconnect.app

import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color

private val WarpColors = darkColorScheme(
    primary = Color(0xFF38BDF8),
    onPrimary = Color(0xFF04121F),
    secondary = Color(0xFF22D3EE),
    onSecondary = Color(0xFF04121F),
    background = Color(0xFF05070F),
    onBackground = Color(0xFFE6EDF7),
    surface = Color(0xFF0B1220),
    onSurface = Color(0xFFE6EDF7),
    surfaceVariant = Color(0xFF13203A),
    onSurfaceVariant = Color(0xFFA9B8CF),
    error = Color(0xFFF87171),
    onError = Color(0xFF1B0505),
)

@Composable
fun WarpTheme(content: @Composable () -> Unit) {
    MaterialTheme(colorScheme = WarpColors, content = content)
}
