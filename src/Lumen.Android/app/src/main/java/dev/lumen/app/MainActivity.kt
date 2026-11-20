package dev.lumen.app

import android.content.Intent
import android.Manifest
import android.content.pm.PackageManager
import android.os.Build
import android.net.Uri
import android.os.Bundle
import android.widget.Toast
import androidx.activity.ComponentActivity
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.compose.setContent
import androidx.activity.result.PickVisualMediaRequest
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.Devices
import androidx.compose.material.icons.filled.History
import androidx.compose.material.icons.filled.Link
import androidx.compose.material.icons.filled.Settings
import androidx.compose.material.icons.filled.SwapVert
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Badge
import androidx.compose.material3.BadgedBox
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.NavigationBar
import androidx.compose.material3.NavigationBarItem
import androidx.compose.material3.NavigationBarItemDefaults
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Snackbar
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.SideEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.core.content.FileProvider
import androidx.core.content.ContextCompat
import androidx.core.view.WindowCompat
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.compose.LifecycleEventEffect
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel
import dev.lumen.app.data.PeerDevice
import dev.lumen.app.data.TransferRecord
import dev.lumen.app.ui.DevicesScreen
import dev.lumen.app.ui.LumenBackdrop
import dev.lumen.app.ui.LumenBackground
import dev.lumen.app.ui.LumenBackgroundDeep
import dev.lumen.app.ui.LumenPrimary
import dev.lumen.app.ui.LumenSurface
import dev.lumen.app.ui.LumenSurfaceHigh
import dev.lumen.app.ui.LumenTextMuted
import dev.lumen.app.ui.LumenTheme
import dev.lumen.app.ui.LoginScreen
import dev.lumen.app.ui.MainViewModel
import dev.lumen.app.ui.SettingsScreen
import dev.lumen.app.ui.SharesScreen
import dev.lumen.app.ui.TransfersScreen
import java.io.File

private data class SharedPayload(
    val uris: List<Uri> = emptyList(),
    val text: String? = null,
    val mimeType: String? = null
)

class MainActivity : ComponentActivity() {
    private var sharedPayload by mutableStateOf<SharedPayload?>(null)

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        if (savedInstanceState == null) {
            captureShareIntent(intent)
        }
        setContent {
            val mainViewModel: MainViewModel = viewModel()
            val themeMode by mainViewModel.themeMode.collectAsStateWithLifecycle()
            val darkTheme = when (themeMode) {
                "light" -> false
                "dark" -> true
                else -> isSystemInDarkTheme()
            }
            SideEffect {
                WindowCompat.getInsetsController(window, window.decorView).apply {
                    isAppearanceLightStatusBars = !darkTheme
                    isAppearanceLightNavigationBars = !darkTheme
                }
            }
            LumenTheme(themeMode) {
                LumenApp(
                    viewModel = mainViewModel,
                    sharedPayload = sharedPayload,
                    onConsumeShared = { sharedPayload = null }
                )
            }
        }
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        setIntent(intent)
        captureShareIntent(intent)
    }

    private fun captureShareIntent(intent: Intent?) {
        when (intent?.action) {
            Intent.ACTION_SEND -> {
                val text = intent.getStringExtra(Intent.EXTRA_TEXT)?.takeIf { it.isNotBlank() }
                val uri = streamUri(intent)
                sharedPayload = when {
                    uri != null -> SharedPayload(listOf(uri), null, intent.type)
                    text != null -> SharedPayload(text = text, mimeType = intent.type)
                    else -> sharedPayload
                }
            }

            Intent.ACTION_SEND_MULTIPLE -> {
                val uris = streamUris(intent)
                if (uris.isNotEmpty()) {
                    sharedPayload = SharedPayload(uris, null, intent.type)
                }
            }
        }
    }

    @Suppress("DEPRECATION")
    private fun streamUri(intent: Intent): Uri? =
        if (android.os.Build.VERSION.SDK_INT >= 33) {
            intent.getParcelableExtra(Intent.EXTRA_STREAM, Uri::class.java)
        } else {
            intent.getParcelableExtra(Intent.EXTRA_STREAM)
        }

    @Suppress("DEPRECATION")
    private fun streamUris(intent: Intent): List<Uri> =
        if (android.os.Build.VERSION.SDK_INT >= 33) {
            intent.getParcelableArrayListExtra(Intent.EXTRA_STREAM, Uri::class.java)
        } else {
            intent.getParcelableArrayListExtra(Intent.EXTRA_STREAM)
        }.orEmpty()
}

