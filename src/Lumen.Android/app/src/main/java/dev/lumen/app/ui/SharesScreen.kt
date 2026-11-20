package dev.lumen.app.ui

import android.content.Intent
import android.net.Uri
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.background
import androidx.compose.material3.MaterialTheme
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
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Add
import androidx.compose.material.icons.filled.ContentCopy
import androidx.compose.material.icons.filled.Description
import androidx.compose.material.icons.filled.Image
import androidx.compose.material.icons.filled.Link
import androidx.compose.material.icons.filled.Lock
import androidx.compose.material.icons.filled.OpenInNew
import androidx.compose.material.icons.filled.Share
import androidx.compose.material.icons.filled.TextFields
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.Icon
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalClipboardManager
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.draw.clip
import androidx.compose.ui.text.AnnotatedString
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import dev.lumen.app.data.ShareDto
import dev.lumen.app.data.TransferKind
import dev.lumen.app.util.formatBytes
import dev.lumen.app.util.formatTime
import dev.lumen.app.util.kindLabel

@OptIn(ExperimentalLayoutApi::class)
@Composable
fun SharesScreen(
    shares: List<ShareDto>,
    onRefresh: () -> Unit,
    onCreateText: (name: String, text: String, hours: Int, password: String?, maxDownloads: Int) -> Unit,
    onCreateFile: (uri: Uri, name: String, hours: Int, password: String?, maxDownloads: Int) -> Unit,
    onRevoke: (ShareDto) -> Unit
) {
    val context = LocalContext.current
    val clipboard = LocalClipboardManager.current
    var showTextDialog by rememberSaveable { mutableStateOf(false) }
    var pendingFile by remember { mutableStateOf<Uri?>(null) }
    var revoking by remember { mutableStateOf<ShareDto?>(null) }
    val filePicker = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocument()) { uri ->
        if (uri != null) pendingFile = uri
    }

    LazyColumn(
        modifier = Modifier.fillMaxWidth(),
        contentPadding = androidx.compose.foundation.layout.PaddingValues(
            start = 18.dp,
            end = 18.dp,
            top = 10.dp,
            bottom = 110.dp
        ),
        verticalArrangement = Arrangement.spacedBy(11.dp)
    ) {
        item {
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.SpaceBetween,
                verticalAlignment = Alignment.Bottom
            ) {
                Column {
                    Text("分享", style = MaterialTheme.typography.headlineMedium)
                    Spacer(Modifier.height(3.dp))
                    Text(
                        "把文件和文字变成一条链接",
                        color = LumenTextMuted,
                        fontSize = 13.sp
                    )
                }
                TextButton(onClick = onRefresh) { Text("刷新", fontSize = 12.sp) }
            }
        }
        item {
            Row(horizontalArrangement = Arrangement.spacedBy(9.dp)) {
                ShareAction(
                    title = "文字分享",
                    subtitle = "便签、口令、地址",
                    icon = Icons.Filled.TextFields,
                    color = LumenPrimary,
                    modifier = Modifier.weight(1f)
                ) { showTextDialog = true }
                ShareAction(
                    title = "文件分享",
                    subtitle = "最多 20 GiB",
                    icon = Icons.Filled.Description,
                    color = LumenSecondary,
                    modifier = Modifier.weight(1f)
                ) { filePicker.launch(arrayOf("*/*")) }
            }
        }
        item {
            LumenCard(Modifier.fillMaxWidth()) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Icon(Icons.Filled.Lock, null, tint = LumenTertiary, modifier = Modifier.size(18.dp))
                    Spacer(Modifier.width(9.dp))
                    Text(
                        "分享链接可设置有效期、访问密码和下载次数上限。到期后服务器会自动清理文件。",
                        fontSize = 11.5.sp,
                        color = LumenTextMuted,
                        lineHeight = 17.sp
                    )
                }
            }
        }

        if (shares.isEmpty()) {
            item {
                EmptyState(
                    icon = Icons.Filled.Link,
                    title = "还没有分享",
                    description = "创建一个文字或文件分享，复制链接发给任何设备。接收方无需安装 Lumen。"
                )
            }
        } else {
            items(shares, key = { it.id }) { share ->
                ShareCard(
                    share = share,
                    onCopy = {
                        clipboard.setText(AnnotatedString(share.url))
                    },
                    onOpen = {
                        runCatching {
                            context.startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(share.url)))
                        }
                    },
                    onRevoke = { revoking = share }
                )
            }
        }
    }

    if (showTextDialog) {
        ShareOptionsDialog(
            title = "创建文字分享",
            needsText = true,
            defaultName = "",
            onDismiss = { showTextDialog = false },
            onConfirm = { name, text, hours, password, maxDownloads ->
                onCreateText(name, text, hours, password, maxDownloads)
                showTextDialog = false
            }
        )
    }
    pendingFile?.let { uri ->
        ShareOptionsDialog(
            title = "分享文件",
            needsText = false,
            defaultName = "",
            onDismiss = { pendingFile = null },
            onConfirm = { name, _, hours, password, maxDownloads ->
                onCreateFile(uri, name, hours, password, maxDownloads)
                pendingFile = null
            }
        )
    }
    revoking?.let { share ->
        AlertDialog(
            onDismissRequest = { revoking = null },
            title = { Text("撤销分享") },
            text = { Text("撤销后链接立即失效，已下载的副本不受影响。确定撤销「${share.name}」？") },
            confirmButton = {
                TextButton(onClick = {
                    onRevoke(share)
                    revoking = null
                }) { Text("撤销", color = LumenDanger) }
            },
            dismissButton = {
                TextButton(onClick = { revoking = null }) { Text("保留") }
            },
            containerColor = LumenSurface
        )
    }
}

