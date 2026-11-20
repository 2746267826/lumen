package dev.lumen.app.discovery

import android.content.Context
import android.net.wifi.WifiManager
import dev.lumen.app.data.DiscoveryPacket
import dev.lumen.app.data.LumenJson
import dev.lumen.app.data.SettingsStore
import dev.lumen.app.data.familyOf
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asSharedFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.serialization.encodeToString
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.MulticastSocket
import java.net.NetworkInterface
import java.net.SocketTimeoutException
import java.time.Instant

data class LanPeer(
    val packet: DiscoveryPacket,
    val lastSeen: Long
)

sealed interface LanEvent {
    data class PeerSeen(val peer: LanPeer) : LanEvent
    data class PeerLost(val deviceId: String) : LanEvent
    data class Error(val message: String) : LanEvent
}

class LanDiscovery(
    private val context: Context,
    private val settings: SettingsStore
) {
    companion object {
        const val GROUP = "239.255.77.88"
        const val PORT = 47654
        const val MAGIC = "LUMEN1"
    }

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private var job: Job? = null
    private var socket: MulticastSocket? = null
    private var lock: WifiManager.MulticastLock? = null
    private var directPort = 0
    @Volatile private var active = false
    private val seen = mutableMapOf<String, Long>()

    private val _events = MutableSharedFlow<LanEvent>(extraBufferCapacity = 128)
    val events: SharedFlow<LanEvent> = _events.asSharedFlow()

    private val _running = MutableStateFlow(false)
    val running: StateFlow<Boolean> = _running.asStateFlow()

    private val _localAddresses = MutableStateFlow<List<String>>(emptyList())
    val localAddresses: StateFlow<List<String>> = _localAddresses.asStateFlow()

    private val _lastError = MutableStateFlow<String?>(null)
    val lastError: StateFlow<String?> = _lastError.asStateFlow()

    fun start(port: Int) {
        if (job?.isActive == true) {
            directPort = port
            return
        }
        directPort = port
        job = scope.launch { runLoop() }
    }

    fun stop() {
        active = false
        job?.cancel()
        job = null
        runCatching { socket?.close() }
        socket = null
        runCatching { lock?.release() }
        lock = null
        seen.clear()
        _running.value = false
    }

    fun refreshAddresses(): List<String> {
        val addresses = localAddresses()
        _localAddresses.value = addresses
        return addresses
    }

    private suspend fun runLoop() {
        try {
            val wifi = context.applicationContext.getSystemService(WifiManager::class.java)
            lock = wifi?.createMulticastLock("lumen-discovery")?.apply {
                setReferenceCounted(true)
                acquire()
            }
            val next = MulticastSocket(null)
            next.reuseAddress = true
            next.bind(InetSocketAddress(PORT))
            next.timeToLive = 3
            next.soTimeout = 1200
            val group = InetAddress.getByName(GROUP)
            var joined = false
            NetworkInterface.getNetworkInterfaces()?.toList().orEmpty().forEach { nic ->
                if (!nic.isUp || nic.isLoopback) return@forEach
                runCatching {
                    next.joinGroup(InetSocketAddress(group, PORT), nic)
                    joined = true
                }
            }
            if (!joined) next.joinGroup(group)
            socket = next
            _running.value = true
            _lastError.value = null
            active = true

            coroutineScope {
                launch { receiveLoop(next) }
                launch { announceLoop() }
                launch { expiryLoop() }
            }
        } catch (error: Exception) {
            active = false
            _lastError.value = "局域网发现启动失败：${error.message}"
            _running.value = false
            _events.tryEmit(LanEvent.Error(_lastError.value.orEmpty()))
        }
    }

    private suspend fun receiveLoop(socket: MulticastSocket) {
        val buffer = ByteArray(64 * 1024)
        while (active) {
            try {
                val packet = DatagramPacket(buffer, buffer.size)
                socket.receive(packet)
                val text = String(packet.data, 0, packet.length, Charsets.UTF_8)
                val parsed = runCatching {
                    LumenJson.decodeFromString(DiscoveryPacket.serializer(), text)
                }.getOrNull() ?: continue
                if (parsed.magic != MAGIC ||
                    parsed.version != 1 ||
                    parsed.deviceId.isBlank() ||
                    parsed.deviceId == settings.deviceId
                ) {
                    continue
                }
                seen[parsed.deviceId] = System.currentTimeMillis()
                _events.tryEmit(LanEvent.PeerSeen(LanPeer(parsed, System.currentTimeMillis())))
            } catch (_: SocketTimeoutException) {
            } catch (_: java.net.SocketException) {
                break
            } catch (error: Exception) {
                _lastError.value = "局域网发现接收异常：${error.message}"
            }
        }
    }

    private suspend fun announceLoop() {
        while (active) {
            runCatching {
                val addresses = refreshAddresses()
                val packet = DiscoveryPacket(
                    magic = MAGIC,
                    version = 1,
                    deviceId = settings.deviceId,
                    name = settings.deviceName,
                    platform = "android",
                    model = android.os.Build.MODEL,
                    port = directPort,
                    addresses = addresses,
                    families = addresses.mapNotNull { familyOf(it) }.distinct(),
                    seenAt = Instant.now().toString(),
                    relayUrl = settings.serverUrl.takeIf { settings.isLoggedIn },
                    userId = settings.userId.takeIf { it.isNotBlank() }
                )
                val bytes = LumenJson.encodeToString(DiscoveryPacket.serializer(), packet)
                    .toByteArray(Charsets.UTF_8)
                val group = InetAddress.getByName(GROUP)
                DatagramSocket().use { sender ->
                    sender.broadcast = true
                    sender.send(DatagramPacket(bytes, bytes.size, group, PORT))
                    runCatching {
                        sender.send(
                            DatagramPacket(
                                bytes,
                                bytes.size,
                                InetAddress.getByName("255.255.255.255"),
                                PORT
                            )
                        )
                    }
                }
            }
            delay(3000)
        }
    }

    private suspend fun expiryLoop() {
        while (active) {
            delay(4000)
            val cutoff = System.currentTimeMillis() - 12_000
            val lost = seen.filterValues { it < cutoff }.keys.toList()
            lost.forEach {
                seen.remove(it)
                _events.tryEmit(LanEvent.PeerLost(it))
            }
        }
    }

    fun localAddresses(): List<String> {
        val result = linkedSetOf<String>()
        runCatching {
            NetworkInterface.getNetworkInterfaces()?.toList().orEmpty().forEach { nic ->
                if (!nic.isUp || nic.isLoopback) return@forEach
                nic.inetAddresses.toList().forEach { address ->
                    if (address.isLoopbackAddress || address.isAnyLocalAddress) return@forEach
                    val host = address.hostAddress ?: return@forEach
                    val clean = host.substringBefore('%')
                    if (address is java.net.Inet4Address) result.add(clean)
                    else if (address is java.net.Inet6Address && !address.isLinkLocalAddress) {
                        result.add(clean)
                    }
                }
            }
        }
        return result.toList()
    }
}
