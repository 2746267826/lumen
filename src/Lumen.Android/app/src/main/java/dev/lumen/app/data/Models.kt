package dev.lumen.app.data

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import kotlinx.serialization.json.Json

val LumenJson = Json {
    ignoreUnknownKeys = true
    isLenient = true
    explicitNulls = false
    encodeDefaults = true
}

@Serializable
data class UserDto(
    val id: String = "",
    val username: String = "",
    val displayName: String = ""
)

@Serializable
data class AuthResponse(
    val token: String,
    val expiresAt: String = "",
    val user: UserDto
)

@Serializable
data class DeviceDto(
    val id: String,
    val userId: String = "",
    val name: String = "设备",
    val platform: String = "android",
    val model: String? = null,
    val online: Boolean = false,
    val lastSeenAt: String = "",
    val lastIp: String? = null,
    val lastIpFamily: String? = null,
    val appVersion: String? = null
)

@Serializable
data class DeviceRegistrationResponse(val device: DeviceDto)

@Serializable
data class DeviceRegisterRequest(
    val deviceId: String? = null,
    val name: String,
    val platform: String = "android",
    val model: String? = null,
    val appVersion: String? = null
)

@Serializable
data class TransferDto(
    val id: String,
    val senderDeviceId: String = "",
    val recipientDeviceId: String = "",
    val senderDeviceName: String = "",
    val recipientDeviceName: String = "",
    val kind: String = "file",
    val name: String = "",
    val mimeType: String = "application/octet-stream",
    val size: Long = 0,
    val sha256: String? = null,
    val chunkSize: Int = 0,
    val totalChunks: Int = 0,
    val completedChunks: Int = 0,
    val transferredBytes: Long = 0,
    val status: String = "pending",
    val route: String = "relay",
    val senderAddress: String? = null,
    val recipientAddress: String? = null,
    val textContent: String? = null,
    val error: String? = null,
    val createdAt: String = "",
    val updatedAt: String = "",
    val completedAt: String? = null,
    val averageSpeedBps: Double? = null
)

@Serializable
data class TransferEventDto(
    val id: String = "",
    val type: String = "",
    val message: String? = null,
    val address: String? = null,
    val ipFamily: String? = null,
    val bytes: Long? = null,
    val speedBps: Double? = null,
    val at: String = ""
)

@Serializable
data class TransferDetailsDto(
    val transfer: TransferDto,
    val events: List<TransferEventDto> = emptyList()
)

@Serializable
data class TransferPageDto(
    val items: List<TransferDto> = emptyList(),
    val total: Int = 0
)

@Serializable
data class TransferCreateRequest(
    val recipientDeviceId: String,
    val kind: String,
    val name: String,
    val mimeType: String,
    val size: Long,
    val sha256: String? = null,
    val chunkSize: Int,
    val textContent: String? = null
)

@Serializable
data class TransferChunksDto(
    val transferId: String,
    val chunkSize: Int,
    val totalChunks: Int,
    val chunks: List<ChunkDownloadInfoDto> = emptyList()
)

@Serializable
data class ChunkDownloadInfoDto(
    val index: Int,
    val size: Int,
    val sha256: String? = null,
    val uploadedAt: String = ""
)

@Serializable
data class ChunkUploadResultDto(
    val completedChunks: Int = 0,
    val totalChunks: Int = 0,
    val transferredBytes: Long = 0,
    val speedBps: Double? = null
)

@Serializable
data class ShareDto(
    val id: String,
    val url: String,
    val name: String,
    val kind: String,
    val mimeType: String,
    val size: Long,
    val passwordProtected: Boolean = false,
    val expiresAt: String = "",
    val maxDownloads: Int = 0,
    val downloadCount: Int = 0,
    val createdAt: String = ""
)

@Serializable
data class DiscoveryPacket(
    val magic: String = "LUMEN1",
    val version: Int = 1,
    val deviceId: String = "",
    val name: String = "",
    val platform: String = "",
    val model: String? = null,
    val port: Int = 0,
    val addresses: List<String> = emptyList(),
    val families: List<String> = emptyList(),
    val seenAt: String = "",
    val relayUrl: String? = null,
    val userId: String? = null
)

@Serializable
data class DirectPingResponse(
    val service: String = "lumen",
    val version: Int = 1,
    val deviceId: String = "",
    val name: String = "",
    val platform: String = "",
    val model: String? = null,
    val port: Int = 0,
    val addresses: List<String> = emptyList(),
    val families: List<String> = emptyList()
)

@Serializable
data class DirectOfferRequest(
    val senderDeviceId: String,
    val senderName: String,
    val senderPlatform: String = "android",
    val kind: String = "file",
    val name: String,
    val mimeType: String,
    val size: Long,
    val sha256: String? = null,
    val chunkSize: Int,
    val textContent: String? = null,
    val createdAt: String = ""
)

