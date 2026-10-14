package com.zhanh.bluebridge

import android.app.*
import android.bluetooth.BluetoothManager
import android.bluetooth.BluetoothSocket
import android.content.Intent
import android.content.pm.ServiceInfo
import android.net.Uri
import android.os.Build
import android.os.IBinder
import androidx.core.app.NotificationCompat
import androidx.core.content.edit
import com.zhanh.bluebridge.core.*
import kotlinx.coroutines.*
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.sync.Semaphore
import kotlinx.coroutines.sync.withPermit
import java.io.File
import java.io.IOException
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.CountDownLatch
import java.util.concurrent.Executors
import java.util.concurrent.TimeUnit

class BridgeService : Service() {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val queue = Channel<SendItem>(32)
    private val preparation = Semaphore(2)
    private val preparations = java.util.concurrent.atomic.AtomicInteger()
    private val retryFiles get() = BridgeRuntime.retryFiles
    private val cancelledIds = ConcurrentHashMap.newKeySet<UUID>()
    private val timeout = Executors.newSingleThreadScheduledExecutor()
    @Volatile private var requested = false
    @Volatile private var session: Session? = null
    @Volatile private var socket: BluetoothSocket? = null
    @Volatile private var generation = 0L
    @Volatile private var activeItem: SendItem? = null
    private var connectionJob: Job? = null
    private var address: String = ""
    private val preferences by lazy { getSharedPreferences("identity", MODE_PRIVATE) }
    private val identity: UUID by lazy {
        val existing = preferences.getString("deviceId", null)
        if (existing != null) UUID.fromString(existing) else UUID.randomUUID().also { preferences.edit { putString("deviceId", it.toString()) } }
    }
    override fun onCreate() {
        super.onCreate(); BridgeRuntime.service = this
        scope.launch { AndroidReceivedFiles.load(this@BridgeService) }
        getSystemService(NotificationManager::class.java).createNotificationChannel(NotificationChannel(CHANNEL, "${Product.NAME} 蓝牙连接", NotificationManager.IMPORTANCE_LOW))
        File(cacheDir, "incoming").apply { mkdirs(); listFiles()?.forEach { it.delete() } }
        if (!BridgeRuntime.cacheInitialized) {
            File(cacheDir, "outgoing").listFiles()?.forEach { it.delete() }
            BridgeRuntime.cacheInitialized = true
        }
        scope.launch {
            for (item in queue) {
                if (cancelledIds.remove(item.id)) { item.file?.delete(); continue }
                activeItem = item
                val connection = session
                if (connection?.isReady != true) { fail(item, "连接已断开，请重新连接后重试"); activeItem = null; continue }
                try {
                    BridgeRuntime.progress(item.id, item.name, 0, item.info?.length ?: 0, false)
                    if (item.text != null) connection.sendText(item.text)
                    else connection.send(TransferSource(item.info!!) { item.file!!.inputStream() }, item.id)
                    BridgeRuntime.finish(item.id, "已发送"); retryFiles.remove(item.id); item.file?.delete()
                } catch (e: Exception) { fail(item, e.message ?: "发送失败") }
                finally { activeItem = null }
            }
        }
    }
    override fun onBind(intent: Intent?): IBinder? = null
    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent?.action == STOP) { stopConnection(); return START_NOT_STICKY }
        if (!BluetoothPermissions.canConnect(this)) { BridgeRuntime.message("请允许蓝牙连接权限"); stopSelf(); return START_NOT_STICKY }
        startForeground(NOTIFICATION, notification("正在连接蓝牙电脑"), ServiceInfo.FOREGROUND_SERVICE_TYPE_CONNECTED_DEVICE)
        val selected = intent?.getStringExtra("address")
        if (selected.isNullOrBlank()) { stopConnection(); return START_NOT_STICKY }
        if (requested && address == selected) return START_NOT_STICKY
        val token = ++generation
        connectionJob?.cancel(); releaseCurrentConnection()
        address = selected; requested = true
        connectionJob = scope.launch { connectLoop(selected, token) }
        return START_NOT_STICKY
    }
    @Suppress("MissingPermission")
    private suspend fun connectLoop(selected: String, token: Long) {
        var failures = 0
        while (requested && currentCoroutineContext().isActive && generation == token && address == selected && failures < 6) {
            var connection: Session? = null
            var localSocket: BluetoothSocket? = null
            try {
                val adapter = getSystemService(BluetoothManager::class.java).adapter ?: throw IOException("此手机没有蓝牙适配器")
                if (!adapter.isEnabled) throw IOException("请打开手机蓝牙")
                val device = adapter.bondedDevices.find { it.address == selected } ?: throw IOException("电脑尚未配对，请到系统蓝牙设置配对")
                val computer = Computer(selected, device.name ?: "已配对电脑")
                BridgeRuntime.update { it.copy(computer = computer, connecting = true, connected = false, trust = null, status = if (failures == 0) "正在连接" else "正在重连 (${failures}/6)") }
                val connectedSocket = device.createRfcommSocketToServiceRecord(Wire.SERVICE_UUID)
                localSocket = connectedSocket
                if (generation != token) { connectedSocket.close(); break }
                socket = connectedSocket
                val deadline = timeout.schedule({ try { connectedSocket.close() } catch (_: Exception) { } }, 15, TimeUnit.SECONDS)
                try { connectedSocket.connect() } finally { deadline.cancel(false) }
                if (!requested || generation != token) { connectedSocket.close(); break }
                val closed = CountDownLatch(1)
                val listener = object : SessionListener {
                    override fun peerHello(id: UUID, name: String) {
                        if (generation != token) return
                        if (preferences.getBoolean("trusted:$selected:$id", false)) connection!!.approve(true)
                        else BridgeRuntime.update { it.copy(trust = TrustPrompt(id, name, selected, token), status = "等待两端确认信任") }
                    }
                    override fun ready() {
                        if (generation != token) return
                        failures = 0
                        BridgeRuntime.update { it.copy(connected = true, connecting = false, trust = null, status = "已连接", notice = "电脑复制的文字与图片会自动到达手机；手机请手动发送。") }
                        getSystemService(NotificationManager::class.java).notify(NOTIFICATION, notification("已连接 ${computer.name}"))
                    }
                    override fun text(id: UUID, value: String) {
                        AndroidFiles.clipboardText(this@BridgeService, value) { requested && generation == token && session === connection }
                        if (generation == token) BridgeRuntime.message("已收到文字并写入剪贴板")
                    }
                    override fun received(id: UUID, info: TransferInfo, file: File) {
                        if (!requested || generation != token) throw IOException("连接已停止")
                        if (info.kind == "image") {
                            val uri = AndroidFiles.clipboardImage(this@BridgeService, file) { requested && generation == token && session === connection }
                            BridgeRuntime.update { it.copy(lastImage = uri.toString(), notice = "图片已写入剪贴板，可粘贴或另存到下载目录。") }
                            BridgeRuntime.finish(id, "已写入剪贴板")
                        } else {
                            val saved = AndroidFiles.saveDownloads(this@BridgeService, file, info.name, info.mime)
                            val record = ReceivedFileRecord(id, saved.uri.toString(), saved.name, saved.mime, info.length, System.currentTimeMillis())
                            BridgeRuntime.finish(id, RECEIVED_FILE_STATUS, receivedFile = record)
                            // History is supplementary: never turn an already saved file into a failed transfer.
                            try { AndroidReceivedFiles.record(this@BridgeService, record) }
                            catch (e: Exception) {
                                android.util.Log.w("ReceivedFiles", "Cannot record received file", e)
                                BridgeRuntime.message("文件已接收，但记录保存失败；重启后可能找不到这条记录。")
                            }
                        }
                    }
                    override fun progress(id: UUID, name: String, bytes: Long, total: Long, incoming: Boolean) { if (generation == token) BridgeRuntime.progress(id, name, bytes, total, incoming) }
                    override fun transferFailed(id: UUID, reason: String) { if (generation == token) { BridgeRuntime.finish(id, reason); BridgeRuntime.message(reason) } }
                    override fun closed(reason: String) {
                        if (generation != token) { closed.countDown(); return }
                        BridgeRuntime.update { it.copy(connected = false, connecting = requested, trust = null, status = reason) }
                        closed.countDown()
                        if (reason.contains("拒绝") || reason.contains("信任")) requested = false
                    }
                }
                connection = Session(connectedSocket.inputStream, connectedSocket.outputStream, identity, (Build.MODEL ?: "Android 手机").take(200), File(cacheDir, "incoming"), listener)
                session = connection
                connection.start()
                // Confirmation has a bounded window; after trust, the read loop owns the session.
                try { connection.awaitReady() } catch (e: Exception) { throw IOException("两端信任确认超时或被拒绝", e) }
                while (requested && generation == token && currentCoroutineContext().isActive && !closed.await(1, TimeUnit.SECONDS)) { }
                if (requested) throw IOException("蓝牙连接中断")
            } catch (e: Exception) {
                if (e is CancellationException) throw e
                if (!requested || generation != token) break
                failures++
                BridgeRuntime.update { it.copy(connected = false, connecting = failures < 6, trust = null, status = e.message ?: "连接失败") }
            } finally {
                connection?.close(); if (session === connection) session = null
                try { localSocket?.close() } catch (_: Exception) { }
                if (socket === localSocket) socket = null
            }
            if (requested && generation == token && failures < 6) delay(minOf(30_000L, 1_000L shl failures))
        }
        if (generation == token && address == selected) { requested = false; BridgeRuntime.update { it.copy(connected = false, connecting = false, trust = null, status = if (failures >= 6) "重连已停止，请手动连接" else "已停止连接") }; stopForeground(STOP_FOREGROUND_REMOVE); stopSelf() }
    }
    fun approve(accepted: Boolean) {
        val prompt = BridgeRuntime.ui.value.trust ?: return
        val connection = session ?: return
        if (prompt.generation != generation) return
        scope.launch {
            if (prompt.generation != generation || session !== connection) return@launch
            try { connection.approve(accepted); if (accepted) preferences.edit { putBoolean("trusted:${prompt.address}:${prompt.id}", true) } else if (prompt.generation == generation) { requested = false; stopConnection() } }
            catch (e: Exception) { BridgeRuntime.message(e.message ?: "确认失败") }
            BridgeRuntime.update { if (it.trust == prompt) it.copy(trust = null) else it }
        }
    }
    fun enqueueText(text: String) {
        if (session?.isReady != true) { BridgeRuntime.message("请先连接电脑并确认信任"); return }
        if (text.toByteArray().size > Wire.MAX_PAYLOAD) { BridgeRuntime.message("文字超过 1 MiB"); return }
        enqueue(SendItem(UUID.randomUUID(), "剪贴板文字", text = text))
    }
    fun enqueueUri(uri: Uri, image: Boolean) {
        if (session?.isReady != true) { BridgeRuntime.message("请先连接电脑，然后重新分享或选择文件"); return }
        if (preparations.incrementAndGet() > 32) { preparations.decrementAndGet(); BridgeRuntime.message("准备队列已满，请稍后重试"); return }
        val token = generation
        scope.launch {
            try { preparation.withPermit { val (info, file) = AndroidFiles.stage(this@BridgeService, uri, image) { requested && generation == token }; enqueue(SendItem(UUID.randomUUID(), info.name, info = info, file = file)) } }
            catch (e: Exception) { BridgeRuntime.message(e.message ?: "无法准备发送内容") }
            finally { preparations.decrementAndGet() }
        }
    }
    private fun enqueue(item: SendItem) {
        if (queue.trySend(item).isSuccess) BridgeRuntime.transfer(TransferRow(item.id, item.name, total = item.info?.length ?: 0))
        else { item.file?.delete(); BridgeRuntime.message("发送队列已满，请稍后重试") }
    }
    private fun fail(item: SendItem, reason: String) {
        if (item.file != null && item.file.exists() && !cancelledIds.remove(item.id)) {
            if (retryFiles.size >= 30) { val old = retryFiles.keys.firstOrNull(); if (old != null) retryFiles.remove(old)?.file?.delete() }
            retryFiles[item.id] = item
            BridgeRuntime.finish(item.id, reason, true)
        } else { item.file?.delete(); BridgeRuntime.finish(item.id, reason) }
    }
    fun retry(id: UUID) { val item = retryFiles.remove(id) ?: return; if (session?.isReady == true) { BridgeRuntime.finish(id, "已重新排队"); enqueue(item.copy(id = UUID.randomUUID())) } else { retryFiles[id] = item; BridgeRuntime.message("请先重新连接电脑") } }
    fun cancel(id: UUID) {
        cancelledIds.add(id); retryFiles.remove(id)?.file?.delete()
        scope.launch { try { session?.cancel(id) } catch (_: IOException) { }; BridgeRuntime.finish(id, "已取消") }
    }
    fun stopConnection() {
        requested = false; generation++; connectionJob?.cancel(); releaseCurrentConnection()
        BridgeRuntime.update { it.copy(connected = false, connecting = false, trust = null, status = "已停止连接") }
        stopForeground(STOP_FOREGROUND_REMOVE); stopSelf()
    }
    private fun notification(text: String): Notification {
        val main = PendingIntent.getActivity(this, 0, Intent(this, MainActivity::class.java), PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        val send = PendingIntent.getActivity(this, 1, Intent(this, SendClipboardActivity::class.java), PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        val stop = PendingIntent.getService(this, 2, Intent(this, BridgeService::class.java).setAction(STOP), PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT)
        return NotificationCompat.Builder(this, CHANNEL).setSmallIcon(R.drawable.ic_bridge).setContentTitle(Product.NAME).setContentText(text).setContentIntent(main).setOngoing(true).addAction(0, "发送剪贴板", send).addAction(0, "停止连接", stop).build()
    }
    override fun onDestroy() {
        requested = false; generation++; releaseCurrentConnection()
        scope.cancel(); queue.close(); timeout.shutdownNow()
        activeItem?.let { fail(it, "连接已停止，请重新连接后重试") }
        while (true) { val item = queue.tryReceive().getOrNull() ?: break; fail(item, "连接已停止，请重新连接后重试") }
        BridgeRuntime.update { state -> state.copy(transfers = state.transfers.map { if (it.status in listOf("接收中", "发送中", "排队中")) it.copy(status = "连接已停止", retryable = retryFiles.containsKey(it.id)) else it }) }
        if (BridgeRuntime.service === this) BridgeRuntime.service = null
        super.onDestroy()
    }
    private fun releaseCurrentConnection() {
        val oldSession = session; val oldSocket = socket
        session = null; socket = null
        // This cleanup must survive scope.cancel(), and must not block the Android main thread.
        Thread({ try { oldSocket?.close() } catch (_: Exception) { }; oldSession?.close() }, "BlueBridge close").apply { isDaemon = true; start() }
    }
    companion object { const val STOP = "com.zhanh.bluebridge.STOP"; private const val CHANNEL = "bluetooth_connection"; private const val NOTIFICATION = 100 }
}
