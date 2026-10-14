package com.zhanh.bluebridge

import androidx.compose.runtime.*
import androidx.compose.ui.test.*
import androidx.compose.ui.test.junit4.createComposeRule
import com.zhanh.bluebridge.core.ReceivedFileRecord
import org.junit.Assert.assertEquals
import org.junit.Rule
import org.junit.Test
import java.util.UUID

class ReceivedFileUiTest {
    @get:Rule val compose = createComposeRule()
    private val file = ReceivedFileRecord(UUID.randomUUID(), "content://media/external/downloads/1", "图片.jpg", "image/jpeg", 0, 1)
    @Test fun successfulRowOpensOnceAndDisablesWhileChooserIsActive() {
        var opened = 0
        val row = TransferRow(file.id, file.name, incoming = true, status = RECEIVED_FILE_STATUS, receivedFile = file)
        compose.setContent {
            var opening by remember { mutableStateOf<UUID?>(null) }
            BlueBridgeTheme { ReceivedFileOpenButton(row, opening) { assertEquals(file, it); opened++; opening = it.id } }
        }
        compose.onNodeWithTag("open-file-${file.id}").performClick()
        compose.onNodeWithTag("open-file-${file.id}").assertIsNotEnabled().performClick()
        compose.runOnIdle { assertEquals(1, opened) }
    }
    @Test fun failedOrIncompleteRowsNeverShowOpenButton() {
        compose.setContent { BlueBridgeTheme { ReceivedFileOpenButton(TransferRow(file.id, file.name, incoming = true, status = "接收中"), null) { error("Unexpected open") } } }
        compose.onNodeWithTag("open-file-${file.id}").assertDoesNotExist()
    }
}
