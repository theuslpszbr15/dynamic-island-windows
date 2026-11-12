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
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Assume.assumeTrue
import org.junit.Before
import org.junit.Test
import java.io.File
import java.io.IOException
import java.net.InetAddress
import java.net.Socket
import java.security.KeyStore
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import javax.net.ssl.KeyManagerFactory
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLServerSocket
import kotlin.concurrent.thread

class RelayPairingTest {
    private val fp = "a".repeat(64)
    private val token = "abcDEF0123456789_-xyz"
    private val key = "Rk_0123456789abcdefXYZ"
    private val base = "dynamicisland://pair?v=1&host=192.168.0.12&port=47900&fp=$fp&token=$token&name=PC"

    @Test fun readsTheRelayFromTheQr() {
        val p = Pairing.parse("$base&relay=wss%3A%2F%2Fdynamic-island-relay.example.workers.dev&rk=$key")!!
        assertEquals("wss://dynamic-island-relay.example.workers.dev", p.relay)
        assertEquals(key, p.relayKey)
        assertEquals(p, Pairing.fromJson(p.toJson()))
        // the LAN address rules don't change
        assertEquals("192.168.0.12", p.host)
    }

    @Test fun withoutRelayItIsTheOldPairing() {
        val p = Pairing.parse(base)!!
        assertNull(p.relay)
        assertNull(p.relayKey)
        assertEquals(p, Pairing.fromJson(p.toJson()))
        // pairings saved before the relay existed
        assertEquals(p, Pairing.fromJson(JSONObject(p.toJson()).apply { remove("relay"); remove("rk") }.toString()))
    }

    @Test fun onlyAPlainWssOrigin() {
        assertEquals("wss://relay.example.com", Pairing.relayOrigin("wss://relay.example.com/"))
        assertEquals("wss://relay.example.com", Pairing.relayOrigin("WSS://Relay.Example.com"))
        assertNull(Pairing.relayOrigin("ws://relay.example.com"))
        assertNull(Pairing.relayOrigin("https://relay.example.com"))
        assertNull(Pairing.relayOrigin("wss://relay.example.com:8443"))
        assertNull(Pairing.relayOrigin("wss://relay.example.com/other/path"))
        assertNull(Pairing.relayOrigin("wss://user@relay.example.com"))
        assertNull(Pairing.relayOrigin("wss://relay.example.com?x=1"))
        assertNull(Pairing.relayOrigin("wss://localhost"))
        assertNull(Pairing.relayOrigin(""))
        // a QR with a bad relay or key is refused as a whole
        assertNull(Pairing.parse("$base&relay=ws%3A%2F%2Frelay.example.com"))
        assertNull(Pairing.parse("$base&relay=wss%3A%2F%2Frelay.example.com&rk=curta"))
    }

    @Test fun roomIsAHashOfTheToken() {
        val p = Pairing.parse(base)!!
        assertEquals("7950b9b1a8206956754b71db2646ce2e632fc3cd0cb5b1a89c02652cc13aaa3e", p.room())
        assertTrue(!p.room().contains(token))
    }

    @Test fun lanErrorsSayWhatToCheck() {
        val p = Pairing.parse(base)!!
        val refused = java.net.ConnectException("failed to connect to /192.168.0.12 (port 47900) after 2000ms: isConnected failed: ECONNREFUSED (Connection refused)")
        assertTrue(LinkClient.lanError(p, refused).contains("recusou"))
        assertTrue(LinkClient.lanError(p, java.net.SocketTimeoutException("failed to connect to /192.168.0.12 (port 47900) after 2000ms")).contains("firewall"))
        assertTrue(LinkClient.lanError(p, refused).contains("192.168.0.12:47900"))
    }

    @Test fun closeCodesInWords() {
        assertNull(RelayWire.closeError(1000))
        assertTrue(RelayWire.closeError(4404) is RelayException)
        assertTrue(RelayWire.closeError(4429) is RelayException)
        assertTrue(RelayWire.closeError(4400) is RelayException)
        assertNotNull(RelayWire.closeError(1011))
        // the public relay's limits and its check of the PC's key
        assertEquals(RelayWire.LIMIT_MESSAGE, RelayWire.closeError(4429, "daily limit")!!.message)
        assertEquals(RelayWire.LIMIT_MESSAGE, RelayWire.closeError(4429, "rate limit")!!.message)
        assertTrue(RelayWire.LIMIT_MESSAGE.contains("seu próprio relay"))
        assertTrue(RelayWire.closeError(4429, "room full")!!.message!!.contains("Aparelhos demais"))
        assertTrue(RelayWire.closeError(4401)!!.message!!.contains("Parear de novo"))
    }

