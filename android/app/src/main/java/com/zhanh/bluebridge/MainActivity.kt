package com.zhanh.bluebridge

import android.bluetooth.BluetoothManager
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.Bundle
import android.provider.Settings
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.core.content.ContextCompat
import androidx.core.content.edit
import androidx.core.net.toUri
import androidx.lifecycle.lifecycleScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import java.io.File
import java.io.IOException
import com.zhanh.bluebridge.core.ContentUriPolicy

class MainActivity : ComponentActivity() {
    private val devices = mutableStateOf<List<Computer>>(emptyList())
    private val selected = mutableStateOf<String?>(null)
    private var pendingConnect = false
    private var pendingShare: Intent? = null
    private var shareHandled = false
    private val permissionRequest = registerForActivityResult(ActivityResultContracts.RequestMultiplePermissions()) {
        refreshDevices()
        if (pendingConnect) { pendingConnect = false; if (BluetoothPermissions.canConnect(this)) connectSelected() else BridgeRuntime.message("蓝牙权限未获允许，可再次点击刷新设备或在应用设置中开启。") }
    }
    private val documentPicker = registerForActivityResult(ActivityResultContracts.OpenMultipleDocuments()) { uris ->
        uris.take(32).forEach { uri ->
            try { contentResolver.takePersistableUriPermission(uri, Intent.FLAG_GRANT_READ_URI_PERMISSION) } catch (_: SecurityException) { }
            BridgeRuntime.service?.enqueueUri(uri, false)
        }
        if (uris.size > 32) BridgeRuntime.message("每次最多选择 32 个文件")
    }
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        shareHandled = savedInstanceState?.getBoolean("shareHandled") ?: false
        if (!shareHandled && (intent.action == Intent.ACTION_SEND || intent.action == Intent.ACTION_SEND_MULTIPLE)) pendingShare = intent
        setContent {
            BlueBridgeTheme {
                val state by BridgeRuntime.ui.collectAsState()
                LaunchedEffect(state.connected) { if (state.connected) dispatchShare() }
                BridgeScreen(state, devices.value, selected.value, { selected.value = it }, { refreshOrRequest() }, { connectSelected() },
                    { BridgeRuntime.service?.stopConnection() }, { sendClipboard(this) }, { documentPicker.launch(arrayOf("*/*")) },
                    { startActivity(Intent(Settings.ACTION_BLUETOOTH_SETTINGS)) }, { saveImage() })
            }
        }
        refreshDevices()
    }
    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent); setIntent(intent)
        if (intent.action == Intent.ACTION_SEND || intent.action == Intent.ACTION_SEND_MULTIPLE) {
            shareHandled = false
            pendingShare = intent
            if (BridgeRuntime.ui.value.connected) dispatchShare() else BridgeRuntime.message("已收到系统分享，请先连接电脑。")
        }
    }
    override fun onResume() { super.onResume(); refreshDevices() }
    override fun onSaveInstanceState(outState: Bundle) { outState.putBoolean("shareHandled", shareHandled); super.onSaveInstanceState(outState) }
    private fun refreshOrRequest() {
        if (!BluetoothPermissions.canConnect(this)) permissionRequest.launch(BluetoothPermissions.required().toTypedArray()) else refreshDevices()
    }
    @Suppress("MissingPermission")
    private fun refreshDevices() {
        if (!BluetoothPermissions.canConnect(this)) return
        try {
            val adapter = getSystemService(BluetoothManager::class.java).adapter
            devices.value = adapter?.bondedDevices?.map { Computer(it.address, it.name ?: "已配对设备") }?.sortedBy { it.name } ?: emptyList()
            if (selected.value == null) selected.value = getSharedPreferences("ui", MODE_PRIVATE).getString("lastComputer", null)?.takeIf { saved -> devices.value.any { it.address == saved } } ?: devices.value.firstOrNull()?.address
            if (adapter?.isEnabled == false) BridgeRuntime.message("手机蓝牙已关闭，请在系统设置中打开。")
        } catch (e: SecurityException) { BridgeRuntime.message("请允许蓝牙连接权限") }
    }
    private fun connectSelected() {
        if (!BluetoothPermissions.canConnect(this)) { pendingConnect = true; permissionRequest.launch(BluetoothPermissions.required().toTypedArray()); return }
        val address = selected.value ?: run { BridgeRuntime.message("请先在系统设置中配对电脑，再刷新设备列表"); return }
        getSharedPreferences("ui", MODE_PRIVATE).edit { putString("lastComputer", address) }
        try { ContextCompat.startForegroundService(this, Intent(this, BridgeService::class.java).putExtra("address", address)) }
        catch (e: Exception) { BridgeRuntime.message(e.message ?: "无法启动蓝牙连接") }
        val notification = BluetoothPermissions.required().filter { checkSelfPermission(it) != android.content.pm.PackageManager.PERMISSION_GRANTED }
        if (notification.isNotEmpty()) permissionRequest.launch(notification.toTypedArray())
    }
    private fun saveImage() {
        val value = BridgeRuntime.ui.value.lastImage ?: return
        lifecycleScope.launch(Dispatchers.IO) {
            try {
                val file = File.createTempFile("save-", ".png", cacheDir)
                try { contentResolver.openInputStream(value.toUri())?.use { source -> file.outputStream().use { source.copyTo(it) } } ?: throw IOException("图片已失效"); AndroidFiles.saveDownloads(this@MainActivity, file, "${Product.NAME}图片-${System.currentTimeMillis()}.png", "image/png") }
                finally { file.delete() }
                BridgeRuntime.message("图片已保存到下载/${Product.NAME}")
            } catch (e: Exception) { BridgeRuntime.message(e.message ?: "保存图片失败") }
        }
    }
    @Suppress("DEPRECATION")
    private fun dispatchShare() {
        val share = pendingShare ?: return
        val service = BridgeRuntime.service ?: return
        pendingShare = null
        shareHandled = true
        val extraUris: List<Uri> = if (share.action == Intent.ACTION_SEND_MULTIPLE) share.getParcelableArrayListExtra<Uri>(Intent.EXTRA_STREAM) ?: emptyList()
        else listOfNotNull(share.getParcelableExtra<Uri>(Intent.EXTRA_STREAM))
        val uris = extraUris.ifEmpty { share.clipData?.let { clip -> (0 until minOf(clip.itemCount, 33)).mapNotNull { clip.getItemAt(it).uri } } ?: emptyList() }
        if (uris.isNotEmpty()) {
            try { uris.forEach { ContentUriPolicy.requireContent(it.scheme) } }
            catch (e: IOException) { BridgeRuntime.message(e.message ?: "分享内容未授权"); return }
            val image = share.action == Intent.ACTION_SEND && uris.size == 1 && (share.type?.startsWith("image/") == true || contentResolver.getType(uris.first())?.startsWith("image/") == true)
            uris.take(32).forEach { service.enqueueUri(it, image) }
            if (uris.size > 32) BridgeRuntime.message("每次最多分享 32 个文件")
        } else {
            val text = share.getCharSequenceExtra(Intent.EXTRA_TEXT)?.toString()
            if (text != null) service.enqueueText(text) else BridgeRuntime.message("分享内容无法读取，请使用选择文件发送")
        }
    }
}

