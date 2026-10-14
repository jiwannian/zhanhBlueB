package com.zhanh.bluebridge

import android.Manifest
import android.content.Context
import android.os.Build
import com.zhanh.bluebridge.core.TransferInfo
import com.zhanh.bluebridge.core.ReceivedFileRecord
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.asStateFlow
import java.util.UUID
import java.io.File
import java.util.concurrent.ConcurrentHashMap

data class Computer(val address: String, val name: String)
data class TrustPrompt(val id: UUID, val name: String, val address: String, val generation: Long)
data class SendItem(val id: UUID, val name: String, val text: String? = null, val info: TransferInfo? = null, val file: File? = null)
const val RECEIVED_FILE_STATUS = "已保存到下载/${Product.NAME}"
data class TransferRow(val id: UUID, val name: String, val bytes: Long = 0, val total: Long = 0, val incoming: Boolean = false, val status: String = "排队中", val retryable: Boolean = false, val receivedFile: ReceivedFileRecord? = null)
fun canOpenReceivedFile(row: TransferRow) = row.incoming && row.status == RECEIVED_FILE_STATUS && row.receivedFile?.id == row.id
fun displayTransfers(current: List<TransferRow>, history: List<ReceivedFileRecord>): List<TransferRow> {
    val currentIds = current.map { it.id }.toSet()
    return current + history.filterNot { it.id in currentIds }.map { file ->
        TransferRow(file.id, file.name, file.size, file.size, true, RECEIVED_FILE_STATUS, receivedFile = file)
    }
}
data class BridgeUiState(
    val status: String = "尚未连接", val connected: Boolean = false, val connecting: Boolean = false,
    val computer: Computer? = null, val trust: TrustPrompt? = null, val transfers: List<TransferRow> = emptyList(),
    val lastImage: String? = null, val notice: String = "先在手机与 Windows 设置中配对蓝牙，再选择电脑连接。"
)

object BridgeRuntime {
    private val state = MutableStateFlow(BridgeUiState())
    val ui = state.asStateFlow()
    @Volatile var service: BridgeService? = null
    val retryFiles = ConcurrentHashMap<UUID, SendItem>()
    @Volatile var cacheInitialized = false
    fun update(action: (BridgeUiState) -> BridgeUiState) = synchronized(this) { state.value = action(state.value) }
    fun message(value: String) = update { it.copy(notice = value) }
    fun transfer(row: TransferRow) = update { current ->
        current.copy(transfers = (listOf(row) + current.transfers.filterNot { it.id == row.id }).take(30))
    }
    fun progress(id: UUID, name: String, bytes: Long, total: Long, incoming: Boolean) = update { current ->
        val previous = current.transfers.find { it.id == id }
        val row = TransferRow(id, name, bytes, total, incoming, if (incoming) "接收中" else "发送中", false)
        current.copy(transfers = if (previous == null) (listOf(row) + current.transfers).take(30) else current.transfers.map { if (it.id == id) row else it })
    }
    fun finish(id: UUID, status: String, retryable: Boolean = false, receivedFile: ReceivedFileRecord? = null) = update { current ->
        current.copy(transfers = current.transfers.map {
            if (it.id == id) it.copy(name = receivedFile?.name ?: it.name, status = status, retryable = retryable, receivedFile = receivedFile) else it
        })
    }
}

object BluetoothPermissions {
    @android.annotation.SuppressLint("InlinedApi") // Explicit SDK policy is also exercised for API 29/31/33/36 in JVM tests.
    fun required(sdk: Int = Build.VERSION.SDK_INT): List<String> = buildList {
        if (sdk >= 31) add(Manifest.permission.BLUETOOTH_CONNECT)
        if (sdk >= 33) add(Manifest.permission.POST_NOTIFICATIONS)
    }
    fun canConnect(context: Context) = Build.VERSION.SDK_INT < 31 || context.checkSelfPermission(Manifest.permission.BLUETOOTH_CONNECT) == android.content.pm.PackageManager.PERMISSION_GRANTED
}