@Composable
private fun ShareAction(
    title: String,
    subtitle: String,
    icon: androidx.compose.ui.graphics.vector.ImageVector,
    color: androidx.compose.ui.graphics.Color,
    modifier: Modifier = Modifier,
    onClick: () -> Unit
) {
    LumenCard(
        modifier = modifier.clickable(onClick = onClick)
    ) {
        Column {
            Box(
                modifier = Modifier
                    .size(40.dp)
                    .background(color.copy(alpha = 0.14f), RoundedCornerShape(13.dp)),
                contentAlignment = Alignment.Center
            ) {
                Icon(icon, null, tint = color, modifier = Modifier.size(21.dp))
            }
            Spacer(Modifier.height(11.dp))
            Text(title, style = MaterialTheme.typography.titleMedium)
            Spacer(Modifier.height(2.dp))
            Text(subtitle, color = LumenTextMuted, fontSize = 11.sp)
        }
    }
}

@Composable
private fun ShareCard(
    share: ShareDto,
    onCopy: () -> Unit,
    onOpen: () -> Unit,
    onRevoke: () -> Unit
) {
    LumenCard(Modifier.fillMaxWidth()) {
        Column {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Box(
                    modifier = Modifier
                        .size(42.dp)
                        .background(
                            (if (share.kind == TransferKind.TEXT) LumenPrimary else LumenSecondary)
                                .copy(alpha = 0.14f),
                            RoundedCornerShape(13.dp)
                        ),
                    contentAlignment = Alignment.Center
                ) {
                    Icon(
                        when (share.kind) {
                            TransferKind.TEXT -> Icons.Filled.TextFields
                            TransferKind.IMAGE -> Icons.Filled.Image
                            else -> Icons.Filled.Description
                        },
                        null,
                        tint = if (share.kind == TransferKind.TEXT) LumenPrimary else LumenSecondary,
                        modifier = Modifier.size(21.dp)
                    )
                }
                Spacer(Modifier.width(11.dp))
                Column(Modifier.weight(1f)) {
                    Text(
                        share.name,
                        style = MaterialTheme.typography.titleMedium,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis
                    )
                    Spacer(Modifier.height(3.dp))
                    Text(
                        "${kindLabel(share.kind)} · ${formatBytes(share.size)} · 到期 ${formatTime(share.expiresAt)}",
                        fontSize = 11.sp,
                        color = LumenTextMuted,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis
                    )
                }
                if (share.passwordProtected) {
                    TagChip("密码", LumenTertiary, Icons.Filled.Lock)
                }
            }
            Spacer(Modifier.height(10.dp))
            FlowRow(
                horizontalArrangement = Arrangement.spacedBy(6.dp),
                verticalArrangement = Arrangement.spacedBy(6.dp)
            ) {
                TagChip("已下载 ${share.downloadCount} 次", LumenTextMuted)
                TagChip(
                    if (share.maxDownloads == 0) "不限次数" else "上限 ${share.maxDownloads} 次",
                    LumenSecondary
                )
            }
            Spacer(Modifier.height(9.dp))
            Box(
                modifier = Modifier
                    .fillMaxWidth()
                    .clip(RoundedCornerShape(10.dp))
                    .background(LumenBackground.copy(alpha = 0.55f))
                    .padding(horizontal = 10.dp, vertical = 9.dp)
            ) {
                Text(
                    share.url,
                    fontSize = 10.5.sp,
                    color = LumenSecondary,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis
                )
            }
            Spacer(Modifier.height(7.dp))
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.End
            ) {
                TextButton(onClick = onCopy) {
                    Icon(Icons.Filled.ContentCopy, null, modifier = Modifier.size(15.dp))
                    Spacer(Modifier.width(4.dp))
                    Text("复制链接", fontSize = 11.sp)
                }
                TextButton(onClick = onOpen) {
                    Icon(Icons.Filled.OpenInNew, null, modifier = Modifier.size(15.dp))
                    Spacer(Modifier.width(4.dp))
                    Text("打开", fontSize = 11.sp)
                }
                TextButton(onClick = onRevoke) {
                    Text("撤销", fontSize = 11.sp, color = LumenDanger)
                }
            }
        }
    }
}

