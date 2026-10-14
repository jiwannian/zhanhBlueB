package com.zhanh.bluebridge

import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.testTag
import com.zhanh.bluebridge.core.ReceivedFileRecord
import java.util.UUID

@Composable
internal fun ReceivedFileOpenButton(row: TransferRow, opening: UUID?, open: (ReceivedFileRecord) -> Unit) {
    if (canOpenReceivedFile(row)) TextButton(
        onClick = { row.receivedFile?.let(open) }, enabled = opening == null,
        modifier = Modifier.testTag("open-file-${row.id}")
    ) { Text(if (opening == row.id) "打开中…" else "打开") }
}
