package io.github.theuslpszbr15.dynamicisland

import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import okhttp3.mockwebserver.MockResponse
import okhttp3.mockwebserver.MockWebServer
import okio.ByteString
import okio.ByteString.Companion.toByteString
import org.json.JSONArray
import org.json.JSONObject
import org.junit.After
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Assume.assumeTrue
import org.junit.Before
import org.junit.Test
import java.io.ByteArrayOutputStream
import java.io.File
import java.io.IOException
import java.io.InputStream
import java.net.InetAddress
import java.net.Socket
import java.security.KeyStore
import java.util.Random
import javax.net.ssl.KeyManagerFactory
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLServerSocket
import kotlin.concurrent.thread

class FileOfferTest {
    private val id = "0123456789abcdef0123456789abcdef"
    private val sha = "a".repeat(64)

    private fun item(name: String = "foto.jpg", size: Long = 10, sha: String = this.sha, text: Boolean = false) =
        JSONObject().put("name", name).put("size", size).put("sha256", sha).apply { if (text) put("text", true) }

    private fun offer(vararg items: JSONObject, id: String = this.id) =
        FileOffer.parse(JSONObject().put("type", "fileOffer").put("id", id).put("items", JSONArray().apply { items.forEach { put(it) } }))

    @Test fun readsABatch() {
        val o = offer(item(), item("video.mp4", 50L * 1024 * 1024))!!
        assertEquals(id, o.id)
        assertEquals(2, o.items.size)
        assertEquals("foto.jpg", o.items[0].name)
        assertEquals(10 + 50L * 1024 * 1024, o.totalBytes)
        assertEquals("2 arquivos", o.label)
        assertNotNull(offer(item(size = 0)))
        assertNotNull(offer(item(size = FileOffer.MAX_BYTES)))
        assertTrue(offer(item(text = true, size = 1000))!!.isText)
    }

    @Test fun limitsBySizeAndKind() {
        assertNull(offer(item(size = FileOffer.MAX_BYTES + 1))) // a photo over 30 MB
        assertNotNull(offer(item("filme.MP4", FileOffer.MAX_VIDEO_BYTES))) // a video up to 60 MB
        assertNull(offer(item("filme.mp4", FileOffer.MAX_VIDEO_BYTES + 1)))
        assertNull(offer(item(text = true, size = FileOffer.MAX_TEXT_BYTES + 1L)))
        assertNull(offer(*Array(FileOffer.MAX_ITEMS + 1) { item() }))
        assertNull(offer(item(text = true), item())) // a long text travels alone
    }

    @Test fun refusesWhatIsNotAnOffer() {
        assertNull(offer(item(size = -1)))
        assertNull(offer(item(), id = "curto"))
        assertNull(offer(item(), id = "0123456789ABCDEF0123456789ABCDEF"))
        assertNull(offer(item(sha = "b".repeat(63))))
        assertNull(offer(item(sha = "z".repeat(64))))
        assertNull(offer())
        assertNull(FileOffer.parse(JSONObject().put("id", id)))
    }

    @Test fun newIdsLookLikeThePcs() {
        val a = FileOffer.newId()
        assertTrue(FileOffer.isId(a))
        assertFalse(a == FileOffer.newId())
    }

    @Test fun onlyAPlainFileName() {
        assertEquals("passwd", FileOffer.safeName("../../etc/passwd"))
        assertEquals("boot.ini", FileOffer.safeName("C:\\Windows\\boot.ini"))
        assertEquals("bashrc", FileOffer.safeName(".bashrc"))
        assertEquals("ab.txt", FileOffer.safeName("a\u0000b\n.txt"))
        assertEquals("arquivo", FileOffer.safeName(""))
        assertEquals("arquivo", FileOffer.safeName("..."))
        assertEquals("nota fiscal.pdf", FileOffer.safeName("nota fiscal.pdf"))
        assertEquals("ab.txt", FileOffer.safeName("a<>:\"|?*b.txt"))
        // a right-to-left override can't disguise the extension
        assertEquals("fotogpj.apk", FileOffer.safeName("foto\u202Egpj.apk"))
        val long = FileOffer.safeName("x".repeat(300) + ".jpeg")
        assertEquals(100, long.length)
        assertTrue(long.endsWith(".jpeg"))
    }
}

