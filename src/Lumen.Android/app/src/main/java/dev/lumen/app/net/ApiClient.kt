package dev.lumen.app.net

import dev.lumen.app.data.ApiError
import dev.lumen.app.data.AuthResponse
import dev.lumen.app.data.ChunkUploadResultDto
import dev.lumen.app.data.DeviceDto
import dev.lumen.app.data.DeviceRegisterRequest
import dev.lumen.app.data.DeviceRegistrationResponse
import dev.lumen.app.data.DirectOfferRequest
import dev.lumen.app.data.DirectOfferResponse
import dev.lumen.app.data.DirectOfferStatus
import dev.lumen.app.data.DirectPingResponse
import dev.lumen.app.data.HealthResponse
import dev.lumen.app.data.LumenJson
import dev.lumen.app.data.ShareDto
import dev.lumen.app.data.ShareTextRequest
import dev.lumen.app.data.SettingsStore
import dev.lumen.app.data.TransferChunksDto
import dev.lumen.app.data.TransferCreateRequest
import dev.lumen.app.data.TransferDetailsDto
import dev.lumen.app.data.TransferDto
import dev.lumen.app.data.TransferPageDto
import dev.lumen.app.data.UserDto
import dev.lumen.app.util.PickedFile
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import kotlinx.serialization.Serializable
import kotlinx.serialization.encodeToString
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.MultipartBody
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody
import okhttp3.RequestBody.Companion.asRequestBody
import okhttp3.RequestBody.Companion.toRequestBody
import okhttp3.Response
import okhttp3.ResponseBody
import okio.BufferedSink
import java.io.File
import java.io.IOException
import java.security.MessageDigest
import java.util.concurrent.TimeUnit

class ApiException(
    val status: Int,
    val code: String,
    override val message: String
) : Exception(message)

@Serializable
private data class LoginRequest(val username: String, val password: String)

@Serializable
private data class RegisterRequest(
    val username: String,
    val password: String,
    val displayName: String
)

class ApiClient(private val settings: SettingsStore) {
    val http: OkHttpClient = OkHttpClient.Builder()
        .connectTimeout(8, TimeUnit.SECONDS)
        .readTimeout(0, TimeUnit.SECONDS)
        .writeTimeout(0, TimeUnit.SECONDS)
        .retryOnConnectionFailure(true)
        .build()

    val baseUrl: String get() = SettingsStore.normalizeServerUrl(settings.serverUrl)

    suspend fun health(): HealthResponse = request("GET", "/api/v1/health", auth = false)

    suspend fun login(username: String, password: String): AuthResponse = request(
        "POST",
        "/api/v1/auth/login",
        body = LumenJson.encodeToString(LoginRequest(username.trim(), password)),
        auth = false
    )

    suspend fun register(username: String, password: String, displayName: String): AuthResponse = request(
        "POST",
        "/api/v1/auth/register",
        body = LumenJson.encodeToString(RegisterRequest(username.trim(), password, displayName.trim())),
        auth = false
    )

    suspend fun me(): UserDto = request("GET", "/api/v1/me")

    suspend fun logout() {
        runCatching { request<Unit>("POST", "/api/v1/auth/logout") }
    }

    suspend fun registerDevice(
        name: String,
        deviceId: String,
        model: String?,
        appVersion: String
    ): DeviceDto {
        val response: DeviceRegistrationResponse = request(
            "POST",
            "/api/v1/devices",
            body = LumenJson.encodeToString(
                DeviceRegisterRequest(
                    deviceId = deviceId,
                    name = name,
                    platform = "android",
                    model = model,
                    appVersion = appVersion
                )
            )
        )
        return response.device
    }

    suspend fun devices(): List<DeviceDto> = request("GET", "/api/v1/devices")

    suspend fun deleteDevice(deviceId: String) {
        request<Unit>("DELETE", "/api/v1/devices/$deviceId")
    }

    suspend fun transfers(limit: Int = 100): TransferPageDto =
        request("GET", "/api/v1/transfers?limit=$limit")

    suspend fun transferDetails(id: String): TransferDetailsDto =
        request("GET", "/api/v1/transfers/$id")

    suspend fun createTransfer(requestBody: TransferCreateRequest): TransferDto =
        request(
            "POST",
            "/api/v1/transfers",
            body = LumenJson.encodeToString(requestBody)
        )

    suspend fun acceptTransfer(id: String): TransferDto =
        request("POST", "/api/v1/transfers/$id/accept")

    suspend fun declineTransfer(id: String): TransferDto =
        request("POST", "/api/v1/transfers/$id/decline")

    suspend fun cancelTransfer(id: String): TransferDto =
        request("POST", "/api/v1/transfers/$id/cancel")

    suspend fun readyTransfer(id: String): TransferDto =
        request("POST", "/api/v1/transfers/$id/ready")

    suspend fun receivedTransfer(id: String): TransferDto =
        request("POST", "/api/v1/transfers/$id/received")

