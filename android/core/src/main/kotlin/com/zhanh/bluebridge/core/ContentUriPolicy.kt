package com.zhanh.bluebridge.core

import java.io.IOException

object ContentUriPolicy {
    fun requireContent(scheme: String?) {
        if (scheme != "content") throw IOException("仅支持已授权的系统内容 URI，请重新选择或分享文件")
    }
}
