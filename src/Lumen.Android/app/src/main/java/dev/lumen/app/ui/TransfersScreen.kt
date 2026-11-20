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
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Cancel
import androidx.compose.material.icons.filled.Check
import androidx.compose.material.icons.filled.Close
import androidx.compose.material.icons.filled.ContentCopy
import androidx.compose.material.icons.filled.Description
import androidx.compose.material.icons.filled.Download
import androidx.compose.material.icons.filled.History
import androidx.compose.material.icons.filled.Image
import androidx.compose.material.icons.filled.OpenInNew
import androidx.compose.material.icons.filled.TextFields
import androidx.compose.material.icons.filled.Upload
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalClipboardManager
import androidx.compose.ui.text.AnnotatedString
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import dev.lumen.app.data.TransferKind
import dev.lumen.app.data.TransferRecord
import dev.lumen.app.data.TransferStatus
import dev.lumen.app.util.formatBytes
import dev.lumen.app.util.formatDuration
import dev.lumen.app.util.formatSpeed
import dev.lumen.app.util.formatTime
import dev.lumen.app.util.kindLabel
import dev.lumen.app.util.routeLabel
import dev.lumen.app.util.shortId
import dev.lumen.app.util.statusLabel

@OptIn(ExperimentalMaterial3Api::class, ExperimentalLayoutApi::class)
@Composable
fun TransfersScreen(
    transfers: List<TransferRecord>,
    onAccept: (TransferRecord) -> Unit,
    onDecline: (TransferRecord) -> Unit,
    onCancel: (TransferRecord) -> Unit,
    onDownload: (TransferRecord) -> Unit,
    onSelect: (TransferRecord) -> Unit,
    onOpenFile: (TransferRecord) -> Unit,
    onClearHistory: () -> Unit
) {
    var filter by rememberSaveable { mutableStateOf("all") }
    var selectedId by rememberSaveable { mutableStateOf<String?>(null) }
    val selected = selectedId?.let { id -> transfers.firstOrNull { it.id == id } }
    val filtered = when (filter) {
        "out" -> transfers.filter { it.direction == "out" }
        "in" -> transfers.filter { it.direction == "in" }
        "active" -> transfers.filter { !TransferStatus.isTerminal(it.status) }
        else -> transfers
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
                    Text("传输", style = MaterialTheme.typography.headlineMedium)
                    Spacer(Modifier.height(3.dp))
                    Text(
                        "每一块、每一跳都有记录",
                        color = LumenTextMuted,
                        fontSize = 13.sp
                    )
                }
                if (transfers.isNotEmpty()) {
                    TextButton(onClick = onClearHistory) {
                        Text("清空", fontSize = 12.sp, color = LumenTextMuted)
                    }
                }
            }
        }
        item {
            FlowRow(
                horizontalArrangement = Arrangement.spacedBy(8.dp),
                verticalArrangement = Arrangement.spacedBy(8.dp)
            ) {
                FilterPill("全部", filter == "all") { filter = "all" }
                FilterPill("进行中", filter == "active") { filter = "active" }
                FilterPill("我发送", filter == "out") { filter = "out" }
                FilterPill("我接收", filter == "in") { filter = "in" }
            }
        }
        if (filtered.isEmpty()) {
            item {
                EmptyState(
                    icon = Icons.Filled.History,
                    title = "暂无传输记录",
                    description = "从设备页发送文件或文字，这里会保留完整的链路、分块和速度记录。"
                )
            }
        } else {
            items(filtered, key = { it.id }) { record ->
                TransferCard(
                    record = record,
                    onClick = {
                        selectedId = record.id
                        onSelect(record)
                    },
                    onAccept = { onAccept(record) },
                    onDecline = { onDecline(record) },
                    onCancel = { onCancel(record) },
                    onDownload = { onDownload(record) },
                    onOpenFile = { onOpenFile(record) }
                )
            }
        }
    }

    if (selected != null) {
        val sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true)
        ModalBottomSheet(
            onDismissRequest = { selectedId = null },
            sheetState = sheetState,
            containerColor = LumenSurface,
            dragHandle = {
                Box(
                    Modifier
                        .padding(top = 10.dp, bottom = 6.dp)
                        .size(width = 40.dp, height = 4.dp)
                        .clip(CircleShape)
                        .background(LumenBorder)
                )
            }
        ) {
            TransferDetails(
                record = selected,
                onAccept = {
                    onAccept(selected)
                    selectedId = null
                },
                onDecline = {
                    onDecline(selected)
                    selectedId = null
                },
                onCancel = { onCancel(selected) },
                onDownload = { onDownload(selected) },
                onOpenFile = { onOpenFile(selected) }
            )
        }
    }
}

