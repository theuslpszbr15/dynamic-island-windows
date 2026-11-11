package io.github.theuslpszbr15.dynamicisland

import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import okhttp3.mockwebserver.MockResponse
import okhttp3.mockwebserver.MockWebServer
import okio.ByteString
import okio.ByteString.Companion.toByteString
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

    private fun offer(name: String = "foto.jpg", size: Long = 10, id: String = this.id, sha: String = this.sha) =
        FileOffer.parse(JSONObject().put("type", "fileOffer").put("id", id).put("name", name).put("size", size).put("sha256", sha))

    @Test fun readsAnOffer() {
        val o = offer()!!
        assertEquals(id, o.id)
        assertEquals("foto.jpg", o.name)
        assertEquals(10, o.size)
        assertEquals(sha, o.sha256)
        assertNotNull(offer(size = 0))
        assertNotNull(offer(size = FileOffer.MAX_BYTES))
    }

    @Test fun refusesWhatIsNotAnOffer() {
        assertNull(offer(size = FileOffer.MAX_BYTES + 1)) // over 30 MB
        assertNull(offer(size = -1))
        assertNull(offer(id = "curto"))
        assertNull(offer(id = "0123456789ABCDEF0123456789ABCDEF"))
        assertNull(offer(sha = "b".repeat(63)))
        assertNull(offer(sha = "z".repeat(64)))
        assertNull(FileOffer.parse(JSONObject().put("id", id).put("sha256", sha)))
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
 * Downloads against a fake PC (a TLS server with a throwaway certificate) that answers a "fetch" line with the
 * header and the bytes, straight over the "LAN" (loopback) and through a fake relay.
 */
class FileFetchTest {
    private val token = "abcDEF0123456789_-xyz"
    private val key = "Rk_0123456789abcdefXYZ"
    private val id = "0123456789abcdef0123456789abcdef"
    private lateinit var server: SSLServerSocket
    private lateinit var fingerprint: String
    private val relay = MockWebServer()
    private val data = ByteArray(2 * 1024 * 1024 + 123).also { Random(7).nextBytes(it) }
    private val sha = LinkClient.sha256Hex(data)
    /** What the fake PC does after the header: the right bytes, other bytes, or a refusal. */
    @Volatile private var mode = "ok"
    @Volatile private var fetchLine: JSONObject? = null

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

    private fun fakePc(socket: Socket): Unit = socket.use {
        val input = it.getInputStream()
        val out = it.getOutputStream()
        fun send(o: JSONObject) { out.write((o.toString() + "\n").toByteArray()); out.flush() }
        val first = JSONObject(line(input) ?: return)
        fetchLine = first
        if (first.optString("type") != "fetch" || first.optString("token") != token) {
            send(JSONObject().put("type", "error").put("code", "auth").put("message", "não")); return
        }
        when (mode) {
            "refuse" -> return send(JSONObject().put("type", "error").put("code", "file").put("message", "Esse envio não vale mais."))
            "short" -> {
                send(JSONObject().put("type", "file").put("id", id).put("size", data.size).put("sha256", sha))
                out.write(data, 0, data.size / 2); out.flush(); return
            }
        }
        send(JSONObject().put("type", "file").put("id", id).put("size", data.size).put("sha256", sha))
        val body = if (mode == "tampered") data.copyOf().also { d -> d[1000] = (d[1000] + 1).toByte() } else data
        out.write(body); out.flush()
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

    private val offer get() = FileOffer(id, "dados.bin", data.size.toLong(), sha, 0)

    @Test fun downloadsOverTheLan() {
        val sink = ByteArrayOutputStream()
        var last = 0L
        LinkClient(pairing(), "teste").fetch(offer, sink) { last = it }
        assertArrayEquals(data, sink.toByteArray())
        assertEquals(data.size.toLong(), last)
        assertEquals("fetch", fetchLine!!.optString("type"))
        assertEquals(id, fetchLine!!.optString("id"))
    }

    @Test fun downloadsThroughTheRelay() {
        relay.enqueue(bridge())
        relay.start()
        val sink = ByteArrayOutputStream()
        LinkClient(pairing(), "teste", Route.Relay).fetch(offer, sink)
        assertArrayEquals(data, sink.toByteArray())
        val upgrade = relay.takeRequest()
        assertEquals("/phone/${pairing().room()}", upgrade.path)
        assertFalse("the token never reaches the relay in the clear", upgrade.requestLine.contains(token))
    }

    @Test fun refusesBytesThatDontMatchTheHash() {
        mode = "tampered"
        val error = runCatching { LinkClient(pairing(), "teste").fetch(offer, ByteArrayOutputStream()) }.exceptionOrNull()
        assertTrue("$error", error is IOException && error.message!!.contains("diferente"))
    }

    @Test fun refusesAFileCutShort() {
        mode = "short"
        val error = runCatching { LinkClient(pairing(), "teste").fetch(offer, ByteArrayOutputStream()) }.exceptionOrNull()
        assertTrue("$error", error is IOException)
    }

    @Test fun refusesAnotherFileThanOffered() {
        val other = offer.copy(sha256 = "b".repeat(64))
        val error = runCatching { LinkClient(pairing(), "teste").fetch(other, ByteArrayOutputStream()) }.exceptionOrNull()
        assertTrue("$error", error is IOException && error.message!!.contains("outro arquivo"))
    }

    @Test fun saysWhenThePcRefuses() {
        mode = "refuse"
        val error = runCatching { LinkClient(pairing(), "teste").fetch(offer, ByteArrayOutputStream()) }.exceptionOrNull()
        assertTrue("$error", error is FileRefusedException && error.message!!.contains("não vale mais"))
    }

    @Test fun needsTheToken() {
        val error = runCatching { LinkClient(pairing("outroToken0123456789"), "teste").fetch(offer, ByteArrayOutputStream()) }.exceptionOrNull()
        assertTrue("$error", error is AuthException)
    }

    @Test fun refusesAnotherPc() {
        val wrong = pairing().copy(fingerprint = "0".repeat(64))
        val error = runCatching { LinkClient(wrong, "teste").fetch(offer, ByteArrayOutputStream()) }.exceptionOrNull()
        assertNotNull("a different certificate must be refused", error)
        assertNull("nothing was asked of a PC that isn't the paired one", fetchLine)
    }
}
