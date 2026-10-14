package com.zhanh.bluebridge.core

import java.io.File
import java.nio.file.FileAlreadyExistsException
import java.nio.file.Files

object AtomicFiles {
    /** Move a verified file within a local filesystem, without replace-existing. */
    fun commit(temporary: File, directory: File, name: String): File {
        TransferInfo(name, temporary.length(), "file", "application/octet-stream").validate()
        directory.mkdirs()
        val staged = File.createTempFile(".bluebridge-", ".part", directory)
        try {
            temporary.inputStream().use { source -> staged.outputStream().use { source.copyTo(it, Wire.CHUNK_SIZE) } }
            val dot = name.lastIndexOf('.').takeIf { it > 0 } ?: name.length
            for (suffix in 0..9999) {
                val candidate = if (suffix == 0) name else name.substring(0, dot).take(210) + " ($suffix)" + name.substring(dot)
                val target = File(directory, candidate)
                try { Files.move(staged.toPath(), target.toPath()); return target }
                catch (_: FileAlreadyExistsException) { }
            }
            throw java.io.IOException("同名文件过多")
        } finally { staged.delete() }
    }
}