@Composable
private fun FilterPill(text: String, selected: Boolean, onClick: () -> Unit) {
    Box(
        modifier = Modifier
            .clip(CircleShape)
            .background(if (selected) LumenPrimary.copy(alpha = 0.16f) else LumenSurfaceHigh.copy(alpha = 0.7f))
            .clickable(onClick = onClick)
            .padding(horizontal = 14.dp, vertical = 7.dp)
    ) {
        Text(
            text,
            fontSize = 12.sp,
            color = if (selected) LumenPrimary else LumenTextMuted,
            fontWeight = if (selected) FontWeight.SemiBold else FontWeight.Normal
        )
    }
}

fun statusColor(status: String): Color = when (status) {
    TransferStatus.COMPLETED -> LumenSuccess
    TransferStatus.FAILED, TransferStatus.DECLINED -> LumenDanger
    TransferStatus.UPLOADING, TransferStatus.ACCEPTED -> LumenSecondary
    TransferStatus.READY -> LumenTertiary
    TransferStatus.PENDING -> LumenWarning
    else -> LumenTextMuted
}

@Composable
private fun TransferCard(
    record: TransferRecord,
    onClick: () -> Unit,
    onAccept: () -> Unit,
    onDecline: () -> Unit,
    onCancel: () -> Unit,
    onDownload: () -> Unit,
    onOpenFile: () -> Unit
) {
    val accent = statusColor(record.status)
    LumenCard(
        Modifier
            .fillMaxWidth()
            .clickable(onClick = onClick)
    ) {
        Column {
            Row(verticalAlignment = Alignment.CenterVertically) {
                Box(
                    modifier = Modifier
                        .size(42.dp)
                        .background(accent.copy(alpha = 0.13f), RoundedCornerShape(13.dp)),
                    contentAlignment = Alignment.Center
                ) {
                    Icon(
                        when {
                            record.direction == "in" -> Icons.Filled.Download
                            record.kind == TransferKind.TEXT -> Icons.Filled.TextFields
                            record.kind == TransferKind.IMAGE -> Icons.Filled.Image
                            else -> Icons.Filled.Upload
                        },
                        null,
                        tint = accent,
                        modifier = Modifier.size(21.dp)
                    )
                }
                Spacer(Modifier.width(11.dp))
                Column(Modifier.weight(1f)) {
                    Text(
                        record.name,
                        style = MaterialTheme.typography.titleMedium,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis
                    )
                    Spacer(Modifier.height(3.dp))
                    Text(
                        buildString {
                            append(if (record.direction == "in") "来自 " else "发往 ")
                            append(record.peerName)
                            record.peerPlatform?.takeIf { it.isNotBlank() }?.let {
                                append(" · ")
                                append(platformLabelSafe(it))
                            }
                        },
                        fontSize = 11.5.sp,
                        color = LumenTextMuted,
                        maxLines = 1,
                        overflow = TextOverflow.Ellipsis
                    )
                }
                Column(horizontalAlignment = Alignment.End) {
                    TagChip(statusLabel(record.status), accent)
                    Spacer(Modifier.height(4.dp))
                    Text(
                        if (record.route == "lan") "局域网" else "中转",
                        fontSize = 10.5.sp,
                        color = if (record.route == "lan") LumenPrimary else LumenSecondary
                    )
                }
            }
            if (!TransferStatus.isTerminal(record.status) || record.status == TransferStatus.READY) {
                Spacer(Modifier.height(11.dp))
                ProgressLine(record.progress)
            }
            Spacer(Modifier.height(10.dp))
            Row(
                modifier = Modifier.fillMaxWidth(),
                verticalAlignment = Alignment.CenterVertically
            ) {
                Text(
                    if (record.size > 0) {
                        "${formatBytes(record.transferredBytes)} / ${formatBytes(record.size)}"
                    } else "—",
                    fontSize = 11.sp,
                    color = LumenTextMuted
                )
                Spacer(Modifier.width(12.dp))
                if (!TransferStatus.isTerminal(record.status) && record.speedBps > 0) {
                    Text(
                        formatSpeed(record.speedBps),
                        fontSize = 11.sp,
                        color = accent
                    )
                }
                Spacer(Modifier.weight(1f))
                Text(formatTime(record.createdAt), fontSize = 10.5.sp, color = LumenTextMuted)
            }
            if (record.error != null && record.status == TransferStatus.FAILED) {
                Spacer(Modifier.height(6.dp))
                Text(record.error, color = LumenDanger, fontSize = 11.sp, maxLines = 2)
            }

            if (record.isIncoming && record.status == TransferStatus.PENDING) {
                Spacer(Modifier.height(11.dp))
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    Button(
                        onClick = onAccept,
                        modifier = Modifier
                            .weight(1f)
                            .height(38.dp),
                        shape = RoundedCornerShape(11.dp),
                        colors = ButtonDefaults.buttonColors(
                            containerColor = LumenPrimary,
                            contentColor = LumenBackgroundDeep
                        )
                    ) {
                        Icon(Icons.Filled.Check, null, modifier = Modifier.size(16.dp))
                        Spacer(Modifier.width(5.dp))
                        Text("接受", fontSize = 12.sp)
                    }
                    OutlinedButton(
                        onClick = onDecline,
                        modifier = Modifier
                            .weight(1f)
                            .height(38.dp),
                        shape = RoundedCornerShape(11.dp)
                    ) {
                        Icon(Icons.Filled.Close, null, modifier = Modifier.size(15.dp))
                        Spacer(Modifier.width(5.dp))
                        Text("拒绝", fontSize = 12.sp)
                    }
                }
            } else if (record.isIncoming && record.status == TransferStatus.READY) {
                Spacer(Modifier.height(11.dp))
                Button(
                    onClick = onDownload,
                    modifier = Modifier
                        .fillMaxWidth()
                        .height(38.dp),
                    shape = RoundedCornerShape(11.dp),
                    colors = ButtonDefaults.buttonColors(
                        containerColor = LumenPrimary,
                        contentColor = LumenBackgroundDeep
                    )
                ) {
                    Icon(Icons.Filled.Download, null, modifier = Modifier.size(16.dp))
                    Spacer(Modifier.width(5.dp))
                    Text("下载到本机", fontSize = 12.sp)
                }
            } else if (!TransferStatus.isTerminal(record.status)) {
                Spacer(Modifier.height(9.dp))
                TextButton(onClick = onCancel, modifier = Modifier.align(Alignment.End)) {
                    Icon(Icons.Filled.Cancel, null, modifier = Modifier.size(15.dp), tint = LumenDanger)
                    Spacer(Modifier.width(4.dp))
                    Text("取消", fontSize = 11.sp, color = LumenDanger)
                }
            } else if (record.status == TransferStatus.COMPLETED &&
                record.kind != TransferKind.TEXT && record.localPath != null
            ) {
                Spacer(Modifier.height(9.dp))
                TextButton(onClick = onOpenFile, modifier = Modifier.align(Alignment.End)) {
                    Icon(Icons.Filled.OpenInNew, null, modifier = Modifier.size(15.dp))
                    Spacer(Modifier.width(4.dp))
                    Text("打开文件", fontSize = 11.sp)
                }
            }
        }
    }
}

