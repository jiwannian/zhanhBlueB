package com.zhanh.bluebridge.core

import com.google.gson.JsonArray
import com.google.gson.JsonObject
import com.google.gson.JsonParser
import java.io.IOException
import java.net.URI
import java.util.Locale
import java.util.UUID

data class ReceivedFileRecord(
    val id: UUID, val uri: String, val name: String, val mime: String,
    val size: Long, val receivedAt: Long
) {
    fun validate() {
        TransferInfo(name, size, "file", mime).validate()
        val parsed = try { URI(uri) } catch (e: Exception) { throw IOException("接收文件 URI 无效", e) }
        if (uri.length > 2048 || parsed.scheme != "content" || parsed.authority != "media" ||
            parsed.rawQuery != null || parsed.rawFragment != null || receivedAt < 0 ||
            !Regex("/[A-Za-z0-9_-]+/downloads/[0-9]+").matches(parsed.path ?: "")) {
            throw IOException("接收文件引用无效")
        }
    }
}

object ReceivedFiles {
    const val LIMIT = 100
    fun normalize(records: List<ReceivedFileRecord>): List<ReceivedFileRecord> = records
        .sortedWith(compareByDescending<ReceivedFileRecord> { it.receivedAt }.thenBy { it.id.toString() })
        .distinctBy { it.id }.take(LIMIT)
}

object ReceivedHistoryCodec {
    const val MAX_BYTES = 1_048_576
    fun encode(records: List<ReceivedFileRecord>): ByteArray {
        val entries = JsonArray()
        ReceivedFiles.normalize(records).forEach { record ->
            record.validate()
            entries.add(JsonObject().apply {
                addProperty("id", record.id.toString()); addProperty("uri", record.uri)
                addProperty("name", record.name); addProperty("mime", record.mime)
                addProperty("size", record.size); addProperty("receivedAt", record.receivedAt)
            })
        }
        return JsonObject().apply { addProperty("version", 1); add("records", entries) }
            .toString().toByteArray(Charsets.UTF_8)
    }

    fun decode(bytes: ByteArray): List<ReceivedFileRecord> {
        if (bytes.size > MAX_BYTES) throw IOException("接收历史文件过大")
        try {
            val root = JsonParser.parseString(Wire.text(bytes)).asJsonObject
            if (number(root, "version") != 1L) throw IOException("接收历史版本不支持")
            val entries = root["records"].asJsonArray
            if (entries.size() > ReceivedFiles.LIMIT) throw IOException("接收历史记录过多")
            return ReceivedFiles.normalize(entries.map { element ->
                val entry = element.asJsonObject
                val id = Wire.string(entry, "id")
                val uuid = UUID.fromString(id)
                if (uuid.toString() != id.lowercase(Locale.ROOT)) throw IOException("接收历史编号无效")
                ReceivedFileRecord(uuid, Wire.string(entry, "uri"), Wire.string(entry, "name"),
                    Wire.string(entry, "mime"), number(entry, "size"), number(entry, "receivedAt"))
                    .also { it.validate() }
            })
        } catch (e: Exception) { throw IOException("接收历史格式无效", e) }
    }

    private fun number(entry: JsonObject, key: String): Long {
        val value = entry[key] ?: throw IOException("缺少 $key")
        if (!value.isJsonPrimitive || !value.asJsonPrimitive.isNumber || !Regex("[0-9]+").matches(value.asString)) {
            throw IOException("接收历史数字无效")
        }
        return value.asString.toLongOrNull() ?: throw IOException("接收历史数字过大")
    }
}

interface ReceivedHistoryStorage {
    fun read(): ByteArray?
    fun write(value: ByteArray)
}

data class ReceivedHistoryResult(val records: List<ReceivedFileRecord>, val error: IOException? = null)

/** Serializes initialization and updates; storage errors never undo a successfully saved file. */
class ReceivedFileHistory(private val storage: ReceivedHistoryStorage) {
    private var loaded = false
    private var records: List<ReceivedFileRecord> = emptyList()
    private var readError: IOException? = null

    @Synchronized fun snapshot(): ReceivedHistoryResult {
        load()
        return ReceivedHistoryResult(records.toList(), readError)
    }

    @Synchronized fun add(record: ReceivedFileRecord): ReceivedHistoryResult {
        record.validate()
        load()
        records = ReceivedFiles.normalize(listOf(record) + records)
        val error = try {
            storage.write(ReceivedHistoryCodec.encode(records))
            readError.also { readError = null }
        } catch (e: Exception) {
            IOException("文件已接收，但记录保存失败；重启后可能找不到这条记录。", e)
        }
        return ReceivedHistoryResult(records.toList(), error)
    }

    private fun load() {
        if (loaded) return
        loaded = true
        try { records = storage.read()?.let(ReceivedHistoryCodec::decode) ?: emptyList() }
        catch (e: Exception) { readError = IOException("接收历史无法读取，本次接收仍可正常使用。", e) }
    }
}

object FileOpenPolicy {
    private fun concrete(value: String?): String? {
        if (value == null || value.any { it == '\r' || it == '\n' }) return null
        val mime = value.substringBefore(';').trim().lowercase(Locale.ROOT)
        if (!Regex("[a-z0-9!#$&^_.+-]+/[a-z0-9!#$&^_.+-]+").matches(mime) ||
            mime in listOf("application/octet-stream", "binary/octet-stream", "application/unknown")) return null
        return mime
    }
    fun mimeCandidates(system: String?, recorded: String?, extension: String?): List<String> =
        listOfNotNull(listOf(system, recorded, extension).firstNotNullOfOrNull(::concrete), "*/*")
}