@Composable
private fun LumenApp(
    sharedPayload: SharedPayload? = null,
    onConsumeShared: () -> Unit = {},
    viewModel: MainViewModel = viewModel()
) {
    val loggedIn by viewModel.loggedIn.collectAsStateWithLifecycle()
    val themeMode by viewModel.themeMode.collectAsStateWithLifecycle()
    val busy by viewModel.busy.collectAsStateWithLifecycle()
    val busyText by viewModel.busyText.collectAsStateWithLifecycle()
    val snackbarHost = remember { SnackbarHostState() }
    val context = LocalContext.current
    val notificationPermission = rememberLauncherForActivityResult(
        ActivityResultContracts.RequestPermission()
    ) {}

    LaunchedEffect(Unit) {
        viewModel.snackbar.collect { message ->
            snackbarHost.showSnackbar(message)
        }
    }

    LaunchedEffect(loggedIn) {
        if (loggedIn && Build.VERSION.SDK_INT >= 33 &&
            ContextCompat.checkSelfPermission(context, Manifest.permission.POST_NOTIFICATIONS) !=
            PackageManager.PERMISSION_GRANTED
        ) {
            notificationPermission.launch(Manifest.permission.POST_NOTIFICATIONS)
        }
    }

    if (!loggedIn) {
        LoginScreen(
            initialServerUrl = viewModel.serverUrl.value,
            busy = busy,
            busyText = busyText,
            onLogin = viewModel::login
        )
        return
    }

    val peers by viewModel.peers.collectAsStateWithLifecycle()
    val transfers by viewModel.transfers.collectAsStateWithLifecycle()
    val shares by viewModel.shares.collectAsStateWithLifecycle()
    val relayState by viewModel.relayState.collectAsStateWithLifecycle()
    val latency by viewModel.latencyMs.collectAsStateWithLifecycle()
    val lanRunning by viewModel.lanRunning.collectAsStateWithLifecycle()
    val lanError by viewModel.lanError.collectAsStateWithLifecycle()
    val localAddresses by viewModel.localAddresses.collectAsStateWithLifecycle()
    val displayName by viewModel.displayName.collectAsStateWithLifecycle()
    val username by viewModel.username.collectAsStateWithLifecycle()
    val serverUrl by viewModel.serverUrl.collectAsStateWithLifecycle()
    val deviceName by viewModel.deviceName.collectAsStateWithLifecycle()
    val autoAccept by viewModel.autoAccept.collectAsStateWithLifecycle()
    val lanDiscovery by viewModel.lanDiscovery.collectAsStateWithLifecycle()
    val chunkSize by viewModel.chunkSize.collectAsStateWithLifecycle()

    var tab by rememberSaveable { mutableIntStateOf(0) }
    var textPeer by remember { mutableStateOf<PeerDevice?>(null) }
    var filePeer by remember { mutableStateOf<PeerDevice?>(null) }
    var imagePeer by remember { mutableStateOf<PeerDevice?>(null) }

    val filePicker = rememberLauncherForActivityResult(
        ActivityResultContracts.OpenMultipleDocuments()
    ) { uris ->
        val peer = filePeer
        filePeer = null
        if (peer != null && uris.isNotEmpty()) {
            viewModel.sendFiles(peer, uris, "file")
        }
    }
    val imagePicker = rememberLauncherForActivityResult(
        ActivityResultContracts.PickMultipleVisualMedia(20)
    ) { uris ->
        val peer = imagePeer
        imagePeer = null
        if (peer != null && uris.isNotEmpty()) {
            viewModel.sendFiles(peer, uris, "image")
        }
    }

    LifecycleEventEffect(Lifecycle.Event.ON_RESUME) {
        viewModel.refresh()
    }

    Box(Modifier.fillMaxSize()) {
        LumenBackdrop {
            Scaffold(
                containerColor = Color.Transparent,
                snackbarHost = {
                    SnackbarHost(
                        snackbarHost,
                        modifier = Modifier
                            .navigationBarsPadding()
                            .padding(bottom = 74.dp)
                    ) { data ->
                        Snackbar(
                            snackbarData = data,
                            containerColor = LumenSurfaceHigh,
                            contentColor = dev.lumen.app.ui.LumenText,
                            shape = RoundedCornerShape(14.dp)
                        )
                    }
                },
                bottomBar = {
                    val pending = transfers.count {
                        it.isIncoming && it.status == "pending"
                    }
                    NavigationBar(
                        containerColor = LumenSurface.copy(alpha = 0.97f),
                        tonalElevation = 0.dp,
                        modifier = Modifier.navigationBarsPadding()
                    ) {
                        NavigationBarItem(
                            selected = tab == 0,
                            onClick = { tab = 0 },
                            icon = { Icon(Icons.Filled.Devices, null) },
                            label = { Text("设备", fontSize = 11.sp) },
                            colors = navColors()
                        )
                        NavigationBarItem(
                            selected = tab == 1,
                            onClick = { tab = 1 },
                            icon = {
                                BadgedBox(
                                    badge = {
                                        if (pending > 0) {
                                            Badge(containerColor = LumenPrimary) {
                                                Text("$pending", color = LumenBackgroundDeep)
                                            }
                                        }
                                    }
                                ) {
                                    Icon(Icons.Filled.SwapVert, null)
                                }
                            },
                            label = { Text("传输", fontSize = 11.sp) },
                            colors = navColors()
                        )
                        NavigationBarItem(
                            selected = tab == 2,
                            onClick = { tab = 2 },
                            icon = { Icon(Icons.Filled.Link, null) },
                            label = { Text("分享", fontSize = 11.sp) },
                            colors = navColors()
                        )
                        NavigationBarItem(
                            selected = tab == 3,
                            onClick = { tab = 3 },
                            icon = { Icon(Icons.Filled.Settings, null) },
                            label = { Text("设置", fontSize = 11.sp) },
                            colors = navColors()
                        )
                    }
                }
            ) { padding ->
                Box(
                    Modifier
                        .fillMaxSize()
                        .padding(padding)
                ) {
                    when (tab) {
                        0 -> DevicesScreen(
                            peers = peers,
                            currentDeviceId = viewModel.deviceId,
                            displayName = displayName,
                            relayState = relayState,
                            latencyMs = latency,
                            lanRunning = lanRunning,
                            lanError = lanError,
                            localAddresses = localAddresses,
                            onPickFiles = { peer ->
                                filePeer = peer
                                filePicker.launch(arrayOf("*/*"))
                            },
                            onPickImages = { peer ->
                                imagePeer = peer
                                imagePicker.launch(
                                    PickVisualMediaRequest(ActivityResultContracts.PickVisualMedia.ImageOnly)
                                )
                            },
                            onSendText = { peer -> textPeer = peer },
                            onRefresh = viewModel::refresh
                        )
                        1 -> TransfersScreen(
                            transfers = transfers,
                            onAccept = viewModel::accept,
                            onDecline = viewModel::decline,
                            onCancel = viewModel::cancel,
                            onDownload = viewModel::download,
                            onSelect = { viewModel.loadDetails(it) },
                            onOpenFile = { record ->
                                openReceivedFile(context, record) { viewModel.notify(it) }
                            },
                            onClearHistory = viewModel::clearHistory
                        )
                        2 -> SharesScreen(
                            shares = shares,
                            onRefresh = viewModel::refreshShares,
                            onCreateText = viewModel::createTextShare,
                            onCreateFile = viewModel::createFileShare,
                            onRevoke = viewModel::revokeShare
                        )
                        else -> SettingsScreen(
                            displayName = displayName,
                            username = username,
                            serverUrl = serverUrl,
                            deviceName = deviceName,
                            deviceId = viewModel.deviceId,
                            autoAccept = autoAccept,
                            lanDiscovery = lanDiscovery,
                            chunkSize = chunkSize,
                            themeMode = themeMode,
                            relayState = relayState,
                            latencyMs = latency,
                            lanRunning = lanRunning,
                            localAddresses = localAddresses,
                            receivePath = viewModel.receivePath,
                            appVersion = viewModel.appVersion,
                            onUpdateDeviceName = viewModel::updateDeviceName,
                            onSetAutoAccept = viewModel::setAutoAccept,
                            onSetLanDiscovery = viewModel::setLanDiscovery,
                            onSetChunkSize = viewModel::setChunkSize,
                            onSetThemeMode = viewModel::setThemeMode,
                            onReconnect = viewModel::reconnect,
                            onLogout = viewModel::logout,
                            onClearHistory = viewModel::clearHistory,
                            onShowPath = viewModel::openPathHint
                        )
                    }
                }
            }
        }

        if (busy) {
            Row(
                modifier = Modifier
                    .align(Alignment.TopCenter)
                    .statusBarsPadding()
                    .padding(top = 8.dp)
                    .background(LumenSurface.copy(alpha = 0.96f), RoundedCornerShape(50))
                    .padding(horizontal = 15.dp, vertical = 9.dp),
                verticalAlignment = Alignment.CenterVertically
            ) {
                CircularProgressIndicator(
                    modifier = Modifier.size(15.dp),
                    strokeWidth = 2.dp,
                    color = LumenPrimary
                )
                Spacer(Modifier.width(9.dp))
                Text(
                    busyText ?: "处理中…",
                    fontSize = 12.sp,
                    color = MaterialTheme.colorScheme.onSurface
                )
            }
        }
    }

    textPeer?.let { peer ->
        SendTextDialog(
            peerName = peer.name,
            onDismiss = { textPeer = null },
            onSend = { text ->
                viewModel.sendText(peer, text)
                textPeer = null
            }
        )
    }

    sharedPayload?.let { payload ->
        SharedTargetDialog(
            payload = payload,
            peers = peers.filter {
                it.id != viewModel.deviceId && (it.online || it.lanOnline)
            },
            onDismiss = onConsumeShared,
            onSend = { peer ->
                val kind = if (payload.uris.isNotEmpty() &&
                    payload.mimeType?.startsWith("image/") == true
                ) "image" else "file"
                if (payload.uris.isNotEmpty()) {
                    viewModel.sendFiles(peer, payload.uris, kind)
                } else {
                    payload.text?.let { viewModel.sendText(peer, it) }
                }
                onConsumeShared()
            }
        )
    }
}

