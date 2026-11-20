package dev.lumen.app.transfer

import dev.lumen.app.data.AppStore
import dev.lumen.app.data.ChunkState
import dev.lumen.app.data.DeviceDto
import dev.lumen.app.data.DirectOfferRequest
import dev.lumen.app.data.DirectPingResponse
import dev.lumen.app.data.LumenJson
import dev.lumen.app.data.PeerDevice
import dev.lumen.app.data.SettingsStore
import dev.lumen.app.data.TransferCreateRequest
import dev.lumen.app.data.TransferDetailsDto
import dev.lumen.app.data.TransferDto
import dev.lumen.app.data.TransferEventDto
import dev.lumen.app.data.TransferEventRecord
import dev.lumen.app.data.TransferKind
import dev.lumen.app.data.TransferRecord
import dev.lumen.app.data.TransferRoute
import dev.lumen.app.data.TransferStatus
import dev.lumen.app.data.familyOf
import dev.lumen.app.discovery.LanDiscovery
import dev.lumen.app.discovery.LanEvent
import dev.lumen.app.net.ApiClient
import dev.lumen.app.net.ApiException
import dev.lumen.app.net.RelayMessage
import dev.lumen.app.net.RelaySocket
import dev.lumen.app.util.FileStore
import dev.lumen.app.util.PickedFile
import dev.lumen.app.util.parseMillis
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withTimeoutOrNull
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.decodeFromJsonElement
import java.io.File
import java.io.RandomAccessFile
import java.security.MessageDigest
import java.util.concurrent.ConcurrentHashMap

class TransferException(message: String) : Exception(message)

private class LanFailure(
    message: String,
    val allowRelayFallback: Boolean
) : Exception(message)

