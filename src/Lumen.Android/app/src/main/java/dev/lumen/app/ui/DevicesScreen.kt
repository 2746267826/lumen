package dev.lumen.app.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ExperimentalLayoutApi
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Description
import androidx.compose.material.icons.filled.Image
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.Send
import androidx.compose.material.icons.filled.TextFields
import androidx.compose.material.icons.filled.Wifi
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import dev.lumen.app.data.PeerDevice
import dev.lumen.app.data.familyOf
import dev.lumen.app.data.platformLabel
import dev.lumen.app.net.RelayState

@OptIn(ExperimentalLayoutApi::class)
@Composable
fun DevicesScreen(
    peers: List<PeerDevice>,
    currentDeviceId: String,
    displayName: String,
    relayState: RelayState,
    latencyMs: Long?,
    lanRunning: Boolean,
    lanError: String?,
    localAddresses: List<String>,
    onPickFiles: (PeerDevice) -> Unit,
    onPickImages: (PeerDevice) -> Unit,
    onSendText: (PeerDevice) -> Unit,
    onRefresh: () -> Unit
) {
    val lanPeers = peers.count { it.lanOnline }
    LazyColumn(
        modifier = Modifier.fillMaxWidth(),
        contentPadding = androidx.compose.foundation.layout.PaddingValues(
            start = 18.dp,
            end = 18.dp,
            top = 10.dp,
            bottom = 110.dp
        ),
        verticalArrangement = Arrangement.spacedBy(13.dp)
    ) {
        item {
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.SpaceBetween,
                verticalAlignment = Alignment.CenterVertically
            ) {
                Column(Modifier.weight(1f)) {
                    Text("设备", style = MaterialTheme.typography.headlineMedium)
                    Spacer(Modifier.height(3.dp))
                    Text(
                        "你好，$displayName",
                        color = LumenTextMuted,
                        fontSize = 13.sp
                    )
                }
                TextButton(onClick = onRefresh) {
                    Icon(Icons.Filled.Refresh, null, modifier = Modifier.size(17.dp))
                    Spacer(Modifier.width(5.dp))
                    Text("刷新")
                }
            }
        }

        item {
            LumenCard(Modifier.fillMaxWidth()) {
                Column {
                    Row(
                        modifier = Modifier.fillMaxWidth(),
                        verticalAlignment = Alignment.CenterVertically
                    ) {
                        BoxIcon(
                            color = if (relayState == RelayState.CONNECTED) LumenPrimary else LumenDanger
                        )
                        Spacer(Modifier.width(12.dp))
                        Column(Modifier.weight(1f)) {
                            Text(
                                when (relayState) {
                                    RelayState.CONNECTED -> "服务器中转已连接"
                                    RelayState.CONNECTING -> "正在连接服务器…"
                                    RelayState.DISCONNECTED -> "服务器中转未连接"
                                },
                                style = MaterialTheme.typography.titleMedium
                            )
                            Spacer(Modifier.height(2.dp))
                            Text(
                                when (relayState) {
                                    RelayState.CONNECTED ->
                                        if (latencyMs != null) "信令在线 · 往返延迟 ${latencyMs}ms"
                                        else "信令在线 · 正在测量延迟"
                                    RelayState.CONNECTING -> "建立 WebSocket 长连接"
                                    RelayState.DISCONNECTED -> "跨网络传输暂不可用，局域网仍可直连"
                                },
                                color = LumenTextMuted,
                                fontSize = 12.sp
                            )
                        }
                        TagChip(
                            when (relayState) {
                                RelayState.CONNECTED -> "中转可用"
                                RelayState.CONNECTING -> "连接中"
                                RelayState.DISCONNECTED -> "离线"
                            },
                            color = if (relayState == RelayState.CONNECTED) LumenPrimary else LumenDanger
                        )
                    }
                    Spacer(Modifier.height(15.dp))
                    Row(
                        modifier = Modifier.fillMaxWidth(),
                        horizontalArrangement = Arrangement.spacedBy(9.dp)
                    ) {
                        MiniStat(
                            label = "局域网",
                            value = if (lanRunning) "$lanPeers 台可见" else "未开启",
                            accent = if (lanRunning) LumenPrimary else LumenTextMuted,
                            modifier = Modifier.weight(1f)
                        )
                        MiniStat(
                            label = "绑定设备",
                            value = "${peers.count { it.online || it.lanOnline }} 台在线",
                            accent = LumenSecondary,
                            modifier = Modifier.weight(1f)
                        )
                        MiniStat(
                            label = "协议栈",
                            value = when {
                                localAddresses.any { it.contains(':') } &&
                                    localAddresses.any { !it.contains(':') } -> "IPv4 + IPv6"
                                localAddresses.any { it.contains(':') } -> "IPv6"
                                localAddresses.isNotEmpty() -> "IPv4"
                                else -> "—"
                            },
                            accent = LumenTertiary,
                            modifier = Modifier.weight(1f)
                        )
                    }
                    if (!lanError.isNullOrBlank()) {
                        Spacer(Modifier.height(10.dp))
                        Text(
                            lanError,
                            color = LumenDanger,
                            fontSize = 11.sp
                        )
                    }
                    if (localAddresses.isNotEmpty()) {
                        Spacer(Modifier.height(12.dp))
                        FlowRow(
                            horizontalArrangement = Arrangement.spacedBy(6.dp),
                            verticalArrangement = Arrangement.spacedBy(6.dp)
                        ) {
                            localAddresses.take(4).forEach { address ->
                                TagChip(
                                    text = address,
                                    color = if (address.contains(':')) LumenSecondary else LumenPrimary,
                                    icon = Icons.Filled.Wifi
                                )
                            }
                            if (localAddresses.size > 4) {
                                TagChip("+${localAddresses.size - 4}", color = LumenTextMuted)
                            }
                        }
                    }
                }
            }
        }

        item {
            Spacer(Modifier.height(3.dp))
            SectionHeader(
                "发现到的设备",
                "同一账户的绑定设备上线即出现；同网设备通过 UDP 组播自动发现"
            )
        }

        if (peers.isEmpty()) {
            item {
                EmptyState(
                    title = "还没有发现设备",
                    description = "在另一台设备登录同一账户，或连接到同一 Wi-Fi。Windows 与 Android 会互相广播。"
                )
            }
        } else {
            items(peers, key = { it.id }) { peer ->
                DeviceCard(
                    peer = peer,
                    isSelf = peer.id == currentDeviceId,
                    onPickFiles = { onPickFiles(peer) },
                    onPickImages = { onPickImages(peer) },
                    onSendText = { onSendText(peer) }
                )
            }
        }
    }
}

