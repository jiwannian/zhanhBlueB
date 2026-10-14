package com.zhanh.bluebridge.core

import org.junit.Assert.*
import org.junit.Test
import java.io.*
import java.util.UUID
import java.util.concurrent.*

class SessionTest {
    private fun temp() = java.nio.file.Files.createTempDirectory("bluebridge-test").toFile()
    private class Events : SessionListener {
        lateinit var session: Session
        val received = LinkedBlockingQueue<Pair<TransferInfo, ByteArray>>()
        val texts = LinkedBlockingQueue<String>()
        val failures = LinkedBlockingQueue<String>()
        override fun peerHello(id: UUID, name: String) { session.approve(true) }
        override fun text(id: UUID, value: String) { texts.add(value) }
        override fun received(id: UUID, info: TransferInfo, file: File) { received.add(info to file.readBytes()) }
        override fun transferFailed(id: UUID, reason: String) { failures.add(reason) }
    }
    private fun pair(): Pair<Events, Events> {
        val aIn = PipedInputStream(131072); val bIn = PipedInputStream(131072)
        val a = Events(); val b = Events()
        a.session = Session(aIn, PipedOutputStream(bIn), UUID.randomUUID(), "A", temp(), a, 3000)
        b.session = Session(bIn, PipedOutputStream(aIn), UUID.randomUUID(), "B", temp(), b, 3000)
        a.session.start(); b.session.start(); a.session.awaitReady(); b.session.awaitReady(); return a to b
    }
    @Test fun bilingualClipboardAndStreamedFilesInBothDirections() {
        val (a, b) = pair()
        try {
            a.session.sendText("你好🌉\n第二行"); assertEquals("你好🌉\n第二行", b.texts.poll(3, TimeUnit.SECONDS))
            b.session.sendText("back"); assertEquals("back", a.texts.poll(3, TimeUnit.SECONDS))
            listOf(0, 1, 65_537, 500_000).forEach { size ->
                val bytes = ByteArray(size) { (it % 251).toByte() }
                a.session.send(TransferSource(TransferInfo("a.bin", size.toLong(), "file", "application/octet-stream")) { ByteArrayInputStream(bytes) })
                assertArrayEquals(bytes, b.received.poll(3, TimeUnit.SECONDS).second)
            }
        } finally { a.session.close(); b.session.close() }
    }
    @Test fun atomicNameCollisionPreservesOriginalAndEmptyFile() {
        val dir = temp(); val source = File(dir, "source").apply { writeBytes(byteArrayOf()) }
        val existing = File(dir, "a.bin").apply { writeText("original") }
        val result = AtomicFiles.commit(source, dir, "a.bin")
        assertEquals("a (1).bin", result.name); assertEquals(0, result.length()); assertEquals("original", existing.readText())
    }
    @Test fun trustGateMalformedHelloAndRejection() {
        val listener = Events(); val out = ByteArrayOutputStream()
        val session = Session(ByteArrayInputStream(byteArrayOf()), out, UUID.randomUUID(), "A", temp(), listener)
        assertThrows(IOException::class.java) { session.sendText("blocked") }
        assertThrows(IOException::class.java) { session.handle(Frame(3, UUID.randomUUID(), "blocked".toByteArray())) }
        assertThrows(IOException::class.java) { session.handle(Frame(2, UUID.randomUUID(), Wire.json("accepted" to true))) }
        assertThrows(IOException::class.java) { session.handle(Frame(1, UUID.randomUUID(), Wire.json("deviceId" to "bad", "deviceName" to "x"))) }
    }
    @Test fun lengthChangeTimeoutCancellationAndDisconnectionFailSafely() {
        val (a, b) = pair()
        try {
            assertThrows(IOException::class.java) { a.session.send(TransferSource(TransferInfo("a", 3, "file", "x")) { ByteArrayInputStream(byteArrayOf(1, 2)) }) }
            assertThrows(IOException::class.java) { a.session.send(TransferSource(TransferInfo("a", 1, "file", "x")) { ByteArrayInputStream(byteArrayOf(1, 2)) }) }
            val id = UUID.randomUUID(); a.session.cancel(id)
            assertThrows(IOException::class.java) { a.session.send(TransferSource(TransferInfo("a", 0, "file", "x")) { ByteArrayInputStream(byteArrayOf()) }, id) }
            b.session.close(); Thread.sleep(30); assertThrows(IOException::class.java) { a.session.sendText("x") }
        } finally { a.session.close(); b.session.close() }
    }
}
