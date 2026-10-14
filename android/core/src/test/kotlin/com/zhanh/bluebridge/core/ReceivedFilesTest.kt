package com.zhanh.bluebridge.core

import org.junit.Assert.*
import org.junit.Test
import java.io.IOException
import java.util.UUID

class ReceivedFilesTest {
    private fun record(index: Int, at: Long = index.toLong()) = ReceivedFileRecord(
        UUID(0, index.toLong()), "content://media/external_primary/downloads/$index",
        "图片 ($index).jpg", "image/jpeg", index.toLong(), at
    )

    private class MemoryStorage(var bytes: ByteArray? = null) : ReceivedHistoryStorage {
        var failWrite = false
        var reads = 0
        override fun read(): ByteArray? { reads++; return bytes?.copyOf() }
        override fun write(value: ByteArray) {
            if (failWrite) throw IOException("disk full")
            bytes = value.copyOf()
        }
    }

    @Test fun newestHundredAreSortedAndDuplicateIdsUseNewestRecord() {
        val duplicate = record(140, 500).copy(name = "重名 (2).jpg")
        val result = ReceivedFiles.normalize((1..150).map { record(it) } + duplicate)
        assertEquals(100, result.size)
        assertEquals(duplicate, result.first())
        assertEquals(100, result.map { it.id }.distinct().size)
        assertFalse(result.any { it.id == record(1).id })
    }

    @Test fun codecRoundTripsChineseNamesEmptyFilesAndActualRenamedUri() {
        val entries = listOf(record(2).copy(size = 0, name = "中文😀 (2).jpg"), record(1))
        assertEquals(entries, ReceivedHistoryCodec.decode(ReceivedHistoryCodec.encode(entries)))
        assertEquals(emptyList<ReceivedFileRecord>(), ReceivedHistoryCodec.decode(ReceivedHistoryCodec.encode(emptyList())))
    }

    @Test fun codecRejectsCorruptionWrongVersionsAndUntrustedUris() {
        val valid = String(ReceivedHistoryCodec.encode(listOf(record(1))), Charsets.UTF_8)
        listOf("{broken", valid.replace("\"version\":1", "\"version\":2"),
            valid.replace("content://media/", "file:///"),
            valid.replace("content://media/", "content://evil/"),
            valid.replace("\"size\":1", "\"size\":-1"),
            valid.replace("\"receivedAt\":1", "\"receivedAt\":\"1\""),
            valid.replace("image/jpeg", ""), valid.replace("图片 (1).jpg", "../escape.jpg")
        ).forEach { invalid -> assertThrows(IOException::class.java) { ReceivedHistoryCodec.decode(invalid.toByteArray()) } }
        assertThrows(IOException::class.java) { ReceivedHistoryCodec.decode(ByteArray(1_048_577)) }
    }

    @Test fun metadataLimitsAreValidated() {
        listOf(record(1).copy(uri = "content://media/external/downloads/1?query=x"),
            record(1).copy(uri = "content://media/external/images/media/1"),
            record(1).copy(receivedAt = -1), record(1).copy(size = TransferInfo.MAX_FILE + 1)
        ).forEach { invalid -> assertThrows(IOException::class.java) { invalid.validate() } }
    }

    @Test fun historyRestoresAcrossInstancesAndLoadsOnlyOnce() {
        val storage = MemoryStorage()
        val first = ReceivedFileHistory(storage)
        assertTrue(first.snapshot().records.isEmpty())
        assertNull(first.add(record(1)).error)
        assertEquals(listOf(record(1)), first.snapshot().records)
        assertEquals(1, storage.reads)
        assertEquals(listOf(record(1)), ReceivedFileHistory(storage).snapshot().records)
    }

    @Test fun failedHistoryWriteKeepsCurrentFileUsableAndPreviousDiskState() {
        val storage = MemoryStorage()
        val history = ReceivedFileHistory(storage)
        history.add(record(1))
        storage.failWrite = true
        val result = history.add(record(2))
        assertNotNull(result.error)
        assertTrue(result.error!!.message!!.contains("重启"))
        assertEquals(listOf(record(2), record(1)), result.records)
        assertEquals(listOf(record(1)), ReceivedFileHistory(storage).snapshot().records)
        storage.failWrite = false
        assertNull(history.add(record(3)).error)
        assertEquals(3, ReceivedFileHistory(storage).snapshot().records.size)
    }

    @Test fun corruptedHistoryWarnsWithoutPreventingNewReceives() {
        val storage = MemoryStorage("broken".toByteArray())
        val history = ReceivedFileHistory(storage)
        assertNotNull(history.snapshot().error)
        assertTrue(history.snapshot().records.isEmpty())
        history.add(record(1))
        assertEquals(listOf(record(1)), ReceivedFileHistory(storage).snapshot().records)
    }

    @Test fun trimmingMetadataDoesNotTouchFilesAndPersistsHundred() {
        val storage = MemoryStorage()
        val history = ReceivedFileHistory(storage)
        (1..105).forEach { history.add(record(it)) }
        assertEquals(100, ReceivedFileHistory(storage).snapshot().records.size)
        assertEquals(record(105), history.snapshot().records.first())
    }

    @Test fun parallelReceivesAndStartupCannotLoseRecords() {
        val storage = MemoryStorage()
        val history = ReceivedFileHistory(storage)
        val threads = (1..20).map { index -> Thread { history.add(record(index)); history.snapshot() } }
        threads.forEach { it.start() }; threads.forEach { it.join() }
        assertEquals((1..20).map { record(it) }.reversed(), ReceivedFileHistory(storage).snapshot().records)
    }

    @Test fun mimeSelectionUsesConcreteSystemThenRecordThenExtension() {
        assertEquals(listOf("image/png", "*/*"), FileOpenPolicy.mimeCandidates("image/png", "text/plain", "application/pdf"))
        assertEquals(listOf("application/pdf", "*/*"), FileOpenPolicy.mimeCandidates("application/octet-stream", "application/pdf", "image/jpeg"))
        assertEquals(listOf("image/jpeg", "*/*"), FileOpenPolicy.mimeCandidates(null, "application/octet-stream", "image/jpeg"))
        assertEquals(listOf("text/plain", "*/*"), FileOpenPolicy.mimeCandidates(" TEXT/PLAIN; charset=utf-8 ", null, null))
    }

    @Test fun unknownOrInvalidMimeFallsBackToUniversalChooser() {
        listOf(null, "", "invalid", "image/*", "*/*", "text/plain\r\nevil", "application/octet-stream", "binary/octet-stream").forEach { mime ->
            assertEquals(listOf("*/*"), FileOpenPolicy.mimeCandidates(mime, mime, mime))
        }
    }
}
