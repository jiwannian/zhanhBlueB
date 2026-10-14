package com.zhanh.bluebridge

import android.content.ClipData
import android.content.ClipboardManager
import android.content.ContentValues
import android.content.Context
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.net.Uri
import android.os.Environment
import android.provider.MediaStore
import android.provider.OpenableColumns
import androidx.core.content.FileProvider
import com.zhanh.bluebridge.core.Png
import com.zhanh.bluebridge.core.ContentUriPolicy
import com.zhanh.bluebridge.core.TransferInfo
import com.zhanh.bluebridge.core.Wire
import java.io.File
import java.io.IOException
import java.util.UUID
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit

object AndroidFiles {
    fun stage(context: Context, uri: Uri, image: Boolean, shouldContinue: () -> Boolean = { true }): Pair<TransferInfo, File> {
        ContentUriPolicy.requireContent(uri.scheme)
        val directory = File(context.cacheDir, "outgoing").apply { mkdirs() }
        val staged = File.createTempFile("send-", ".part", directory)
        try {
            context.contentResolver.openInputStream(uri)?.use { source ->
                staged.outputStream().use { target ->
                    val buffer = ByteArray(Wire.CHUNK_SIZE); var total = 0L
                    while (true) { if (!shouldContinue()) throw IOException("连接已停止，文件准备已取消"); val count = source.read(buffer); if (count < 0) break; if (count == 0) continue; total += count
                        if (total > if (image) TransferInfo.MAX_IMAGE else TransferInfo.MAX_FILE) throw IOException("内容超过允许大小")
                        target.write(buffer, 0, count)
                    }
                }
            } ?: throw IOException("无法读取文件，请重新选择或分享")
            if (image) return normalize(context, staged)
            val display = context.contentResolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME), null, null, null)?.use { if (it.moveToFirst()) it.getString(0) else null } ?: "文件-${UUID.randomUUID()}.bin"
            val safe = safeName(display)
            return TransferInfo(safe, staged.length(), "file", context.contentResolver.getType(uri) ?: "application/octet-stream").also { it.validate() } to staged
        } catch (e: Exception) { staged.delete(); throw e }
    }
    private fun normalize(context: Context, original: File): Pair<TransferInfo, File> {
        val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
        BitmapFactory.decodeFile(original.path, bounds)
        if (bounds.outWidth <= 0 || bounds.outHeight <= 0 || bounds.outWidth.toLong() * bounds.outHeight > 32_000_000) throw IOException("图片无法读取或超过 3200 万像素")
        val bitmap = BitmapFactory.decodeFile(original.path) ?: throw IOException("图片格式不支持")
        val target = File.createTempFile("png-", ".png", original.parentFile)
        try {
            target.outputStream().use { if (!bitmap.compress(Bitmap.CompressFormat.PNG, 100, it)) throw IOException("无法生成 PNG 图片") }
            if (target.length() > TransferInfo.MAX_IMAGE) throw IOException("PNG 图片超过 32 MiB")
            return TransferInfo("剪贴板-${System.currentTimeMillis()}.png", target.length(), "image", "image/png") to target
        } catch (e: Exception) { target.delete(); throw e }
        finally { bitmap.recycle(); original.delete() }
    }
    fun clipboardImage(context: Context, file: File, shouldApply: () -> Boolean = { true }): Uri {
        val header = ByteArray(24); file.inputStream().use { if (it.read(header) != 24) throw IOException("图片过短") }; Png.validate(header)
        val bitmap = BitmapFactory.decodeFile(file.path) ?: throw IOException("收到的 PNG 图片无法解码")
        bitmap.recycle()
        val directory = File(context.filesDir, "clipboard").apply { mkdirs() }
        // Keep recent FileProvider images so existing clipboard URI grants remain usable.
        directory.listFiles()?.sortedByDescending { it.lastModified() }?.drop(7)?.forEach { it.delete() }
        val target = File(directory, "clipboard-${UUID.randomUUID()}.png")
        file.inputStream().use { source -> target.outputStream().use { source.copyTo(it, Wire.CHUNK_SIZE) } }
        val uri = FileProvider.getUriForFile(context, "${context.packageName}.files", target)
        onMain {
            if (!shouldApply()) { target.delete(); throw IOException("连接已停止，图片未应用") }
            (context.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager).setPrimaryClip(ClipData.newUri(context.contentResolver, "${Product.NAME}图片", uri))
        }
        return uri
    }
    fun clipboardText(context: Context, value: String, shouldApply: () -> Boolean = { true }) = onMain {
        if (!shouldApply()) throw IOException("连接已停止，文字未应用")
        (context.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager).setPrimaryClip(ClipData.newPlainText(Product.NAME, value))
    }
    fun saveDownloads(context: Context, file: File, name: String, mime: String): Uri = synchronized(this) {
        TransferInfo(name, file.length(), "file", mime).validate()
        val resolver = context.contentResolver
        val used = mutableSetOf<String>()
        resolver.query(MediaStore.Downloads.EXTERNAL_CONTENT_URI, arrayOf(MediaStore.MediaColumns.DISPLAY_NAME), "${MediaStore.MediaColumns.RELATIVE_PATH}=?", arrayOf(Product.DOWNLOAD_PATH), null)?.use { cursor -> while (cursor.moveToNext()) used.add(cursor.getString(0)) }
        val dot = name.lastIndexOf('.').takeIf { it > 0 } ?: name.length
        var unique = name; var suffix = 0
        while (unique in used) { suffix++; unique = name.substring(0, dot).take(210) + " ($suffix)" + name.substring(dot); if (suffix > 9999) throw IOException("同名文件过多") }
        val values = ContentValues().apply { put(MediaStore.MediaColumns.DISPLAY_NAME, unique); put(MediaStore.MediaColumns.MIME_TYPE, mime); put(MediaStore.MediaColumns.RELATIVE_PATH, Product.DOWNLOAD_PATH); put(MediaStore.MediaColumns.IS_PENDING, 1) }
        val uri = resolver.insert(MediaStore.Downloads.EXTERNAL_CONTENT_URI, values) ?: throw IOException("无法创建下载文件")
        try {
            resolver.openOutputStream(uri, "w")?.use { output -> file.inputStream().use { it.copyTo(output, Wire.CHUNK_SIZE) } } ?: throw IOException("无法写入下载目录")
            if (resolver.update(uri, ContentValues().apply { put(MediaStore.MediaColumns.IS_PENDING, 0) }, null, null) != 1) throw IOException("无法完成文件保存")
            return uri
        } catch (e: Exception) { resolver.delete(uri, null, null); throw e }
    }
    fun safeName(name: String): String {
        var result = name.substringAfterLast('/').substringAfterLast('\\').map { if (it.code < 32 || it in ":*?\"<>|" || it.code == 127) '_' else it }.joinToString("").take(240).trimEnd(' ', '.')
        if (result.isBlank() || result == "." || result == "..") result = "文件.bin"
        try { TransferInfo(result, 0, "file", "x").validate() } catch (_: IOException) { result = "文件_$result".take(240) }
        return result
    }
    private fun onMain(action: () -> Unit) {
        val latch = CountDownLatch(1); var failure: Exception? = null
        android.os.Handler(android.os.Looper.getMainLooper()).post { try { action() } catch (e: Exception) { failure = e } finally { latch.countDown() } }
        if (!latch.await(10, TimeUnit.SECONDS)) throw IOException("剪贴板写入超时")
        failure?.let { throw it }
    }
}
