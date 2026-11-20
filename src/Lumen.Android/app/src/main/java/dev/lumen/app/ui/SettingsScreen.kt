package dev.lumen.app.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
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
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.DeleteSweep
import androidx.compose.material.icons.filled.Devices
import androidx.compose.material.icons.filled.Dns
import androidx.compose.material.icons.filled.Edit
import androidx.compose.material.icons.filled.Folder
import androidx.compose.material.icons.filled.Info
import androidx.compose.material.icons.filled.Logout
import androidx.compose.material.icons.filled.Palette
import androidx.compose.material.icons.filled.Person
import androidx.compose.material.icons.filled.Refresh
import androidx.compose.material.icons.filled.Security
import androidx.compose.material.icons.filled.Speed
import androidx.compose.material.icons.filled.Tune
import androidx.compose.material.icons.filled.Wifi
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Switch
import androidx.compose.material3.SwitchDefaults
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import dev.lumen.app.net.RelayState
import dev.lumen.app.util.formatBytes

@OptIn(ExperimentalLayoutApi::class)
@Composable
fun SettingsScreen(
    displayName: String,
    username: String,
    serverUrl: String,
    deviceName: String,
    deviceId: String,
    autoAccept: Boolean,
    lanDiscovery: Boolean,
    chunkSize: Int,
    themeMode: String,
    relayState: RelayState,
    latencyMs: Long?,
    lanRunning: Boolean,
    localAddresses: List<String>,
    receivePath: String,
    appVersion: String,
    onUpdateDeviceName: (String) -> Unit,
    onSetAutoAccept: (Boolean) -> Unit,
    onSetLanDiscovery: (Boolean) -> Unit,
    onSetChunkSize: (Int) -> Unit,
    onSetThemeMode: (String) -> Unit,
    onReconnect: () -> Unit,
    onLogout: () -> Unit,
    onClearHistory: () -> Unit,
    onShowPath: () -> Unit
) {
    var editingName by rememberSaveable { mutableStateOf(false) }
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
            Text("设置", style = MaterialTheme.typography.headlineMedium)
            Spacer(Modifier.height(3.dp))
            Text("账户、设备与传输策略", color = LumenTextMuted, fontSize = 13.sp)
        }
        item {
            LumenCard(Modifier.fillMaxWidth()) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Box(
                        modifier = Modifier
                            .size(54.dp)
                            .background(
                                androidx.compose.ui.graphics.Brush.linearGradient(
                                    listOf(LumenPrimary, LumenSecondary)
                                ),
                                RoundedCornerShape(17.dp)
                            ),
                        contentAlignment = Alignment.Center
                    ) {
                        Text(
                            displayName.take(1).ifBlank { "L" },
                            fontSize = 24.sp,
                            fontWeight = FontWeight.Bold,
                            color = LumenBackgroundDeep
                        )
                    }
                    Spacer(Modifier.width(13.dp))
                    Column(Modifier.weight(1f)) {
                        Text(displayName.ifBlank { username }, style = MaterialTheme.typography.titleLarge)
                        Spacer(Modifier.height(2.dp))
                        Text("@$username", color = LumenTextMuted, fontSize = 12.sp)
                        Spacer(Modifier.height(3.dp))
                        Text(serverUrl, color = LumenSecondary, fontSize = 10.5.sp, maxLines = 1)
                    }
                    TextButton(onClick = onLogout) {
                        Icon(Icons.Filled.Logout, null, modifier = Modifier.size(16.dp), tint = LumenDanger)
                        Spacer(Modifier.width(4.dp))
                        Text("退出", color = LumenDanger, fontSize = 12.sp)
                    }
                }
            }
        }

        item {
            SettingsSection(title = "连接状态", icon = Icons.Filled.Wifi) {
                SettingRow(
                    title = "服务器中转",
                    subtitle = when (relayState) {
                        RelayState.CONNECTED -> "已连接 · 延迟 ${latencyMs ?: "—"}ms"
                        RelayState.CONNECTING -> "正在建立长连接"
                        RelayState.DISCONNECTED -> "未连接"
                    },
                    trailing = {
                        TagChip(
                            if (relayState == RelayState.CONNECTED) "在线" else "离线",
                            if (relayState == RelayState.CONNECTED) LumenPrimary else LumenDanger
                        )
                    }
                )
                SettingRow(
                    title = "局域网发现",
                    subtitle = if (lanRunning) "UDP 组播运行中 · ${localAddresses.size} 个地址" else "已关闭",
                    trailing = {
                        TagChip(
                            if (lanRunning) "运行中" else "停止",
                            if (lanRunning) LumenPrimary else LumenTextMuted
                        )
                    }
                )
                if (localAddresses.isNotEmpty()) {
                    FlowRow(
                        horizontalArrangement = Arrangement.spacedBy(6.dp),
                        verticalArrangement = Arrangement.spacedBy(6.dp),
                        modifier = Modifier.padding(top = 4.dp)
                    ) {
                        localAddresses.forEach { address ->
                            TagChip(
                                address,
                                if (address.contains(':')) LumenSecondary else LumenPrimary
                            )
                        }
                    }
                }
                Spacer(Modifier.height(8.dp))
                OutlinedButton(
                    onClick = onReconnect,
                    modifier = Modifier.fillMaxWidth(),
                    shape = RoundedCornerShape(12.dp)
                ) {
                    Icon(Icons.Filled.Refresh, null, modifier = Modifier.size(16.dp))
                    Spacer(Modifier.width(5.dp))
                    Text("重新连接服务器", fontSize = 12.sp)
                }
            }
        }

        item {
            SettingsSection(title = "本机设备", icon = Icons.Filled.Devices) {
                SettingRow(
                    title = "设备名称",
                    subtitle = deviceName,
                    trailing = {
                        TextButton(onClick = { editingName = true }) {
                            Icon(Icons.Filled.Edit, null, Modifier.size(15.dp))
                            Spacer(Modifier.width(4.dp))
                            Text("修改", fontSize = 11.sp)
                        }
                    }
                )
                SettingRow(
                    title = "设备标识",
                    subtitle = deviceId,
                    mono = true
                )
                SettingRow(
                    title = "平台",
                    subtitle = "Android · ${android.os.Build.MANUFACTURER} ${android.os.Build.MODEL}"
                )
            }
        }

        item {
            SettingsSection(title = "外观", icon = Icons.Filled.Palette) {
                Text("主题模式", fontSize = 12.sp, color = LumenTextMuted)
                Spacer(Modifier.height(8.dp))
                FlowRow(horizontalArrangement = Arrangement.spacedBy(7.dp)) {
                    listOf(
                        "system" to "跟随系统",
                        "light" to "浅色",
                        "dark" to "深色"
                    ).forEach { (mode, label) ->
                        Box(
                            modifier = Modifier
                                .clip(CircleShape)
                                .background(
                                    if (themeMode == mode) LumenPrimary.copy(alpha = 0.18f)
                                    else LumenSurfaceHigh
                                )
                                .clickable { onSetThemeMode(mode) }
                                .padding(horizontal = 13.dp, vertical = 7.dp)
                        ) {
                            Text(
                                label,
                                fontSize = 11.5.sp,
                                color = if (themeMode == mode) LumenPrimary else LumenTextMuted,
                                fontWeight = if (themeMode == mode) FontWeight.SemiBold else FontWeight.Normal
                            )
                        }
                    }
                }
                Spacer(Modifier.height(9.dp))
                Text(
                    "浅色适合明亮环境，深色适合夜间；跟随系统会使用 Android 的深色模式设置。",
                    fontSize = 11.sp,
                    color = LumenTextMuted,
                    lineHeight = 17.sp
                )
            }
        }

        item {
            SettingsSection(title = "传输策略", icon = Icons.Filled.Tune) {
                SettingRow(
                    title = "自动接受同一账户设备",
                    subtitle = "仅对已绑定设备生效，开启后无需手动确认",
                    trailing = {
                        Switch(
                            checked = autoAccept,
                            onCheckedChange = onSetAutoAccept,
                            colors = SwitchDefaults.colors(
                                checkedTrackColor = LumenPrimary,
                                checkedThumbColor = LumenBackgroundDeep
                            )
                        )
                    }
                )
                SettingRow(
                    title = "局域网自动直连",
                    subtitle = "同网设备优先 UDP 发现 + HTTP 直传",
                    trailing = {
                        Switch(
                            checked = lanDiscovery,
                            onCheckedChange = onSetLanDiscovery,
                            colors = SwitchDefaults.colors(
                                checkedTrackColor = LumenPrimary,
                                checkedThumbColor = LumenBackgroundDeep
                            )
                        )
                    }
                )
                Spacer(Modifier.height(8.dp))
                Text("分块大小", fontSize = 12.sp, color = LumenTextMuted)
                Spacer(Modifier.height(6.dp))
                FlowRow(horizontalArrangement = Arrangement.spacedBy(7.dp)) {
                    listOf(256 * 1024, 1024 * 1024, 4 * 1024 * 1024).forEach { size ->
                        Box(
                            modifier = Modifier
                                .clip(CircleShape)
                                .background(
                                    if (chunkSize == size) LumenPrimary.copy(alpha = 0.18f)
                                    else LumenSurfaceHigh
                                )
                                .clickable { onSetChunkSize(size) }
                                .padding(horizontal = 12.dp, vertical = 6.dp)
                        ) {
                            Text(
                                formatBytes(size.toLong()),
                                fontSize = 11.sp,
                                color = if (chunkSize == size) LumenPrimary else LumenTextMuted
                            )
                        }
                    }
                }
                Spacer(Modifier.height(10.dp))
                SettingRow(
                    title = "接收目录",
                    subtitle = receivePath,
                    trailing = {
                        TextButton(onClick = onShowPath) {
                            Icon(Icons.Filled.Folder, null, Modifier.size(15.dp))
                            Spacer(Modifier.width(4.dp))
                            Text("查看", fontSize = 11.sp)
                        }
                    }
                )
                SettingRow(
                    title = "清空本地记录",
                    subtitle = "只清除历史列表，不删除已接收文件",
                    trailing = {
                        TextButton(onClick = onClearHistory) {
                            Icon(Icons.Filled.DeleteSweep, null, Modifier.size(15.dp), tint = LumenDanger)
                            Spacer(Modifier.width(4.dp))
                            Text("清空", fontSize = 11.sp, color = LumenDanger)
                        }
                    }
                )
            }
        }

        item {
            SettingsSection(title = "安全与说明", icon = Icons.Filled.Security) {
                Text(
                    "会话令牌保存在 Android Keystore 加密的本地存储中；公网部署建议在反向代理上启用 HTTPS。文件在服务器上以分块形式暂存，接收端逐块校验 SHA-256。",
                    fontSize = 12.sp,
                    color = LumenTextMuted,
                    lineHeight = 18.sp
                )
            }
        }
        item {
            LumenCard(Modifier.fillMaxWidth()) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Icon(Icons.Filled.Info, null, tint = LumenTextMuted, modifier = Modifier.size(18.dp))
                    Spacer(Modifier.width(8.dp))
                    Column {
                        Text("Lumen Android $appVersion", fontSize = 12.sp, color = LumenText)
                        Text("协议 v1 · 与 Windows / Docker 服务端互通", fontSize = 11.sp, color = LumenTextMuted)
                    }
                }
            }
        }
    }

    if (editingName) {
        var value by rememberSaveable { mutableStateOf(deviceName) }
        AlertDialog(
            onDismissRequest = { editingName = false },
            containerColor = LumenSurface,
            title = { Text("修改设备名称") },
            text = {
                OutlinedTextField(
                    value = value,
                    onValueChange = { value = it.take(60) },
                    label = { Text("设备名称") },
                    singleLine = true,
                    modifier = Modifier.fillMaxWidth(),
                    shape = RoundedCornerShape(13.dp),
                    colors = lumenFieldColors()
                )
            },
            confirmButton = {
                TextButton(onClick = {
                    onUpdateDeviceName(value)
                    editingName = false
                }) { Text("保存") }
            },
            dismissButton = {
                TextButton(onClick = { editingName = false }) { Text("取消") }
            }
        )
    }
}

@Composable
private fun SettingsSection(
    title: String,
    icon: androidx.compose.ui.graphics.vector.ImageVector,
    content: @Composable () -> Unit
) {
    LumenCard(Modifier.fillMaxWidth()) {
        Column {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Icon(icon, null, tint = LumenPrimary, modifier = Modifier.size(18.dp))
                Spacer(Modifier.width(8.dp))
                Text(title, style = MaterialTheme.typography.titleMedium)
            }
            Spacer(Modifier.height(11.dp))
            content()
        }
    }
}

@Composable
private fun SettingRow(
    title: String,
    subtitle: String,
    trailing: @Composable (() -> Unit)? = null,
    mono: Boolean = false
) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .padding(vertical = 6.dp),
        verticalAlignment = Alignment.CenterVertically
    ) {
        Column(Modifier.weight(1f)) {
            Text(title, fontSize = 13.sp, color = LumenText)
            Spacer(Modifier.height(2.dp))
            Text(
                subtitle,
                fontSize = if (mono) 9.5.sp else 11.sp,
                color = LumenTextMuted,
                maxLines = if (mono) 1 else 2
            )
        }
        trailing?.invoke()
    }
}
