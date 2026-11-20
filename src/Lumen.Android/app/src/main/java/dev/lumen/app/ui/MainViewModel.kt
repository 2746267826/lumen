package dev.lumen.app.ui

import android.app.Application
import android.net.Uri
import android.os.Build
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import dev.lumen.app.BuildConfig
import dev.lumen.app.LumenApplication
import dev.lumen.app.TransferForegroundService
import dev.lumen.app.data.PeerDevice
import dev.lumen.app.data.ShareDto
import dev.lumen.app.data.SettingsStore
import dev.lumen.app.data.TransferRecord
import dev.lumen.app.data.TransferStatus
import dev.lumen.app.data.platformLabel
import dev.lumen.app.net.RelayState
import dev.lumen.app.util.FileStore
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch

class MainViewModel(application: Application) : AndroidViewModel(application) {
    private val app = application as LumenApplication
    private val settings = app.settings
    private val store = app.store
    private val files = app.files
    private val coordinator = app.coordinator

    val peers: StateFlow<List<PeerDevice>> = store.peers
    val transfers: StateFlow<List<TransferRecord>> = store.transfers
    val shares: StateFlow<List<ShareDto>> = store.shares
    val relayState: StateFlow<RelayState> = app.relay.state
    val latencyMs: StateFlow<Long?> = app.relay.latencyMs
    val lanRunning: StateFlow<Boolean> = app.discovery.running
    val lanError: StateFlow<String?> = app.discovery.lastError
    val localAddresses: StateFlow<List<String>> = app.discovery.localAddresses

    private val _loggedIn = MutableStateFlow(settings.isLoggedIn)
    val loggedIn: StateFlow<Boolean> = _loggedIn.asStateFlow()

    private val _displayName = MutableStateFlow(settings.displayName.ifBlank { settings.username })
    val displayName: StateFlow<String> = _displayName.asStateFlow()

    private val _username = MutableStateFlow(settings.username)
    val username: StateFlow<String> = _username.asStateFlow()

    private val _serverUrl = MutableStateFlow(settings.serverUrl)
    val serverUrl: StateFlow<String> = _serverUrl.asStateFlow()

    private val _deviceName = MutableStateFlow(settings.deviceName)
    val deviceName: StateFlow<String> = _deviceName.asStateFlow()

    private val _autoAccept = MutableStateFlow(settings.autoAccept)
    val autoAccept: StateFlow<Boolean> = _autoAccept.asStateFlow()

    private val _lanDiscovery = MutableStateFlow(settings.lanDiscovery)
    val lanDiscovery: StateFlow<Boolean> = _lanDiscovery.asStateFlow()

    private val _chunkSize = MutableStateFlow(settings.chunkSize)
    val chunkSize: StateFlow<Int> = _chunkSize.asStateFlow()

    private val _busy = MutableStateFlow(false)
    val busy: StateFlow<Boolean> = _busy.asStateFlow()

    private val _busyText = MutableStateFlow<String?>(null)
    val busyText: StateFlow<String?> = _busyText.asStateFlow()

    private val _snackbar = MutableSharedFlow<String>(extraBufferCapacity = 32)
    val snackbar: SharedFlow<String> = _snackbar.asSharedFlow()

    val deviceId: String get() = settings.deviceId
    val appVersion: String get() = BuildConfig.VERSION_NAME
    val receivePath: String get() = files.receiveRoot.absolutePath

    init {
        viewModelScope.launch {
            coordinator.messages.collect { _snackbar.tryEmit(it) }
        }
        if (settings.isLoggedIn) {
            TransferForegroundService.start(app)
            viewModelScope.launch { runCatching { coordinator.start() } }
        }
    }

    fun login(
        rawServerUrl: String,
        user: String,
        password: String,
        register: Boolean,
        displayNameValue: String
    ) {
        if (user.isBlank() || password.isBlank()) {
            _snackbar.tryEmit("请输入用户名和密码。")
            return
        }
        runAction("正在连接服务器…") {
            settings.serverUrl = rawServerUrl
            val normalized = SettingsStore.normalizeServerUrl(rawServerUrl)
            if (normalized.isBlank()) throw IllegalArgumentException("请输入服务器地址。")
            val auth = if (register) {
                app.api.register(
                    user,
                    password,
                    displayNameValue.ifBlank { user }
                )
            } else {
                app.api.login(user, password)
            }
            settings.saveAuth(auth)
            val device = app.api.registerDevice(
                name = settings.deviceName,
                deviceId = settings.deviceId,
                model = Build.MODEL,
                appVersion = BuildConfig.VERSION_NAME
            )
            settings.deviceId = device.id
            TransferForegroundService.start(app)
            _serverUrl.value = settings.serverUrl
            _displayName.value = auth.user.displayName
            _username.value = auth.user.username
            _loggedIn.value = true
            coordinator.start()
            _snackbar.tryEmit(if (register) "账户已创建，设备已自动绑定。" else "登录成功，正在发现设备。")
        }
    }

    fun logout() {
        runAction("正在退出…") {
            runCatching { app.api.logout() }
            settings.clearAuth()
            coordinator.stop()
            TransferForegroundService.stop(app)
            store.setPeers(emptyList())
            store.setShares(emptyList())
            _loggedIn.value = false
            _displayName.value = ""
            _username.value = ""
        }
    }

    fun refresh() {
        if (!settings.isLoggedIn) return
        viewModelScope.launch {
            app.relay.connect()
            coordinator.refresh()
        }
    }

