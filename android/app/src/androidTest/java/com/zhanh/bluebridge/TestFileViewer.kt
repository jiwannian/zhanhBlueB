package com.zhanh.bluebridge

import android.app.Activity
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Bundle
import android.os.Process
import android.os.ResultReceiver
import java.security.MessageDigest

/** A separate test APK reads the granted URI to verify the actual cross-app permission boundary. */
class TestFileViewer : Activity() {
    @Suppress("DEPRECATION")
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val reply = intent.getParcelableExtra<ResultReceiver>("reply")
        val result = Bundle()
        try {
            val uri = intent.data ?: error("No URI")
            val hash = MessageDigest.getInstance("SHA-256")
            contentResolver.openInputStream(uri)?.use { input ->
                val buffer = ByteArray(8192)
                while (true) { val count = input.read(buffer); if (count < 0) break; hash.update(buffer, 0, count) }
            } ?: error("No stream")
            result.putString("sha256", hash.digest().joinToString("") { "%02x".format(it) })
            result.putBoolean("writeGranted", checkUriPermission(uri, Process.myPid(), Process.myUid(), Intent.FLAG_GRANT_WRITE_URI_PERMISSION) == PackageManager.PERMISSION_GRANTED)
            reply?.send(Activity.RESULT_OK, result)
        } catch (e: Exception) { result.putString("error", e.toString()); reply?.send(Activity.RESULT_CANCELED, result) }
        finally { finish() }
    }
}