    suspend fun listChunks(id: String): TransferChunksDto =
        request("GET", "/api/v1/transfers/$id/chunks")

    suspend fun uploadChunk(
        transferId: String,
        index: Int,
        data: ByteArray,
        sha256: String
    ): ChunkUploadResultDto = withContext(Dispatchers.IO) {
        val url = "$baseUrl/api/v1/transfers/$transferId/chunks/$index"
        val request = authorizedBuilder(url)
            .put(data.toRequestBody("application/octet-stream".toMediaType()))
            .header("X-Lumen-Chunk-Sha256", sha256)
            .build()
        executeJson(request)
    }

    suspend fun downloadChunk(
        transferId: String,
        index: Int,
        target: File
    ): Pair<String, Long> = withContext(Dispatchers.IO) {
        val url = "$baseUrl/api/v1/transfers/$transferId/chunks/$index"
        val request = authorizedBuilder(url).get().build()
        http.newCall(request).execute().use { response ->
            if (!response.isSuccessful) throw errorFrom(response)
            val expectedSha = response.header("X-Lumen-Chunk-Sha256")
            val digest = MessageDigest.getInstance("SHA-256")
            var total = 0L
            response.body?.byteStream()?.use { input ->
                target.outputStream().use { output ->
                    val buffer = ByteArray(256 * 1024)
                    while (true) {
                        val read = input.read(buffer)
                        if (read <= 0) break
                        digest.update(buffer, 0, read)
                        output.write(buffer, 0, read)
                        total += read
                    }
                }
            }
            val sha = digest.digest().joinToString("") { "%02x".format(it) }
            if (!expectedSha.isNullOrBlank() && !sha.equals(expectedSha, ignoreCase = true)) {
                target.delete()
                throw IOException("分块 $index SHA-256 校验失败。")
            }
            sha to total
        }
    }

    suspend fun shares(): List<ShareDto> = request("GET", "/api/v1/shares")

    suspend fun createTextShare(
        name: String,
        text: String,
        expiresInHours: Int,
        password: String?,
        maxDownloads: Int
    ): ShareDto = request(
        "POST",
        "/api/v1/shares/text",
        body = LumenJson.encodeToString(
            ShareTextRequest(
                name = name,
                text = text,
                expiresInHours = expiresInHours,
                password = password?.takeIf { it.isNotBlank() },
                maxDownloads = maxDownloads
            )
        )
    )

    suspend fun createFileShare(
        picked: PickedFile,
        name: String,
        expiresInHours: Int,
        password: String?,
        maxDownloads: Int,
        onProgress: (sent: Long, total: Long) -> Unit = { _, _ -> }
    ): ShareDto = withContext(Dispatchers.IO) {
        val query = buildString {
            append("/api/v1/shares/file?name=")
            append(java.net.URLEncoder.encode(name.ifBlank { picked.name }, "UTF-8"))
            append("&mime=")
            append(java.net.URLEncoder.encode(picked.mimeType, "UTF-8"))
            append("&expiresInHours=")
            append(expiresInHours)
            append("&maxDownloads=")
            append(maxDownloads)
        }
        val builder = authorizedBuilder("$baseUrl$query")
            .post(ProgressFileBody(picked.cachedFile, picked.mimeType, onProgress))
        if (!password.isNullOrBlank()) {
            builder.header("X-Lumen-Share-Password", password)
        }
        executeJson(builder.build())
    }

    suspend fun revokeShare(id: String) {
        request<Unit>("DELETE", "/api/v1/shares/$id")
    }

    suspend fun directPing(base: String): DirectPingResponse = withContext(Dispatchers.IO) {
        val request = Request.Builder()
            .url("$base/lumen/v1/ping")
            .header("User-Agent", "Lumen-Android/1.0")
            .get()
            .build()
        directClient().newCall(request).execute().use { response ->
            if (!response.isSuccessful) throw IOException("HTTP ${response.code}")
            val body = response.body?.string().orEmpty()
            LumenJson.decodeFromString(DirectPingResponse.serializer(), body)
        }
    }

    suspend fun sendDirectOffer(base: String, offer: DirectOfferRequest): DirectOfferResponse =
        withContext(Dispatchers.IO) {
            val request = Request.Builder()
                .url("$base/lumen/v1/offers")
                .post(
                    LumenJson.encodeToString(DirectOfferRequest.serializer(), offer)
                        .toRequestBody("application/json".toMediaType())
                )
                .build()
            directClient().newCall(request).execute().use { response ->
                if (!response.isSuccessful) throw IOException("HTTP ${response.code}")
                LumenJson.decodeFromString(
                    DirectOfferResponse.serializer(),
                    response.body?.string().orEmpty()
                )
            }
        }

    suspend fun directOfferStatus(base: String, transferId: String): DirectOfferStatus =
        withContext(Dispatchers.IO) {
            val request = Request.Builder()
                .url("$base/lumen/v1/offers/$transferId")
                .get()
                .build()
            directClient().newCall(request).execute().use { response ->
                if (!response.isSuccessful) throw IOException("HTTP ${response.code}")
                LumenJson.decodeFromString(
                    DirectOfferStatus.serializer(),
                    response.body?.string().orEmpty()
                )
            }
        }

