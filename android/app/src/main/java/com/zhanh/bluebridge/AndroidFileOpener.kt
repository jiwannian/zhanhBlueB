package com.zhanh.bluebridge

import android.content.ClipData
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.webkit.MimeTypeMap
import androidx.core.net.toUri
import com.zhanh.bluebridge.core.FileOpenPolicy
import com.zhanh.bluebridge.core.ReceivedFileRecord
import java.io.FileNotFoundException
import java.io.IOException
import java.util.Locale

object AndroidFileOpener {
    /** Run on an IO thread. Checking the descriptor also catches deleted or inaccessible files. */
    fun prepare(context: Context, file: ReceivedFileRecord): Intent {
        file.validate()
        val uri = file.uri.toUri()
        try {
            context.contentResolver.openAssetFileDescriptor(uri, "r")?.use { }
                ?: throw FileNotFoundException()
            val systemMime = context.contentResolver.getType(uri)
            val extension = file.name.substringAfterLast('.', "").lowercase(Locale.ROOT)
            val extensionMime = MimeTypeMap.getSingleton().getMimeTypeFromExtension(extension)
            val view = FileOpenPolicy.mimeCandidates(systemMime, file.mime, extensionMime)
                .asSequence().map { viewIntent(file, it) }
                .firstOrNull { context.packageManager.queryIntentActivities(it, PackageManager.MATCH_DEFAULT_ONLY).isNotEmpty() }
                ?: throw IOException("未找到可打开此文件的应用，请先安装支持该文件类型的应用。")
            return Intent.createChooser(view, "选择打开方式").apply {
                addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
                clipData = view.clipData
            }
        } catch (e: FileNotFoundException) {
            throw IOException("文件已被删除或移动，无法打开。", e)
        } catch (e: SecurityException) {
            throw IOException("无法读取此文件，读取权限可能已失效。", e)
        }
    }

    internal fun viewIntent(file: ReceivedFileRecord, mime: String): Intent {
        file.validate()
        val uri = file.uri.toUri()
        return Intent(Intent.ACTION_VIEW).setDataAndType(uri, mime).apply {
            addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
            clipData = ClipData.newRawUri(file.name, uri)
        }
    }
}