/**
 * Both directions against a fake PC (a TLS server with a throwaway certificate): "fetch" gets a batch of two files,
 * "push" sends one, straight over the "LAN" (loopback) and through a fake relay.
 */
class FileFetchTest {
    private val token = "abcDEF0123456789_-xyz"
    private val key = "Rk_0123456789abcdefXYZ"
    private val id = "0123456789abcdef0123456789abcdef"
    private lateinit var server: SSLServerSocket
    private lateinit var fingerprint: String
    private val relay = MockWebServer()
    private val data = ByteArray(2 * 1024 * 1024 + 123).also { Random(7).nextBytes(it) }
    private val small = "olá, celular".toByteArray()
    private val files = listOf(data, small)
    /** What the fake PC does: the right bytes, other bytes, a cut file, or a refusal. */
    @Volatile private var mode = "ok"
    @Volatile private var firstLine: JSONObject? = null
    /** What a push delivered, as the fake PC checked it. */
    @Volatile private var pushed: List<ByteArray>? = null

    @Before fun setUp() {
        val keytool = listOf("bin/keytool", "bin/keytool.exe").map { File(System.getProperty("java.home"), it) }.firstOrNull { it.exists() } ?: File("keytool")
        assumeTrue("keytool not found", keytool.exists())
        val store = File.createTempFile("pc-cert", ".p12").apply { delete() }
        val made = ProcessBuilder(keytool.path, "-genkeypair", "-alias", "pc", "-keyalg", "EC", "-groupname", "secp256r1",
            "-dname", "CN=pc", "-validity", "2", "-storetype", "PKCS12", "-keystore", store.path,
            "-storepass", "changeit", "-keypass", "changeit").redirectErrorStream(true).start().waitFor()
        assumeTrue("keytool failed", made == 0)
        val ks = KeyStore.getInstance("PKCS12").apply { store.inputStream().use { load(it, "changeit".toCharArray()) } }
        fingerprint = LinkClient.sha256Hex(ks.getCertificate("pc").encoded)
        val kmf = KeyManagerFactory.getInstance(KeyManagerFactory.getDefaultAlgorithm()).apply { init(ks, "changeit".toCharArray()) }
        val tls = SSLContext.getInstance("TLS").apply { init(kmf.keyManagers, null, null) }
        server = tls.serverSocketFactory.createServerSocket(0, 5, InetAddress.getLoopbackAddress()) as SSLServerSocket
        thread(isDaemon = true) { while (true) { val s = try { server.accept() } catch (_: IOException) { break }; thread(isDaemon = true) { fakePc(s) } } }
    }

    @After fun tearDown() {
        if (::server.isInitialized) server.close()
        relay.shutdown()
    }

    private fun line(input: InputStream): String? {
        val out = ByteArrayOutputStream()
        while (true) {
            val b = input.read()
            if (b == -1) return null
            if (b == '\n'.code) return out.toString(Charsets.UTF_8.name())
            out.write(b)
        }
    }

    private fun readExactly(input: InputStream, size: Int): ByteArray {
        val out = ByteArray(size)
        var at = 0
        while (at < size) {
            val n = input.read(out, at, size - at)
            if (n == -1) throw IOException("cut")
            at += n
        }
        return out
    }