@Composable
private fun SharedTargetDialog(
    payload: SharedPayload,
    peers: List<PeerDevice>,
    onDismiss: () -> Unit,
    onSend: (PeerDevice) -> Unit
) {
    val summary = when {
        payload.uris.isNotEmpty() -> "${payload.uris.size} 个${if (payload.mimeType?.startsWith("image/") == true) "图片" else "文件"}"
        else -> payload.text?.take(80)?.replace('\n', ' ') ?: "文字"
    }
    AlertDialog(
        onDismissRequest = onDismiss,
        containerColor = LumenSurface,
        title = { Text("选择接收设备") },
        text = {
            Column {
                Text(
                    "来自系统分享 · $summary",
                    fontSize = 12.sp,
                    color = LumenTextMuted
                )
                Spacer(Modifier.height(10.dp))
                if (peers.isEmpty()) {
                    Text(
                        "暂无可用设备。请先登录同一账户并让另一台设备上线。",
                        fontSize = 13.sp,
                        color = LumenTextMuted
                    )
                } else {
                    Column(
                        modifier = Modifier
                            .fillMaxWidth()
                            .heightIn(max = 360.dp)
                            .verticalScroll(rememberScrollState())
                    ) {
                        peers.forEach { peer ->
                            Row(
                                modifier = Modifier
                                    .fillMaxWidth()
                                    .clickable { onSend(peer) }
                                    .padding(vertical = 10.dp, horizontal = 2.dp),
                                verticalAlignment = Alignment.CenterVertically
                            ) {
                                Column(Modifier.weight(1f)) {
                                    Text(peer.name, fontSize = 14.sp)
                                    Text(
                                        buildString {
                                            append(if (peer.platform == "windows") "Windows" else "Android")
                                            if (peer.online) append(" · 中转在线")
                                            if (peer.lanOnline) append(" · 局域网可用")
                                            if (!peer.online && !peer.lanOnline) append(" · 离线")
                                        },
                                        fontSize = 10.sp,
                                        color = LumenTextMuted
                                    )
                                }
                                if (peer.lanOnline) {
                                    Text("局域网", fontSize = 10.sp, color = LumenPrimary)
                                } else if (peer.online) {
                                    Text("中转", fontSize = 10.sp, color = LumenPrimary)
                                }
                            }
                        }
                    }
                }
            }
        },
        confirmButton = {},
        dismissButton = {
            TextButton(onClick = onDismiss) { Text("取消") }
        }
    )
}

