package com.zhanh.bluebridge

import org.junit.Assert.*
import org.junit.Test

class PermissionPolicyTest {
    @Test fun onlyNeededRuntimePermissionsForEachSupportedApi() {
        assertTrue(BluetoothPermissions.required(29).isEmpty())
        assertTrue(BluetoothPermissions.required(30).isEmpty())
        assertEquals(listOf("android.permission.BLUETOOTH_CONNECT"), BluetoothPermissions.required(31))
        assertEquals(listOf("android.permission.BLUETOOTH_CONNECT"), BluetoothPermissions.required(32))
        for (sdk in listOf(33, 34, 35, 36)) assertEquals(listOf("android.permission.BLUETOOTH_CONNECT", "android.permission.POST_NOTIFICATIONS"), BluetoothPermissions.required(sdk))
    }
}
