package com.zhanh.bluebridge.core

import com.google.gson.JsonObject
import com.google.gson.JsonParser
import java.io.*
import java.nio.ByteBuffer
import java.nio.charset.CodingErrorAction
import java.util.UUID

object Wire {
    val SERVICE_UUID: UUID = UUID.fromString("dc0b3714-a8e6-4c25-a219-775346d2ef91")
    const val MAX_PAYLOAD = 1_048_576
    const val CHUNK_SIZE = 65_536
    const val CONTROL_LIMIT = 16_384
    fun json(vararg values: Pair<String, Any>): ByteArray = JsonObject().apply {
        values.forEach { (k, v) -> when (v) { is Boolean -> addProperty(k, v); is Number -> addProperty(k, v); else -> addProperty(k, v.toString()) } }
    }.toString().toByteArray(Charsets.UTF_8)
    fun text(bytes: ByteArray): String = try {
        Charsets.UTF_8.newDecoder().onMalformedInput(CodingErrorAction.REPORT).onUnmappableCharacter(CodingErrorAction.REPORT).decode(ByteBuffer.wrap(bytes)).toString()
    } catch (e: Exception) { throw IOException("无效的 UTF-8 内容", e) }
    fun parse(bytes: ByteArray): JsonObject {
        if (bytes.size > CONTROL_LIMIT) throw IOException("控制消息过长")
        return try { JsonParser.parseString(text(bytes)).asJsonObject } catch (e: Exception) { throw IOException("控制消息格式错误", e) }
    }
    fun string(json: JsonObject, key: String): String {
        val value = json[key] ?: throw IOException("缺少 $key")
        if (!value.isJsonPrimitive || !value.asJsonPrimitive.isString) throw IOException("无效的 $key")
        return value.asString
    }
}

class Frame(val type: Int, val id: UUID, payload: ByteArray) {
    private val data = payload.copyOf()
    val payload: ByteArray get() = data.copyOf()
    override fun equals(other: Any?) = other is Frame && type == other.type && id == other.id && data.contentEquals(other.data)
    override fun hashCode() = 31 * (31 * type + id.hashCode()) + data.contentHashCode()
}

object Codec {
    fun read(input: InputStream): Frame? {
        val first = input.read(); if (first == -1) return null
        val header = ByteArray(28); header[0] = first.toByte(); readExactly(input, header, 1, 27)
        val b = ByteBuffer.wrap(header)
        if (b.int != 0x42425247 || b.get().toInt() != 1) throw IOException("蓝桥协议版本或标识不正确")
        val type = b.get().toInt() and 255
        if (type !in 1..11 || b.short.toInt() != 0) throw IOException("无效的消息类型")
        val id = UUID(b.long, b.long); val length = b.int
        if (length < 0 || length > Wire.MAX_PAYLOAD) throw IOException("消息超过限制")
        if ((type == 5 && length > Wire.CHUNK_SIZE) || (type in listOf(1, 2, 4, 6, 7, 8, 9) && length > Wire.CONTROL_LIMIT) || (type in 10..11 && length != 0)) throw IOException("消息长度不正确")
        return Frame(type, id, ByteArray(length).also { readExactly(input, it, 0, length) })
    }
    fun write(output: OutputStream, frame: Frame) {
        val data = frame.payload
        if (frame.type !in 1..11 || data.size > Wire.MAX_PAYLOAD || (frame.type == 5 && data.size > Wire.CHUNK_SIZE)) throw IOException("无效的发送消息")
        val header = ByteBuffer.allocate(28).putInt(0x42425247).put(1).put(frame.type.toByte()).putShort(0).putLong(frame.id.mostSignificantBits).putLong(frame.id.leastSignificantBits).putInt(data.size).array()
        output.write(header); output.write(data); output.flush()
    }
    private fun readExactly(input: InputStream, bytes: ByteArray, offset: Int, length: Int) {
        var position = offset
        while (position < offset + length) { val n = input.read(bytes, position, offset + length - position); if (n <= 0) throw EOFException("蓝牙消息传输中断"); position += n }
    }
}

data class TransferInfo(val name: String, val length: Long, val kind: String, val mime: String) {
    fun validate() {
        if (name.isEmpty() || name.length > 240 || name == "." || name == ".." || name.last() in ". " || name.any { it.code < 32 || it.code == 127 || it in "\\/:*?\"<>|" } || Regex("(?i)^(CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\\..*)?$").matches(name)) throw IOException("文件名无效")
        if (length < 0 || length > MAX_FILE || kind !in listOf("file", "image") || mime.isBlank() || mime.length > 200) throw IOException("文件信息无效")
        if (kind == "image" && (mime != "image/png" || length > MAX_IMAGE)) throw IOException("剪贴板图片必须为 PNG 且不超过 32 MiB")
    }
    fun json(): ByteArray = Wire.json("name" to name, "length" to length, "kind" to kind, "mime" to mime)
    companion object {
        const val MAX_FILE = 1_099_511_627_776L
        const val MAX_IMAGE = 33_554_432L
        fun parse(bytes: ByteArray): TransferInfo {
            val json = Wire.parse(bytes); val value = json["length"] ?: throw IOException("缺少文件大小")
            if (!value.isJsonPrimitive || !value.asJsonPrimitive.isNumber || !Regex("[0-9]+").matches(value.asString)) throw IOException("文件大小必须为整数")
            val length = value.asString.toLongOrNull() ?: throw IOException("文件过大")
            return TransferInfo(Wire.string(json, "name"), length, Wire.string(json, "kind"), Wire.string(json, "mime")).also { it.validate() }
        }
    }
}

object Png {
    fun validate(bytes: ByteArray) {
        val signature = byteArrayOf(-119, 80, 78, 71, 13, 10, 26, 10)
        if (bytes.size < 24 || !bytes.copyOfRange(0, 8).contentEquals(signature) || !bytes.copyOfRange(12, 16).contentEquals("IHDR".toByteArray()) || ByteBuffer.wrap(bytes, 8, 4).int != 13) throw IOException("PNG 图片头无效")
        val b = ByteBuffer.wrap(bytes, 16, 8); val width = b.int; val height = b.int
        if (width <= 0 || height <= 0 || width.toLong() * height > 32_000_000L) throw IOException("图片尺寸超过 3200 万像素")
    }
}