    fun reconnect() {
        viewModelScope.launch {
            runAction("正在重新连接…") {
                app.relay.disconnect()
                app.relay.connect()
                coordinator.refresh()
            }
        }
    }

    fun sendFiles(peer: PeerDevice, uris: List<Uri>, kind: String) {
        if (uris.isEmpty()) return
        runAction(if (kind == "image") "正在读取图片…" else "正在读取文件…") {
            var success = 0
            var failed = 0
            for (uri in uris) {
                try {
                    _busyText.value = "正在计算校验值…"
                    val picked = files.prepareForSending(uri)
                    _busyText.value = "正在发送 ${picked.name}"
                    coordinator.send(peer, picked, kind)
                    success++
                } catch (error: Exception) {
                    failed++
                    _snackbar.tryEmit("发送失败：${error.message ?: "未知错误"}")
                }
            }
            if (success > 0) {
                _snackbar.tryEmit("已发起 $success 个${if (kind == "image") "图片" else "文件"}传输")
            }
            if (failed > 0) {
                _snackbar.tryEmit("$failed 个文件发送失败")
            }
        }
    }

    fun sendText(peer: PeerDevice, text: String) {
        runAction("正在发送文字…") {
            coordinator.sendText(peer, text)
            _snackbar.tryEmit("文字传输已发起")
        }
    }

    fun accept(record: TransferRecord) {
        runAction(null) {
            coordinator.acceptIncoming(record)
        }
    }

    fun decline(record: TransferRecord) {
        runAction(null) {
            coordinator.declineIncoming(record)
        }
    }

    fun cancel(record: TransferRecord) {
        runAction(null) {
            coordinator.cancel(record)
        }
    }

    fun download(record: TransferRecord) {
        runAction("正在接收…") {
            coordinator.beginDownload(record)
        }
    }

    fun loadDetails(record: TransferRecord) {
        viewModelScope.launch { coordinator.loadDetails(record) }
    }

    fun createTextShare(
        name: String,
        text: String,
        hours: Int,
        password: String?,
        maxDownloads: Int
    ) {
        runAction("正在创建分享…") {
            val share = coordinator.createTextShare(name, text, hours, password, maxDownloads)
            _snackbar.tryEmit("分享已创建：${share.url}")
        }
    }

    fun refreshShares() {
        viewModelScope.launch {
            runCatching { coordinator.refreshShares() }
                .onFailure { _snackbar.tryEmit(it.message ?: "刷新分享失败。") }
        }
    }

    fun createFileShare(
        uri: Uri,
        name: String,
        hours: Int,
        password: String?,
        maxDownloads: Int
    ) {
        runAction("正在读取文件…") {
            val picked = files.prepareForSending(uri)
            val display = name.ifBlank { picked.name }
            _busyText.value = "正在上传分享文件…"
            val share = coordinator.createFileShare(picked, display, hours, password, maxDownloads) { sent, total ->
                val percent = if (total <= 0) 0 else (sent * 100 / total).toInt()
                _busyText.value = "正在上传分享文件 $percent%"
            }
            files.deleteCached(picked.cachedFile)
            _snackbar.tryEmit("分享已创建：${share.url}（$display）")
        }
    }

    fun revokeShare(share: ShareDto) {
        runAction("正在撤销分享…") {
            coordinator.revokeShare(share.id)
            _snackbar.tryEmit("分享已撤销")
        }
    }

    fun updateDeviceName(value: String) {
        val next = value.trim()
        if (next.isBlank()) return
        settings.deviceName = next
        _deviceName.value = settings.deviceName
        viewModelScope.launch {
            runCatching {
                if (settings.isLoggedIn) {
                    app.api.registerDevice(
                        settings.deviceName,
                        settings.deviceId,
                        Build.MODEL,
                        BuildConfig.VERSION_NAME
                    )
                }
            }
            coordinator.restartLan()
            _snackbar.tryEmit("设备名称已更新")
        }
    }

    fun setAutoAccept(value: Boolean) {
        settings.autoAccept = value
        _autoAccept.value = value
    }

    fun setLanDiscovery(value: Boolean) {
        settings.lanDiscovery = value
        _lanDiscovery.value = value
        coordinator.restartLan()
    }

    fun setChunkSize(value: Int) {
        settings.chunkSize = value
        _chunkSize.value = settings.chunkSize
    }

    fun clearHistory() {
        store.clearHistory()
        _snackbar.tryEmit("本地传输记录已清空")
    }

    fun openPathHint() {
        _snackbar.tryEmit("接收目录：$receivePath")
    }

    fun notify(message: String) {
        _snackbar.tryEmit(message)
    }

    fun peerSubtitle(peer: PeerDevice): String {
        val tags = mutableListOf<String>()
        if (peer.lanOnline) tags.add("局域网")
        if (peer.online) tags.add("中转在线")
        if (peer.lastIpFamily != null) tags.add(peer.lastIpFamily!!)
        if (tags.isEmpty()) tags.add("离线")
        return "${platformLabel(peer.platform)} · ${tags.joinToString(" · ")}"
    }

    private fun runAction(label: String?, block: suspend () -> Unit) {
        viewModelScope.launch {
            _busy.value = true
            _busyText.value = label
            try {
                block()
            } catch (error: Exception) {
                if (error is kotlinx.coroutines.CancellationException) throw error
                _snackbar.tryEmit(error.message ?: "操作失败。")
            } finally {
                _busy.value = false
                _busyText.value = null
            }
        }
    }
}
