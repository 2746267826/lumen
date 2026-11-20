package dev.lumen.app.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material.icons.Icons
import androidx.compose.material.icons.filled.ArrowForward
import androidx.compose.material.icons.filled.Dns
import androidx.compose.material.icons.filled.Lock
import androidx.compose.material.icons.filled.Person
import androidx.compose.material.icons.filled.Visibility
import androidx.compose.material.icons.filled.VisibilityOff
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.OutlinedTextFieldDefaults
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.text.input.VisualTransformation
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp

@Composable
fun LoginScreen(
    initialServerUrl: String,
    busy: Boolean,
    busyText: String?,
    onLogin: (serverUrl: String, username: String, password: String, register: Boolean, displayName: String) -> Unit
) {
    var serverUrl by rememberSaveable { mutableStateOf(initialServerUrl) }
    var username by rememberSaveable { mutableStateOf("") }
    var displayName by rememberSaveable { mutableStateOf("") }
    var password by rememberSaveable { mutableStateOf("") }
    var register by rememberSaveable { mutableStateOf(false) }
    var showPassword by rememberSaveable { mutableStateOf(false) }

    Box(
        modifier = Modifier
            .fillMaxSize()
            .background(
                Brush.verticalGradient(
                    listOf(LumenBackgroundDeep, LumenBackground, Color(0xFF0A1529))
                )
            )
    ) {
        Box(
            modifier = Modifier
                .align(Alignment.TopCenter)
                .size(430.dp)
                .background(
                    Brush.radialGradient(
                        listOf(LumenPrimary.copy(alpha = 0.16f), Color.Transparent)
                    )
                )
        )
        Column(
            modifier = Modifier
                .fillMaxSize()
                .verticalScroll(rememberScrollState())
                .statusBarsPadding()
                .imePadding()
                .padding(horizontal = 24.dp, vertical = 28.dp),
            horizontalAlignment = Alignment.CenterHorizontally
        ) {
            Spacer(Modifier.height(28.dp))
            Box(
                modifier = Modifier
                    .size(76.dp)
                    .background(
                        Brush.linearGradient(listOf(LumenPrimary, LumenSecondary)),
                        RoundedCornerShape(24.dp)
                    ),
                contentAlignment = Alignment.Center
            ) {
                Text(
                    "L",
                    fontSize = 38.sp,
                    fontWeight = FontWeight.Bold,
                    color = LumenBackgroundDeep
                )
            }
            Spacer(Modifier.height(18.dp))
            Text(
                "Lumen",
                style = MaterialTheme.typography.headlineLarge,
                color = LumenText
            )
            Spacer(Modifier.height(6.dp))
            Text(
                "设备之间，一束光就够了",
                color = LumenTextMuted,
                fontSize = 14.sp
            )
            Spacer(Modifier.height(30.dp))

            LumenCard(Modifier.fillMaxWidth()) {
                Column {
                    Text(
                        if (register) "创建账户" else "登录 Lumen",
                        style = MaterialTheme.typography.titleLarge
                    )
                    Spacer(Modifier.height(5.dp))
                    Text(
                        "登录后设备会自动绑定，重连时无需在局域网内反复搜索。",
                        style = MaterialTheme.typography.bodyMedium,
                        color = LumenTextMuted
                    )
                    Spacer(Modifier.height(18.dp))
                    LumenField(
                        value = serverUrl,
                        onValueChange = { serverUrl = it },
                        label = "服务器地址",
                        placeholder = "https://lumen.example.com",
                        icon = Icons.Filled.Dns,
                        keyboardType = KeyboardType.Uri
                    )
                    Spacer(Modifier.height(11.dp))
                    LumenField(
                        value = username,
                        onValueChange = { username = it },
                        label = "用户名",
                        placeholder = "3-32 位字母或数字",
                        icon = Icons.Filled.Person
                    )
                    if (register) {
                        Spacer(Modifier.height(11.dp))
                        LumenField(
                            value = displayName,
                            onValueChange = { displayName = it },
                            label = "显示名称（可选）",
                            placeholder = "在设备列表中展示的名字",
                            icon = Icons.Filled.Person
                        )
                    }
                    Spacer(Modifier.height(11.dp))
                    OutlinedTextField(
                        value = password,
                        onValueChange = { password = it },
                        label = { Text("密码") },
                        placeholder = { Text("至少 8 个字符") },
                        singleLine = true,
                        leadingIcon = {
                            Icon(Icons.Filled.Lock, null, tint = LumenTextMuted)
                        },
                        trailingIcon = {
                            IconButton(onClick = { showPassword = !showPassword }) {
                                Icon(
                                    if (showPassword) Icons.Filled.VisibilityOff else Icons.Filled.Visibility,
                                    contentDescription = "显示密码",
                                    tint = LumenTextMuted
                                )
                            }
                        },
                        visualTransformation = if (showPassword) {
                            VisualTransformation.None
                        } else PasswordVisualTransformation(),
                        keyboardOptions = androidx.compose.foundation.text.KeyboardOptions(
                            keyboardType = KeyboardType.Password,
                            imeAction = ImeAction.Done
                        ),
                        modifier = Modifier.fillMaxWidth(),
                        shape = RoundedCornerShape(14.dp),
                        colors = lumenFieldColors()
                    )
                    Spacer(Modifier.height(18.dp))
                    Button(
                        onClick = { onLogin(serverUrl, username, password, register, displayName) },
                        enabled = !busy,
                        modifier = Modifier
                            .fillMaxWidth()
                            .height(52.dp),
                        shape = RoundedCornerShape(15.dp),
                        colors = ButtonDefaults.buttonColors(
                            containerColor = LumenPrimary,
                            contentColor = LumenBackgroundDeep
                        )
                    ) {
                        if (busy) {
                            CircularProgressIndicator(
                                modifier = Modifier.size(19.dp),
                                strokeWidth = 2.dp,
                                color = LumenBackgroundDeep
                            )
                            Spacer(Modifier.size(9.dp))
                            Text(busyText ?: "正在连接…")
                        } else {
                            Text(if (register) "注册并绑定" else "登录")
                            Spacer(Modifier.size(7.dp))
                            Icon(Icons.Filled.ArrowForward, null, modifier = Modifier.size(18.dp))
                        }
                    }
                    Spacer(Modifier.height(13.dp))
                    Row(
                        modifier = Modifier.fillMaxWidth(),
                        horizontalArrangement = Arrangement.Center
                    ) {
                        Text(
                            if (register) "已有账户？" else "还没有账户？",
                            color = LumenTextMuted,
                            fontSize = 13.sp
                        )
                        Text(
                            if (register) "直接登录" else "创建账户",
                            color = LumenPrimary,
                            fontSize = 13.sp,
                            fontWeight = FontWeight.SemiBold,
                            modifier = Modifier
                                .padding(start = 5.dp)
                                .clickable { register = !register }
                        )
                    }
                }
            }

            Spacer(Modifier.height(20.dp))
            Text(
                "局域网内自动直连 · 跨网络自动中转\n文件分块校验，传输进度与链路全程可见",
                color = LumenTextMuted.copy(alpha = 0.8f),
                fontSize = 12.sp,
                textAlign = TextAlign.Center,
                lineHeight = 19.sp
            )
            Spacer(Modifier.height(24.dp))
        }
    }
}