private fun platformLabelSafe(platform: String): String = when (platform.lowercase()) {
    "windows" -> "Windows"
    "android" -> "Android"
    "linux" -> "Linux"
    "macos" -> "macOS"
    else -> platform
}

@Composable
fun ProgressLine(progress: Float) {
    Box(
        modifier = Modifier
            .fillMaxWidth()
            .height(6.dp)
            .clip(CircleShape)
            .background(LumenSurfaceSoft)
    ) {
        Box(
            modifier = Modifier
                .fillMaxWidth(progress.coerceIn(0f, 1f))
                .height(6.dp)
                .clip(CircleShape)
                .background(Brush.horizontalGradient(listOf(LumenSecondary, LumenPrimary)))
        )
    }
}

@OptIn(ExperimentalLayoutApi::class)
@Composable
private fun TransferDetails(
    record: TransferRecord,
    onAccept: () -> Unit,
    onDecline: () -> Unit,
    onCancel: () -> Unit,
    onDownload: () -> Unit,
    onOpenFile: () -> Unit
) {
    val clipboard = LocalClipboardManager.current
    val accent = statusColor(record.status)
    Column(
        modifier = Modifier
            .fillMaxWidth()
            .verticalScroll(rememberScrollState())
            .padding(horizontal = 20.dp)
            .padding(bottom = 34.dp)
    ) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Box(
                modifier = Modifier
                    .size(46.dp)
                    .background(accent.copy(alpha = 0.14f), RoundedCornerShape(14.dp)),
                contentAlignment = Alignment.Center
            ) {
                Icon(
                    when (record.kind) {
                        TransferKind.TEXT -> Icons.Filled.TextFields
                        TransferKind.IMAGE -> Icons.Filled.Image
                        else -> Icons.Filled.Description
                    },
                    null,
                    tint = accent,
                    modifier = Modifier.size(23.dp)
                )
            }
            Spacer(Modifier.width(12.dp))
            Column(Modifier.weight(1f)) {
                Text(record.name, style = MaterialTheme.typography.titleLarge, maxLines = 2)
                Spacer(Modifier.height(4.dp))
                Text(
                    "${kindLabel(record.kind)} · ${formatBytes(record.size)}",
                    color = LumenTextMuted,
                    fontSize = 12.sp
                )
            }
            TagChip(statusLabel(record.status), accent)
        }
        Spacer(Modifier.height(14.dp))
        Row(horizontalArrangement = Arrangement.spacedBy(7.dp)) {
            TagChip(routeLabel(record.route), if (record.route == "lan") LumenPrimary else LumenSecondary)
            record.ipFamily?.let { TagChip(it, if (it == "IPv6") LumenSecondary else LumenTertiary) }
            TagChip(if (record.direction == "in") "接收" else "发送", LumenTextMuted)
            if (record.sha256 != null) {
                TagChip("SHA-256 已校验", LumenSuccess)
            }
        }
        if (!record.error.isNullOrBlank()) {
            Spacer(Modifier.height(10.dp))
            Text(
                record.error,
                color = if (record.status == TransferStatus.FAILED) LumenDanger else LumenWarning,
                fontSize = 11.5.sp,
                lineHeight = 16.sp
            )
        }
        if (!TransferStatus.isTerminal(record.status) || record.status == TransferStatus.READY) {
            Spacer(Modifier.height(14.dp))
            ProgressLine(record.progress)
            Spacer(Modifier.height(6.dp))
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween) {
                Text(
                    "${formatBytes(record.transferredBytes)} / ${formatBytes(record.size)}",
                    fontSize = 11.sp,
                    color = LumenTextMuted
                )
                Text(
                    "${(record.progress * 100).toInt()}%",
                    fontSize = 11.sp,
                    color = accent,
                    fontWeight = FontWeight.SemiBold
                )
            }
        }

        if (record.kind == TransferKind.TEXT && !record.textContent.isNullOrBlank()) {
            Spacer(Modifier.height(16.dp))
            LumenCard(Modifier.fillMaxWidth()) {
                Column {
                    Row(
                        modifier = Modifier.fillMaxWidth(),
                        horizontalArrangement = Arrangement.SpaceBetween,
                        verticalAlignment = Alignment.CenterVertically
                    ) {
                        Text("文字内容", style = MaterialTheme.typography.titleMedium)
                        TextButton(onClick = {
                            clipboard.setText(AnnotatedString(record.textContent.orEmpty()))
                        }) {
                            Icon(Icons.Filled.ContentCopy, null, modifier = Modifier.size(15.dp))
                            Spacer(Modifier.width(4.dp))
                            Text("复制", fontSize = 11.sp)
                        }
                    }
                    Text(
                        record.textContent.orEmpty(),
                        style = MaterialTheme.typography.bodyLarge,
                        color = LumenText
                    )
                }
            }
        }

        Spacer(Modifier.height(16.dp))
        LumenCard(Modifier.fillMaxWidth()) {
            Column {
                Text("传输参数", style = MaterialTheme.typography.titleMedium)
                Spacer(Modifier.height(8.dp))
                InfoRow("传输 ID", record.id)
                InfoRow("分块", "${record.completedChunks} / ${record.totalChunks} 块")
                InfoRow("分块大小", if (record.chunkSize > 0) formatBytes(record.chunkSize.toLong()) else "—")
                InfoRow("平均速度", formatSpeed(record.averageSpeedBps ?: record.speedBps))
                InfoRow("对端设备", "${record.peerName} (${shortId(record.peerDeviceId)})")
                InfoRow("IPv4/IPv6", record.ipFamily ?: "—")
                InfoRow("本地地址", record.localAddress ?: "—")
                InfoRow("远端地址", record.remoteAddress ?: "—")
                InfoRow("开始时间", formatTime(record.createdAt))
                InfoRow(
                    "完成时间",
                    record.completedAt?.let { formatTime(it) } ?: "—"
                )
                InfoRow("耗时", formatDuration(record.createdAt, record.completedAt))
                if (!record.sha256.isNullOrBlank()) {
                    Spacer(Modifier.height(5.dp))
                    Row(
                        modifier = Modifier.fillMaxWidth(),
                        horizontalArrangement = Arrangement.SpaceBetween,
                        verticalAlignment = Alignment.CenterVertically
                    ) {
                        Text("SHA-256", fontSize = 12.sp, color = LumenTextMuted)
                        TextButton(onClick = {
                            clipboard.setText(AnnotatedString(record.sha256.orEmpty()))
                        }) {
                            Text("复制", fontSize = 11.sp)
                        }
                    }
                    Text(
                        record.sha256 ?: "",
                        fontSize = 10.sp,
                        color = LumenTextMuted,
                        lineHeight = 15.sp
                    )
                }
                if (record.localPath != null) {
                    InfoRow("保存位置", record.localPath!!)
                }
            }
        }

        if (record.chunks.isNotEmpty()) {
            Spacer(Modifier.height(14.dp))
            LumenCard(Modifier.fillMaxWidth()) {
                Column {
                    Text("分块状态", style = MaterialTheme.typography.titleMedium)
                    Spacer(Modifier.height(3.dp))
                    Text(
                        "绿色为已完成，蓝色为进行中，灰色为待处理",
                        fontSize = 11.sp,
                        color = LumenTextMuted
                    )
                    Spacer(Modifier.height(11.dp))
                    FlowRow(
                        horizontalArrangement = Arrangement.spacedBy(4.dp),
                        verticalArrangement = Arrangement.spacedBy(4.dp)
                    ) {
                        record.chunks.take(240).forEach { chunk ->
                            val done = chunk.uploaded || chunk.downloaded
                            Box(
                                modifier = Modifier
                                    .size(14.dp)
                                    .clip(RoundedCornerShape(4.dp))
                                    .background(
                                        if (done) LumenPrimary.copy(alpha = 0.7f)
                                        else LumenSurfaceSoft
                                    )
                            )
                        }
                    }
                    if (record.chunks.size > 240) {
                        Spacer(Modifier.height(5.dp))
                        Text("另有 ${record.chunks.size - 240} 块未展示", fontSize = 11.sp, color = LumenTextMuted)
                    }
                }
            }
        }

        Spacer(Modifier.height(14.dp))
        LumenCard(Modifier.fillMaxWidth()) {
            Column {
                Text("事件记录", style = MaterialTheme.typography.titleMedium)
                Spacer(Modifier.height(10.dp))
                if (record.events.isEmpty()) {
                    Text("暂无事件", color = LumenTextMuted, fontSize = 12.sp)
                } else {
                    record.events.sortedBy { it.at }.forEach { event ->
                        Row(Modifier.padding(vertical = 5.dp)) {
                            Box(
                                Modifier
                                    .padding(top = 5.dp)
                                    .size(7.dp)
                                    .clip(CircleShape)
                                    .background(accent.copy(alpha = 0.8f))
                            )
                            Spacer(Modifier.width(10.dp))
                            Column(Modifier.weight(1f)) {
                                Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween) {
                                    Text(
                                        event.type,
                                        fontSize = 11.sp,
                                        fontWeight = FontWeight.SemiBold,
                                        color = LumenText
                                    )
                                    Text(formatTime(event.at), fontSize = 10.sp, color = LumenTextMuted)
                                }
                                event.message?.let {
                                    Text(it, fontSize = 11.5.sp, color = LumenTextMuted, lineHeight = 16.sp)
                                }
                                val details = buildList {
                                    event.bytes?.let { add(formatBytes(it)) }
                                    event.speedBps?.let { add(formatSpeed(it)) }
                                    event.address?.let { add(it) }
                                    event.ipFamily?.let { add(it) }
                                }
                                if (details.isNotEmpty()) {
                                    Text(
                                        details.joinToString(" · "),
                                        fontSize = 10.sp,
                                        color = LumenTextMuted.copy(alpha = 0.8f)
                                    )
                                }
                            }
                        }
                    }
                }
            }
        }

        Spacer(Modifier.height(18.dp))
        when {
            record.isIncoming && record.status == TransferStatus.PENDING -> Row(
                horizontalArrangement = Arrangement.spacedBy(9.dp)
            ) {
                Button(
                    onClick = onAccept,
                    modifier = Modifier
                        .weight(1f)
                        .height(46.dp),
                    shape = RoundedCornerShape(13.dp)
                ) { Text("接受传输") }
                OutlinedButton(
                    onClick = onDecline,
                    modifier = Modifier
                        .weight(1f)
                        .height(46.dp),
                    shape = RoundedCornerShape(13.dp)
                ) { Text("拒绝") }
            }
            record.isIncoming && record.status == TransferStatus.READY -> Button(
                onClick = onDownload,
                modifier = Modifier
                    .fillMaxWidth()
                    .height(46.dp),
                shape = RoundedCornerShape(13.dp)
            ) { Text("下载到本机") }
            !TransferStatus.isTerminal(record.status) -> OutlinedButton(
                onClick = onCancel,
                modifier = Modifier
                    .fillMaxWidth()
                    .height(46.dp),
                shape = RoundedCornerShape(13.dp)
            ) { Text("取消传输", color = LumenDanger) }
            record.status == TransferStatus.COMPLETED &&
                record.kind != TransferKind.TEXT && record.localPath != null -> Button(
                onClick = onOpenFile,
                modifier = Modifier
                    .fillMaxWidth()
                    .height(46.dp),
                shape = RoundedCornerShape(13.dp)
            ) {
                Icon(Icons.Filled.OpenInNew, null, modifier = Modifier.size(17.dp))
                Spacer(Modifier.width(6.dp))
                Text("打开文件")
            }
        }
    }
}