fun sendClipboard(activity: ComponentActivity) {
    if (!activity.hasWindowFocus()) { BridgeRuntime.message("请在 ${Product.NAME} 页面获得焦点后发送剪贴板"); return }
    val service = BridgeRuntime.service ?: run { BridgeRuntime.message("请先连接电脑"); return }
    if (!BridgeRuntime.ui.value.connected) { BridgeRuntime.message("请先连接电脑并在两端确认信任"); return }
    try {
        val clip = (activity.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager).primaryClip
        if (clip == null || clip.itemCount == 0) { BridgeRuntime.message("剪贴板为空，或系统限制读取；图片可通过系统分享发送。"); return }
        val item = clip.getItemAt(0)
        if (item.uri != null) {
            val image = clip.description.hasMimeType("image/*") || activity.contentResolver.getType(item.uri)?.startsWith("image/") == true
            if (image) service.enqueueUri(item.uri, true) else BridgeRuntime.message("此剪贴板内容不能直接发送，请通过系统分享或选择文件。")
        } else if (item.text != null) service.enqueueText(item.text.toString())
        else BridgeRuntime.message("系统没有提供可读取的文字或图片，请使用系统分享。")
    } catch (e: Exception) { BridgeRuntime.message(e.message ?: "无法读取剪贴板，请使用系统分享。") }
}