@Composable
private fun MiniStat(
    label: String,
    value: String,
    accent: Color,
    modifier: Modifier = Modifier
) {
    Column(
        modifier = modifier
            .background(
                Brush.verticalGradient(
                    listOf(LumenSurfaceHigh.copy(alpha = 0.85f), LumenSurface.copy(alpha = 0.6f))
                ),
                RoundedCornerShape(13.dp)
            )
            .padding(horizontal = 10.dp, vertical = 9.dp)
    ) {
        Text(label, fontSize = 10.sp, color = LumenTextMuted)
        Spacer(Modifier.height(3.dp))
        Text(value, fontSize = 12.sp, color = accent, fontWeight = FontWeight.SemiBold, maxLines = 1)
    }
}

@Composable
private fun BoxIcon(color: Color) {
    androidx.compose.foundation.layout.Box(
        modifier = Modifier
            .size(46.dp)
            .background(color.copy(alpha = 0.14f), RoundedCornerShape(15.dp)),
        contentAlignment = Alignment.Center
    ) {
        Icon(
            Icons.Filled.Wifi,
            null,
            tint = color,
            modifier = Modifier.size(23.dp)
        )
    }
}

@Composable
private fun DeviceCard(
    peer: PeerDevice,
    isSelf: Boolean,
    onPickFiles: () -> Unit,
    onPickImages: () -> Unit,
    onSendText: () -> Unit
) {
    LumenCard(Modifier.fillMaxWidth()) {
        Column {
            Row(
                modifier = Modifier.fillMaxWidth(),
                verticalAlignment = Alignment.CenterVertically
            ) {
                PlatformAvatar(peer.platform, 48)
                Spacer(Modifier.width(12.dp))
                Column(Modifier.weight(1f)) {
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Text(
                            peer.name,
                            style = MaterialTheme.typography.titleMedium,
                            maxLines = 1,
                            overflow = androidx.compose.ui.text.style.TextOverflow.Ellipsis,
                            modifier = Modifier.weight(1f, fill = false)
                        )
                        if (isSelf) {
                            Spacer(Modifier.width(7.dp))
                            TagChip("当前设备", LumenTertiary)
                        }
                    }
                    Spacer(Modifier.height(4.dp))
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        StatusDot(peer.lanOnline || peer.online)
                        Spacer(Modifier.width(6.dp))
                        Text(
                            buildString {
                                append(platformLabel(peer.platform))
                                peer.model?.takeIf { it.isNotBlank() }?.let { append(" · ").append(it) }
                            },
                            fontSize = 12.sp,
                            color = LumenTextMuted,
                            maxLines = 1
                        )
                    }
                }
            }
            Spacer(Modifier.height(11.dp))
            FlowRow(
                horizontalArrangement = Arrangement.spacedBy(6.dp),
                verticalArrangement = Arrangement.spacedBy(6.dp)
            ) {
                if (peer.lanOnline) {
                    TagChip("局域网直连", LumenPrimary, Icons.Filled.Wifi)
                    peer.lanFamilies.forEach { family ->
                        TagChip(family, if (family == "IPv6") LumenSecondary else LumenPrimary)
                    }
                    peer.primaryLanAddress?.let { TagChip("$it:${peer.lanPort}", LumenTextMuted) }
                } else {
                    TagChip("不在同一局域网", LumenTextMuted)
                }
                if (peer.online) {
                    TagChip("服务器中转", LumenSecondary)
                }
                peer.lastIpFamily?.let { TagChip("最近 $it", LumenTertiary) }
            }
            if (!isSelf) {
                Spacer(Modifier.height(13.dp))
                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.spacedBy(8.dp)
                ) {
                    DeviceAction(
                        text = "文件",
                        icon = Icons.Filled.Description,
                        enabled = peer.lanOnline || peer.online,
                        onClick = onPickFiles,
                        modifier = Modifier.weight(1f)
                    )
                    DeviceAction(
                        text = "图片",
                        icon = Icons.Filled.Image,
                        enabled = peer.lanOnline || peer.online,
                        onClick = onPickImages,
                        modifier = Modifier.weight(1f)
                    )
                    DeviceAction(
                        text = "文字",
                        icon = Icons.Filled.TextFields,
                        enabled = peer.lanOnline || peer.online,
                        onClick = onSendText,
                        modifier = Modifier.weight(1f)
                    )
                }
            }
        }
    }
}

@Composable
private fun DeviceAction(
    text: String,
    icon: androidx.compose.ui.graphics.vector.ImageVector,
    enabled: Boolean,
    onClick: () -> Unit,
    modifier: Modifier = Modifier
) {
    OutlinedButton(
        onClick = onClick,
        enabled = enabled,
        modifier = modifier.height(38.dp),
        shape = RoundedCornerShape(11.dp),
        colors = ButtonDefaults.outlinedButtonColors(
            contentColor = LumenText,
            disabledContentColor = LumenTextMuted.copy(alpha = 0.45f)
        )
    ) {
        Icon(icon, null, modifier = Modifier.size(15.dp))
        Spacer(Modifier.width(5.dp))
        Text(text, fontSize = 12.sp)
    }
}
