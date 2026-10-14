package com.zhanh.bluebridge.core

import java.io.File
import java.util.UUID
import java.util.concurrent.CompletableFuture
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicInteger
import kotlin.system.exitProcess

/** Test-only stdin/stdout RFCOMM surrogate. This source set is excluded from the APK. */
fun main(args: Array<String>) {
    try {
        var receiveDir = File("received"); var expected = 0
        val sends = mutableListOf<Pair<String, String>>()
        var cursor = 0
        while (cursor < args.size) {
            val key = args[cursor++]; val value = args.getOrNull(cursor++) ?: error("Missing $key argument")
            when (key) {
                "--receive-dir" -> receiveDir = File(value)
                "--expect" -> expected = value.toInt()
                "--send-file", "--send-text-base64", "--send-image" -> sends.add(key to value)
                else -> error("Unknown argument $key")
            }
        }
        receiveDir.mkdirs(); val temporary = File(receiveDir, ".partial")
        val count = AtomicInteger(); val completed = CompletableFuture<Unit>()
        if (expected == 0) completed.complete(Unit)
        lateinit var session: Session
        val listener = object : SessionListener {
            override fun peerHello(id: UUID, name: String) { session.approve(true) }
            override fun text(id: UUID, value: String) {
                val temp = File.createTempFile("text-", ".part", temporary); temp.writeText(value, Charsets.UTF_8)
                AtomicFiles.commit(temp, receiveDir, "text-$id.txt"); temp.delete(); received()
            }
            override fun received(id: UUID, info: TransferInfo, file: File) { AtomicFiles.commit(file, receiveDir, info.name); received() }
            private fun received() { if (count.incrementAndGet() >= expected) completed.complete(Unit) }
            override fun transferFailed(id: UUID, reason: String) { System.err.println("Transfer $id: $reason") }
            override fun closed(reason: String) { if (count.get() < expected) completed.completeExceptionally(java.io.IOException(reason)) }
        }
        session = Session(System.`in`, System.out, UUID.randomUUID(), "Android test peer", temporary, listener)
        session.start(); session.awaitReady()
        sends.forEach { (kind, value) ->
            if (kind == "--send-text-base64") session.sendText(Wire.text(java.util.Base64.getDecoder().decode(value)))
            else {
                val file = File(value); val image = kind == "--send-image"
                val info = TransferInfo(file.name, file.length(), if (image) "image" else "file", if (image) "image/png" else "application/octet-stream")
                session.send(TransferSource(info) { file.inputStream() })
            }
        }
        completed.get(120, TimeUnit.SECONDS)
        // Allow the last completion ACK to cross the process bridge before exit.
        Thread.sleep(150)
    } catch (e: Exception) { System.err.println(e.cause?.message ?: e.message); exitProcess(1) }
}
