package dev.lumen.app.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Android
import androidx.compose.material.icons.filled.Computer
import androidx.compose.material.icons.filled.Devices
import androidx.compose.material.icons.filled.Inbox
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import dev.lumen.app.data.platformLabel

@Composable
fun LumenBackdrop(content: @Composable () -> Unit) {
    Box(
        modifier = Modifier
            .fillMaxWidth()
            .background(
                Brush.verticalGradient(
                    listOf(LumenBackgroundDeep, LumenBackground, Color(0xFF0A1226))
                )
            )
    ) {
        Box(
            modifier = Modifier
                .size(260.dp)
                .background(
                    Brush.radialGradient(
                        listOf(LumenPrimary.copy(alpha = 0.10f), Color.Transparent)
                    )
                )
        )
        Box(
            modifier = Modifier
                .align(Alignment.TopEnd)
                .size(300.dp)
                .background(
                    Brush.radialGradient(
                        listOf(LumenSecondary.copy(alpha = 0.13f), Color.Transparent)
                    )
                )
        )
        content()
    }
}

@Composable
fun LumenCard(
    modifier: Modifier = Modifier,
    content: @Composable () -> Unit
) {
    Box(
        modifier = modifier
            .clip(RoundedCornerShape(20.dp))
            .background(
                Brush.linearGradient(
                    listOf(
                        LumenSurface.copy(alpha = 0.98f),
                        Color(0xFF101A38).copy(alpha = 0.96f)
                    )
                )
            )
            .border(1.dp, LumenBorder.copy(alpha = 0.75f), RoundedCornerShape(20.dp))
            .padding(16.dp)
    ) {
        content()
    }
}

@Composable
fun TagChip(
    text: String,
    color: Color = LumenPrimary,
    icon: ImageVector? = null,
    modifier: Modifier = Modifier
) {
    Row(
        modifier = modifier
            .clip(CircleShape)
            .background(color.copy(alpha = 0.13f))
            .border(1.dp, color.copy(alpha = 0.28f), CircleShape)
            .padding(horizontal = 9.dp, vertical = 4.dp),
        verticalAlignment = Alignment.CenterVertically
    ) {
        if (icon != null) {
            Icon(
                icon,
                contentDescription = null,
                tint = color,
                modifier = Modifier.size(13.dp)
            )
            Spacer(Modifier.width(5.dp))
        }
        Text(
            text,
            color = color,
            fontSize = 11.sp,
            fontWeight = FontWeight.SemiBold,
            maxLines = 1
        )
    }
}

@Composable
fun StatusDot(online: Boolean, size: Int = 8, color: Color? = null) {
    Box(
        modifier = Modifier
            .size(size.dp)
            .clip(CircleShape)
            .background(
                color ?: if (online) LumenSuccess else LumenTextMuted.copy(alpha = 0.45f)
            )
    )
}

@Composable
fun SectionHeader(title: String, subtitle: String? = null, trailing: @Composable (() -> Unit)? = null) {
    Row(
        modifier = Modifier.fillMaxWidth(),
        verticalAlignment = Alignment.Bottom,
        horizontalArrangement = Arrangement.SpaceBetween
    ) {
        Column {
            Text(
                title,
                style = MaterialTheme.typography.titleLarge,
                color = LumenText
            )
            if (!subtitle.isNullOrBlank()) {
                Spacer(Modifier.height(3.dp))
                Text(
                    subtitle,
                    style = MaterialTheme.typography.bodyMedium,
                    color = LumenTextMuted
                )
            }
        }
        trailing?.invoke()
    }
}

@Composable
fun EmptyState(
    icon: ImageVector = Icons.Filled.Inbox,
    title: String,
    description: String
) {
    Column(
        modifier = Modifier
            .fillMaxWidth()
            .padding(vertical = 34.dp),
        horizontalAlignment = Alignment.CenterHorizontally
    ) {
        Box(
            modifier = Modifier
                .size(62.dp)
                .clip(RoundedCornerShape(20.dp))
                .background(LumenSurfaceHigh),
            contentAlignment = Alignment.Center
        ) {
            Icon(icon, contentDescription = null, tint = LumenTextMuted, modifier = Modifier.size(28.dp))
        }
        Spacer(Modifier.height(14.dp))
        Text(title, style = MaterialTheme.typography.titleMedium, color = LumenText)
        Spacer(Modifier.height(5.dp))
        Text(
            description,
            style = MaterialTheme.typography.bodyMedium,
            color = LumenTextMuted,
            textAlign = TextAlign.Center,
            modifier = Modifier.padding(horizontal = 30.dp)
        )
    }
}

@Composable
fun PlatformAvatar(platform: String, size: Int = 46) {
    val (icon, color) = when (platform.lowercase()) {
        "windows" -> Icons.Filled.Computer to LumenSecondary
        "android" -> Icons.Filled.Android to LumenPrimary
        else -> Icons.Filled.Devices to LumenTertiary
    }
    Box(
        modifier = Modifier
            .size(size.dp)
            .clip(RoundedCornerShape((size * 0.32f).dp))
            .background(
                Brush.linearGradient(
                    listOf(color.copy(alpha = 0.22f), color.copy(alpha = 0.08f))
                )
            )
            .border(1.dp, color.copy(alpha = 0.25f), RoundedCornerShape((size * 0.32f).dp)),
        contentAlignment = Alignment.Center
    ) {
        Icon(
            icon,
            contentDescription = platformLabel(platform),
            tint = color,
            modifier = Modifier.size((size * 0.52f).dp)
        )
    }
}

@Composable
fun Metric(label: String, value: String, accent: Color = LumenText) {
    Column {
        Text(label, fontSize = 11.sp, color = LumenTextMuted)
        Spacer(Modifier.height(2.dp))
        Text(value, fontSize = 13.sp, fontWeight = FontWeight.SemiBold, color = accent)
    }
}

@Composable
fun InfoRow(label: String, value: String, mono: Boolean = false) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .padding(vertical = 4.dp),
        horizontalArrangement = Arrangement.SpaceBetween,
        verticalAlignment = Alignment.Top
    ) {
        Text(label, fontSize = 12.sp, color = LumenTextMuted, modifier = Modifier.width(96.dp))
        Text(
            value,
            fontSize = 12.sp,
            color = LumenText,
            textAlign = TextAlign.End,
            modifier = Modifier.weight(1f)
        )
    }
}
