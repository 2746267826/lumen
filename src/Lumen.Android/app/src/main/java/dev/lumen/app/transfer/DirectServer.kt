package dev.lumen.app.transfer

import android.os.Handler
import android.os.Looper
import dev.lumen.app.data.AppStore
import dev.lumen.app.data.ChunkState
import dev.lumen.app.data.ChunkUploadResultDto
import dev.lumen.app.data.DirectOfferRequest
import dev.lumen.app.data.DirectOfferResponse
import dev.lumen.app.data.DirectOfferStatus
import dev.lumen.app.data.DirectPingResponse
import dev.lumen.app.data.LumenJson
import dev.lumen.app.data.SettingsStore
import dev.lumen.app.data.TransferEventRecord
import dev.lumen.app.data.TransferKind
import dev.lumen.app.data.TransferRecord
import dev.lumen.app.data.TransferRoute
import dev.lumen.app.data.TransferStatus
import dev.lumen.app.data.familyOf
import dev.lumen.app.discovery.LanDiscovery
import dev.lumen.app.util.FileStore
import fi.iki.elonen.NanoHTTPD
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.serialization.encodeToString
import java.io.File
import java.io.FileOutputStream
import java.net.ServerSocket
import java.security.MessageDigest
import java.security.SecureRandom
import java.util.Base64
import java.util.concurrent.ConcurrentHashMap

