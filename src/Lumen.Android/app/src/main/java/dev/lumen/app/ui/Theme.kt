package dev.lumen.app.ui

import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.ReadOnlyComposable
import androidx.compose.runtime.staticCompositionLocalOf
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.sp

data class LumenExtraColors(
    val warning: Color,
    val backdropStart: Color,
    val backdropEnd: Color
)

private val DarkExtra = LumenExtraColors(
    warning = Color(0xFFFFC45C),
    backdropStart = Color(0xFF040711),
    backdropEnd = Color(0xFF0A1226)
)

private val LightExtra = LumenExtraColors(
    warning = Color(0xFF8F5E00),
    backdropStart = Color(0xFFE9F0FA),
    backdropEnd = Color(0xFFF8FBFF)
)

private val LocalLumenExtra = staticCompositionLocalOf { DarkExtra }

private val LumenDarkScheme = darkColorScheme(
    primary = Color(0xFF4EE3C2),
    onPrimary = Color(0xFF04221D),
    primaryContainer = Color(0xFF103D38),
    onPrimaryContainer = Color(0xFFB9FFF0),
    secondary = Color(0xFF7AA7FF),
    onSecondary = Color(0xFF07183B),
    secondaryContainer = Color(0xFF1A2D5C),
    onSecondaryContainer = Color(0xFFD9E4FF),
    tertiary = Color(0xFFFFBB6B),
    onTertiary = Color(0xFF2B1600),
    tertiaryContainer = Color(0xFF3A2600),
    onTertiaryContainer = Color(0xFFFFE0B0),
    background = Color(0xFF070B18),
    onBackground = Color(0xFFE9EFFF),
    surface = Color(0xFF0F1630),
    onSurface = Color(0xFFE9EFFF),
    surfaceVariant = Color(0xFF172044),
    onSurfaceVariant = Color(0xFF8895B9),
    outline = Color(0xFF24305A),
    outlineVariant = Color(0xFF1B2547),
    error = Color(0xFFFF6B81),
    onError = Color(0xFF2B0007),
    errorContainer = Color(0xFF4A0D18),
    onErrorContainer = Color(0xFFFFD9DE)
)

private val LumenLightScheme = lightColorScheme(
    primary = Color(0xFF0B7F70),
    onPrimary = Color(0xFFFFFFFF),
    primaryContainer = Color(0xFFD6F5EE),
    onPrimaryContainer = Color(0xFF063B33),
    secondary = Color(0xFF3F63C8),
    onSecondary = Color(0xFFFFFFFF),
    secondaryContainer = Color(0xFFE3E9FF),
    onSecondaryContainer = Color(0xFF152B63),
    tertiary = Color(0xFF946000),
    onTertiary = Color(0xFFFFFFFF),
    tertiaryContainer = Color(0xFFFFE8C2),
    onTertiaryContainer = Color(0xFF3D2600),
    background = Color(0xFFF2F5FA),
    onBackground = Color(0xFF151A28),
    surface = Color(0xFFFFFFFF),
    onSurface = Color(0xFF151A28),
    surfaceVariant = Color(0xFFE9EEF7),
    onSurfaceVariant = Color(0xFF545F72),
    outline = Color(0xFFCBD5E5),
    outlineVariant = Color(0xFFE1E7F1),
    error = Color(0xFFC22F45),
    onError = Color(0xFFFFFFFF),
    errorContainer = Color(0xFFFFDDE2),
    onErrorContainer = Color(0xFF5C0011)
)

val LumenBackground: Color
    @Composable @ReadOnlyComposable get() = MaterialTheme.colorScheme.background

val LumenBackgroundDeep: Color
    @Composable @ReadOnlyComposable get() = MaterialTheme.colorScheme.onPrimary

val LumenSurface: Color
    @Composable @ReadOnlyComposable get() = MaterialTheme.colorScheme.surface

val LumenSurfaceHigh: Color
    @Composable @ReadOnlyComposable get() = MaterialTheme.colorScheme.surfaceVariant

val LumenSurfaceSoft: Color
    @Composable @ReadOnlyComposable get() = MaterialTheme.colorScheme.surfaceVariant

val LumenPrimary: Color
    @Composable @ReadOnlyComposable get() = MaterialTheme.colorScheme.primary

val LumenSecondary: Color
    @Composable @ReadOnlyComposable get() = MaterialTheme.colorScheme.secondary

val LumenTertiary: Color
    @Composable @ReadOnlyComposable get() = MaterialTheme.colorScheme.tertiary

val LumenDanger: Color
    @Composable @ReadOnlyComposable get() = MaterialTheme.colorScheme.error

val LumenSuccess: Color
    @Composable @ReadOnlyComposable get() = MaterialTheme.colorScheme.primary

val LumenWarning: Color
    @Composable @ReadOnlyComposable get() = LocalLumenExtra.current.warning

val LumenText: Color
    @Composable @ReadOnlyComposable get() = MaterialTheme.colorScheme.onSurface

val LumenTextMuted: Color
    @Composable @ReadOnlyComposable get() = MaterialTheme.colorScheme.onSurfaceVariant

val LumenBorder: Color
    @Composable @ReadOnlyComposable get() = MaterialTheme.colorScheme.outline

val LumenBackdropStart: Color
    @Composable @ReadOnlyComposable get() = LocalLumenExtra.current.backdropStart

val LumenBackdropEnd: Color
    @Composable @ReadOnlyComposable get() = LocalLumenExtra.current.backdropEnd

@Composable
fun LumenTheme(
    themeMode: String = "system",
    content: @Composable () -> Unit
) {
    val dark = when (themeMode) {
        "light" -> false
        "dark" -> true
        else -> isSystemInDarkTheme()
    }

    CompositionLocalProvider(LocalLumenExtra provides if (dark) DarkExtra else LightExtra) {
        MaterialTheme(
            colorScheme = if (dark) LumenDarkScheme else LumenLightScheme,
            typography = MaterialTheme.typography.copy(
                headlineLarge = TextStyle(
                    fontWeight = FontWeight.SemiBold,
                    fontSize = 32.sp,
                    letterSpacing = (-0.6).sp
                ),
                headlineMedium = TextStyle(
                    fontWeight = FontWeight.SemiBold,
                    fontSize = 25.sp,
                    letterSpacing = (-0.4).sp
                ),
                titleLarge = TextStyle(
                    fontWeight = FontWeight.SemiBold,
                    fontSize = 19.sp
                ),
                titleMedium = TextStyle(
                    fontWeight = FontWeight.SemiBold,
                    fontSize = 16.sp
                ),
                bodyLarge = TextStyle(fontSize = 15.sp, lineHeight = 22.sp),
                bodyMedium = TextStyle(fontSize = 13.5.sp, lineHeight = 20.sp),
                labelLarge = TextStyle(fontWeight = FontWeight.SemiBold, fontSize = 13.sp)
            ),
            content = content
        )
    }
}