    @Test fun theQrOfThePublicRelayHasNoKey() {
        val p = Pairing.parse("$base&relay=wss%3A%2F%2Fdynamic-island-relay.brendoribeiron.workers.dev")!!
        assertEquals("wss://dynamic-island-relay.brendoribeiron.workers.dev", p.relay)
        assertNull(p.relayKey)
        assertEquals(p, Pairing.fromJson(p.toJson()))
        // and an old QR with rk= still pairs (an own relay with a key)
        assertEquals(key, Pairing.parse("$base&relay=wss%3A%2F%2Frelay.example.com&rk=$key")!!.relayKey)
    }

    @Test fun filesThroughTheRelayStopAt10Mb() {
        assertTrue(FileOffer.fitsRoute(FileOffer.RELAY_MAX_BYTES, Route.Relay))
        assertTrue(!FileOffer.fitsRoute(FileOffer.RELAY_MAX_BYTES + 1, Route.Relay))
        assertTrue(FileOffer.fitsRoute(FileOffer.MAX_BYTES, Route.Lan))
        assertTrue(FileOffer.RELAY_TOO_BIG.contains("10 MB"))
    }

    @Test fun pingsRarelyThroughTheRelay() {
        assertEquals(LinkClient.PING_EVERY_MS, LinkClient.pingEvery(Route.Lan))
        assertEquals(300_000L, LinkClient.pingEvery(Route.Relay))
        assertTrue(LinkClient.RELAY_READ_TIMEOUT_MS > LinkClient.RELAY_PING_EVERY_MS)
        val pairing = Pairing.parse(base)!!
        fun attempts(route: Route, times: List<Long>) = LinkClient(pairing, "teste", route).let { c ->
            times.count { t -> try { c.pingIfDue(t) } catch (_: IOException) { true } } // no connection: the try still counts
        }
        val minute = (0L..10L).map { 1_000_000L + it * 20_000L } // a ping tick every 20 s for ~3 minutes
        assertEquals(1, attempts(Route.Relay, minute))
        assertEquals(minute.size, attempts(Route.Lan, minute))
        assertEquals(2, attempts(Route.Relay, listOf(1_000_000L, 1_200_000L, 1_300_000L)))
    }
}

/**
 * The whole path on this machine: a fake relay (MockWebServer) that bridges the phone's WebSocket to a fake PC
 * (a TLS server with a throwaway certificate) speaking the link's JSON.
 */
class RelayLinkTest {
    private val token = "abcDEF0123456789_-xyz"
    private val key = "Rk_0123456789abcdefXYZ"
    private lateinit var server: SSLServerSocket
    private lateinit var fingerprint: String
    private val relay = MockWebServer()
    private val heard = mutableListOf<String>()

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

    /** Answers like the island: welcome (with the relay), sessions, pong. */
    private fun fakePc(socket: Socket) = socket.use {
        val input = it.getInputStream().bufferedReader()
        val out = it.getOutputStream()
        fun send(o: JSONObject) { out.write((o.toString() + "\n").toByteArray()); out.flush() }
        val hello = JSONObject(input.readLine() ?: return)
        if (hello.optString("token") != token) { send(JSONObject().put("type", "error").put("code", "auth").put("message", "não")); return }
        send(JSONObject().put("type", "welcome").put("v", 1).put("desktop", "PC-TESTE")
            .put("relay", "wss://novo-relay.example.com").put("relayKey", "Nova_chave_0123456789"))
        send(JSONObject().put("type", "sessions").put("sessions", org.json.JSONArray()
            .put(JSONObject().put("id", "1").put("agent", "Claude Code").put("project", "x").put("state", "working")
                .put("detail", "y".repeat(40_000)).put("updatedAt", 1))))
        while (true) {
            val line = input.readLine() ?: break
            synchronized(heard) { heard += JSONObject(line).optString("type") }
            if (JSONObject(line).optString("type") == "ping") send(JSONObject().put("type", "pong"))
        }
    }

