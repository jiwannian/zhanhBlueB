package com.zhanh.bluebridge

import com.zhanh.bluebridge.core.ReceivedFileRecord
import org.junit.Assert.*
import org.junit.Test
import java.util.UUID

class TransferHistoryTest {
    private val file = ReceivedFileRecord(UUID(0, 1), "content://media/external/downloads/1", "同名 (1).jpg", "image/jpeg", 12, 1)
    @Test fun restoredHistoryCanOpenOfflineAndUsesActualSavedName() {
        val row = displayTransfers(emptyList(), listOf(file)).single()
        assertEquals(file.name, row.name)
        assertTrue(canOpenReceivedFile(row))
        assertEquals(file.uri, row.receivedFile!!.uri)
    }
    @Test fun currentAndHistoricalRecordsAreDeduplicatedByTransferId() {
        val live = TransferRow(file.id, file.name, incoming = true, status = RECEIVED_FILE_STATUS, receivedFile = file)
        assertEquals(listOf(live), displayTransfers(listOf(live), listOf(file)))
    }
    @Test fun activeFailedCancelledSentAndClipboardRowsCannotOpen() {
        listOf("接收中", "接收失败", "已取消", "已写入剪贴板").forEach { status ->
            val live = TransferRow(file.id, file.name, incoming = true, status = status)
            assertFalse(canOpenReceivedFile(displayTransfers(listOf(live), listOf(file)).single()))
        }
        assertFalse(canOpenReceivedFile(TransferRow(file.id, file.name, receivedFile = file, status = RECEIVED_FILE_STATUS)))
        assertFalse(canOpenReceivedFile(TransferRow(UUID.randomUUID(), file.name, incoming = true, receivedFile = file, status = RECEIVED_FILE_STATUS)))
    }
}
