package dev.lumen.app.util

import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import java.util.concurrent.TimeUnit

fun formatBytes(bytes: Long): String {
    if (bytes < 0) return "—"
    if (bytes < 1024) return "$bytes B"
    val units = arrayOf("KiB", "MiB", "GiB", "TiB")
    var value = bytes.toDouble()
    var unit = -1
    while (value >= 1024 && unit < units.lastIndex) {
        value /= 1024
        unit++
    }
    return if (value >= 100) {
        String.format(Locale.US, "%.0f %s", value, units[unit])
    } else if (value >= 10) {
        String.format(Locale.US, "%.1f %s", value, units[unit])
    } else {
        String.format(Locale.US, "%.2f %s", value, units[unit])
    }
}

fun formatSpeed(bps: Double?): String {
    if (bps == null || bps <= 0) return "—"
    return "${formatBytes(bps.toLong())}/s"
}

private fun parseIso(value: String?): Date? {
    if (value.isNullOrBlank()) return null
    return runCatching {
        val cleaned = value.replace(Regex("""\.\d+"""), "")
        val parser = SimpleDateFormat("yyyy-MM-dd'T'HH:mm:ss", Locale.US)
        parser.timeZone = java.util.TimeZone.getTimeZone("UTC")
        parser.parse(cleaned)
    }.getOrNull()
}

fun formatTime(value: String?): String {
    val date = parseIso(value) ?: return "—"
    return SimpleDateFormat("MM-dd HH:mm", Locale.getDefault()).format(date)
}

fun formatTime(millis: Long): String =
    SimpleDateFormat("MM-dd HH:mm:ss", Locale.getDefault()).format(Date(millis))

fun parseMillis(value: String?): Long? {
    if (value.isNullOrBlank()) return null
    return runCatching { java.time.Instant.parse(value).toEpochMilli() }
        .getOrElse { parseIso(value)?.time }
}

fun formatDuration(startMillis: Long, endMillis: Long? = null): String {
    val millis = ((endMillis ?: System.currentTimeMillis()) - startMillis).coerceAtLeast(0)
    val seconds = TimeUnit.MILLISECONDS.toSeconds(millis)
    return when {
        seconds < 60 -> "${seconds}s"
        seconds < 3600 -> "${seconds / 60}m ${seconds % 60}s"
        else -> "${seconds / 3600}h ${(seconds % 3600) / 60}m"
    }
}

fun shortId(value: String?): String {
    if (value.isNullOrBlank()) return "—"
    return if (value.length > 8) value.take(8) else value
}

fun statusLabel(status: String): String = when (status) {
    "pending" -> "等待确认"
    "accepted" -> "已接受"
    "uploading" -> "传输中"
    "ready" -> "待接收"
    "completed" -> "已完成"
    "declined" -> "已拒绝"
    "cancelled" -> "已取消"
    "failed" -> "失败"
    else -> status
}

fun routeLabel(route: String): String =
    if (route == "lan") "局域网直连" else "服务器中转"

fun kindLabel(kind: String): String = when (kind) {
    "image" -> "图片"
    "text" -> "文字"
    else -> "文件"
}
