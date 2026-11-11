package io.github.theuslpszbr15.dynamicisland

import org.json.JSONObject
import java.io.ByteArrayOutputStream
import java.io.Closeable
import java.io.EOFException
import java.io.IOException
import java.io.InputStream
import java.io.OutputStream
import java.net.InetSocketAddress
import java.net.Socket
import java.security.MessageDigest
import java.security.SecureRandom
import java.security.cert.CertificateException
import java.security.cert.X509Certificate
import javax.net.ssl.SSLContext
import javax.net.ssl.SSLSocket
import javax.net.ssl.X509TrustManager

data class Session(
    val id: String,
    val agent: String,
    val project: String,
    val state: String,
    val detail: String,
    val updatedAt: Long,
)

data class Approval(
    val fingerprint: String,
    val agent: String,
    val project: String,
    val command: String,
    val truncated: Boolean,
    /** Phone clock: the PC gives up after ~54 s, so the phone counts from when it heard. */
    val receivedAt: Long,
)

class AuthException(message: String) : IOException(message)

/** Something pinned in the PC's file tray. [icon] is a small PNG; the path never leaves the PC. */
data class TrayItem(val id: String, val name: String, val kind: String, val icon: ByteArray?) {
    override fun equals(other: Any?) = other is TrayItem && other.id == id && other.name == name
    override fun hashCode() = id.hashCode()
}

/** How a connection reaches the PC: straight over the local network, or through the relay. */
enum class Route { Lan, Relay }

/**
 * One TLS connection to the PC (plain JVM code, so it can be tested off the phone). The PC's certificate is
 * accepted only if its SHA-256 is the one from the pairing QR code; then newline-delimited JSON. Through the
 * relay the TLS is the same, end to end: the relay only carries its bytes.
 */
