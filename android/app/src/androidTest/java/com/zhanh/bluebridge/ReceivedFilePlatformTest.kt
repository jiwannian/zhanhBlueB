package com.zhanh.bluebridge

import android.app.Activity
import android.content.ComponentName
import android.content.Intent
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.os.ResultReceiver
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import com.zhanh.bluebridge.core.*
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith
import java.io.File
import java.io.IOException
import java.security.MessageDigest
import java.util.UUID
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit

@RunWith(AndroidJUnit4::class)
class ReceivedFilePlatformTest {
    private val context get() = InstrumentationRegistry.getInstrumentation().targetContext
    private fun record(saved: SavedDownload, size: Long) = ReceivedFileRecord(UUID.randomUUID(), saved.uri.toString(), saved.name, saved.mime, size, System.currentTimeMillis())

    @Test fun actualRenamedFilesAreReadableByAnotherAppWithReadOnlyGrant() {
        val source = File.createTempFile("received-", ".bin", context.cacheDir)
        val downloads = mutableListOf<SavedDownload>()
        try {
            val payload = "图片😀\nactual bytes".toByteArray()
            source.writeBytes(payload)
            val name = "测试-${UUID.randomUUID()}.jpg"
            val first = AndroidFiles.saveDownloads(context, source, name, "image/jpeg").also { downloads.add(it) }
            source.writeText("second file")
            val second = AndroidFiles.saveDownloads(context, source, name, "image/jpeg").also { downloads.add(it) }
            assertNotEquals(first.uri, second.uri)
            assertEquals(name.removeSuffix(".jpg") + " (1).jpg", second.name)
            assertEquals("second file", context.contentResolver.openInputStream(second.uri)!!.use { it.reader().readText() })
            verifyExternalRead(record(first, payload.size.toLong()), payload)
        } finally { downloads.forEach { context.contentResolver.delete(it.uri, null, null) }; source.delete() }
    }

    @Suppress("DEPRECATION")
    @Test fun chooserWrapsViewIntentAndDeletedFilesFailWithoutChangingRecord() {
        val source = File.createTempFile("received-", ".bin", context.cacheDir).apply { writeBytes(byteArrayOf()) }
        val saved = AndroidFiles.saveDownloads(context, source, "空文件-${UUID.randomUUID()}.unknown", "application/octet-stream")
        val file = record(saved, 0)
        try {
            val chooser = AndroidFileOpener.prepare(context, file)
            assertEquals(Intent.ACTION_CHOOSER, chooser.action)
            val view = chooser.getParcelableExtra<Intent>(Intent.EXTRA_INTENT)!!
            assertEquals(Intent.ACTION_VIEW, view.action)
            assertEquals("*/*", view.type)
            assertEquals(saved.uri, view.data)
            assertEquals(saved.uri, chooser.clipData!!.getItemAt(0).uri)
            assertEquals(Intent.FLAG_GRANT_READ_URI_PERMISSION, view.flags and (Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_GRANT_WRITE_URI_PERMISSION))
            assertEquals(0, view.flags and Intent.FLAG_GRANT_PERSISTABLE_URI_PERMISSION)
            context.contentResolver.delete(saved.uri, null, null)
            assertThrows(IOException::class.java) { AndroidFileOpener.prepare(context, file) }
            assertEquals(saved.uri.toString(), file.uri)
        } finally { context.contentResolver.delete(saved.uri, null, null); source.delete() }
    }

    @Test fun atomicHistoryRestoresAcrossInstancesAndRecoversBackup() {
        val directory = File(context.cacheDir, "history-${UUID.randomUUID()}").apply { mkdirs() }
        try {
            val target = File(directory, "received.json")
            val file = ReceivedFileRecord(UUID.randomUUID(), "content://media/external/downloads/123", "文件.pdf", "application/pdf", 0, 1)
            val store = AtomicReceivedHistoryStorage(target)
            assertNull(store.read())
            assertNull(ReceivedFileHistory(store).add(file).error)
            assertEquals(listOf(file), ReceivedFileHistory(AtomicReceivedHistoryStorage(target)).snapshot().records)
            target.copyTo(File(target.path + ".bak"))
            target.writeText("interrupted")
            assertEquals(listOf(file), ReceivedFileHistory(AtomicReceivedHistoryStorage(target)).snapshot().records)
        } finally { directory.deleteRecursively() }
    }

    private fun verifyExternalRead(file: ReceivedFileRecord, payload: ByteArray) {
        val finished = CountDownLatch(1)
        var code = Activity.RESULT_CANCELED
        var result: Bundle? = null
        val receiver = object : ResultReceiver(Handler(Looper.getMainLooper())) {
            override fun onReceiveResult(resultCode: Int, resultData: Bundle?) { code = resultCode; result = resultData; finished.countDown() }
        }
        val viewerPackage = InstrumentationRegistry.getInstrumentation().context.packageName
        val view = AndroidFileOpener.viewIntent(file, file.mime).setComponent(ComponentName(viewerPackage, TestFileViewer::class.java.name))
            .putExtra("reply", receiver).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        context.startActivity(view)
        assertTrue("Viewer timed out", finished.await(10, TimeUnit.SECONDS))
        assertEquals(result?.getString("error"), Activity.RESULT_OK, code)
        assertEquals(MessageDigest.getInstance("SHA-256").digest(payload).joinToString("") { "%02x".format(it) }, result!!.getString("sha256"))
        assertFalse(result!!.getBoolean("writeGranted"))
    }
}