    /** The relay's job: carry binary messages to and from the PC's TCP port, unchanged. */
    private fun bridge() = MockResponse().withWebSocketUpgrade(object : WebSocketListener() {
        private var pc: Socket? = null
        override fun onOpen(webSocket: WebSocket, response: Response) {
            val s = Socket(InetAddress.getLoopbackAddress(), server.localPort)
            pc = s
            thread(isDaemon = true) {
                val buffer = ByteArray(8192)
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

    private fun pairing(fp: String = fingerprint) =
        Pairing("192.168.0.12", 47900, fp, token, "PC", "ws://127.0.0.1:${relay.port}", key)

    private open class Quiet : LinkClient.Listener {
        override fun onWelcome(desktop: String) {}
        override fun onSessions(sessions: List<Session>) {}
        override fun onApproval(approval: Approval) {}
        override fun onResolved(fingerprint: String) {}
    }

    @Test fun talksToThePcThroughTheRelay() {
        relay.enqueue(bridge())
        relay.start()
        val client = LinkClient(pairing(), "teste", Route.Relay)
        val welcomed = CountDownLatch(1)
        val listed = CountDownLatch(1)
        val ponged = CountDownLatch(1)
        var learned: Pair<String?, String?>? = null
        var sessions: List<Session> = emptyList()
        var failure: Throwable? = null
        val worker = thread {
            try {
                client.run(object : Quiet() {
                    override fun onWelcome(desktop: String) { assertEquals("PC-TESTE", desktop); welcomed.countDown() }
                    override fun onSessions(list: List<Session>) { sessions = list; listed.countDown() }
                    override fun onPong() = ponged.countDown()
                    override fun onRelay(relay: String?, relayKey: String?) { learned = relay to relayKey }
                })
            } catch (t: Throwable) { failure = t }
        }
        assertTrue("no welcome ($failure)", welcomed.await(10, TimeUnit.SECONDS))
        assertTrue("no sessions", listed.await(5, TimeUnit.SECONDS))
        // a 40 KB line crosses several relay messages and TLS records
        assertEquals(40_000, sessions.single().detail.length)
        assertEquals("wss://novo-relay.example.com" to "Nova_chave_0123456789", learned)
        client.ping()
        assertTrue("no pong", ponged.await(5, TimeUnit.SECONDS))
        client.close()
        worker.join(3000)
        assertNull(failure)

        val upgrade = relay.takeRequest(1, TimeUnit.SECONDS)!!
        assertEquals("/phone/${pairing().room()}", upgrade.path)
        assertEquals("Bearer $key", upgrade.getHeader("Authorization"))
        // the token never reaches the relay in the clear
        assertTrue(upgrade.requestLine.contains(token).not())
        synchronized(heard) { assertTrue(heard.contains("ping")) }
    }

    @Test fun refusesAnotherPcBehindTheRelay() {
        relay.enqueue(bridge())
        relay.start()
        val error = runCatching { LinkClient(pairing("0".repeat(64)), "teste", Route.Relay).run(Quiet()) }.exceptionOrNull()
        assertNotNull("a different certificate must be refused", error)
    }

    @Test fun saysWhenTheKeyIsWrong() {
        relay.enqueue(MockResponse().setResponseCode(401))
        relay.start()
        val error = runCatching { LinkClient(pairing(), "teste", Route.Relay).run(Quiet()) }.exceptionOrNull()
        assertTrue("$error", error is RelayException && error.message!!.contains("Chave do relay"))
    }

    @Test fun withoutAKeyNothingIsSentButTheRoom() {
        relay.enqueue(bridge())
        relay.start()
        val client = LinkClient(pairing().copy(relayKey = null), "teste", Route.Relay)
        val welcomed = CountDownLatch(1)
        var failure: Throwable? = null
        val worker = thread {
            try { client.run(object : Quiet() { override fun onWelcome(desktop: String) = welcomed.countDown() }) } catch (t: Throwable) { failure = t }
        }
        assertTrue("no welcome ($failure)", welcomed.await(10, TimeUnit.SECONDS))
        client.close()
        worker.join(3000)
        val upgrade = relay.takeRequest(1, TimeUnit.SECONDS)!!
        assertEquals("/phone/${pairing().room()}", upgrade.path)
        assertNull(upgrade.getHeader("Authorization"))
    }

    @Test fun saysWhenTheRelayLimitIsHit() {
        relay.enqueue(MockResponse().withWebSocketUpgrade(object : WebSocketListener() {
            override fun onOpen(webSocket: WebSocket, response: Response) { webSocket.close(4429, "daily limit") }
        }))
        relay.start()
        val error = runCatching { LinkClient(pairing(), "teste", Route.Relay).run(Quiet()) }.exceptionOrNull()
        assertTrue("$error", error is RelayException && error.message == RelayWire.LIMIT_MESSAGE)
    }

    @Test fun saysWhenThePcIsNotThere() {
        relay.enqueue(MockResponse().withWebSocketUpgrade(object : WebSocketListener() {
            override fun onOpen(webSocket: WebSocket, response: Response) { webSocket.close(4404, "pc offline") }
        }))
        relay.start()
        val error = runCatching { LinkClient(pairing(), "teste", Route.Relay).run(Quiet()) }.exceptionOrNull()
        assertTrue("$error", error is RelayException && error.message!!.contains("não está no relay"))
    }
}