    suspend fun directUploadChunk(
        base: String,
        transferId: String,
        index: Int,
        uploadToken: String,
        data: ByteArray,
        sha256: String
    ): ChunkUploadResultDto = withContext(Dispatchers.IO) {
        val request = Request.Builder()
            .url("$base/lumen/v1/transfers/$transferId/chunks/$index")
            .put(data.toRequestBody("application/octet-stream".toMediaType()))
            .header("X-Lumen-Upload-Token", uploadToken)
            .header("X-Lumen-Chunk-Sha256", sha256)
            .build()
        directClient().newCall(request).execute().use { response ->
            if (!response.isSuccessful) throw IOException("HTTP ${response.code}")
            LumenJson.decodeFromString(
                ChunkUploadResultDto.serializer(),
                response.body?.string().orEmpty()
            )
        }
    }

    suspend fun directComplete(
        base: String,
        transferId: String,
        uploadToken: String
    ) = withContext(Dispatchers.IO) {
        val request = Request.Builder()
            .url("$base/lumen/v1/transfers/$transferId/complete")
            .post(ByteArray(0).toRequestBody(null))
            .header("X-Lumen-Upload-Token", uploadToken)
            .build()
        directClient().newCall(request).execute().use { response ->
            if (!response.isSuccessful) throw IOException("HTTP ${response.code}")
        }
    }

    suspend fun directCancel(
        base: String,
        transferId: String,
        uploadToken: String
    ) {
        withContext(Dispatchers.IO) {
            runCatching {
                val request = Request.Builder()
                    .url("$base/lumen/v1/transfers/$transferId/cancel")
                    .post(ByteArray(0).toRequestBody(null))
                    .header("X-Lumen-Upload-Token", uploadToken)
                    .build()
                directClient().newCall(request).execute().use { }
            }
        }
    }

    private fun directClient(): OkHttpClient = http.newBuilder()
        .connectTimeout(1500, TimeUnit.MILLISECONDS)
        .readTimeout(30, TimeUnit.SECONDS)
        .writeTimeout(3, TimeUnit.MINUTES)
        .build()

    private suspend inline fun <reified T> request(
        method: String,
        path: String,
        body: String? = null,
        auth: Boolean = true
    ): T = withContext(Dispatchers.IO) {
        val builder = if (auth) authorizedBuilder("$baseUrl$path") else Request.Builder().url("$baseUrl$path")
        builder.header("User-Agent", "Lumen-Android/1.0")
        when (method) {
            "GET" -> builder.get()
            "DELETE" -> builder.delete()
            "POST", "PUT" -> {
                val contentType = "application/json; charset=utf-8".toMediaType()
                builder.method(method, (body ?: "").toRequestBody(contentType))
            }
            else -> error("Unsupported method $method")
        }
        executeJson(builder.build())
    }

    private fun authorizedBuilder(url: String): Request.Builder {
        val builder = Request.Builder()
            .url(url)
            .header("User-Agent", "Lumen-Android/1.0")
        if (settings.token.isNotBlank()) {
            builder.header("Authorization", "Bearer ${settings.token}")
        }
        if (settings.deviceId.isNotBlank()) {
            builder.header("X-Lumen-Device", settings.deviceId)
        }
        return builder
    }

    private inline fun <reified T> executeJson(request: Request): T {
        http.newCall(request).execute().use { response ->
            if (!response.isSuccessful) throw errorFrom(response)
            val body = response.body?.string().orEmpty()
            if (T::class == Unit::class) {
                @Suppress("UNCHECKED_CAST")
                return Unit as T
            }
            if (body.isBlank()) {
                throw ApiException(response.code, "empty_response", "服务器返回了空响应。")
            }
            return LumenJson.decodeFromString<T>(body)
        }
    }

    private fun errorFrom(response: Response): ApiException {
        val text = runCatching { response.body?.string().orEmpty() }.getOrDefault("")
        val parsed = runCatching { LumenJson.decodeFromString<ApiError>(text) }.getOrNull()
        return ApiException(
            response.code,
            parsed?.code ?: "http_${response.code}",
            parsed?.message ?: "请求失败（HTTP ${response.code}）。"
        )
    }

    private class ProgressFileBody(
        private val file: File,
        private val mime: String,
        private val onProgress: (Long, Long) -> Unit
    ) : RequestBody() {
        override fun contentType() = mime.toMediaType()
        override fun contentLength(): Long = file.length()

        override fun writeTo(sink: BufferedSink) {
            val total = file.length()
            var sent = 0L
            file.inputStream().use { input ->
                val buffer = ByteArray(256 * 1024)
                while (true) {
                    val read = input.read(buffer)
                    if (read <= 0) break
                    sink.write(buffer, 0, read)
                    sent += read
                    onProgress(sent, total)
                }
            }
        }
    }
}
