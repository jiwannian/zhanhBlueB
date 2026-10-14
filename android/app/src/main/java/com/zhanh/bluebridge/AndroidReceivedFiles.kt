package com.zhanh.bluebridge

import android.content.Context
import android.util.AtomicFile
import android.util.Log
import com.zhanh.bluebridge.core.*
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import java.io.File
import java.io.FileNotFoundException
import java.io.IOException

internal class AtomicReceivedHistoryStorage(private val file: File) : ReceivedHistoryStorage {
    private val atomic = AtomicFile(file)
    override fun read(): ByteArray? = try {
        atomic.openRead().use { input ->
            if (input.channel.size() > ReceivedHistoryCodec.MAX_BYTES) throw IOException("接收历史文件过大")
            input.readBytes()
        }
    } catch (e: FileNotFoundException) {
        if (file.exists() || File(file.path + ".bak").exists()) throw e
        null
    }
    override fun write(value: ByteArray) {
        val output = atomic.startWrite()
        try {
            output.write(value)
            output.fd.sync()
            atomic.finishWrite(output)
            if (read()?.contentEquals(value) != true) throw IOException("接收历史写入校验失败")
        }
        catch (e: Exception) { atomic.failWrite(output); throw e }
    }
}

/** Both initialization and publishing run under one lock, including the StateFlow update. */
object AndroidReceivedFiles {
    private val state = MutableStateFlow<List<ReceivedFileRecord>>(emptyList())
    val files = state.asStateFlow()
    private var history: ReceivedFileHistory? = null

    @Synchronized fun load(context: Context) = publish(store(context).snapshot())
    @Synchronized fun record(context: Context, record: ReceivedFileRecord) = publish(store(context).add(record))

    private fun store(context: Context): ReceivedFileHistory = history ?: ReceivedFileHistory(
        AtomicReceivedHistoryStorage(File(context.applicationContext.filesDir, "received-files.json"))
    ).also { history = it }

    private fun publish(result: ReceivedHistoryResult) {
        state.value = result.records
        result.error?.let { error ->
            Log.w("ReceivedFiles", "History storage failed", error)
            BridgeRuntime.message(error.message ?: "接收记录保存失败，重启后可能丢失。")
        }
    }
}