class LinkClient(
    private val pairing: Pairing,
    private val device: String,
    val route: Route = Route.Lan,
    private val connectTimeoutMs: Int = CONNECT_TIMEOUT_MS,
) : Closeable {

    interface Listener {
        fun onWelcome(desktop: String)
        fun onSessions(sessions: List<Session>)
        fun onApproval(approval: Approval)
        fun onResolved(fingerprint: String)
        fun onTray(items: List<TrayItem>) {}
        fun onPong() {}
        /** The PC said which relay to use (null: none); only ever heard inside the pinned TLS. */
        fun onRelay(relay: String?, relayKey: String?) {}
    }

    private val writeLock = Any()
    @Volatile private var pipe: Pipe? = null
    @Volatile private var output: OutputStream? = null
    @Volatile private var closed = false

    /** Connects and reads until the link drops (throws) or [close] is called (returns). */
    fun run(listener: Listener) {
        val context = SSLContext.getInstance("TLS")
        context.init(null, arrayOf(PinningTrustManager(pairing.fingerprint)), SecureRandom())
        val link = try {
            if (route == Route.Lan) openLan(context) else openRelay(context)
        } catch (e: Exception) {
            if (closed) return else throw e
        }
        output = link.output
        send(JSONObject().put("type", "hello").put("v", 1).put("token", pairing.token).put("device", device))

        val input = link.input.buffered()
        while (!closed) {
            val line = readLine(input) ?: if (closed) return else throw EOFException("O PC fechou a conexão")
            val o = JSONObject(line)
            when (o.optString("type")) {
                "welcome" -> {
                    if (o.optInt("v") != 1) throw IOException("Versão diferente no PC; atualize os dois.")
                    // the PC answers every ping, so silence this long means the link is gone
                    link.setReadTimeout(READ_TIMEOUT_MS)
                    if (o.has("relay")) {
                        val relay = Pairing.relayOrigin(o.optString("relay"))
                        listener.onRelay(relay, if (relay == null) null else Pairing.relayKeyOrNull(o.optString("relayKey")))
                    }
                    listener.onWelcome(o.optString("desktop", pairing.name))
                }
                "error" -> if (o.optString("code") == "auth") throw AuthException(o.optString("message"))
                "sessions" -> {
                    val list = o.optJSONArray("sessions")
                    listener.onSessions((0 until (list?.length() ?: 0)).map { i ->
                        val s = list!!.getJSONObject(i)
                        Session(s.optString("id"), s.optString("agent"), s.optString("project"), s.optString("state"),
                            s.optString("detail"), s.optLong("updatedAt"))
                    })
                }
                "approval" -> listener.onApproval(Approval(
                    o.optString("fingerprint"), o.optString("agent"), o.optString("project"),
                    o.optString("command").take(MAX_COMMAND), o.optBoolean("truncated"), System.currentTimeMillis(),
                ))
                "approvalResolved" -> listener.onResolved(o.optString("fingerprint"))
                "tray" -> {
                    val list = o.optJSONArray("items")
                    listener.onTray((0 until (list?.length() ?: 0)).map { i ->
                        val t = list!!.getJSONObject(i)
                        val icon = t.optString("icon").takeIf { it.isNotEmpty() }?.let {
                            try { java.util.Base64.getDecoder().decode(it) } catch (_: IllegalArgumentException) { null }
                        }
                        TrayItem(t.optString("id"), t.optString("name").take(80), t.optString("kind", "file"), icon)
                    })
                }
                "pong" -> listener.onPong()
            }
        }
    }

    private fun openLan(context: SSLContext): Pipe {
        val raw = Socket()
        raw.connect(InetSocketAddress(pairing.host, pairing.port), connectTimeoutMs)
        val ssl = context.socketFactory.createSocket(raw, pairing.host, pairing.port, true) as SSLSocket
        return started(SocketPipe(ssl)) { ssl.startHandshake() }
    }

    private fun openRelay(context: SSLContext): Pipe {
        val relay = pairing.relay ?: throw IOException("Sem relay configurado")
        val wire = RelayWire.open(relay, pairing.room(), pairing.relayKey, RELAY_TIMEOUT_MS)
        val engine = context.createSSLEngine(pairing.host, pairing.port).apply { useClientMode = true }
        val tls = TlsPipe(engine, wire)
        return started(tls) { tls.handshake() }
    }

    /** Publishes [link] so [close] can reach it, then shakes hands (bounded by the hello timeout). */
    private fun started(link: Pipe, handshake: () -> Unit): Pipe {
        pipe = link
        if (closed) { link.close(); throw IOException("Fechado") }
        link.setReadTimeout(HELLO_TIMEOUT_MS)
        try {
            handshake()
        } catch (e: Exception) {
            try { link.close() } catch (_: Exception) {}
            throw e
        }
        return link
    }

    fun decide(fingerprint: String, allow: Boolean) =
        send(JSONObject().put("type", "decision").put("fingerprint", fingerprint).put("decision", if (allow) "allow" else "deny"))

    fun ping() = send(JSONObject().put("type", "ping"))

    /** Opens a tray item on the PC (only ids the PC sent; it looks the path up itself). */
    fun open(id: String) = send(JSONObject().put("type", "open").put("id", id))

    private fun send(message: JSONObject) {
        synchronized(writeLock) {
            val out = output ?: throw IOException("Sem conexão")
            out.write((message.toString() + "\n").toByteArray(Charsets.UTF_8))
            out.flush()
        }
    }

    override fun close() {
        closed = true
        try { send(JSONObject().put("type", "bye")) } catch (_: Exception) {}
        try { pipe?.close() } catch (_: Exception) {}
    }

    private fun readLine(input: InputStream): String? {
        val line = ByteArrayOutputStream()
        while (true) {
            val b = input.read()
            if (b == -1) return null
            if (b == '\n'.code) return line.toString(Charsets.UTF_8.name()).trimEnd('\r')
            line.write(b)
            if (line.size() > MAX_LINE) throw IOException("Mensagem grande demais")
        }
    }

    companion object {
        const val CONNECT_TIMEOUT_MS = 5_000
        /** On Wi-Fi with a relay to fall back on, the LAN gets less time before the phone moves on. */
        const val LAN_FIRST_TIMEOUT_MS = 2_000
        const val RELAY_TIMEOUT_MS = 10_000
        const val HELLO_TIMEOUT_MS = 10_000
        const val READ_TIMEOUT_MS = 70_000
        const val PING_EVERY_MS = 20_000L
        const val MAX_LINE = 64 * 1024
        const val MAX_COMMAND = 500

        fun sha256Hex(bytes: ByteArray): String =
            MessageDigest.getInstance("SHA-256").digest(bytes).joinToString("") { "%02x".format(it) }
    }
}

/** Trusts exactly one certificate: the PC's, pinned at pairing. No CA, no host name. */
class PinningTrustManager(private val expected: String) : X509TrustManager {
    override fun checkServerTrusted(chain: Array<out X509Certificate>?, authType: String?) {
        val leaf = chain?.firstOrNull() ?: throw CertificateException("Sem certificado")
        val actual = LinkClient.sha256Hex(leaf.encoded)
        if (!MessageDigest.isEqual(actual.toByteArray(), expected.toByteArray()))
            throw CertificateException("Este não é o PC pareado (certificado diferente)")
    }

    override fun checkClientTrusted(chain: Array<out X509Certificate>?, authType: String?) =
        throw CertificateException("Não usado")

    override fun getAcceptedIssuers(): Array<X509Certificate> = emptyArray()
}
