package dev.lumen.app.util

import android.content.Context
import android.net.Uri
import android.provider.OpenableColumns
import dev.lumen.app.data.ChunkState
import dev.lumen.app.data.DirectOfferRequest
import dev.lumen.app.data.TransferKind
import dev.lumen.app.data.TransferRecord
import java.io.File
import java.io.FileOutputStream
import java.security.MessageDigest
import java.util.UUID

data class PickedFile(
    val uri: Uri,
    val name: String,
    val mimeType: String,
    val size: Long,
    val cachedFile: File,
    val sha256: String
)

class FileStore(private val context: Context) {
    val root: File = File(context.filesDir, "lumen")
    val incomingRoot: File = File(root, "incoming")
    val cacheRoot: File = File(context.cacheDir, "outgoing")
    val receiveRoot: File = File(
        context.getExternalFilesDir(android.os.Environment.DIRECTORY_DOWNLOADS) ?: context.filesDir,
        "Lumen"
    )

    init {
        root.mkdirs()
        incomingRoot.mkdirs()
        cacheRoot.mkdirs()
        receiveRoot.mkdirs()
    }

    fun incomingDir(transferId: String): File =
        File(incomingRoot, transferId.replace("-", "")).apply { mkdirs() }

    fun clearIncoming(transferId: String) {
        incomingDir(transferId).deleteRecursively()
    }

    fun prepareForSending(uri: Uri): PickedFile {
        val resolver = context.contentResolver
        var name = "Lumen 文件"
        var size = -1L
        resolver.query(uri, null, null, null, null)?.use { cursor ->
            val nameIndex = cursor.getColumnIndex(OpenableColumns.DISPLAY_NAME)
            val sizeIndex = cursor.getColumnIndex(OpenableColumns.SIZE)
            if (cursor.moveToFirst()) {
                if (nameIndex >= 0) name = cursor.getString(nameIndex) ?: name
                if (sizeIndex >= 0 && !cursor.isNull(sizeIndex)) size = cursor.getLong(sizeIndex)
            }
        }
        val mime = resolver.getType(uri) ?: guessMime(name)
        val target = File(cacheRoot, UUID.randomUUID().toString().replace("-", ""))
        val digest = MessageDigest.getInstance("SHA-256")
        var total = 0L
        resolver.openInputStream(uri)?.use { input ->
            FileOutputStream(target).use { output ->
                val buffer = ByteArray(256 * 1024)
                while (true) {
                    val read = input.read(buffer)
                    if (read <= 0) break
                    output.write(buffer, 0, read)
                    digest.update(buffer, 0, read)
                    total += read
                }
            }
        } ?: error("无法读取所选文件。")
        if (size < 0) size = total
        val sha = digest.digest().joinToString("") { "%02x".format(it) }
        return PickedFile(uri, name, mime, size, target, sha)
    }

    fun prepareBytesForSending(bytes: ByteArray, name: String, mime: String): PickedFile {
        val target = File(cacheRoot, UUID.randomUUID().toString().replace("-", ""))
        target.writeBytes(bytes)
        val digest = MessageDigest.getInstance("SHA-256")
        val sha = digest.digest(bytes).joinToString("") { "%02x".format(it) }
        return PickedFile(Uri.fromFile(target), name, mime, bytes.size.toLong(), target, sha)
    }

    fun sha256(file: File): String {
        val digest = MessageDigest.getInstance("SHA-256")
        file.inputStream().use { input ->
            val buffer = ByteArray(256 * 1024)
            while (true) {
                val read = input.read(buffer)
                if (read <= 0) break
                digest.update(buffer, 0, read)
            }
        }
        return digest.digest().joinToString("") { "%02x".format(it) }
    }

    fun mergeChunks(record: TransferRecord, chunks: List<ChunkState>): File {
        val sourceDir = incomingDir(record.id)
        val finalFile = uniqueReceiveFile(record.name)
        val temp = File(finalFile.parentFile, finalFile.name + ".lumenpart")
        val digest = MessageDigest.getInstance("SHA-256")
        try {
            FileOutputStream(temp).use { output ->
                val buffer = ByteArray(1024 * 1024)
                chunks.sortedBy { it.index }.forEach { chunk ->
                    val chunkFile = File(sourceDir, "%08d.chunk".format(chunk.index))
                    chunkFile.inputStream().use { input ->
                        while (true) {
                            val read = input.read(buffer)
                            if (read <= 0) break
                            digest.update(buffer, 0, read)
                            output.write(buffer, 0, read)
                        }
                    }
                }
                output.fd.sync()
            }
            val sha = digest.digest().joinToString("") { "%02x".format(it) }
            if (!record.sha256.isNullOrBlank() && !sha.equals(record.sha256, ignoreCase = true)) {
                throw IllegalStateException("文件整体 SHA-256 校验失败，内容可能已损坏。")
            }
            if (!temp.renameTo(finalFile)) {
                temp.copyTo(finalFile, overwrite = true)
                temp.delete()
            }
            clearIncoming(record.id)
            return finalFile
        } catch (error: Exception) {
            temp.delete()
            throw error
        }
    }

    fun uniqueReceiveFile(name: String): File {
        val safe = sanitizeName(name.ifBlank { "Lumen 文件" })
        var candidate = File(receiveRoot, safe)
        if (!candidate.exists()) return candidate
        val dot = safe.lastIndexOf('.')
        val base = if (dot > 0) safe.substring(0, dot) else safe
        val ext = if (dot > 0) safe.substring(dot) else ""
        var index = 1
        while (candidate.exists()) {
            candidate = File(receiveRoot, "$base ($index)$ext")
            index++
        }
        return candidate
    }

    fun deleteCached(file: File?) {
        runCatching { file?.takeIf { it.parentFile == cacheRoot }?.delete() }
    }

    fun findReceivedFile(record: TransferRecord): File? {
        record.localPath?.let { path -> File(path).takeIf { it.exists() }?.let { return it } }
        val direct = File(receiveRoot, sanitizeName(record.name))
        return direct.takeIf { it.exists() }
    }

    fun toOffer(
        picked: PickedFile,
        deviceId: String,
        deviceName: String,
        kind: String,
        chunkSize: Int,
        textContent: String? = null
    ): DirectOfferRequest = DirectOfferRequest(
        senderDeviceId = deviceId,
        senderName = deviceName,
        senderPlatform = "android",
        kind = kind,
        name = picked.name,
        mimeType = picked.mimeType,
        size = picked.size,
        sha256 = picked.sha256,
        chunkSize = if (kind == TransferKind.TEXT) 0 else chunkSize,
        textContent = textContent,
        createdAt = java.time.Instant.now().toString()
    )

    private fun sanitizeName(name: String): String {
        val cleaned = name
            .replace(Regex("""[\\/:*?"<>|\u0000-\u001f]"""), "_")
            .trim()
            .trimEnd('.')
            .take(180)
        return cleaned.ifBlank { "Lumen 文件" }
    }

    private fun guessMime(name: String): String {
        val ext = name.substringAfterLast('.', "").lowercase()
        return when (ext) {
            "jpg", "jpeg" -> "image/jpeg"
            "png" -> "image/png"
            "gif" -> "image/gif"
            "webp" -> "image/webp"
            "pdf" -> "application/pdf"
            "txt", "md", "log" -> "text/plain"
            "zip" -> "application/zip"
            "mp4" -> "video/mp4"
            "mp3" -> "audio/mpeg"
            "apk" -> "application/vnd.android.package-archive"
            else -> "application/octet-stream"
        }
    }
}