class TransferCoordinator(
    private val api: ApiClient,
    private val settings: SettingsStore,
    private val store: AppStore,
    private val files: FileStore,
    private val relay: RelaySocket,
    private val discovery: LanDiscovery,
    val directServer: DirectServer
) {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val _messages = MutableSharedFlow<String>(extraBufferCapacity = 32)
    val messages: SharedFlow<String> = _messages.asSharedFlow()

    private val downloading = ConcurrentHashMap.newKeySet<String>()
    private val autoDownload = ConcurrentHashMap.newKeySet<String>()
    private val scopeMutex = Mutex()
    private var started = false

    init {
        directServer.onChanged = { record -> store.upsertTransfer(record) }
        directServer.onOffer = { record ->
            store.upsertTransfer(record)
            _messages.tryEmit("收到来自 ${record.peerName} 的局域网传输邀请")
            val bound = store.peers.value
                .firstOrNull { it.id == record.peerDeviceId }
                ?.lastSeenAt
                ?.isNotBlank() == true
            if (settings.autoAccept && bound && record.status == TransferStatus.PENDING) {
                directServer.accept(record.id)
            }
        }
        scope.launch {
            relay.messages.collect { handleRelay(it) }
        }
        scope.launch {
            discovery.events.collect { event ->
                when (event) {
                    is LanEvent.PeerSeen -> {
                        val packet = event.peer.packet
                        store.markPeerSeenLan(packet, settings.userId)
                    }
                    is LanEvent.PeerLost -> store.markLanPeerLost(event.deviceId)
                    is LanEvent.Error -> _messages.tryEmit(event.message)
                }
            }
        }
    }

    suspend fun start() {
        scopeMutex.withLock {
            if (started) {
                relay.connect()
                return
            }
            started = true
            val port = directServer.start()
            if (port > 0 && settings.lanDiscovery) discovery.start(port)
            relay.connect()
        }
        refresh()
    }

    fun stop() {
        relay.disconnect()
        discovery.stop()
        directServer.stop()
        started = false
    }

    fun restartLan() {
        directServer.stop()
        discovery.stop()
        if (!settings.lanDiscovery) return
        val port = directServer.start()
        if (port > 0) discovery.start(port)
    }

    suspend fun refresh() {
        if (!settings.isLoggedIn) return
        runCatching {
            val devices = api.devices()
            store.setRelayDevices(devices)
            val page = api.transfers(120)
            page.items.forEach { dto ->
                applyServerTransfer(dto)
            }
            refreshShares()
        }.onFailure { error ->
            if (error !is CancellationException) {
                _messages.tryEmit("同步服务器数据失败：${friendly(error)}")
            }
        }
    }

    suspend fun refreshShares() {
        if (!settings.isLoggedIn) return
        runCatching { store.setShares(api.shares()) }
    }

    suspend fun loadDetails(record: TransferRecord) {
        val serverId = record.serverTransferId ?: record.id
        if (record.route != TransferRoute.RELAY) return
        runCatching {
            val details = api.transferDetails(serverId)
            applyServerDetails(details)
        }.onFailure { _messages.tryEmit("读取传输详情失败：${friendly(it)}") }
    }

    suspend fun send(
        peer: PeerDevice,
        picked: PickedFile,
        kind: String,
        textContent: String? = null
    ): TransferRecord {
        if (peer.id == settings.deviceId) throw TransferException("不能向当前设备发送。")
        if (kind != TransferKind.TEXT && picked.size < 0) {
            throw TransferException("无法读取文件大小。")
        }
        if (peer.lanOnline && settings.lanDiscovery && directServer.port > 0) {
            try {
                return tryLanTransfer(peer, picked, kind, textContent)
            } catch (failure: LanFailure) {
                if (!failure.allowRelayFallback) {
                    throw TransferException(failure.message ?: "局域网传输已被拒绝。")
                }
                _messages.tryEmit("局域网直连失败，自动切换服务器中转：${failure.message}")
            }
        }
        if (!peer.online) {
            throw TransferException("设备不在线，且当前不在同一局域网。")
        }
        if (relay.state.value != dev.lumen.app.net.RelayState.CONNECTED) {
            throw TransferException("未连接服务器，暂时无法中转。")
        }
        return sendRelay(peer, picked, kind, textContent)
    }

    suspend fun sendText(peer: PeerDevice, text: String): TransferRecord {
        if (text.isBlank()) throw TransferException("文字内容不能为空。")
        val bytes = text.toByteArray(Charsets.UTF_8)
        if (bytes.size > 1024 * 1024) throw TransferException("文字消息不能超过 1 MiB。")
        val picked = files.prepareBytesForSending(
            bytes,
            "文字消息-${System.currentTimeMillis()}.txt",
            "text/plain; charset=utf-8"
        )
        return try {
            send(peer, picked, TransferKind.TEXT, text)
        } finally {
            files.deleteCached(picked.cachedFile)
        }
    }

    suspend fun acceptIncoming(record: TransferRecord) {
        if (!record.isIncoming) return
        if (record.route == TransferRoute.LAN) {
            directServer.accept(record.id)
            return
        }
        val id = record.serverTransferId ?: record.id
        val result = api.acceptTransfer(id)
        applyServerTransfer(result)
        autoDownload.add(id)
        _messages.tryEmit("已接受传输，等待发送设备上传。")
        scope.launch { waitForReady(id) }
    }

    suspend fun declineIncoming(record: TransferRecord) {
        if (!record.isIncoming) return
        if (record.route == TransferRoute.LAN) {
            directServer.decline(record.id)
            return
        }
        val id = record.serverTransferId ?: record.id
        runCatching { applyServerTransfer(api.declineTransfer(id)) }
    }

    suspend fun cancel(record: TransferRecord) {
        if (TransferStatus.isTerminal(record.status)) return
        if (record.route == TransferRoute.LAN) {
            if (record.direction == "out" && !record.directBase.isNullOrBlank() &&
                !record.uploadToken.isNullOrBlank()
            ) {
                api.directCancel(record.directBase, record.id, record.uploadToken)
            }
            store.updateTransfer(record.id) {
                it.copy(
                    status = TransferStatus.CANCELLED,
                    updatedAt = System.currentTimeMillis(),
                    error = "已取消",
                    events = it.events + TransferEventRecord(
                        type = "cancelled",
                        message = "传输已取消。"
                    )
                )
            }
            return
        }
        val id = record.serverTransferId ?: record.id
        runCatching {
            val dto = api.cancelTransfer(id)
            applyServerTransfer(dto)
        }.onFailure { _messages.tryEmit("取消失败：${friendly(it)}") }
    }

    suspend fun downloadAgain(record: TransferRecord) {
        if (record.route == TransferRoute.LAN) {
            _messages.tryEmit("该记录为局域网直连接收，文件已保存。")
            return
        }
        downloadRelay(record)
    }

    suspend fun beginDownload(record: TransferRecord) {
        if (record.route == TransferRoute.LAN) {
            _messages.tryEmit("局域网传输由发送端直接推送，无需手动下载。")
            return
        }
        if (record.status == TransferStatus.PENDING) {
            acceptIncoming(record)
            return
        }
        val id = record.serverTransferId ?: record.id
        autoDownload.add(id)
        if (record.status == TransferStatus.READY) {
            downloadRelay(record)
        } else {
            _messages.tryEmit("已加入接收队列，内容就绪后自动下载。")
            waitForReady(id)
        }
    }

    suspend fun createTextShare(
        name: String,
        text: String,
        hours: Int,
        password: String?,
        maxDownloads: Int
    ) = api.createTextShare(name, text, hours, password, maxDownloads).also { store.upsertShare(it) }

    suspend fun createFileShare(
        picked: PickedFile,
        name: String,
        hours: Int,
        password: String?,
        maxDownloads: Int,
        onProgress: (Long, Long) -> Unit
    ) = api.createFileShare(picked, name, hours, password, maxDownloads, onProgress)
        .also { store.upsertShare(it) }

    suspend fun revokeShare(id: String) {
        api.revokeShare(id)
        store.removeShare(id)
    }

    private suspend fun tryLanTransfer(
        peer: PeerDevice,
        picked: PickedFile,
        kind: String,
        textContent: String?
    ): TransferRecord {
        val endpoint = probePeer(peer)
            ?: throw LanFailure("未找到可用的局域网直连地址。", true)
        val offer = DirectOfferRequest(
            senderDeviceId = settings.deviceId,
            senderName = settings.deviceName,
            senderPlatform = "android",
            kind = kind,
            name = picked.name,
            mimeType = picked.mimeType,
            size = picked.size,
            sha256 = picked.sha256,
            chunkSize = if (kind == TransferKind.TEXT) 0 else settings.chunkSize,
            textContent = textContent,
            createdAt = java.time.Instant.now().toString()
        )
        val offerResponse = try {
            withTimeoutOrNull(20_000) { api.sendDirectOffer(endpoint.base, offer) }
                ?: throw LanFailure("局域网设备响应超时。", true)
        } catch (error: Exception) {
            if (error is CancellationException) throw error
            throw LanFailure("发送局域网邀请失败：${friendly(error)}", true)
        }

        val now = System.currentTimeMillis()
        val totalChunks = if (kind == TransferKind.TEXT || picked.size == 0L) {
            0
        } else {
            ((picked.size + settings.chunkSize - 1) / settings.chunkSize).toInt()
        }
        var record = TransferRecord(
            id = offerResponse.transferId,
            direction = "out",
            route = TransferRoute.LAN,
            status = TransferStatus.PENDING,
            kind = kind,
            name = picked.name,
            mimeType = picked.mimeType,
            size = picked.size,
            sha256 = picked.sha256,
            chunkSize = if (kind == TransferKind.TEXT) 0 else settings.chunkSize,
            totalChunks = totalChunks,
            peerName = peer.name,
            peerDeviceId = peer.id,
            peerPlatform = peer.platform,
            remoteAddress = endpoint.address,
            localAddress = discovery.localAddresses().firstOrNull { familyOf(it) == endpoint.family },
            ipFamily = endpoint.family,
            textContent = textContent,
            uploadToken = offerResponse.uploadToken,
            directBase = endpoint.base,
            createdAt = now,
            updatedAt = now,
            events = listOf(
                TransferEventRecord(
                    at = now,
                    type = "route.probe",
                    message = "已探测到局域网设备 ${endpoint.address}:${peer.lanPort}。",
                    address = endpoint.address,
                    ipFamily = endpoint.family
                ),
                TransferEventRecord(
                    at = now,
                    type = "route.selected",
                    message = "选择局域网直连 · ${endpoint.family}，不经服务器。",
                    address = endpoint.address,
                    ipFamily = endpoint.family
                ),
                TransferEventRecord(
                    at = now,
                    type = "created",
                    message = "局域网传输邀请已发送，等待接收设备确认。",
                    address = endpoint.address,
                    ipFamily = endpoint.family
                )
            ),
            chunks = if (totalChunks > 0) {
                (0 until totalChunks).map { index ->
                    val offset = index.toLong() * settings.chunkSize
                    ChunkState(
                        index = index,
                        size = minOf(settings.chunkSize.toLong(), picked.size - offset).toInt()
                    )
                }
            } else emptyList()
        )
        store.upsertTransfer(record)

        val accepted = waitForDirectAccept(endpoint.base, record)
        record = accepted
        if (record.status == TransferStatus.DECLINED) {
            throw LanFailure("对方拒绝了局域网传输。", false)
        }
        if (record.status != TransferStatus.ACCEPTED) {
            throw LanFailure("等待对方确认超时。", true)
        }

        if (kind == TransferKind.TEXT) {
            withTimeoutOrNull(30_000) { api.directComplete(endpoint.base, record.id, offerResponse.uploadToken) }
                ?: throw LanFailure("局域网完成回执超时。", true)
            val completed = record.copy(
                status = TransferStatus.COMPLETED,
                transferredBytes = record.size,
                completedAt = System.currentTimeMillis(),
                updatedAt = System.currentTimeMillis(),
                events = record.events + TransferEventRecord(
                    type = "completed",
                    message = "文字已通过局域网直连送达。",
                    address = endpoint.address,
                    ipFamily = endpoint.family
                )
            )
            store.upsertTransfer(completed)
            return completed
        }

        record = uploadDirectChunks(endpoint.base, record, offerResponse.uploadToken, picked.cachedFile)
        try {
            withTimeoutOrNull(60_000) {
                api.directComplete(endpoint.base, record.id, offerResponse.uploadToken)
            } ?: throw LanFailure("局域网完成回执超时。", true)
        } catch (error: Exception) {
            if (error is CancellationException) throw error
            if (error is LanFailure) throw error
            throw LanFailure("局域网完成确认失败：${friendly(error)}", true)
        }
        val completed = record.copy(
            status = TransferStatus.COMPLETED,
            completedChunks = record.totalChunks,
            transferredBytes = record.size,
            completedAt = System.currentTimeMillis(),
            updatedAt = System.currentTimeMillis(),
            chunks = record.chunks.map { it.copy(uploaded = true) },
            events = record.events + TransferEventRecord(
                type = "completed",
                message = "文件已通过局域网直连送达，接收端已完成 SHA-256 校验。",
                address = endpoint.address,
                ipFamily = endpoint.family,
                bytes = record.size
            )
        )
        store.upsertTransfer(completed)
        files.deleteCached(picked.cachedFile)
        return completed
    }

    private suspend fun waitForDirectAccept(base: String, initial: TransferRecord): TransferRecord {
        var record = initial
        val deadline = System.currentTimeMillis() + 45_000
        while (System.currentTimeMillis() < deadline) {
            val status = runCatching { api.directOfferStatus(base, record.id) }.getOrNull()
            if (status != null) {
                when (status.status) {
                    TransferStatus.ACCEPTED -> {
                        val next = record.copy(
                            status = TransferStatus.ACCEPTED,
                            updatedAt = System.currentTimeMillis(),
                            events = record.events + TransferEventRecord(
                                type = "accepted",
                                message = "接收设备已接受局域网传输。"
                            )
                        )
                        store.upsertTransfer(next)
                        return next
                    }
                    TransferStatus.DECLINED -> {
                        val next = record.copy(
                            status = TransferStatus.DECLINED,
                            updatedAt = System.currentTimeMillis(),
                            error = status.error ?: "对方拒绝或等待超时。",
                            events = record.events + TransferEventRecord(
                                type = "declined",
                                message = "接收设备拒绝或邀请已超时。"
                            )
                        )
                        store.upsertTransfer(next)
                        return next
                    }
                    TransferStatus.FAILED -> {
                        throw LanFailure(status.error ?: "局域网设备处理失败。", true)
                    }
                }
            }
            delay(500)
        }
        val failed = record.copy(
            status = TransferStatus.FAILED,
            updatedAt = System.currentTimeMillis(),
            error = "等待确认超时",
            events = record.events + TransferEventRecord(
                type = "failed",
                message = "等待对方确认超时。"
            )
        )
        store.upsertTransfer(failed)
        return failed
    }

    private suspend fun uploadDirectChunks(
        base: String,
        initial: TransferRecord,
        token: String,
        file: File
    ): TransferRecord {
        var record = initial
        val started = System.currentTimeMillis()
        for (index in 0 until record.totalChunks) {
            val offset = index.toLong() * record.chunkSize
            val length = minOf(record.chunkSize.toLong(), record.size - offset).toInt()
            val data = readChunk(file, offset, length)
            val sha = sha256(data)
            var lastError: Exception? = null
            for (attempt in 1..3) {
                try {
                    val chunkStart = System.nanoTime()
                    val result = withTimeoutOrNull(180_000) {
                        api.directUploadChunk(base, record.id, index, token, data, sha)
                    } ?: throw TransferException("分块上传超时。")
                    val seconds = ((System.nanoTime() - chunkStart) / 1_000_000_000.0).coerceAtLeast(0.001)
                    val speed = data.size / seconds
                    val average = record.averageSpeedBps?.let { it * 0.65 + speed * 0.35 } ?: speed
                    val now = System.currentTimeMillis()
                    record = record.copy(
                        status = TransferStatus.UPLOADING,
                        completedChunks = result.completedChunks,
                        transferredBytes = result.transferredBytes,
                        speedBps = speed,
                        averageSpeedBps = average,
                        updatedAt = now,
                        chunks = record.chunks.map {
                            if (it.index == index) it.copy(
                                uploaded = true,
                                sha256 = sha,
                                speedBps = speed,
                                attempts = it.attempts + attempt
                            ) else it
                        },
                        events = record.events + TransferEventRecord(
                            at = now,
                            type = "chunk.uploaded",
                            message = "已发送分块 ${index + 1}/${record.totalChunks}。",
                            bytes = length.toLong(),
                            speedBps = speed,
                            address = record.remoteAddress,
                            ipFamily = record.ipFamily
                        )
                    )
                    store.upsertTransfer(record)
                    lastError = null
                    break
                } catch (error: Exception) {
                    if (error is CancellationException) throw error
                    lastError = error
                    delay(500L * attempt)
                }
            }
            if (lastError != null) {
                val failed = record.copy(
                    status = TransferStatus.FAILED,
                    updatedAt = System.currentTimeMillis(),
                    error = "分块 $index 发送失败",
                    events = record.events + TransferEventRecord(
                        type = "failed",
                        message = "分块 ${index + 1} 发送失败：${friendly(lastError!!)}"
                    )
                )
                store.upsertTransfer(failed)
                api.directCancel(base, record.id, token)
                throw LanFailure("局域网分块发送失败：${friendly(lastError!!)}", true)
            }
        }
        val elapsed = (System.currentTimeMillis() - started).coerceAtLeast(1)
        return record.copy(
            averageSpeedBps = record.averageSpeedBps
                ?: (record.size * 1000.0 / elapsed)
        )
    }

    private suspend fun sendRelay(
        peer: PeerDevice,
        picked: PickedFile,
        kind: String,
        textContent: String?
    ): TransferRecord {
        val request = TransferCreateRequest(
            recipientDeviceId = peer.id,
            kind = kind,
            name = picked.name,
            mimeType = picked.mimeType,
            size = if (kind == TransferKind.TEXT) picked.size else picked.size,
            sha256 = if (kind == TransferKind.TEXT) null else picked.sha256,
            chunkSize = if (kind == TransferKind.TEXT) 0 else settings.chunkSize,
            textContent = textContent
        )
        val created = try {
            api.createTransfer(request)
        } catch (error: ApiException) {
            throw TransferException(error.message)
        }
        var record = applyServerTransfer(created).copy(
            events = listOf(
                TransferEventRecord(
                    type = "route.selected",
                    message = "选择服务器中转，等待接收设备确认。"
                )
            )
        )
        store.upsertTransfer(record)
        _messages.tryEmit("已发送传输邀请，等待 ${peer.name} 确认。")

        val accepted = waitForRelayAccept(created.id)
        record = accepted
        if (record.status != TransferStatus.ACCEPTED &&
            record.status != TransferStatus.UPLOADING
        ) {
            throw TransferException(
                when (record.status) {
                    TransferStatus.DECLINED -> "接收设备拒绝了传输。"
                    TransferStatus.CANCELLED -> "传输已取消。"
                    else -> "等待接收设备确认超时。"
                }
            )
        }

        if (kind == TransferKind.TEXT) {
            val ready = api.readyTransfer(created.id)
            val next = applyServerTransfer(ready).copy(
                transferredBytes = record.size,
                events = record.events + TransferEventRecord(
                    type = "ready",
                    message = "文字已上传到服务器中转。"
                )
            )
            store.upsertTransfer(next)
            _messages.tryEmit("文字已发送，等待接收设备收取。")
            return next
        }

        record = uploadRelayChunks(record, picked.cachedFile)
        val ready = try {
            api.readyTransfer(created.id)
        } catch (error: Exception) {
            val failed = record.copy(
                status = TransferStatus.FAILED,
                error = "服务器确认失败：${friendly(error)}",
                updatedAt = System.currentTimeMillis(),
                events = record.events + TransferEventRecord(
                    type = "failed",
                    message = "服务器未能确认文件完整：${friendly(error)}"
                )
            )
            store.upsertTransfer(failed)
            throw TransferException(failed.error ?: "服务器确认失败。")
        }
        val readyRecord = applyServerTransfer(ready).copy(
            events = record.events + TransferEventRecord(
                type = "ready",
                message = "文件已完整上传到服务器中转。"
            )
        )
        store.upsertTransfer(readyRecord)
        files.deleteCached(picked.cachedFile)
        _messages.tryEmit("文件已上传，等待接收设备下载。")
        return readyRecord
    }

    private suspend fun waitForRelayAccept(id: String): TransferRecord {
        val deadline = System.currentTimeMillis() + 5 * 60_000
        while (System.currentTimeMillis() < deadline) {
            val existing = store.transfer(id)
            if (existing != null &&
                (existing.status == TransferStatus.ACCEPTED ||
                    existing.status == TransferStatus.UPLOADING)
            ) {
                return existing
            }
            if (existing != null && TransferStatus.isTerminal(existing.status)) return existing
            runCatching {
                val dto = api.transferDetails(id).transfer
                val next = applyServerTransfer(dto)
                if (next.status == TransferStatus.ACCEPTED ||
                    next.status == TransferStatus.UPLOADING ||
                    TransferStatus.isTerminal(next.status)
                ) {
                    return next
                }
            }
            delay(1500)
        }
        val timedOut = store.transfer(id)
        if (timedOut != null && timedOut.status == TransferStatus.PENDING) {
            runCatching { api.cancelTransfer(id) }
            store.updateTransfer(id) {
                it.copy(
                    status = TransferStatus.CANCELLED,
                    error = "等待接收设备确认超时",
                    updatedAt = System.currentTimeMillis(),
                    events = it.events + TransferEventRecord(
                        type = "cancelled",
                        message = "等待接收设备确认超时，已自动取消。"
                    )
                )
            }
        }
        return store.transfer(id) ?: throw TransferException("传输记录丢失。")
    }

    private suspend fun uploadRelayChunks(
        initial: TransferRecord,
        file: File
    ): TransferRecord {
        var record = initial
        val started = System.currentTimeMillis()
        for (index in 0 until record.totalChunks) {
            val offset = index.toLong() * record.chunkSize
            val length = minOf(record.chunkSize.toLong(), record.size - offset).toInt()
            val data = readChunk(file, offset, length)
            val sha = sha256(data)
            var lastError: Exception? = null
            for (attempt in 1..3) {
                try {
                    val chunkStart = System.nanoTime()
                    val result = api.uploadChunk(record.id, index, data, sha)
                    val seconds = ((System.nanoTime() - chunkStart) / 1_000_000_000.0).coerceAtLeast(0.001)
                    val speed = result.speedBps ?: (data.size / seconds)
                    val average = record.averageSpeedBps?.let { it * 0.65 + speed * 0.35 } ?: speed
                    val now = System.currentTimeMillis()
                    record = record.copy(
                        status = TransferStatus.UPLOADING,
                        completedChunks = result.completedChunks,
                        transferredBytes = result.transferredBytes,
                        speedBps = speed,
                        averageSpeedBps = average,
                        updatedAt = now,
                        chunks = record.chunks.map {
                            if (it.index == index) it.copy(
                                uploaded = true,
                                sha256 = sha,
                                speedBps = speed,
                                attempts = it.attempts + attempt
                            ) else it
                        },
                        events = record.events + TransferEventRecord(
                            at = now,
                            type = "chunk.uploaded",
                            message = "服务器已接收分块 ${index + 1}/${record.totalChunks}。",
                            bytes = length.toLong(),
                            speedBps = speed,
                            address = record.remoteAddress,
                            ipFamily = record.ipFamily
                        )
                    )
                    store.upsertTransfer(record)
                    lastError = null
                    break
                } catch (error: Exception) {
                    if (error is CancellationException) throw error
                    lastError = error
                    delay(600L * attempt)
                }
            }
            if (lastError != null) {
                val failed = record.copy(
                    status = TransferStatus.FAILED,
                    updatedAt = System.currentTimeMillis(),
                    error = "上传分块 ${index + 1} 失败",
                    events = record.events + TransferEventRecord(
                        type = "failed",
                        message = "上传分块失败：${friendly(lastError!!)}"
                    )
                )
                store.upsertTransfer(failed)
                runCatching { api.cancelTransfer(record.id) }
                throw TransferException(failed.error ?: "上传失败。")
            }
        }
        val elapsed = (System.currentTimeMillis() - started).coerceAtLeast(1)
        return record.copy(
            averageSpeedBps = record.averageSpeedBps
                ?: (record.size * 1000.0 / elapsed)
        )
    }

    private suspend fun waitForReady(id: String) {
        val deadline = System.currentTimeMillis() + 10 * 60_000
        while (System.currentTimeMillis() < deadline) {
            val local = store.transfer(id)
            if (local != null && TransferStatus.isTerminal(local.status)) return
            val dto = runCatching { api.transferDetails(id).transfer }.getOrNull()
            if (dto != null) {
                val record = applyServerTransfer(dto)
                if (record.status == TransferStatus.READY) {
                    downloadRelay(record)
                    return
                }
                if (TransferStatus.isTerminal(record.status)) return
            }
            delay(1500)
        }
    }

    private suspend fun downloadRelay(record: TransferRecord) {
        val id = record.serverTransferId ?: record.id
        if (downloading.contains(id)) return
        if (TransferStatus.isTerminal(record.status)) return
        if (!downloading.add(id)) return
        try {
            if (record.kind == TransferKind.TEXT) {
                val completed = api.receivedTransfer(id)
                applyServerTransfer(completed)
                _messages.tryEmit("已接收文字消息。")
                return
            }
            val details = api.transferDetails(id).transfer
            if (details.status != TransferStatus.READY) {
                applyServerTransfer(details)
                _messages.tryEmit("内容尚未就绪，稍后会自动重试。")
                return
            }
            val chunkInfo = api.listChunks(id)
            if (chunkInfo.chunks.size != chunkInfo.totalChunks) {
                _messages.tryEmit("服务器分块尚未收齐（${chunkInfo.chunks.size}/${chunkInfo.totalChunks}）。")
                return
            }
            var local = store.transfer(id) ?: record
            val directory = files.incomingDir(id)
            directory.mkdirs()
            val chunks = chunkInfo.chunks.map { info ->
                ChunkState(
                    index = info.index,
                    size = info.size,
                    sha256 = info.sha256,
                    downloaded = File(directory, "%08d.chunk".format(info.index)).let {
                        it.exists() && it.length() == info.size.toLong()
                    }
                )
            }
            val started = System.currentTimeMillis()
            var transferred = chunks.filter { it.downloaded }.sumOf { it.size.toLong() }
            local = local.copy(
                status = TransferStatus.UPLOADING,
                completedChunks = chunks.count { it.downloaded },
                transferredBytes = transferred,
                totalChunks = chunkInfo.totalChunks,
                chunkSize = chunkInfo.chunkSize,
                chunks = chunks,
                updatedAt = System.currentTimeMillis()
            )
            store.upsertTransfer(local)
            for (info in chunkInfo.chunks) {
                val target = File(directory, "%08d.chunk".format(info.index))
                if (target.exists() && target.length() == info.size.toLong()) continue
                val chunkStart = System.nanoTime()
                val (sha, size) = api.downloadChunk(id, info.index, target)
                val seconds = ((System.nanoTime() - chunkStart) / 1_000_000_000.0).coerceAtLeast(0.001)
                val speed = size / seconds
                transferred += size
                val now = System.currentTimeMillis()
                local = local.copy(
                    completedChunks = local.completedChunks + 1,
                    transferredBytes = transferred,
                    speedBps = speed,
                    averageSpeedBps = local.averageSpeedBps?.let { it * 0.65 + speed * 0.35 } ?: speed,
                    updatedAt = now,
                    chunks = local.chunks.map {
                        if (it.index == info.index) it.copy(
                            downloaded = true,
                            sha256 = sha,
                            speedBps = speed
                        ) else it
                    },
                    events = local.events + TransferEventRecord(
                        at = now,
                        type = "chunk.downloaded",
                        message = "已下载分块 ${info.index + 1}/${chunkInfo.totalChunks}。",
                        bytes = size,
                        speedBps = speed,
                        address = local.remoteAddress,
                        ipFamily = local.ipFamily
                    )
                )
                store.upsertTransfer(local)
            }
            val finalFile = files.mergeChunks(local, local.chunks)
            val elapsed = (System.currentTimeMillis() - started).coerceAtLeast(1)
            local = local.copy(
                localPath = finalFile.absolutePath,
                averageSpeedBps = local.averageSpeedBps ?: (local.size * 1000.0 / elapsed),
                events = local.events + TransferEventRecord(
                    type = "merged",
                    message = "分块已合并并保存：${finalFile.name}",
                    bytes = local.size
                )
            )
            store.upsertTransfer(local)
            val completed = api.receivedTransfer(id)
            val next = applyServerTransfer(completed).copy(
                localPath = finalFile.absolutePath,
                events = local.events + TransferEventRecord(
                    type = "completed",
                    message = "文件已保存到 Lumen 接收目录。"
                )
            )
            store.upsertTransfer(next)
            _messages.tryEmit("已接收 ${record.name}")
        } catch (error: Exception) {
            if (error is CancellationException) throw error
            val current = store.transfer(id)
            if (current != null) {
                store.upsertTransfer(
                    current.copy(
                        status = if (current.status == TransferStatus.COMPLETED) {
                            current.status
                        } else TransferStatus.READY,
                        error = friendly(error),
                        updatedAt = System.currentTimeMillis(),
                        events = current.events + TransferEventRecord(
                            type = "download.failed",
                            message = "下载中断：${friendly(error)}"
                        )
                    )
                )
            }
            _messages.tryEmit("下载失败：${friendly(error)}")
        } finally {
            downloading.remove(id)
        }
    }

    private suspend fun probePeer(peer: PeerDevice): Endpoint? {
        if (peer.lanPort <= 0) return null
        val candidates = peer.lanAddresses
            .distinct()
            .sortedBy { if (it.contains(':')) 1 else 0 }
            .take(6)
            .mapNotNull { address ->
                if (address.isBlank()) null
                else Endpoint(
                    base = if (address.contains(':')) "http://[$address]:${peer.lanPort}"
                    else "http://$address:${peer.lanPort}",
                    address = address,
                    family = familyOf(address) ?: "未知"
                )
            }
        for (candidate in candidates) {
            val ping: DirectPingResponse? = withTimeoutOrNull(1800) {
                runCatching { api.directPing(candidate.base) }.getOrNull()
            }
            if (ping != null && ping.deviceId == peer.id) return candidate
        }
        return null
    }

    private suspend fun handleRelay(message: RelayMessage) {
        when (message.type) {
            "hello" -> {
                val devices = message.root["devices"]
                if (devices != null) {
                    runCatching {
                        val list = LumenJson.decodeFromJsonElement<List<DeviceDto>>(devices)
                        store.setRelayDevices(list)
                    }
                }
            }
            "device.online", "device.offline" -> {
                val online = message.type == "device.online"
                val deviceElement = message.root["device"]
                val dto = runCatching {
                    deviceElement?.let {
                        LumenJson.decodeFromJsonElement(DeviceDto.serializer(), it)
                    }
                }.getOrNull()
                if (dto != null) {
                    store.applyRelayPresence(dto, online)
                } else {
                    val id = message.string("deviceId")
                    if (!id.isNullOrBlank()) {
                        store.updatePeer(id) { it.copy(online = online) }
                    }
                }
            }
            "transfer.created" -> {
                val dto = decodeTransfer(message.root["transfer"]) ?: return
                val record = applyServerTransfer(dto)
                if (record.isIncoming && record.status == TransferStatus.PENDING) {
                    _messages.tryEmit("收到来自 ${record.peerName} 的传输邀请")
                    if (settings.autoAccept) {
                        scope.launch { runCatching { acceptIncoming(record) } }
                    }
                }
            }
            "transfer.accepted" -> {
                val dto = decodeTransfer(message.root["transfer"]) ?: return
                applyServerTransfer(dto)
            }
            "transfer.progress" -> {
                val dto = decodeTransfer(message.root["transfer"]) ?: return
                val event = decodeEvent(message.root["event"])
                applyServerTransfer(dto, event)
            }
            "transfer.ready" -> {
                val dto = decodeTransfer(message.root["transfer"]) ?: return
                val record = applyServerTransfer(dto)
                if (record.isIncoming &&
                    (autoDownload.contains(record.id) || settings.autoAccept)
                ) {
                    scope.launch { downloadRelay(record) }
                }
            }
            "transfer.completed" -> {
                val dto = decodeTransfer(message.root["transfer"])
                if (dto != null) applyServerTransfer(dto)
            }
            "transfer.declined", "transfer.cancelled" -> {
                val dto = decodeTransfer(message.root["transfer"])
                if (dto != null) {
                    applyServerTransfer(dto)
                } else {
                    val id = message.string("transferId") ?: return
                    store.updateTransfer(id) {
                        it.copy(
                            status = if (message.type == "transfer.declined") {
                                TransferStatus.DECLINED
                            } else TransferStatus.CANCELLED,
                            updatedAt = System.currentTimeMillis()
                        )
                    }
                }
            }
            "transfer.failed" -> {
                val dto = decodeTransfer(message.root["transfer"])
                if (dto != null) applyServerTransfer(dto)
            }
        }
    }

    private fun applyServerDetails(details: TransferDetailsDto) {
        val mapped = details.events.map { dto ->
            TransferEventRecord(
                at = parseMillis(dto.at) ?: System.currentTimeMillis(),
                type = dto.type,
                message = dto.message,
                address = dto.address,
                ipFamily = dto.ipFamily,
                bytes = dto.bytes,
                speedBps = dto.speedBps
            )
        }
        applyServerTransfer(details.transfer, events = mapped)
    }

    private fun decodeTransfer(element: JsonElement?): TransferDto? {
        if (element == null) return null
        return runCatching {
            LumenJson.decodeFromJsonElement(TransferDto.serializer(), element)
        }.getOrNull()
    }

    private fun decodeEvent(element: JsonElement?): TransferEventDto? {
        if (element == null) return null
        return runCatching {
            LumenJson.decodeFromJsonElement(TransferEventDto.serializer(), element)
        }.getOrNull()
    }

    private fun applyServerTransfer(
        dto: TransferDto,
        event: TransferEventDto? = null,
        events: List<TransferEventRecord>? = null
    ): TransferRecord {
        val existing = store.transfer(dto.id)
        val incoming = dto.recipientDeviceId == settings.deviceId
        val peerId = if (incoming) dto.senderDeviceId else dto.recipientDeviceId
        val peerPlatform = store.peers.value.firstOrNull { it.id == peerId }?.platform
        val created = parseMillis(dto.createdAt) ?: System.currentTimeMillis()
        val updated = parseMillis(dto.updatedAt) ?: created
        val mappedEvent = event?.let {
            TransferEventRecord(
                at = parseMillis(it.at) ?: System.currentTimeMillis(),
                type = it.type,
                message = it.message,
                address = it.address,
                ipFamily = it.ipFamily,
                bytes = it.bytes,
                speedBps = it.speedBps
            )
        }
        val mergedEvents = buildList {
            addAll(events ?: existing?.events.orEmpty())
            if (mappedEvent != null &&
                none { it.type == mappedEvent.type && kotlin.math.abs(it.at - mappedEvent.at) < 1500 }
            ) {
                add(mappedEvent)
            }
        }
        val chunks = if (dto.totalChunks > 0) {
            val total = dto.totalChunks
            val chunkSize = dto.chunkSize
            (0 until total).map { index ->
                val old = existing?.chunks?.firstOrNull { it.index == index }
                val size = minOf(chunkSize.toLong(), dto.size - index.toLong() * chunkSize)
                    .coerceAtLeast(0)
                    .toInt()
                old?.copy(size = if (old.size == 0) size else old.size)
                    ?: ChunkState(index = index, size = size)
            }
        } else emptyList()
        val record = TransferRecord(
            id = dto.id,
            direction = if (incoming) "in" else "out",
            route = dto.route.ifBlank { TransferRoute.RELAY },
            status = dto.status,
            kind = dto.kind,
            name = dto.name,
            mimeType = dto.mimeType,
            size = dto.size,
            sha256 = dto.sha256 ?: existing?.sha256,
            chunkSize = dto.chunkSize,
            totalChunks = dto.totalChunks,
            completedChunks = dto.completedChunks,
            transferredBytes = dto.transferredBytes,
            speedBps = existing?.speedBps ?: 0.0,
            averageSpeedBps = dto.averageSpeedBps ?: existing?.averageSpeedBps,
            peerName = if (incoming) dto.senderDeviceName else dto.recipientDeviceName,
            peerDeviceId = if (incoming) dto.senderDeviceId else dto.recipientDeviceId,
            peerPlatform = existing?.peerPlatform ?: peerPlatform,
            senderName = dto.senderDeviceName,
            recipientName = dto.recipientDeviceName,
            remoteAddress = if (incoming) dto.senderAddress else dto.recipientAddress,
            localAddress = existing?.localAddress,
            ipFamily = familyOf(if (incoming) dto.senderAddress else dto.recipientAddress),
            textContent = dto.textContent ?: existing?.textContent,
            localPath = existing?.localPath,
            error = dto.error,
            serverTransferId = dto.id,
            createdAt = created,
            updatedAt = updated,
            completedAt = parseMillis(dto.completedAt),
            events = mergedEvents,
            chunks = chunks
        )
        store.upsertTransfer(record)
        return record
    }

    private fun readChunk(file: File, offset: Long, length: Int): ByteArray {
        RandomAccessFile(file, "r").use { raf ->
            raf.seek(offset)
            val data = ByteArray(length)
            raf.readFully(data)
            return data
        }
    }

    private fun sha256(data: ByteArray): String =
        MessageDigest.getInstance("SHA-256").digest(data)
            .joinToString("") { "%02x".format(it) }

    private fun friendly(error: Throwable): String = when (error) {
        is ApiException -> error.message
        is TransferException -> error.message ?: "传输失败。"
        else -> error.message ?: error::class.simpleName ?: "未知错误"
    }

    private data class Endpoint(val base: String, val address: String, val family: String)
}
