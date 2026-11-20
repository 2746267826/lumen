package dev.lumen.app.data

import android.content.Context
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import java.io.File

class AppStore(context: Context) {
    private val stateFile = File(context.filesDir, "lumen-state.json")
    private val lock = Any()

    private val _transfers = MutableStateFlow<List<TransferRecord>>(emptyList())
    val transfers: StateFlow<List<TransferRecord>> = _transfers.asStateFlow()

    private val _peers = MutableStateFlow<List<PeerDevice>>(emptyList())
    val peers: StateFlow<List<PeerDevice>> = _peers.asStateFlow()

    private val _shares = MutableStateFlow<List<ShareDto>>(emptyList())
    val shares: StateFlow<List<ShareDto>> = _shares.asStateFlow()

    init {
        load()
    }

    private fun load() {
        runCatching {
            if (!stateFile.exists()) return
            val state = LumenJson.decodeFromString<PersistedState>(stateFile.readText())
            _transfers.value = state.transfers.sortedByDescending { it.createdAt }
            _peers.value = state.peers
            _shares.value = state.shares
        }
    }

    private fun persist() {
        synchronized(lock) {
            runCatching {
                val state = PersistedState(
                    transfers = _transfers.value.take(500),
                    peers = _peers.value,
                    shares = _shares.value
                )
                val temp = File(stateFile.parentFile, stateFile.name + ".tmp")
                temp.writeText(LumenJson.encodeToString(PersistedState.serializer(), state))
                if (!temp.renameTo(stateFile)) {
                    stateFile.writeText(temp.readText())
                    temp.delete()
                }
            }
        }
    }

    fun upsertTransfer(record: TransferRecord) {
        val current = _transfers.value.toMutableList()
        val index = current.indexOfFirst { it.id == record.id }
        if (index >= 0) current[index] = record else current.add(0, record)
        _transfers.value = current.sortedByDescending { it.createdAt }
        persist()
    }

    fun transfer(id: String): TransferRecord? = _transfers.value.firstOrNull { it.id == id }

    fun updateTransfer(id: String, block: (TransferRecord) -> TransferRecord): TransferRecord? {
        val current = transfer(id) ?: return null
        val next = block(current)
        upsertTransfer(next)
        return next
    }

    fun addEvent(id: String, event: TransferEventRecord) {
        updateTransfer(id) { it.copy(events = it.events + event, updatedAt = System.currentTimeMillis()) }
    }

    fun removeTransfer(id: String) {
        _transfers.value = _transfers.value.filterNot { it.id == id }
        persist()
    }

    fun clearHistory() {
        _transfers.value = emptyList()
        persist()
    }

    fun setPeers(peers: List<PeerDevice>) {
        _peers.value = peers.sortedWith(
            compareByDescending<PeerDevice> { it.lanOnline }
                .thenByDescending { it.online }
                .thenBy { it.name.lowercase() }
        )
        persist()
    }

    fun upsertPeer(peer: PeerDevice) {
        val current = _peers.value.toMutableList()
        val index = current.indexOfFirst { it.id == peer.id }
        if (index >= 0) {
            current[index] = current[index].copy(
                name = peer.name,
                platform = peer.platform,
                model = peer.model,
                online = peer.online,
                lastSeenAt = peer.lastSeenAt,
                lastIp = peer.lastIp,
                lastIpFamily = peer.lastIpFamily,
                appVersion = peer.appVersion,
                lanOnline = peer.lanOnline,
                lanPort = peer.lanPort,
                lanAddresses = peer.lanAddresses,
                lanFamilies = peer.lanFamilies,
                lastLanSeen = peer.lastLanSeen,
                relayUrl = peer.relayUrl
            )
        } else {
            current.add(peer)
        }
        setPeers(current)
    }

