package com.zhanh.bluebridge.core

import org.junit.Assert.*
import org.junit.Test
import java.io.*
import java.security.MessageDigest
import java.util.UUID

class ReceiveSafetyTest {
    private class Fixture(private val diskFailure: Boolean = false) : SessionListener {
        val directory = java.nio.file.Files.createTempDirectory("bluebridge-safety").toFile()
        val output = ByteArrayOutputStream()
        var bytes: ByteArray? = null
        var failures = 0
        val session = Session(ByteArrayInputStream(byteArrayOf()), output, UUID.randomUUID(), "local", directory, this)
        init {
            session.handle(Frame(1, UUID.randomUUID(), Wire.json("deviceId" to UUID.randomUUID(), "deviceName" to "peer")))
            session.handle(Frame(2, UUID.randomUUID(), Wire.json("accepted" to true)))
        }
        override fun peerHello(id: UUID, name: String) { session.approve(true) }
        override fun text(id: UUID, value: String) { bytes = value.toByteArray() }
        override fun received(id: UUID, info: TransferInfo, file: File) { if (diskFailure) throw IOException("磁盘空间不足"); bytes = file.readBytes() }
        override fun transferFailed(id: UUID, reason: String) { failures++ }
        fun begin(id: UUID, length: Long = 3) = session.handle(Frame(4, id, TransferInfo("file.bin", length, "file", "application/octet-stream").json()))
        fun end(id: UUID, data: ByteArray) = session.handle(Frame(6, id, Wire.json("sha256" to MessageDigest.getInstance("SHA-256").digest(data).hex())))
        fun frames(): List<Frame> { val input = ByteArrayInputStream(output.toByteArray()); return generateSequence { Codec.read(input) }.toList() }
    }
    @Test fun duplicateActiveBeginDoesNotDestroyValidTransfer() {
        val f = Fixture(); val id = UUID.randomUUID(); f.begin(id); f.begin(id)
        val data = byteArrayOf(1, 2, 3); f.session.handle(Frame(5, id, data)); f.end(id, data)
        assertArrayEquals(data, f.bytes); assertEquals(1, f.failures); assertTrue(f.directory.listFiles()!!.isEmpty())
        f.begin(id); assertEquals(2, f.failures); assertTrue(f.directory.listFiles()!!.isEmpty())
    }
    @Test fun invalidHashTruncatedAndOversizedChunkRemoveTemporary() {
        val f = Fixture(); val id = UUID.randomUUID(); f.begin(id); f.session.handle(Frame(5, id, byteArrayOf(1, 2, 3))); f.end(id, byteArrayOf(4, 5, 6))
        assertNull(f.bytes); assertTrue(f.directory.listFiles()!!.isEmpty())
        val other = UUID.randomUUID(); f.begin(other); f.session.handle(Frame(5, other, ByteArray(Wire.CHUNK_SIZE + 1)))
        assertTrue(f.directory.listFiles()!!.isEmpty())
        val third = UUID.randomUUID(); f.begin(third); f.end(third, byteArrayOf())
        assertTrue(f.directory.listFiles()!!.isEmpty()); assertEquals(3, f.failures)
    }
    @Test fun cancelLateFramesAndForeignIdsPreserveNextTransfer() {
        val f = Fixture(); val id = UUID.randomUUID(); f.begin(id); f.session.handle(Frame(8, id, byteArrayOf()))
        assertTrue(f.directory.listFiles()!!.isEmpty()); f.session.handle(Frame(5, id, byteArrayOf(1)))
        val next = UUID.randomUUID(); f.begin(next); f.session.handle(Frame(5, UUID.randomUUID(), byteArrayOf(1)))
        val data = byteArrayOf(7, 8, 9); f.session.handle(Frame(5, next, data)); f.end(next, data)
        assertArrayEquals(data, f.bytes); assertTrue(f.directory.listFiles()!!.isEmpty())
    }
    @Test fun atomicPublishFailureReturnsErrorAndCleansPartial() {
        val f = Fixture(true); val id = UUID.randomUUID(); f.begin(id, 0); f.end(id, byteArrayOf())
        assertEquals(1, f.failures); assertTrue(f.directory.listFiles()!!.isEmpty())
        assertFalse(f.frames().any { it.type == 7 && Wire.string(Wire.parse(it.payload), "stage") == "complete" })
        assertTrue(f.frames().any { it.type == 9 })
    }
    @Test fun pngValidationAndPingDuplicateTextControlFlow() {
        val f = Fixture(); val id = UUID.randomUUID()
        f.session.handle(Frame(10, id, byteArrayOf())); assertTrue(f.frames().any { it.type == 11 && it.id == id })
        f.session.handle(Frame(3, id, "original".toByteArray())); f.session.handle(Frame(3, id, "duplicate".toByteArray()))
        assertArrayEquals("original".toByteArray(), f.bytes)
        val image = UUID.randomUUID(); f.session.handle(Frame(4, image, TransferInfo("a.png", 3, "image", "image/png").json())); f.session.handle(Frame(5, image, byteArrayOf(1, 2, 3))); f.end(image, byteArrayOf(1, 2, 3))
        assertEquals(1, f.failures); assertTrue(f.directory.listFiles()!!.isEmpty())
        assertThrows(IOException::class.java) { f.session.handle(Frame(1, UUID.randomUUID(), Wire.json("deviceId" to UUID.randomUUID(), "deviceName" to "again"))) }
        assertThrows(IOException::class.java) { f.session.handle(Frame(2, UUID.randomUUID(), Wire.json("accepted" to true))) }
    }
    @Test fun malformedControlAndUtf8AreRejected() {
        assertThrows(IOException::class.java) { Wire.parse(ByteArray(Wire.CONTROL_LIMIT + 1)) }
        assertThrows(IOException::class.java) { Wire.parse("bad".toByteArray()) }
        assertThrows(IOException::class.java) { Wire.text(byteArrayOf(0xff.toByte())) }
        assertThrows(IOException::class.java) { Wire.string(com.google.gson.JsonObject(), "missing") }
        assertThrows(IOException::class.java) { Wire.string(com.google.gson.JsonObject().apply { addProperty("a", 1) }, "a") }
        assertThrows(IOException::class.java) { TransferInfo.parse("{}".toByteArray()) }
        assertThrows(IOException::class.java) { TransferInfo.parse("{\"length\":999999999999999999999999}".toByteArray()) }
    }
    @Test fun boundedDuplicateCacheAndMalformedPeerIdentity() {
        val f = Fixture()
        repeat(258) { f.session.handle(Frame(3, UUID.randomUUID(), "x".toByteArray())) }
        repeat(258) { f.session.handle(Frame(8, UUID.randomUUID(), "cancel".toByteArray())) }
        val directory = java.nio.file.Files.createTempDirectory("bluebridge-identity").toFile()
        val listener = object : SessionListener { override fun peerHello(id: UUID, name: String) {}; override fun text(id: UUID, value: String) {}; override fun received(id: UUID, info: TransferInfo, file: File) {} }
        listOf("", "x\n", "a".repeat(201)).forEach { name ->
            val session = Session(ByteArrayInputStream(byteArrayOf()), ByteArrayOutputStream(), UUID.randomUUID(), "A", directory, listener)
            assertThrows(IOException::class.java) { session.handle(Frame(1, UUID.randomUUID(), Wire.json("deviceId" to UUID.randomUUID(), "deviceName" to name))) }
        }
        val session = Session(ByteArrayInputStream(byteArrayOf()), ByteArrayOutputStream(), UUID.randomUUID(), "A", directory, listener)
        assertThrows(IOException::class.java) { session.approve(true) }
        session.handle(Frame(1, UUID.randomUUID(), Wire.json("deviceId" to UUID.randomUUID(), "deviceName" to "peer")))
        assertThrows(IOException::class.java) { session.handle(Frame(2, UUID.randomUUID(), Wire.json("notAccepted" to true))) }
        assertThrows(IOException::class.java) { session.handle(Frame(2, UUID.randomUUID(), Wire.json("accepted" to "yes"))) }
        assertThrows(IOException::class.java) { session.handle(Frame(2, UUID.randomUUID(), Wire.json("accepted" to false))) }
        session.handle(Frame(11, UUID.randomUUID(), byteArrayOf()))
    }
    @Test fun closingDuringPublishNeverWaitsOnUiCallbackMonitor() {
        val directory = java.nio.file.Files.createTempDirectory("bluebridge-close").toFile()
        lateinit var session: Session
        val listener = object : SessionListener {
            override fun peerHello(id: UUID, name: String) { session.approve(true) }
            override fun text(id: UUID, value: String) {}
            override fun received(id: UUID, info: TransferInfo, file: File) {
                assertFalse("UI-marshaled callbacks must not hold the session monitor", Thread.holdsLock(session))
                val closeFinished = java.util.concurrent.CompletableFuture<Unit>()
                Thread { session.close(); closeFinished.complete(Unit) }.start()
                closeFinished.get(1, java.util.concurrent.TimeUnit.SECONDS)
            }
        }
        session = Session(ByteArrayInputStream(byteArrayOf()), ByteArrayOutputStream(), UUID.randomUUID(), "local", directory, listener)
        session.handle(Frame(1, UUID.randomUUID(), Wire.json("deviceId" to UUID.randomUUID(), "deviceName" to "peer")))
        session.handle(Frame(2, UUID.randomUUID(), Wire.json("accepted" to true)))
        val id = UUID.randomUUID(); session.handle(Frame(4, id, TransferInfo("empty.bin", 0, "file", "x").json()))
        session.handle(Frame(6, id, Wire.json("sha256" to MessageDigest.getInstance("SHA-256").digest(byteArrayOf()).hex())))
        assertTrue(directory.listFiles()!!.isEmpty())
    }
}