@Composable
private fun navColors() = NavigationBarItemDefaults.colors(
    selectedIconColor = LumenBackgroundDeep,
    selectedTextColor = LumenPrimary,
    indicatorColor = LumenPrimary,
    unselectedIconColor = LumenTextMuted,
    unselectedTextColor = LumenTextMuted
)

@Composable
private fun SendTextDialog(
    peerName: String,
    onDismiss: () -> Unit,
    onSend: (String) -> Unit
) {
    var text by rememberSaveable { mutableStateOf("") }
    AlertDialog(
        onDismissRequest = onDismiss,
        containerColor = dev.lumen.app.ui.LumenSurface,
        title = { Text("发送文字给 $peerName") },
        text = {
            Column {
                OutlinedTextField(
                    value = text,
                    onValueChange = { if (it.length <= 8000) text = it },
                    placeholder = { Text("输入要发送的文字，支持 Emoji…") },
                    minLines = 5,
                    maxLines = 10,
                    modifier = Modifier.fillMaxWidth(),
                    shape = RoundedCornerShape(13.dp),
                    colors = dev.lumen.app.ui.lumenFieldColors()
                )
                Spacer(Modifier.height(6.dp))
                Text(
                    "${text.toByteArray().size} / 1048576 字节",
                    fontSize = 10.sp,
                    color = LumenTextMuted
                )
            }
        },
        confirmButton = {
            TextButton(
                onClick = { if (text.isNotBlank()) onSend(text) },
                enabled = text.isNotBlank()
            ) { Text("发送", fontWeight = FontWeight.SemiBold) }
        },
        dismissButton = {
            TextButton(onClick = onDismiss) { Text("取消") }
        }
    )
}

private fun openReceivedFile(
    context: android.content.Context,
    record: TransferRecord,
    onError: (String) -> Unit
) {
    val path = record.localPath
    if (path.isNullOrBlank()) {
        onError("此记录没有可打开的文件。")
        return
    }
    val file = File(path)
    if (!file.exists()) {
        onError("文件已不存在：$path")
        return
    }
    runCatching {
        val uri = FileProvider.getUriForFile(context, "${context.packageName}.files", file)
        val intent = Intent(Intent.ACTION_VIEW).apply {
            setDataAndType(uri, record.mimeType.ifBlank { "*/*" })
            addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_ACTIVITY_NEW_TASK)
        }
        context.startActivity(Intent.createChooser(intent, "打开 ${record.name}"))
    }.onFailure {
        Toast.makeText(context, "没有可打开此文件的应用", Toast.LENGTH_SHORT).show()
    }
}