    fun markPeerSeenLan(packet: DiscoveryPacket, myUserId: String?) {
        val existing = _peers.value.firstOrNull { it.id == packet.deviceId }
        upsertPeer(
            PeerDevice(
                id = packet.deviceId,
                name = packet.name,
                platform = packet.platform,
                model = packet.model,
                online = existing?.online ?: false,
                lastSeenAt = existing?.lastSeenAt.orEmpty(),
                lastIp = existing?.lastIp,
                lastIpFamily = existing?.lastIpFamily,
                appVersion = existing?.appVersion,
                lanOnline = true,
                lanPort = packet.port,
                lanAddresses = packet.addresses,
                lanFamilies = packet.families,
                lastLanSeen = System.currentTimeMillis(),
                relayUrl = packet.relayUrl
            )
        )
    }

    fun markLanPeerLost(deviceId: String) {
        updatePeer(deviceId) { it.copy(lanOnline = false, lastLanSeen = System.currentTimeMillis()) }
    }

    fun updatePeer(id: String, block: (PeerDevice) -> PeerDevice) {
        val current = _peers.value.toMutableList()
        val index = current.indexOfFirst { it.id == id }
        if (index < 0) return
        current[index] = block(current[index])
        setPeers(current)
    }

    fun setRelayDevices(devices: List<DeviceDto>) {
        val now = System.currentTimeMillis()
        val merged = devices.map { dto ->
            val local = _peers.value.firstOrNull { it.id == dto.id }
            PeerDevice(
                id = dto.id,
                name = dto.name,
                platform = dto.platform,
                model = dto.model,
                online = dto.online,
                lastSeenAt = dto.lastSeenAt,
                lastIp = dto.lastIp,
                lastIpFamily = dto.lastIpFamily,
                appVersion = dto.appVersion,
                lanOnline = local?.lanOnline == true && now - local.lastLanSeen < 15_000,
                lanPort = local?.lanPort ?: 0,
                lanAddresses = local?.lanAddresses.orEmpty(),
                lanFamilies = local?.lanFamilies.orEmpty(),
                lastLanSeen = local?.lastLanSeen ?: 0L,
                relayUrl = local?.relayUrl
            )
        }.toMutableList()
        val relayIds = devices.map { it.id }.toSet()
        _peers.value.forEach { local ->
            if (local.id !in relayIds && local.lanOnline) {
                merged.add(local)
            }
        }
        setPeers(merged)
    }

    fun applyRelayPresence(dto: DeviceDto, online: Boolean) {
        val local = _peers.value.firstOrNull { it.id == dto.id }
        val next = local?.copy(
            name = dto.name,
            platform = dto.platform,
            model = dto.model,
            online = online,
            lastSeenAt = dto.lastSeenAt,
            lastIp = dto.lastIp,
            lastIpFamily = dto.lastIpFamily,
            appVersion = dto.appVersion
        ) ?: PeerDevice(
            id = dto.id,
            name = dto.name,
            platform = dto.platform,
            model = dto.model,
            online = online,
            lastSeenAt = dto.lastSeenAt,
            lastIp = dto.lastIp,
            lastIpFamily = dto.lastIpFamily,
            appVersion = dto.appVersion
        )
        upsertPeer(next)
    }

    fun setShares(shares: List<ShareDto>) {
        _shares.value = shares.sortedByDescending { it.createdAt }
        persist()
    }

    fun upsertShare(share: ShareDto) {
        val current = _shares.value.toMutableList()
        val index = current.indexOfFirst { it.id == share.id }
        if (index >= 0) current[index] = share else current.add(0, share)
        _shares.value = current.sortedByDescending { it.createdAt }
        persist()
    }

    fun removeShare(id: String) {
        _shares.value = _shares.value.filterNot { it.id == id }
        persist()
    }

    private fun sameRelay(left: String?, right: String?): Boolean {
        if (left.isNullOrBlank() || right.isNullOrBlank()) return false
        return SettingsStore.normalizeServerUrl(left) == SettingsStore.normalizeServerUrl(right)
    }
}
