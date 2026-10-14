package com.zhanh.bluebridge

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp

/** A visible focused Activity is required to read Android 10+ clipboard contents. */
class SendClipboardActivity : ComponentActivity() {
    private var sent = false
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContent {
            BlueBridgeTheme {
                val state by BridgeRuntime.ui.collectAsState()
                Surface(Modifier.fillMaxSize()) { Column(Modifier.safeDrawingPadding().padding(26.dp), verticalArrangement = Arrangement.Center) {
                    Text("发送剪贴板", style = MaterialTheme.typography.headlineMedium)
                    Text(state.notice, Modifier.padding(vertical = 22.dp))
                    Button(onClick = { sendClipboard(this@SendClipboardActivity) }, enabled = state.connected, modifier = Modifier.fillMaxWidth()) { Text("再次发送") }
                    TextButton(onClick = { finish() }, modifier = Modifier.fillMaxWidth()) { Text("返回") }
                } }
            }
        }
    }
    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (hasFocus && !sent) { sent = true; sendClipboard(this) }
    }
}
