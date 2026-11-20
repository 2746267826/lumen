package dev.lumen.app.data

import android.content.Context
import android.os.Build
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.util.Base64
import java.security.KeyStore
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

class SecretStore {
    private val alias = "lumen_session_token"

    private fun key(): SecretKey {
        val store = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        (store.getEntry(alias, null) as? KeyStore.SecretKeyEntry)?.let { return it.secretKey }
        val generator = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore")
        generator.init(
            KeyGenParameterSpec.Builder(
                alias,
                KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT
            )
                .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                .setKeySize(256)
                .build()
        )
        return generator.generateKey()
    }

    fun encrypt(value: String): String {
        if (value.isEmpty()) return ""
        return runCatching {
            val cipher = Cipher.getInstance("AES/GCM/NoPadding")
            cipher.init(Cipher.ENCRYPT_MODE, key())
            val encrypted = cipher.doFinal(value.toByteArray(Charsets.UTF_8))
            val payload = cipher.iv + encrypted
            Base64.encodeToString(payload, Base64.NO_WRAP)
        }.getOrDefault("")
    }

    fun decrypt(value: String): String {
        if (value.isEmpty()) return ""
        return runCatching {
            val payload = Base64.decode(value, Base64.NO_WRAP)
            if (payload.size < 13) return ""
            val iv = payload.copyOfRange(0, 12)
            val body = payload.copyOfRange(12, payload.size)
            val cipher = Cipher.getInstance("AES/GCM/NoPadding")
            cipher.init(Cipher.DECRYPT_MODE, key(), GCMParameterSpec(128, iv))
            String(cipher.doFinal(body), Charsets.UTF_8)
        }.getOrDefault("")
    }
}

class SettingsStore(context: Context) {
    private val prefs = context.getSharedPreferences("lumen.settings", Context.MODE_PRIVATE)
    private val secrets = SecretStore()

    var serverUrl: String
        get() = prefs.getString("server_url", "").orEmpty()
        set(value) = prefs.edit().putString("server_url", normalizeServerUrl(value)).apply()

    var token: String
        get() = secrets.decrypt(prefs.getString("token", "").orEmpty())
        set(value) = prefs.edit().putString("token", secrets.encrypt(value)).apply()

    var userId: String
        get() = prefs.getString("user_id", "").orEmpty()
        set(value) = prefs.edit().putString("user_id", value).apply()

    var username: String
        get() = prefs.getString("username", "").orEmpty()
        set(value) = prefs.edit().putString("username", value).apply()

    var displayName: String
        get() = prefs.getString("display_name", "").orEmpty()
        set(value) = prefs.edit().putString("display_name", value).apply()

    var deviceId: String
        get() {
            val existing = prefs.getString("device_id", "").orEmpty()
            if (existing.isNotEmpty()) return existing
            val created = java.util.UUID.randomUUID().toString()
            prefs.edit().putString("device_id", created).apply()
            return created
        }
        set(value) = prefs.edit().putString("device_id", value).apply()

    var deviceName: String
        get() = prefs.getString("device_name", defaultDeviceName()).orEmpty()
        set(value) = prefs.edit().putString("device_name", value.trim().ifBlank { defaultDeviceName() }).apply()

    var autoAccept: Boolean
        get() = prefs.getBoolean("auto_accept", false)
        set(value) = prefs.edit().putBoolean("auto_accept", value).apply()

    var lanDiscovery: Boolean
        get() = prefs.getBoolean("lan_discovery", true)
        set(value) = prefs.edit().putBoolean("lan_discovery", value).apply()

    var chunkSize: Int
        get() = prefs.getInt("chunk_size", 1024 * 1024)
        set(value) = prefs.edit().putInt("chunk_size", value.coerceIn(64 * 1024, 8 * 1024 * 1024)).apply()

    var themeMode: String
        get() = prefs.getString("theme_mode", "system") ?: "system"
        set(value) = prefs.edit().putString("theme_mode", value).apply()

    val isLoggedIn: Boolean get() = token.isNotBlank() && userId.isNotBlank()

    fun saveAuth(auth: AuthResponse) {
        token = auth.token
        userId = auth.user.id
        username = auth.user.username
        displayName = auth.user.displayName
    }

    fun clearAuth() {
        token = ""
        userId = ""
        username = ""
        displayName = ""
    }

    fun defaultDeviceName(): String {
        val manufacturer = Build.MANUFACTURER?.trim().orEmpty()
        val model = Build.MODEL?.trim().orEmpty()
        return when {
            model.isBlank() -> "Android 设备"
            manufacturer.isBlank() || model.startsWith(manufacturer, ignoreCase = true) -> model
            else -> "$manufacturer $model"
        }
    }

    companion object {
        fun normalizeServerUrl(raw: String): String {
            var value = raw.trim()
            if (value.isEmpty()) return ""
            if (!value.startsWith("http://", true) && !value.startsWith("https://", true)) {
                value = "http://$value"
            }
            return value.trimEnd('/')
        }
    }
}