    private fun fakePc(socket: Socket): Unit = socket.use {
        val input = it.getInputStream()
        val out = it.getOutputStream()
        fun send(o: JSONObject) { out.write((o.toString() + "\n").toByteArray()); out.flush() }
        val first = JSONObject(line(input) ?: return)
        firstLine = first
        if (first.optString("type") !in setOf("fetch", "push") || first.optString("token") != token) {
            send(JSONObject().put("type", "error").put("code", "auth").put("message", "não")); return
        }
        if (mode == "refuse") return send(JSONObject().put("type", "error").put("code", "file").put("message", "Esse envio não vale mais."))
        if (first.optString("type") == "push") {
            send(JSONObject().put("type", "ready"))
            val got = mutableListOf<ByteArray>()
            for (i in 0 until 1) {
                val h = JSONObject(line(input) ?: return)
                val bytes = readExactly(input, h.optInt("size"))
                if (h.optInt("index") != i || LinkClient.sha256Hex(bytes) != h.optString("sha256")) {
                    send(JSONObject().put("type", "saved").put("ok", false)); return
                }
                got += bytes
            }
            pushed = got
            send(JSONObject().put("type", "saved").put("ok", true).put("count", got.size))
            return
        }
        files.forEachIndexed { i, bytes ->
            send(JSONObject().put("type", "file").put("id", id).put("index", i).put("size", bytes.size).put("sha256", LinkClient.sha256Hex(bytes)))
            when {
                mode == "short" -> { out.write(bytes, 0, bytes.size / 2); out.flush(); return }
                mode == "tampered" && i == 1 -> out.write(bytes.copyOf().also { d -> d[3] = (d[3] + 1).toByte() })
                else -> out.write(bytes)
            }
            out.flush()
        }
        input.read() // the phone hangs up when it has everything
        Unit
    }

    /** The relay's job: carry binary messages to and from the PC's TCP port, unchanged. */
    private fun bridge() = MockResponse().withWebSocketUpgrade(object : WebSocketListener() {
        private var pc: Socket? = null
        override fun onOpen(webSocket: WebSocket, response: Response) {
            val s = Socket(InetAddress.getLoopbackAddress(), server.localPort)
            pc = s
            thread(isDaemon = true) {
                val buffer = ByteArray(16 * 1024)
                try {
                    while (true) {
                        val n = s.getInputStream().read(buffer)
                        if (n == -1) break
                        webSocket.send(buffer.toByteString(0, n))
                    }
                } catch (_: IOException) {}
                webSocket.close(1000, null)
            }
        }
        override fun onMessage(webSocket: WebSocket, bytes: ByteString) {
            pc?.getOutputStream()?.apply { write(bytes.toByteArray()); flush() }
        }
        override fun onClosing(webSocket: WebSocket, code: Int, reason: String) { pc?.close() }
    })

    private fun pairing(t: String = token) =
        Pairing("127.0.0.1", server.localPort, fingerprint, t, "PC", "ws://127.0.0.1:${relay.port}", key)

    private val offer get() = FileOffer(id, files.mapIndexed { i, b -> OfferItem("f$i.bin", b.size.toLong(), LinkClient.sha256Hex(b)) }, 0)

    /** Collects what fetch keeps and drops. */
    private class Collect : LinkClient.Sink {
        val open = mutableMapOf<Int, ByteArrayOutputStream>()
        val kept = mutableMapOf<Int, ByteArray>()
        val dropped = mutableListOf<Int>()
        var last = 0L
        override fun open(index: Int, item: OfferItem) = ByteArrayOutputStream().also { open[index] = it }
        override fun keep(index: Int, item: OfferItem) { kept[index] = open.remove(index)!!.toByteArray() }
        override fun drop(index: Int, item: OfferItem) { open.remove(index); dropped += index }
        override fun progress(done: Long) { last = done }
    }

    @Test fun downloadsABatchOverTheLan() {
        val sink = Collect()
        LinkClient(pairing(), "teste").fetch(offer, sink)
        assertArrayEquals(data, sink.kept[0])
        assertArrayEquals(small, sink.kept[1])
        assertEquals(offer.totalBytes, sink.last)
        assertEquals("fetch", firstLine!!.optString("type"))
        assertEquals(id, firstLine!!.optString("id"))
    }

    @Test fun downloadsThroughTheRelay() {
        relay.enqueue(bridge())
        relay.start()
        val sink = Collect()
        LinkClient(pairing(), "teste", Route.Relay).fetch(offer, sink)
        assertArrayEquals(data, sink.kept[0])
        val upgrade = relay.takeRequest()
        assertEquals("/phone/${pairing().room()}", upgrade.path)
        assertFalse("the token never reaches the relay in the clear", upgrade.requestLine.contains(token))
    }