@OptIn(ExperimentalLayoutApi::class)
@Composable
private fun ShareOptionsDialog(
    title: String,
    needsText: Boolean,
    defaultName: String,
    onDismiss: () -> Unit,
    onConfirm: (name: String, text: String, hours: Int, password: String?, maxDownloads: Int) -> Unit
) {
    var name by rememberSaveable { mutableStateOf(defaultName) }
    var text by rememberSaveable { mutableStateOf("") }
    var password by rememberSaveable { mutableStateOf("") }
    var maxDownloads by rememberSaveable { mutableStateOf("0") }
    var hours by rememberSaveable { mutableStateOf(24) }
    AlertDialog(
        onDismissRequest = onDismiss,
        containerColor = LumenSurface,
        title = { Text(title) },
        text = {
            Column(
                modifier = Modifier
                    .fillMaxWidth()
                    .verticalScroll(rememberScrollState())
            ) {
                OutlinedTextField(
                    value = name,
                    onValueChange = { name = it },
                    label = { Text("分享名称") },
                    supportingText = if (!needsText) {
                        { Text("留空时使用原文件名", fontSize = 10.sp) }
                    } else {
                        null
                    },
                    singleLine = true,
                    modifier = Modifier.fillMaxWidth(),
                    shape = RoundedCornerShape(13.dp),
                    colors = lumenFieldColors()
                )
                if (needsText) {
                    Spacer(Modifier.height(10.dp))
                    OutlinedTextField(
                        value = text,
                        onValueChange = { text = it },
                        label = { Text("文字内容") },
                        placeholder = { Text("输入要分享的文字…") },
                        minLines = 4,
                        maxLines = 8,
                        modifier = Modifier.fillMaxWidth(),
                        shape = RoundedCornerShape(13.dp),
                        colors = lumenFieldColors()
                    )
                }
                Spacer(Modifier.height(12.dp))
                Text("有效期", fontSize = 12.sp, color = LumenTextMuted)
                Spacer(Modifier.height(6.dp))
                FlowRow(horizontalArrangement = Arrangement.spacedBy(7.dp)) {
                    listOf(1 to "1 小时", 24 to "1 天", 72 to "3 天", 168 to "7 天").forEach { (value, label) ->
                        Box(
                            modifier = Modifier
                                .clip(CircleShape)
                                .background(
                                    if (hours == value) LumenPrimary.copy(alpha = 0.18f)
                                    else LumenSurfaceHigh
                                )
                                .clickable { hours = value }
                                .padding(horizontal = 12.dp, vertical = 6.dp)
                        ) {
                            Text(
                                label,
                                fontSize = 11.sp,
                                color = if (hours == value) LumenPrimary else LumenTextMuted
                            )
                        }
                    }
                }
                Spacer(Modifier.height(12.dp))
                Row(horizontalArrangement = Arrangement.spacedBy(9.dp)) {
                    OutlinedTextField(
                        value = password,
                        onValueChange = { password = it },
                        label = { Text("访问密码（可选）") },
                        singleLine = true,
                        modifier = Modifier.weight(1f),
                        shape = RoundedCornerShape(13.dp),
                        colors = lumenFieldColors()
                    )
                    OutlinedTextField(
                        value = maxDownloads,
                        onValueChange = { value ->
                            maxDownloads = value.filter { it.isDigit() }.take(5)
                        },
                        label = { Text("次数上限") },
                        singleLine = true,
                        keyboardOptions = androidx.compose.foundation.text.KeyboardOptions(
                            keyboardType = KeyboardType.Number
                        ),
                        modifier = Modifier.width(102.dp),
                        shape = RoundedCornerShape(13.dp),
                        colors = lumenFieldColors()
                    )
                }
                Spacer(Modifier.height(5.dp))
                Text("次数上限填 0 表示不限次数", fontSize = 10.sp, color = LumenTextMuted)
            }
        },
        confirmButton = {
            Button(
                onClick = {
                    if (needsText && text.isBlank()) return@Button
                    onConfirm(
                        name.ifBlank { if (needsText) "文字分享" else "" },
                        text,
                        hours,
                        password.ifBlank { null },
                        maxDownloads.toIntOrNull() ?: 0
                    )
                },
                enabled = !needsText || text.isNotBlank(),
                colors = ButtonDefaults.buttonColors(
                    containerColor = LumenPrimary,
                    contentColor = LumenBackgroundDeep
                )
            ) { Text("创建分享") }
        },
        dismissButton = {
            TextButton(onClick = onDismiss) { Text("取消") }
        }
    )
}
