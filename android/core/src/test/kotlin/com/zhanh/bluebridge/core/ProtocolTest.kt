package com.zhanh.bluebridge.core

import com.google.gson.JsonParser
import org.junit.Assert.*
import org.junit.Test
import java.io.*
import java.util.UUID

class ProtocolTest {
    private val id = UUID.fromString("00112233-4455-6677-8899-aabbccddeeff")
    @Test fun exportedShareAllowsOnlyGrantedContentUris() {
        listOf(null, "file", "http", "https", "custom", "CONTENT", "").forEach { scheme -> assertThrows(IOException::class.java) { ContentUriPolicy.requireContent(scheme) } }
        ContentUriPolicy.requireContent("content")
    }
    @Test fun goldenFramesAndFragmentedReads() {
        JsonParser.parseString(File(System.getProperty("goldenPath")).readText()).asJsonArray.forEach { item ->
            val j = item.asJsonObject
            val bytes = hex(j["frameHex"].asString)
            val frame = Frame(j["type"].asInt, UUID.fromString(j["id"].asString), hex(j["payloadHex"].asString))
            val out = ByteArrayOutputStream(); Codec.write(out, frame); assertArrayEquals(bytes, out.toByteArray())
            val stream = object : ByteArrayInputStream(bytes) { override fun read(b: ByteArray, off: Int, len: Int) = super.read(b, off, minOf(1, len)) }
            assertEquals(frame, Codec.read(stream)); assertNull(Codec.read(stream))
        }
    }
    @Test fun malformedFramesAreRejectedBeforeAllocation() {
        val out = ByteArrayOutputStream(); Codec.write(out, Frame(3, id, "hello".toByteArray()))
        val good = out.toByteArray()
        listOf(0, 4, 5, 6).forEach { i -> val bad = good.copyOf(); bad[i] = 99; assertThrows(IOException::class.java) { Codec.read(ByteArrayInputStream(bad)) } }
        val oversized = good.copyOf(); oversized[24] = 0x7f; assertThrows(IOException::class.java) { Codec.read(ByteArrayInputStream(oversized)) }
        for (n in 1 until good.size) assertThrows(IOException::class.java) { Codec.read(ByteArrayInputStream(good.copyOf(n))) }
    }
    @Test fun filenameAndBeginValidation() {
        listOf("", ".", "..", "../x", "C:\\x", "CON", "Lpt9.txt", "x?y", "x\n", "x.", "x ", "a".repeat(241)).forEach { assertThrows(IOException::class.java) { TransferInfo(it, 0, "file", "application/octet-stream").validate() } }
        TransferInfo("中文😀.bin", 0, "file", "application/octet-stream").validate()
        listOf(-1L, TransferInfo.MAX_FILE + 1).forEach { assertThrows(IOException::class.java) { TransferInfo("a", it, "file", "a").validate() } }
        assertThrows(IOException::class.java) { TransferInfo.parse("{\"name\":\"a\",\"length\":1.5,\"kind\":\"file\",\"mime\":\"a\"}".toByteArray()) }
        assertThrows(IOException::class.java) { TransferInfo("a.png", 33L * 1024 * 1024, "image", "image/png").validate() }
    }
    @Test fun immutablePayloadAndPngBounds() {
        val bytes = byteArrayOf(1, 2); val frame = Frame(5, id, bytes); bytes[0] = 9; assertEquals(1, frame.payload[0].toInt())
        val copy = frame.payload; copy[0] = 8; assertEquals(1, frame.payload[0].toInt())
        assertThrows(IOException::class.java) { Png.validate(byteArrayOf(1, 2)) }
        val header = hex("89504e470d0a1a0a0000000d494844520000000100000001"); Png.validate(header)
        header[16] = 0x7f; assertThrows(IOException::class.java) { Png.validate(header) }
    }
    @Test fun codecLengthAndValueBoundaries() {
        val valid = Frame(3, id, byteArrayOf(1)); assertEquals(valid.hashCode(), Frame(3, id, byteArrayOf(1)).hashCode())
        assertNotEquals(valid, Frame(4, id, byteArrayOf(1))); assertNotEquals(valid, null)
        listOf(0, 12).forEach { type -> assertThrows(IOException::class.java) { Codec.write(ByteArrayOutputStream(), Frame(type, id, byteArrayOf())) } }
        assertThrows(IOException::class.java) { Codec.write(ByteArrayOutputStream(), Frame(3, id, ByteArray(Wire.MAX_PAYLOAD + 1))) }
        assertThrows(IOException::class.java) { Codec.write(ByteArrayOutputStream(), Frame(5, id, ByteArray(Wire.CHUNK_SIZE + 1))) }
        listOf(1 to (Wire.CONTROL_LIMIT + 1), 10 to 1, 11 to 1).forEach { (type, length) ->
            val out = ByteArrayOutputStream(); Codec.write(out, Frame(type, id, ByteArray(length)))
            assertThrows(IOException::class.java) { Codec.read(ByteArrayInputStream(out.toByteArray())) }
        }
        listOf(TransferInfo("a", 0, "other", "x"), TransferInfo("a", 0, "file", ""), TransferInfo("a", 0, "file", "x".repeat(201)), TransferInfo("a.png", 0, "image", "image/jpeg")).forEach { assertThrows(IOException::class.java) { it.validate() } }
        listOf("\"1\"", "-1", "true", "null", "{}", "1e3").forEach { length -> assertThrows(IOException::class.java) { TransferInfo.parse("{\"name\":\"a\",\"length\":$length,\"kind\":\"file\",\"mime\":\"x\"}".toByteArray()) } }
    }
    private fun hex(s: String) = s.chunked(2).map { it.toInt(16).toByte() }.toByteArray()
}