@Composable
fun BlueBridgeTheme(content: @Composable () -> Unit) {
    MaterialTheme(colorScheme = lightColorScheme(primary = Color(0xFF2563EB), onPrimary = Color.White, background = Color(0xFFF5F7FB), surface = Color.White, onSurface = Color(0xFF152A4A), secondary = Color(0xFF59708C)), content = content)
}

@Composable
private fun BridgeScreen(
    state: BridgeUiState, devices: List<Computer>, selected: String?, choose: (String) -> Unit,
    refresh: () -> Unit, connect: () -> Unit, disconnect: () -> Unit, clipboard: () -> Unit, files: () -> Unit, settings: () -> Unit, saveImage: () -> Unit
) {
    Surface(modifier = Modifier.fillMaxSize(), color = MaterialTheme.colorScheme.background) {
        LazyColumn(contentPadding = PaddingValues(horizontal = 22.dp, vertical = 28.dp), verticalArrangement = Arrangement.spacedBy(16.dp), modifier = Modifier.safeDrawingPadding()) {
            item {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Surface(shape = RoundedCornerShape(18.dp), color = MaterialTheme.colorScheme.primary, modifier = Modifier.size(52.dp)) { Box(contentAlignment = Alignment.Center) { Text("Z", fontSize = 26.sp, color = Color.White, fontWeight = FontWeight.Bold) } }
                    Column(Modifier.padding(start = 14.dp)) { Text(Product.NAME, fontSize = 27.sp, fontWeight = FontWeight.Bold); Text("离线蓝牙互传", color = MaterialTheme.colorScheme.secondary, fontSize = 13.sp) }
                }
            }
            item {
                BridgeCard {
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Box(Modifier.size(9.dp).background(if (state.connected) Color(0xFF14A77A) else Color(0xFFB2BED0), RoundedCornerShape(9.dp)))
                        Text(state.status, Modifier.padding(start = 8.dp), fontWeight = FontWeight.SemiBold)
                    }
                    Spacer(Modifier.height(14.dp))
                    if (state.connected || state.connecting) {
                        Text(state.computer?.name ?: "蓝牙电脑", fontSize = 20.sp, fontWeight = FontWeight.Bold)
                        Text(state.computer?.address ?: "", color = MaterialTheme.colorScheme.secondary, fontSize = 12.sp)
                        OutlinedButton(onClick = disconnect, modifier = Modifier.fillMaxWidth().padding(top = 12.dp)) { Text("停止连接") }
                    } else {
                        Text("选择已配对的电脑", fontWeight = FontWeight.Bold)
                        if (devices.isEmpty()) Text("暂无已配对设备", color = MaterialTheme.colorScheme.secondary, modifier = Modifier.padding(vertical = 10.dp))
                        devices.forEach { device ->
                            Surface(onClick = { choose(device.address) }, shape = RoundedCornerShape(12.dp), color = if (selected == device.address) Color(0xFFEDF3FF) else Color.White, modifier = Modifier.fillMaxWidth()) {
                                Row(Modifier.padding(vertical = 8.dp), verticalAlignment = Alignment.CenterVertically) {
                                    RadioButton(selected == device.address, onClick = { choose(device.address) })
                                    Column { Text(device.name, fontWeight = FontWeight.Medium); Text(device.address, fontSize = 11.sp, color = MaterialTheme.colorScheme.secondary) }
                                }
                            }
                        }
                        Button(onClick = connect, modifier = Modifier.fillMaxWidth().padding(top = 8.dp), enabled = selected != null) { Text("连接电脑") }
                        Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween) { TextButton(onClick = refresh) { Text("刷新设备") }; TextButton(onClick = settings) { Text("系统蓝牙设置") } }
                    }
                }
            }
            item {
                BridgeCard {
                    Text("发送到电脑", fontSize = 18.sp, fontWeight = FontWeight.Bold)
                    Text("手机手动发送，电脑复制的内容自动接收", color = MaterialTheme.colorScheme.secondary, fontSize = 12.sp, modifier = Modifier.padding(top = 5.dp, bottom = 14.dp))
                    Button(onClick = clipboard, enabled = state.connected, modifier = Modifier.fillMaxWidth()) { Text("发送剪贴板") }
                    OutlinedButton(onClick = files, enabled = state.connected, modifier = Modifier.fillMaxWidth().padding(top = 6.dp)) { Text("选择文件 · 支持多选") }
                    if (state.lastImage != null) TextButton(onClick = saveImage, modifier = Modifier.fillMaxWidth()) { Text("另存最近收到的图片") }
                }
            }
            item { Text(state.notice, color = MaterialTheme.colorScheme.secondary, fontSize = 13.sp, lineHeight = 20.sp, modifier = Modifier.padding(horizontal = 3.dp)) }
            if (state.transfers.isNotEmpty()) item { Text("传输记录", fontSize = 18.sp, fontWeight = FontWeight.Bold) }
            items(state.transfers, key = { it.id }) { row ->
                BridgeCard {
                    Text((if (row.incoming) "↓ " else "↑ ") + row.name, fontWeight = FontWeight.Medium, maxLines = 2)
                    if (row.status in listOf("接收中", "发送中")) {
                        LinearProgressIndicator(progress = { if (row.total == 0L) 0f else (row.bytes.toFloat() / row.total).coerceIn(0f, 1f) }, modifier = Modifier.fillMaxWidth().padding(vertical = 10.dp))
                    }
                    Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.SpaceBetween) {
                        Text(if (row.status in listOf("接收中", "发送中")) "${formatSize(row.bytes)} / ${formatSize(row.total)}" else row.status, fontSize = 12.sp, color = MaterialTheme.colorScheme.secondary, modifier = Modifier.weight(1f))
                        if (row.status in listOf("接收中", "发送中", "排队中")) TextButton(onClick = { BridgeRuntime.service?.cancel(row.id) }) { Text("取消") }
                        if (row.retryable) TextButton(onClick = { BridgeRuntime.service?.retry(row.id) }) { Text("重试") }
                    }
                }
            }
            item { Text("接收的文件保存于 下载 / ${Product.NAME}\n仅使用蓝牙 · 无需网络和账号", fontSize = 12.sp, lineHeight = 19.sp, color = MaterialTheme.colorScheme.secondary) }
        }
        state.trust?.let { prompt ->
            AlertDialog(onDismissRequest = { }, title = { Text("信任这台电脑？") },
                text = { Text("${prompt.name}\n蓝牙地址：${prompt.address}\n\n请同时在 Windows 确认信任。确认后可自动接收剪贴板和文件。") },
                confirmButton = { TextButton(onClick = { BridgeRuntime.service?.approve(true) }) { Text("信任并连接") } },
                dismissButton = { TextButton(onClick = { BridgeRuntime.service?.approve(false) }) { Text("拒绝") } })
        }
    }
}

@Composable
private fun BridgeCard(content: @Composable ColumnScope.() -> Unit) {
    Card(shape = RoundedCornerShape(22.dp), colors = CardDefaults.cardColors(containerColor = Color.White), modifier = Modifier.fillMaxWidth()) { Column(Modifier.padding(18.dp), content = content) }
}
private fun formatSize(bytes: Long) = when { bytes >= 1_048_576 -> "%.1f MiB".format(bytes / 1_048_576.0); bytes >= 1024 -> "%.1f KiB".format(bytes / 1024.0); else -> "$bytes B" }