@Composable
fun LumenField(
    value: String,
    onValueChange: (String) -> Unit,
    label: String,
    placeholder: String,
    icon: androidx.compose.ui.graphics.vector.ImageVector,
    keyboardType: KeyboardType = KeyboardType.Text
) {
    OutlinedTextField(
        value = value,
        onValueChange = onValueChange,
        label = { Text(label) },
        placeholder = { Text(placeholder, color = LumenTextMuted.copy(alpha = 0.65f)) },
        singleLine = true,
        leadingIcon = { Icon(icon, null, tint = LumenTextMuted) },
        keyboardOptions = androidx.compose.foundation.text.KeyboardOptions(
            keyboardType = keyboardType,
            imeAction = ImeAction.Next
        ),
        modifier = Modifier.fillMaxWidth(),
        shape = RoundedCornerShape(14.dp),
        colors = lumenFieldColors()
    )
}

@Composable
fun lumenFieldColors() = OutlinedTextFieldDefaults.colors(
    focusedTextColor = LumenText,
    unfocusedTextColor = LumenText,
    focusedBorderColor = LumenPrimary,
    unfocusedBorderColor = LumenBorder,
    focusedLabelColor = LumenPrimary,
    unfocusedLabelColor = LumenTextMuted,
    cursorColor = LumenPrimary,
    focusedContainerColor = LumenSurfaceHigh.copy(alpha = 0.55f),
    unfocusedContainerColor = LumenSurfaceHigh.copy(alpha = 0.35f)
)