class DirectServer(
    private val settings: SettingsStore,
    private val store: AppStore,
    private val files: FileStore,
    private val discovery: LanDiscovery
) {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val main = Handler(Looper.getMainLooper())
    private val states = ConcurrentHashMap<String, IncomingState>()
    private var server: Impl? = null

    var port: Int = 0
        private set
    var lastError: String? = null
        private set

    var onOffer: ((TransferRecord) -> Unit)? = null
    var onChanged: ((TransferRecord) -> Unit)? = null

    fun start(): Int {
        if (server != null) return port
        val nextPort = findPort()
        return try {
            val instance = Impl(nextPort)
            instance.start(NanoHTTPD.SOCKET_READ_TIMEOUT, false)
            server = instance
            port = nextPort
            lastError = null
            nextPort
        } catch (error: Exception) {
            lastError = "局域网直连服务启动失败：${error.message}"
            0
        }
    }

    fun stop() {
        server?.stop()
        server = null
        states.clear()
    }

    fun accept(transferId: String) {
        val state = states[transferId] ?: return
        if (state.record.status != TransferStatus.PENDING) return
        val now = System.currentTimeMillis()
        val next = state.record.copy(
            status = TransferStatus.ACCEPTED,
            updatedAt = now,
            events = state.record.events + TransferEventRecord(
                at = now,
                type = "accepted",
                message = "已接受局域网传输邀请。",
                address = state.record.remoteAddress,
                ipFamily = state.record.ipFamily
            )
        )
        state.record = next
        store.upsertTransfer(next)
        notify(next)
    }

    fun decline(transferId: String) {
        val state = states[transferId] ?: return
        if (state.record.status != TransferStatus.PENDING) return
        val now = System.currentTimeMillis()
        val next = state.record.copy(
            status = TransferStatus.DECLINED,
            updatedAt = now,
            events = state.record.events + TransferEventRecord(
                at = now,
                type = "declined",
                message = "已拒绝局域网传输。"
            )
        )
        state.record = next
        store.upsertTransfer(next)
        notify(next)
    }

    private fun notify(record: TransferRecord) {
        main.post { onChanged?.invoke(record) }
    }

    private fun offerNotify(record: TransferRecord) {
        main.post { onOffer?.invoke(record) }
    }

    private fun findPort(): Int {
        for (candidate in 47655..47685) {
            runCatching {
                ServerSocket(candidate).use { }
                return candidate
            }
        }
        return 47686
    }

    private inner class Impl(port: Int) : NanoHTTPD(port) {
        override fun serve(session: IHTTPSession): Response {
            return try {
                route(session)
            } catch (error: DirectHttpException) {
                jsonError(error.status, error.code, error.message ?: "局域网请求失败。")
            } catch (error: Exception) {
                jsonError(500, "direct_server_error", error.message ?: "局域网直连服务处理失败。")
            }
        }
    }

    private fun route(session: NanoHTTPD.IHTTPSession): NanoHTTPD.Response {
        val uri = session.uri ?: ""
        val method = session.method
        if (method == NanoHTTPD.Method.GET && uri == "/lumen/v1/ping") {
            val addresses = discovery.localAddresses()
            val response = DirectPingResponse(
                service = "lumen",
                version = 1,
                deviceId = settings.deviceId,
                name = settings.deviceName,
                platform = "android",
                model = android.os.Build.MODEL,
                port = port,
                addresses = addresses,
                families = addresses.mapNotNull { familyOf(it) }.distinct()
            )
            return json(NanoHTTPD.Response.Status.OK, LumenJson.encodeToString(response))
        }

        if (method == NanoHTTPD.Method.POST && uri == "/lumen/v1/offers") {
            return createOffer(session)
        }

        if (method == NanoHTTPD.Method.GET && uri.startsWith("/lumen/v1/offers/")) {
            val id = uri.removePrefix("/lumen/v1/offers/")
            val state = states[id] ?: throw DirectHttpException(404, "not_found", "传输邀请不存在。")
            val response = DirectOfferStatus(
                transferId = id,
                status = state.record.status,
                error = state.record.error,
                updatedAt = java.time.Instant.ofEpochMilli(state.record.updatedAt).toString()
            )
            return json(NanoHTTPD.Response.Status.OK, LumenJson.encodeToString(response))
        }

        if (method == NanoHTTPD.Method.PUT && uri.contains("/chunks/")) {
            val parts = uri.trim('/').split('/')
            if (parts.size >= 2 && parts[0] == "lumen" && parts[1] == "v1") {
                val id = parts.getOrNull(3) ?: throw DirectHttpException(400, "invalid_path", "路径无效。")
                val index = parts.getOrNull(5)?.toIntOrNull()
                    ?: throw DirectHttpException(400, "invalid_chunk", "分块序号无效。")
                return uploadChunk(session, id, index)
            }
        }

        if (method == NanoHTTPD.Method.POST && uri.endsWith("/complete")) {
            val parts = uri.trim('/').split('/')
            val id = parts.getOrNull(3) ?: throw DirectHttpException(400, "invalid_path", "路径无效。")
            return complete(session, id)
        }

        if (method == NanoHTTPD.Method.POST && uri.endsWith("/cancel")) {
            val parts = uri.trim('/').split('/')
            val id = parts.getOrNull(3) ?: throw DirectHttpException(400, "invalid_path", "路径无效。")
            return cancel(session, id)
        }

        throw DirectHttpException(404, "not_found", "接口不存在。")
    }

    private fun createOffer(session: NanoHTTPD.IHTTPSession): NanoHTTPD.Response {
        val body = readBody(session, 2 * 1024 * 1024)
        val offer = runCatching {
            LumenJson.decodeFromString(DirectOfferRequest.serializer(), body)
        }.getOrNull() ?: throw DirectHttpException(400, "invalid_offer", "传输邀请格式无效。")

        if (offer.senderDeviceId.isBlank() || offer.senderDeviceId == settings.deviceId) {
            throw DirectHttpException(400, "invalid_offer", "传输邀请参数无效。")
        }
        if (offer.kind !in setOf(TransferKind.FILE, TransferKind.IMAGE, TransferKind.TEXT)) {
            throw DirectHttpException(400, "invalid_kind", "传输类型无效。")
        }
        if (offer.kind == TransferKind.TEXT) {
            if (offer.textContent.isNullOrEmpty() ||
                offer.textContent.toByteArray(Charsets.UTF_8).size > 1024 * 1024
            ) {
                throw DirectHttpException(400, "invalid_text", "文字内容为空或超过 1 MiB。")
            }
        } else if (offer.size < 0) {
            throw DirectHttpException(400, "invalid_size", "文件大小无效。")
        }

        val transferId = java.util.UUID.randomUUID().toString()
        val chunkSize = if (offer.kind == TransferKind.TEXT) {
            0
        } else {
            offer.chunkSize.coerceIn(64 * 1024, 8 * 1024 * 1024)
        }
        val totalChunks = if (offer.kind == TransferKind.TEXT || offer.size == 0L) {
            0
        } else {
            ((offer.size + chunkSize - 1) / chunkSize).toInt()
        }
        val remoteIp = session.remoteIpAddress ?: ""
        val localIp = discovery.localAddresses().firstOrNull { familyOf(it) == familyOf(remoteIp) }
            ?: discovery.localAddresses().firstOrNull()
        val now = System.currentTimeMillis()
        val size = if (offer.kind == TransferKind.TEXT) {
            offer.textContent!!.toByteArray(Charsets.UTF_8).size.toLong()
        } else {
            offer.size
        }
        val record = TransferRecord(
            id = transferId,
            direction = "in",
            route = TransferRoute.LAN,
            status = TransferStatus.PENDING,
            kind = offer.kind,
            name = offer.name.ifBlank { "Lumen 传输" },
            mimeType = offer.mimeType.ifBlank { "application/octet-stream" },
            size = size,
            sha256 = offer.sha256,
            chunkSize = chunkSize,
            totalChunks = totalChunks,
            peerName = offer.senderName.ifBlank { "局域网设备" },
            peerDeviceId = offer.senderDeviceId,
            peerPlatform = offer.senderPlatform,
            remoteAddress = remoteIp,
            localAddress = localIp,
            ipFamily = familyOf(remoteIp),
            textContent = offer.textContent.takeIf { offer.kind == TransferKind.TEXT },
            createdAt = now,
            updatedAt = now,
            events = listOf(
                TransferEventRecord(
                    at = now,
                    type = "created",
                    message = "收到局域网直连传输邀请。",
                    address = remoteIp,
                    ipFamily = familyOf(remoteIp)
                ),
                TransferEventRecord(
                    at = now,
                    type = "route.selected",
                    message = "局域网直连 · ${familyOf(remoteIp) ?: "未知协议"}，不经过服务器。",
                    address = remoteIp,
                    ipFamily = familyOf(remoteIp)
                )
            ),
            chunks = if (totalChunks > 0) {
                val baseSize = chunkSize
                (0 until totalChunks).map { index ->
                    ChunkState(
                        index = index,
                        size = minOf(baseSize.toLong(), size - index.toLong() * baseSize).toInt()
                    )
                }
            } else emptyList()
        )
        val state = IncomingState(record, newToken(), files.incomingDir(transferId))
        states[transferId] = state
        store.upsertTransfer(record)
        offerNotify(record)
        scope.launch {
            delay(120_000)
            if (state.record.status == TransferStatus.PENDING) {
                val expired = state.record.copy(
                    status = TransferStatus.DECLINED,
                    updatedAt = System.currentTimeMillis(),
                    events = state.record.events + TransferEventRecord(
                        at = System.currentTimeMillis(),
                        type = "declined",
                        message = "传输邀请等待超时，已自动拒绝。"
                    )
                )
                state.record = expired
                store.upsertTransfer(expired)
                notify(expired)
            }
        }
        val response = DirectOfferResponse(transferId, TransferStatus.PENDING, state.uploadToken)
        return json(NanoHTTPD.Response.Status.OK, LumenJson.encodeToString(response))
    }

    private fun uploadChunk(
        session: NanoHTTPD.IHTTPSession,
        transferId: String,
        index: Int
    ): NanoHTTPD.Response {
        val state = states[transferId] ?: throw DirectHttpException(404, "not_found", "传输不存在。")
        requireToken(session, state)
        if (state.record.status != TransferStatus.ACCEPTED &&
            state.record.status != TransferStatus.UPLOADING
        ) {
            throw DirectHttpException(409, "invalid_state", "接收设备尚未接受传输。")
        }
        if (index < 0 || index >= state.record.totalChunks) {
            throw DirectHttpException(400, "invalid_chunk", "分块序号超出范围。")
        }
        val offset = index.toLong() * state.record.chunkSize
        val expected = minOf(state.record.chunkSize.toLong(), state.record.size - offset).toInt()
        val declared = session.headers["content-length"]?.toLongOrNull()
        if (declared != null && declared != expected.toLong()) {
            throw DirectHttpException(400, "chunk_size_mismatch", "分块长度与声明不一致。")
        }
        val directory = files.incomingDir(transferId)
        directory.mkdirs()
        val target = File(directory, "%08d.chunk".format(index))
        val temp = File(directory, "%08d.chunk.part".format(index))
        val expectedSha = session.headers["x-lumen-chunk-sha256"]
        val started = System.nanoTime()
        val digest = MessageDigest.getInstance("SHA-256")
        var total = 0
        try {
            val input = session.inputStream
            FileOutputStream(temp).use { output ->
                val buffer = ByteArray(128 * 1024)
                while (total < expected) {
                    val read = input.read(buffer, 0, minOf(buffer.size, expected - total))
                    if (read <= 0) break
                    digest.update(buffer, 0, read)
                    output.write(buffer, 0, read)
                    total += read
                }
                output.fd.sync()
            }
            if (total != expected) {
                throw DirectHttpException(
                    400,
                    "chunk_incomplete",
                    "分块应为 $expected 字节，实际收到 $total 字节。"
                )
            }
            val sha = digest.digest().joinToString("") { "%02x".format(it) }
            if (!expectedSha.isNullOrBlank() && !sha.equals(expectedSha, ignoreCase = true)) {
                throw DirectHttpException(400, "chunk_hash_mismatch", "分块哈希校验失败。")
            }
            if (target.exists()) target.delete()
            if (!temp.renameTo(target)) {
                temp.copyTo(target, overwrite = true)
                temp.delete()
            }
            val speed = total / ((System.nanoTime() - started) / 1_000_000_000.0).coerceAtLeast(0.001)
            synchronized(state) {
                state.uploaded[index] = total
                val chunks = state.record.chunks.map {
                    if (it.index == index) it.copy(uploaded = true, size = total, sha256 = sha, speedBps = speed, attempts = it.attempts + 1)
                    else it
                }
                val transferred = state.uploaded.values.sumOf { it.toLong() }
                val now = System.currentTimeMillis()
                val average = state.record.averageSpeedBps?.let { it * 0.65 + speed * 0.35 } ?: speed
                val next = state.record.copy(
                    status = TransferStatus.UPLOADING,
                    completedChunks = state.uploaded.size,
                    transferredBytes = transferred,
                    speedBps = speed,
                    averageSpeedBps = average,
                    updatedAt = now,
                    chunks = chunks,
                    events = state.record.events + TransferEventRecord(
                        at = now,
                        type = "chunk.downloaded",
                        message = "已接收分块 ${index + 1}/${state.record.totalChunks}。",
                        bytes = total.toLong(),
                        speedBps = speed,
                        address = state.record.remoteAddress,
                        ipFamily = state.record.ipFamily
                    )
                )
                state.record = next
                store.upsertTransfer(next)
                notify(next)
                return json(
                    NanoHTTPD.Response.Status.OK,
                    LumenJson.encodeToString(
                        ChunkUploadResultDto(
                            completedChunks = next.completedChunks,
                            totalChunks = next.totalChunks,
                            transferredBytes = next.transferredBytes,
                            speedBps = speed
                        )
                    )
                )
            }
        } finally {
            if (temp.exists()) runCatching { temp.delete() }
        }
    }

    private fun complete(session: NanoHTTPD.IHTTPSession, transferId: String): NanoHTTPD.Response {
        val state = states[transferId] ?: throw DirectHttpException(404, "not_found", "传输不存在。")
        requireToken(session, state)
        synchronized(state) {
            if (state.record.status == TransferStatus.COMPLETED) {
                return json(NanoHTTPD.Response.Status.OK, LumenJson.encodeToString(state.record))
            }
            if (state.record.status != TransferStatus.ACCEPTED &&
                state.record.status != TransferStatus.UPLOADING
            ) {
                throw DirectHttpException(409, "invalid_state", "当前状态不能完成传输。")
            }
            val isText = state.record.kind == TransferKind.TEXT
            val received = state.uploaded.values.sumOf { it.toLong() }
            if (!isText &&
                (state.uploaded.size != state.record.totalChunks || received != state.record.size)
            ) {
                throw DirectHttpException(409, "transfer_incomplete", "分块尚未收齐。")
            }
            var localPath: String? = null
            if (!isText) {
                localPath = mergeChunks(state)
            }
            val now = System.currentTimeMillis()
            val next = state.record.copy(
                status = TransferStatus.COMPLETED,
                completedChunks = if (isText) 0 else state.record.totalChunks,
                transferredBytes = state.record.size,
                completedAt = now,
                updatedAt = now,
                localPath = localPath,
                events = state.record.events + listOf(
                    TransferEventRecord(
                        at = now,
                        type = if (isText) "completed" else "merged",
                        message = if (isText) "文字已接收。" else "分块已按顺序合并，SHA-256 校验通过。",
                        bytes = state.record.size,
                        address = state.record.remoteAddress,
                        ipFamily = state.record.ipFamily
                    ),
                    TransferEventRecord(
                        at = now,
                        type = "completed",
                        message = "局域网传输完成。",
                        address = state.record.remoteAddress,
                        ipFamily = state.record.ipFamily
                    )
                )
            )
            state.record = next
            store.upsertTransfer(next)
            notify(next)
            files.clearIncoming(transferId)
            return json(NanoHTTPD.Response.Status.OK, LumenJson.encodeToString(next))
        }
    }

    private fun mergeChunks(state: IncomingState): String {
        val record = state.record
        val finalFile = files.uniqueReceiveFile(record.name)
        val temp = File(finalFile.parentFile, finalFile.name + ".lumenpart")
        val digest = MessageDigest.getInstance("SHA-256")
        try {
            FileOutputStream(temp).use { output ->
                val buffer = ByteArray(1024 * 1024)
                for (index in 0 until record.totalChunks) {
                    val chunk = File(state.tempDir, "%08d.chunk".format(index))
                    if (!chunk.exists()) {
                        throw DirectHttpException(409, "missing_chunk", "分块 $index 缺失。")
                    }
                    chunk.inputStream().use { input ->
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
                throw DirectHttpException(422, "file_hash_mismatch", "文件整体 SHA-256 校验失败。")
            }
            if (!temp.renameTo(finalFile)) {
                temp.copyTo(finalFile, overwrite = true)
                temp.delete()
            }
            return finalFile.absolutePath
        } catch (error: Exception) {
            runCatching { temp.delete() }
            throw error
        }
    }

    private fun cancel(session: NanoHTTPD.IHTTPSession, transferId: String): NanoHTTPD.Response {
        val state = states[transferId] ?: throw DirectHttpException(404, "not_found", "传输不存在。")
        requireToken(session, state)
        if (!TransferStatus.isTerminal(state.record.status)) {
            val now = System.currentTimeMillis()
            val next = state.record.copy(
                status = TransferStatus.CANCELLED,
                updatedAt = now,
                events = state.record.events + TransferEventRecord(
                    at = now,
                    type = "cancelled",
                    message = "发送设备取消了局域网传输。"
                )
            )
            state.record = next
            store.upsertTransfer(next)
            notify(next)
        }
        return json(NanoHTTPD.Response.Status.OK, LumenJson.encodeToString(state.record))
    }

    private fun requireToken(session: NanoHTTPD.IHTTPSession, state: IncomingState) {
        val token = session.headers["x-lumen-upload-token"].orEmpty()
        if (token.isEmpty() ||
            !MessageDigest.isEqual(token.toByteArray(), state.uploadToken.toByteArray())
        ) {
            throw DirectHttpException(403, "upload_token_invalid", "上传凭证无效。")
        }
    }

    private fun readBody(session: NanoHTTPD.IHTTPSession, max: Int): String {
        val length = session.headers["content-length"]?.toIntOrNull() ?: 0
        if (length <= 0) return ""
        if (length > max) throw DirectHttpException(413, "body_too_large", "请求内容过大。")
        val buffer = ByteArray(length)
        var total = 0
        val input = session.inputStream
        while (total < length) {
            val read = input.read(buffer, total, length - total)
            if (read <= 0) break
            total += read
        }
        return String(buffer, 0, total, Charsets.UTF_8)
    }

    private fun json(status: NanoHTTPD.Response.Status, body: String): NanoHTTPD.Response =
        NanoHTTPD.newFixedLengthResponse(status, "application/json; charset=utf-8", body).apply {
            addHeader("Cache-Control", "no-store")
        }

    private fun jsonError(status: Int, code: String, message: String): NanoHTTPD.Response =
        json(
            NanoHTTPD.Response.Status.lookup(status) ?: NanoHTTPD.Response.Status.INTERNAL_ERROR,
            """{"code":${quote(code)},"message":${quote(message)}}"""
        )

    private fun quote(value: String): String =
        "\"" + value.replace("\\", "\\\\").replace("\"", "\\\"").replace("\n", "\\n") + "\""

    private fun newToken(): String {
        val bytes = ByteArray(24)
        SecureRandom().nextBytes(bytes)
        return Base64.getUrlEncoder().withoutPadding().encodeToString(bytes)
    }

    private class IncomingState(
        @Volatile var record: TransferRecord,
        val uploadToken: String,
        val tempDir: File
    ) {
        val uploaded = mutableMapOf<Int, Int>()
    }

    private class DirectHttpException(
        val status: Int,
        val code: String,
        message: String
    ) : Exception(message)
}