@Serializable
data class DirectOfferResponse(
    val transferId: String,
    val status: String,
    val uploadToken: String
)

@Serializable
data class DirectOfferStatus(
    val transferId: String,
    val status: String,
    val error: String? = null,
    val updatedAt: String = ""
)

@Serializable
data class ApiError(val code: String = "error", val message: String = "请求失败。")

@Serializable
data class ShareTextRequest(
    val name: String,
    val text: String,
    val expiresInHours: Int = 24,
    val password: String? = null,
    val maxDownloads: Int = 0
)

@Serializable
data class HealthResponse(
    val status: String = "",
    val version: String = "",
    val time: String = ""
)

object TransferKind {
    const val FILE = "file"
    const val IMAGE = "image"
    const val TEXT = "text"
}

object TransferStatus {
    const val PENDING = "pending"
    const val ACCEPTED = "accepted"
    const val UPLOADING = "uploading"
    const val READY = "ready"
    const val COMPLETED = "completed"
    const val DECLINED = "declined"
    const val CANCELLED = "cancelled"
    const val FAILED = "failed"

    fun isTerminal(status: String): Boolean =
        status == COMPLETED || status == DECLINED ||
            status == CANCELLED || status == FAILED
}

object TransferRoute {
    const val LAN = "lan"
    const val RELAY = "relay"
}

@Serializable
data class TransferEventRecord(
    val at: Long = System.currentTimeMillis(),
    val type: String = "",
    val message: String? = null,
    val address: String? = null,
    val ipFamily: String? = null,
    val bytes: Long? = null,
    val speedBps: Double? = null
)

@Serializable
data class ChunkState(
    val index: Int,
    val size: Int,
    val sha256: String? = null,
    val uploaded: Boolean = false,
    val downloaded: Boolean = false,
    val speedBps: Double? = null,
    val attempts: Int = 0
)

@Serializable
data class TransferRecord(
    val id: String,
    val direction: String,
    val route: String,
    val status: String,
    val kind: String,
    val name: String,
    val mimeType: String,
    val size: Long,
    val sha256: String? = null,
    val chunkSize: Int = 0,
    val totalChunks: Int = 0,
    val completedChunks: Int = 0,
    val transferredBytes: Long = 0,
    val speedBps: Double = 0.0,
    val averageSpeedBps: Double? = null,
    val peerName: String = "",
    val peerDeviceId: String? = null,
    val peerPlatform: String? = null,
    val senderName: String? = null,
    val recipientName: String? = null,
    val remoteAddress: String? = null,
    val localAddress: String? = null,
    val ipFamily: String? = null,
    val textContent: String? = null,
    val localPath: String? = null,
    val error: String? = null,
    val serverTransferId: String? = null,
    val uploadToken: String? = null,
    val directBase: String? = null,
    val createdAt: Long = System.currentTimeMillis(),
    val updatedAt: Long = System.currentTimeMillis(),
    val completedAt: Long? = null,
    val events: List<TransferEventRecord> = emptyList(),
    val chunks: List<ChunkState> = emptyList()
) {
    val progress: Float
        get() = when {
            status == TransferStatus.COMPLETED -> 1f
            size <= 0 -> 0f
            else -> (transferredBytes.toDouble() / size).toFloat().coerceIn(0f, 1f)
        }

    val isIncoming: Boolean get() = direction == "in"
}

@Serializable
data class PeerDevice(
    val id: String,
    val name: String,
    val platform: String = "android",
    val model: String? = null,
    val online: Boolean = false,
    val lastSeenAt: String = "",
    val lastIp: String? = null,
    val lastIpFamily: String? = null,
    val appVersion: String? = null,
    val lanOnline: Boolean = false,
    val lanPort: Int = 0,
    val lanAddresses: List<String> = emptyList(),
    val lanFamilies: List<String> = emptyList(),
    val lastLanSeen: Long = 0L,
    val relayUrl: String? = null
) {
    val primaryLanAddress: String?
        get() = lanAddresses.firstOrNull { !it.contains(':') } ?: lanAddresses.firstOrNull()
}

@Serializable
data class PersistedState(
    val transfers: List<TransferRecord> = emptyList(),
    val peers: List<PeerDevice> = emptyList(),
    val shares: List<ShareDto> = emptyList()
)

fun String?.isIpv6(): Boolean = this?.contains(':') == true

fun familyOf(address: String?): String? = when {
    address.isNullOrBlank() -> null
    address.contains(':') -> "IPv6"
    Regex("""\d{1,3}(\.\d{1,3}){3}""").matches(address) -> "IPv4"
    else -> null
}

fun platformLabel(platform: String): String = when (platform.lowercase()) {
    "windows" -> "Windows"
    "android" -> "Android"
    "linux" -> "Linux"
    "macos" -> "macOS"
    "ios" -> "iOS"
    else -> platform.ifBlank { "设备" }
}
