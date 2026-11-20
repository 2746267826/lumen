package dev.lumen.app.ui

import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.sp

val LumenBackground = Color(0xFF070B18)
val LumenBackgroundDeep = Color(0xFF040711)
val LumenSurface = Color(0xFF0F1630)
val LumenSurfaceHigh = Color(0xFF172044)
val LumenSurfaceSoft = Color(0xFF1C2752)
val LumenPrimary = Color(0xFF4EE3C2)
val LumenSecondary = Color(0xFF7AA7FF)
val LumenTertiary = Color(0xFFFFBB6B)
val LumenDanger = Color(0xFFFF6B81)
val LumenSuccess = Color(0xFF4EE3C2)
val LumenWarning = Color(0xFFFFC45C)
val LumenText = Color(0xFFE9EFFF)
val LumenTextMuted = Color(0xFF8895B9)
val LumenBorder = Color(0xFF24305A)

private val LumenScheme = darkColorScheme(
    primary = LumenPrimary,
    onPrimary = Color(0xFF04221D),
    primaryContainer = Color(0xFF103D38),
    onPrimaryContainer = Color(0xFFB9FFF0),
    secondary = LumenSecondary,
    onSecondary = Color(0xFF07183B),
    secondaryContainer = Color(0xFF1A2D5C),
    onSecondaryContainer = Color(0xFFD9E4FF),
    tertiary = LumenTertiary,
    onTertiary = Color(0xFF2B1600),
    background = LumenBackground,
    onBackground = LumenText,
    surface = LumenSurface,
    onSurface = LumenText,
    surfaceVariant = LumenSurfaceHigh,
    onSurfaceVariant = LumenTextMuted,
    outline = LumenBorder,
    outlineVariant = Color(0xFF1B2547),
    error = LumenDanger,
    onError = Color(0xFF2B0007)
)

@Composable
fun LumenTheme(content: @Composable () -> Unit) {
    MaterialTheme(
        colorScheme = LumenScheme,
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
