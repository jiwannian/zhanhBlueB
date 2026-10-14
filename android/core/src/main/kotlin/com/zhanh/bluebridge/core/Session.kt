package com.zhanh.bluebridge.core

import java.io.*
import java.security.MessageDigest
import java.util.UUID
import java.util.concurrent.*
import java.util.concurrent.atomic.AtomicBoolean

interface SessionListener {
    fun peerHello(id: UUID, name: String)
    fun ready() {}
    fun text(id: UUID, value: String)
    /** Consumes the verified temporary file; callers must atomically publish or copy it. */
    fun received(id: UUID, info: TransferInfo, file: File)
    fun progress(id: UUID, name: String, bytes: Long, total: Long, incoming: Boolean) {}
    fun transferFailed(id: UUID, reason: String) {}
    fun closed(reason: String) {}
}

data class TransferSource(val info: TransferInfo, val open: () -> InputStream)

class Session(
    private val input: InputStream, private val output: OutputStream,
    private val deviceId: UUID, private val deviceName: String,
    private val tempDirectory: File, private val listener: SessionListener,
    private val ackTimeoutMillis: Long = 30_000
) : Closeable {
    private val stopped = AtomicBoolean(false)
    private val writeLock = Any()
    private val readyFuture = CompletableFuture<Unit>()
    private val pending = ConcurrentHashMap<UUID, Pair<String, CompletableFuture<Unit>>>()
    private val cancelled = ConcurrentHashMap.newKeySet<UUID>()
    private val sending = AtomicBoolean(false)
    private val receivedText = LinkedHashSet<UUID>()
    @Volatile private var peerId: UUID? = null
    @Volatile private var localTrust = false
    @Volatile private var remoteTrust = false
    @Volatile private var incoming: Receiving? = null
    val isReady: Boolean get() = localTrust && remoteTrust && !stopped.get()

    fun start() {
        tempDirectory.mkdirs()
        write(Frame(1, UUID.randomUUID(), Wire.json("deviceId" to deviceId, "deviceName" to deviceName.take(200))))
        Thread({ readLoop() }, "BlueBridge reader").apply { isDaemon = true; start() }
    }
    fun approve(accepted: Boolean) {
        if (peerId == null || localTrust) throw IOException("尚未收到设备身份或已确认")
        try {
            synchronized(writeLock) {
                if (accepted) localTrust = true
                write(Frame(2, UUID.randomUUID(), Wire.json("accepted" to accepted)))
            }
        } catch (e: Exception) { closeWith("信任确认发送失败"); throw e }
        if (!accepted) { closeWith("已拒绝连接"); return }
        becomeReady()
    }
    fun awaitReady() { readyFuture.get(ackTimeoutMillis, TimeUnit.MILLISECONDS) }
    fun sendText(value: String) {
        requireReady(); val bytes = value.toByteArray(Charsets.UTF_8)
        if (bytes.size > Wire.MAX_PAYLOAD) throw IOException("文字超过 1 MiB")
        write(Frame(3, UUID.randomUUID(), bytes))
    }
    fun send(source: TransferSource, id: UUID = UUID.randomUUID()) {
        requireReady(); source.info.validate()
        if (!sending.compareAndSet(false, true)) throw IOException("请等待当前文件发送完成")
        try {
            awaitAck(id, "begin") { write(Frame(4, id, source.info.json())) }
            val hash = MessageDigest.getInstance("SHA-256"); var sent = 0L
            source.open().use { stream ->
                val bytes = ByteArray(Wire.CHUNK_SIZE)
                while (true) {
                    checkCancelled(id); val count = stream.read(bytes); if (count < 0) break; if (count == 0) continue
                    sent += count; if (sent > source.info.length) throw IOException("文件在发送过程中发生变化")
                    hash.update(bytes, 0, count); write(Frame(5, id, bytes.copyOf(count)))
                    listener.progress(id, source.info.name, sent, source.info.length, false)
                }
            }
            if (sent != source.info.length) throw IOException("文件长度与声明不一致")
            checkCancelled(id)
            awaitAck(id, "complete") { write(Frame(6, id, Wire.json("sha256" to hash.digest().hex()))) }
        } catch (e: Exception) {
            try { write(Frame(8, id, "发送未完成".toByteArray())) } catch (_: IOException) { }
            throw IOException(e.cause?.message ?: e.message ?: "发送失败", e)
        } finally { pending.remove(id); cancelled.remove(id); sending.set(false) }
    }
    fun cancel(id: UUID) {
        cancelled.add(id); pending[id]?.second?.completeExceptionally(IOException("已取消"))
        synchronized(this) { if (incoming?.id == id) { incoming?.remove(); incoming = null } }
        write(Frame(8, id, "用户取消".toByteArray()))
    }
    private fun awaitAck(id: UUID, stage: String, action: () -> Unit) {
        val future = CompletableFuture<Unit>(); pending[id] = stage to future
        try { action(); future.get(ackTimeoutMillis, TimeUnit.MILLISECONDS) } finally { pending.remove(id) }
    }
    private fun checkCancelled(id: UUID) { if (cancelled.contains(id) || stopped.get()) throw IOException("传输已取消或连接已断开") }
    private fun requireReady() { if (!isReady) throw IOException("请先连接并在两端确认信任") }
    private fun write(frame: Frame) = synchronized(writeLock) {
        if (stopped.get()) throw IOException("连接已断开")
        Codec.write(output, frame)
    }
    private fun readLoop() {
        try { while (!stopped.get()) { val frame = Codec.read(input) ?: throw EOFException("电脑已断开连接"); handle(frame) } }
        catch (e: Exception) { closeWith(e.message ?: "连接中断") }
    }
    internal fun handle(frame: Frame) {
        if (frame.type in 3..7 && !isReady) throw IOException("设备尚未互相信任")
        when (frame.type) {
            1 -> hello(frame)
            2 -> trust(frame)
            3 -> { val value = Wire.text(frame.payload); if (receivedText.add(frame.id)) { if (receivedText.size > 256) receivedText.remove(receivedText.first()); listener.text(frame.id, value) } }
            4, 5, 6 -> receive(frame)
            7 -> { val stage = Wire.string(Wire.parse(frame.payload), "stage"); val wait = pending[frame.id]; if (wait != null && wait.first == stage) wait.second.complete(Unit) }
            8, 9 -> {
                val reason = Wire.text(frame.payload).take(300).ifBlank { "对方已取消传输" }
                cancelled.add(frame.id); pending[frame.id]?.second?.completeExceptionally(IOException(reason))
                synchronized(this) { if (incoming?.id == frame.id) { incoming?.remove(); incoming = null } }
                listener.transferFailed(frame.id, reason)
                if (cancelled.size > 256) cancelled.clear()
            }
            10 -> write(Frame(11, frame.id, byteArrayOf()))
            11 -> Unit
        }
    }
    private fun hello(frame: Frame) {
        if (peerId != null) throw IOException("设备重复发送身份")
        val j = Wire.parse(frame.payload)
        val id = try { UUID.fromString(Wire.string(j, "deviceId")) } catch (e: Exception) { throw IOException("设备身份不正确", e) }
        if (id.toString() != Wire.string(j, "deviceId").lowercase()) throw IOException("设备身份不正确")
        val name = Wire.string(j, "deviceName"); if (name.isBlank() || name.length > 200 || name.any { it.code < 32 }) throw IOException("设备名称不正确")
        peerId = id; listener.peerHello(id, name)
    }
    private fun trust(frame: Frame) {
        if (peerId == null || remoteTrust) throw IOException("意外的信任确认")
        val accepted = Wire.parse(frame.payload)["accepted"] ?: throw IOException("缺少信任确认")
        if (!accepted.isJsonPrimitive || !accepted.asJsonPrimitive.isBoolean) throw IOException("信任确认格式错误")
        if (!accepted.asBoolean) throw IOException("对方拒绝了连接")
        remoteTrust = true; becomeReady()
    }
    private fun becomeReady() { if (isReady && readyFuture.complete(Unit)) listener.ready() }
    private fun receive(frame: Frame) {
        var completed: Receiving? = null
        var progress: Receiving? = null
        try {
            synchronized(this) {
                val previous = incoming
                try { when (frame.type) {
                4 -> {
                    if (incoming != null) throw IOException("已有文件正在接收")
                    val info = TransferInfo.parse(frame.payload)
                    if (cancelled.contains(frame.id)) throw IOException("重复或已取消的传输")
                    incoming = Receiving(frame.id, info, File.createTempFile("bb-", ".part", tempDirectory))
                    write(Frame(7, frame.id, Wire.json("stage" to "begin")))
                    progress = incoming
                }
                5 -> {
                    val transfer = incoming?.takeIf { it.id == frame.id } ?: throw IOException("找不到接收中的文件")
                    val bytes = frame.payload; if (bytes.size > Wire.CHUNK_SIZE || transfer.length + bytes.size > transfer.info.length) throw IOException("文件块超过声明长度")
                    transfer.stream.write(bytes); transfer.hash.update(bytes); transfer.length += bytes.size
                    progress = transfer
                }
                6 -> {
                    val transfer = incoming?.takeIf { it.id == frame.id } ?: throw IOException("文件完成消息顺序错误")
                    val digest = Wire.string(Wire.parse(frame.payload), "sha256")
                    if (!Regex("[0-9a-f]{64}").matches(digest) || transfer.length != transfer.info.length || transfer.hash.digest().hex() != digest) throw IOException("文件校验失败")
                    transfer.stream.close()
                    if (transfer.info.kind == "image") transfer.file.inputStream().use { val header = ByteArray(24); if (it.read(header) != 24) throw IOException("图片过短"); Png.validate(header) }
                    completed = transfer; incoming = null
                }
                } } catch (e: Exception) {
                    if (incoming?.id == frame.id && (frame.type != 4 || incoming !== previous)) { incoming?.remove(); incoming = null }
                    throw e
                }
            }
            progress?.let { listener.progress(frame.id, it.info.name, it.length, it.info.length, true) }
            completed?.let { transfer ->
                checkCancelled(frame.id)
                // External callbacks may marshal to the UI thread. Never hold the session monitor here.
                listener.received(frame.id, transfer.info, transfer.file)
                cancelled.add(frame.id); if (cancelled.size > 256) cancelled.clear()
                write(Frame(7, frame.id, Wire.json("stage" to "complete")))
            }
        } catch (e: Exception) {
            if (!stopped.get()) write(Frame(9, frame.id, (e.message ?: "接收失败").take(300).toByteArray()))
            listener.transferFailed(frame.id, e.message ?: "接收失败")
        } finally { completed?.remove() }
    }
    override fun close() = closeWith("已停止连接")
    private fun closeWith(reason: String) {
        if (!stopped.compareAndSet(false, true)) return
        readyFuture.completeExceptionally(IOException(reason)); pending.values.forEach { it.second.completeExceptionally(IOException(reason)) }
        synchronized(this) { incoming?.remove(); incoming = null }
        try { input.close() } catch (_: IOException) { }; try { output.close() } catch (_: IOException) { }
        listener.closed(reason)
    }
    private class Receiving(val id: UUID, val info: TransferInfo, val file: File) {
        val stream = file.outputStream(); val hash = MessageDigest.getInstance("SHA-256"); var length = 0L
        fun remove() { try { stream.close() } finally { file.delete() } }
    }
}

fun ByteArray.hex(): String = joinToString("") { "%02x".format(it.toInt() and 255) }