    @Test fun dropsBytesThatDontMatchTheHash() {
        mode = "tampered"
        val sink = Collect()
        val error = runCatching { LinkClient(pairing(), "teste").fetch(offer, sink) }.exceptionOrNull()
        assertTrue("$error", error is IOException && error.message!!.contains("diferente"))
        assertArrayEquals("the good file before it stays", data, sink.kept[0])
        assertEquals(listOf(1), sink.dropped)
    }

    @Test fun dropsAFileCutShort() {
        mode = "short"
        val sink = Collect()
        val error = runCatching { LinkClient(pairing(), "teste").fetch(offer, sink) }.exceptionOrNull()
        assertTrue("$error", error is IOException)
        assertTrue(sink.kept.isEmpty())
        assertEquals(listOf(0), sink.dropped)
    }

    @Test fun refusesAnotherFileThanOffered() {
        val other = offer.copy(items = listOf(offer.items[0].copy(sha256 = "b".repeat(64)), offer.items[1]))
        val error = runCatching { LinkClient(pairing(), "teste").fetch(other, Collect()) }.exceptionOrNull()
        assertTrue("$error", error is IOException && error.message!!.contains("outro arquivo"))
    }

    @Test fun saysWhenThePcRefuses() {
        mode = "refuse"
        val error = runCatching { LinkClient(pairing(), "teste").fetch(offer, Collect()) }.exceptionOrNull()
        assertTrue("$error", error is FileRefusedException && error.message!!.contains("não vale mais"))
    }

    @Test fun needsTheToken() {
        val error = runCatching { LinkClient(pairing("outroToken0123456789"), "teste").fetch(offer, Collect()) }.exceptionOrNull()
        assertTrue("$error", error is AuthException)
    }

    @Test fun refusesAnotherPc() {
        val wrong = pairing().copy(fingerprint = "0".repeat(64))
        val error = runCatching { LinkClient(wrong, "teste").fetch(offer, Collect()) }.exceptionOrNull()
        assertNotNull("a different certificate must be refused", error)
        assertNull("nothing was asked of a PC that isn't the paired one", firstLine)
    }

    @Test fun sendsAFileToThePc() {
        val item = OfferItem("foto.jpg", data.size.toLong(), LinkClient.sha256Hex(data))
        var last = 0L
        LinkClient(pairing(), "teste").push(id, listOf(LinkClient.Outgoing(item) { data.inputStream() })) { last = it }
        assertEquals("push", firstLine!!.optString("type"))
        assertArrayEquals(data, pushed!!.single())
        assertEquals(data.size.toLong(), last)
    }

    @Test fun sendsThroughTheRelay() {
        relay.enqueue(bridge())
        relay.start()
        val item = OfferItem("foto.jpg", data.size.toLong(), LinkClient.sha256Hex(data))
        LinkClient(pairing(), "teste", Route.Relay).push(id, listOf(LinkClient.Outgoing(item) { data.inputStream() }))
        assertArrayEquals(data, pushed!!.single())
    }

    @Test fun saysWhenThePcDidNotAccept() {
        mode = "refuse"
        val item = OfferItem("foto.jpg", small.size.toLong(), LinkClient.sha256Hex(small))
        val error = runCatching { LinkClient(pairing(), "teste").push(id, listOf(LinkClient.Outgoing(item) { small.inputStream() })) }.exceptionOrNull()
        assertTrue("$error", error is FileRefusedException)
    }

    @Test fun thePcNoticesAFileThatChanged() {
        // the phone promised one hash and sends other bytes: the PC says it didn't save
        val item = OfferItem("foto.jpg", small.size.toLong(), "c".repeat(64))
        val error = runCatching { LinkClient(pairing(), "teste").push(id, listOf(LinkClient.Outgoing(item) { small.inputStream() })) }.exceptionOrNull()
        assertTrue("$error", error is IOException && error.message!!.contains("guardar"))
        assertNull(pushed)
    }
}
