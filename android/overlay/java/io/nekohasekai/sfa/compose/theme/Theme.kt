package io.nekohasekai.sfa.compose.theme

import android.app.Activity
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalView
import androidx.core.view.WindowCompat

val HydraBg = Color(0xff090c14)
val HydraPanel = Color(0xff121725)
val HydraRaised = Color(0xff191f31)
val HydraBorder = Color(0xff283048)
val HydraText = Color(0xffe6ecff)
val HydraMuted = Color(0xff9ba8c6)
val HydraCyan = Color(0xff22d3ee)
val HydraViolet = Color(0xffa855f7)

private val scheme = darkColorScheme(
    primary = HydraCyan, onPrimary = HydraBg, primaryContainer = Color(0xff123345), onPrimaryContainer = HydraCyan,
    secondary = HydraViolet, onSecondary = HydraBg, secondaryContainer = Color(0xff153344), onSecondaryContainer = HydraCyan,
    tertiary = Color(0xff34d399), background = HydraBg, onBackground = HydraText,
    surface = HydraBg, onSurface = HydraText, surfaceVariant = HydraRaised, onSurfaceVariant = HydraMuted,
    surfaceContainerLowest = HydraBg, surfaceContainerLow = Color(0xff0d111c), surfaceContainer = HydraPanel,
    surfaceContainerHigh = HydraRaised, surfaceContainerHighest = Color(0xff20283d),
    outline = HydraMuted, outlineVariant = HydraBorder, error = Color(0xfff87171),
)

@Composable
fun Theme(content: @Composable () -> Unit) {
    val view = LocalView.current
    if (!view.isInEditMode) SideEffect {
        (view.context as? Activity)?.window?.let {
            WindowCompat.getInsetsController(it, view).apply {
                isAppearanceLightStatusBars = false
                isAppearanceLightNavigationBars = false
            }
        }
    }
    MaterialTheme(colorScheme = scheme, typography = Typography, shapes = Shapes, content = content)
}
