package dev.lumen.app.net

import dev.lumen.app.data.LumenJson
import dev.lumen.app.data.SettingsStore
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.launch
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.buildJsonObject
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive
import kotlinx.serialization.json.long
import kotlinx.serialization.json.put
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean

enum class RelayState { DISCONNECTED, CONNECTING, CONNECTED }

data class RelayMessage(
    val type: String,
    val root: JsonObject
) {
    fun string(key: String): String? =
        runCatching { root[key]?.jsonPrimitive?.content }.getOrNull()

    fun obj(key: String): JsonObject? =
        runCatching { root[key]?.jsonObject }.getOrNull()
}

class RelaySocket(
    private val settings: SettingsStore,
    private val http: OkHttpClient
) {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val _state = MutableStateFlow(RelayState.DISCONNECTED)
    val state: StateFlow<RelayState> = _state.asStateFlow()

    private val _messages = MutableSharedFlow<RelayMessage>(extraBufferCapacity = 64)
    val messages: SharedFlow<RelayMessage> = _messages.asSharedFlow()

    private val _latencyMs = MutableStateFlow<Long?>(null)
    val latencyMs: StateFlow<Long?> = _latencyMs.asStateFlow()

    private var socket: WebSocket? = null
    private var reconnectJob: Job? = null
    private var pingJob: Job? = null
    private var generation = 0
    private val manuallyClosed = AtomicBoolean(false)
    private var baseUrl = ""
    private var token = ""
    private var deviceId = ""

    fun connect() {
        val nextBase = SettingsStore.normalizeServerUrl(settings.serverUrl)
        val nextToken = settings.token
        val nextDevice = settings.deviceId
        if (nextBase.isBlank() || nextToken.isBlank()) {
            disconnect()
            return
        }
        if (_state.value == RelayState.CONNECTED &&
            baseUrl == nextBase && token == nextToken && deviceId == nextDevice
        ) {
            return
        }
        baseUrl = nextBase
        token = nextToken
        deviceId = nextDevice
        manuallyClosed.set(false)
        reconnectJob?.cancel()
        reconnectJob = scope.launch { reconnectLoop() }
    }

    fun disconnect() {
        manuallyClosed.set(true)
        reconnectJob?.cancel()
        pingJob?.cancel()
        generation++
        socket?.close(1000, "bye")
        socket = null
        _state.value = RelayState.DISCONNECTED
        _latencyMs.value = null
    }

    fun shutdown() {
        disconnect()
    }

    private suspend fun reconnectLoop() {
        var attempt = 0
        while (!manuallyClosed.get()) {
            val current = generation
            val opened = openOnce(current)
            if (opened) {
                attempt = 0
                while (!manuallyClosed.get() && generation == current &&
                    _state.value == RelayState.CONNECTED
                ) {
                    delay(500)
                }
                if (manuallyClosed.get()) return
            }
            if (manuallyClosed.get()) return
            _state.value = RelayState.DISCONNECTED
            attempt++
            val wait = (1000L * (1 shl attempt.coerceAtMost(4))).coerceAtMost(20_000L)
            delay(wait)
        }
    }

    private suspend fun openOnce(expectedGeneration: Int): Boolean {
        if (_state.value == RelayState.CONNECTED) return true
        _state.value = RelayState.CONNECTING
        val wsScheme = if (baseUrl.startsWith("https://", true)) "wss" else "ws"
        val host = baseUrl.removePrefix("https://").removePrefix("http://")
        val request = Request.Builder()
            .url("$wsScheme://$host/api/v1/ws?deviceId=$deviceId")
            .header("Authorization", "Bearer $token")
            .header("User-Agent", "Lumen-Android/1.0")
            .build()
        val opened = java.util.concurrent.CountDownLatch(1)
        val success = AtomicBoolean(false)
        val ws = http.newBuilder()
            .pingInterval(15, TimeUnit.SECONDS)
            .build()
            .newWebSocket(request, object : WebSocketListener() {
                override fun onOpen(webSocket: WebSocket, response: Response) {
                    if (expectedGeneration != generation || manuallyClosed.get()) {
                        webSocket.close(1000, "stale")
                        opened.countDown()
                        return
                    }
                    socket = webSocket
                    success.set(true)
                    _state.value = RelayState.CONNECTED
                    opened.countDown()
                    startPing(webSocket)
                }

                override fun onMessage(webSocket: WebSocket, text: String) {
                    val parsed = runCatching {
                        val root = LumenJson.parseToJsonElement(text).jsonObject
                        RelayMessage(root["type"]?.jsonPrimitive?.content ?: "", root)
                    }.getOrNull() ?: return
                    if (parsed.type == "pong") {
                        val sent = runCatching { parsed.root["ts"]?.jsonPrimitive?.long }.getOrNull()
                        if (sent != null) {
                            _latencyMs.value = (System.currentTimeMillis() - sent).coerceAtLeast(0)
                        }
                    }
                    _messages.tryEmit(parsed)
                }

                override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) {
                    if (expectedGeneration == generation) {
                        socket = null
                        _state.value = RelayState.DISCONNECTED
                    }
                    opened.countDown()
                }

                override fun onClosed(webSocket: WebSocket, code: Int, reason: String) {
                    if (expectedGeneration == generation) {
                        socket = null
                        _state.value = RelayState.DISCONNECTED
                    }
                    opened.countDown()
                }
            })
        val completed = withContextIO { opened.await(10, TimeUnit.SECONDS) }
        if (!completed || !success.get()) {
            if (expectedGeneration == generation) {
                runCatching { socket?.cancel() }
                socket = null
                _state.value = RelayState.DISCONNECTED
            }
            return false
        }
        return true
    }

    private fun startPing(webSocket: WebSocket) {
        pingJob?.cancel()
        pingJob = scope.launch {
            while (!manuallyClosed.get() && _state.value == RelayState.CONNECTED) {
                val payload = buildJsonObject {
                    put("type", "ping")
                    put("ts", System.currentTimeMillis())
                }
                runCatching { webSocket.send(LumenJson.encodeToString(JsonObject.serializer(), payload)) }
                delay(10_000)
            }
        }
    }

    private suspend fun <T> withContextIO(block: () -> T): T =
        kotlinx.coroutines.withContext(Dispatchers.IO) { block() }
}
