package com.zhanh.bluebridge

import android.Manifest
import android.content.Intent
import android.content.pm.PackageManager
import android.content.pm.ServiceInfo
import android.net.Uri
import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import org.junit.Assert.*
import org.junit.Test
import org.junit.runner.RunWith

@RunWith(AndroidJUnit4::class)
class PlatformTest {
    @Test fun offlineManifestAndSecureComponentBoundary() {
        val context = InstrumentationRegistry.getInstrumentation().targetContext
        val info = context.packageManager.getPackageInfo(context.packageName, PackageManager.GET_PERMISSIONS or PackageManager.GET_SERVICES or PackageManager.GET_PROVIDERS)
        assertFalse(info.requestedPermissions!!.contains(Manifest.permission.INTERNET))
        assertFalse(info.requestedPermissions!!.contains(Manifest.permission.READ_EXTERNAL_STORAGE))
        val service = info.services!!.single { it.name.endsWith("BridgeService") }
        assertFalse(service.exported); assertEquals(ServiceInfo.FOREGROUND_SERVICE_TYPE_CONNECTED_DEVICE, service.foregroundServiceType)
        val provider = info.providers!!.single { it.name == "androidx.core.content.FileProvider" }; assertFalse(provider.exported); assertTrue(provider.grantUriPermissions)
    }
    @Test fun permissionPolicyAcrossSupportedAndroidVersions() {
        assertTrue(BluetoothPermissions.required(29).isEmpty())
        assertEquals(listOf(Manifest.permission.BLUETOOTH_CONNECT), BluetoothPermissions.required(31))
        listOf(33, 36).forEach { assertEquals(listOf(Manifest.permission.BLUETOOTH_CONNECT, Manifest.permission.POST_NOTIFICATIONS), BluetoothPermissions.required(it)) }
    }
    @Test fun shareReceiverCanResolveTextAndImage() {
        val context = InstrumentationRegistry.getInstrumentation().targetContext
        listOf("text/plain", "image/png", "application/octet-stream").forEach { type ->
            val intent = Intent(Intent.ACTION_SEND).setType(type).setPackage(context.packageName)
            assertNotNull(context.packageManager.resolveActivity(intent, 0))
        }
    }
}
