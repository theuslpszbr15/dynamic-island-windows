package io.github.theuslpszbr15.dynamicisland

import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import okio.ByteString
import okio.ByteString.Companion.toByteString
import java.io.Closeable
import java.io.EOFException
import java.io.IOException
import java.io.InputStream
import java.io.InterruptedIOException
import java.io.OutputStream
import java.net.SocketTimeoutException
import java.net.UnknownHostException
import java.nio.ByteBuffer
import java.util.concurrent.CountDownLatch
import java.util.concurrent.LinkedBlockingQueue
import java.util.concurrent.TimeUnit
import javax.net.ssl.SSLEngine
import javax.net.ssl.SSLEngineResult
import javax.net.ssl.SSLSocket
import kotlin.math.min

/** A byte stream to the PC with a read timeout: what the link's newline-delimited JSON travels on. */
interface Pipe : Closeable {
    val input: InputStream
    val output: OutputStream
    fun setReadTimeout(ms: Int)
}

/** The LAN case: the pinned TLS socket straight to the PC. */
class SocketPipe(private val socket: SSLSocket) : Pipe {
    override val input: InputStream get() = socket.inputStream
    override val output: OutputStream get() = socket.outputStream
    override fun setReadTimeout(ms: Int) { socket.soTimeout = ms }
    override fun close() = socket.close()
}

/** The relay said why it would not carry the link (shown on the screen as is). */
class RelayException(message: String) : IOException(message)

/**
 * The phone's end of the relay: one WebSocket to `<relay>/phone/<room>` whose binary messages are the raw
 * bytes of the TLS connection to the PC, in order and with no meaning to where one message ends. The relay
 * never sees the token (only the room, a hash of it) and never sees anything but TLS records.
 */
class RelayWire private constructor() : WebSocketListener(), Pipe {
    private val chunks = LinkedBlockingQueue<ByteArray>()
    private val opened = CountDownLatch(1)
    @Volatile private var socket: WebSocket? = null
    @Volatile private var failure: IOException? = null
    @Volatile private var ended = false
    @Volatile private var readTimeoutMs = 0

    override fun setReadTimeout(ms: Int) { readTimeoutMs = ms }

    override val input: InputStream = object : InputStream() {
        private var current = ByteArray(0)
        private var position = 0

        override fun read(): Int {
            val one = ByteArray(1)
            return if (read(one, 0, 1) == -1) -1 else one[0].toInt() and 0xff
        }

        override fun read(b: ByteArray, off: Int, len: Int): Int {
            if (len == 0) return 0
            if (position >= current.size) {
                val next = try {
                    if (readTimeoutMs > 0) chunks.poll(readTimeoutMs.toLong(), TimeUnit.MILLISECONDS) else chunks.take()
                } catch (_: InterruptedException) {
                    throw InterruptedIOException()
                } ?: throw SocketTimeoutException("O PC não respondeu pelo relay")
                if (next === END) {
                    chunks.offer(END) // every later read ends too
                    failure?.let { throw it }
                    return -1
                }
                current = next
                position = 0
            }
            val n = min(len, current.size - position)
            System.arraycopy(current, position, b, off, n)
            position += n
            return n
        }

        override fun available() = current.size - position
    }

    override val output: OutputStream = object : OutputStream() {
        override fun write(b: Int) = write(byteArrayOf(b.toByte()), 0, 1)

        override fun write(b: ByteArray, off: Int, len: Int) {
            var at = off
            while (at < off + len) {
                val n = min(FRAME, off + len - at)
                val ws = socket
                if (ended || ws == null || !ws.send(b.toByteString(at, n)))
                    throw failure ?: IOException("O relay fechou a conexão")
                at += n
            }
        }
    }

    override fun onOpen(webSocket: WebSocket, response: Response) = opened.countDown()

    override fun onMessage(webSocket: WebSocket, bytes: ByteString) {
        if (bytes.size > 0) chunks.offer(bytes.toByteArray())
    }

    // only binary belongs in the tunnel; text would mean something is wrong with the relay
    override fun onMessage(webSocket: WebSocket, text: String) {
        end(RelayException("O relay mandou algo inesperado"))
        webSocket.cancel()
    }

    override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
        end(closeError(code))
        webSocket.close(1000, null)
    }

    override fun onClosed(webSocket: WebSocket, code: Int, reason: String) = end(closeError(code))

    override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) = end(when {
        response?.code == 401 -> RelayException("Chave do relay inválida: conecte uma vez na rede de casa para atualizar")
        response != null -> RelayException("O relay recusou a conexão (HTTP ${response.code})")
        t is UnknownHostException -> RelayException("Sem internet para chegar ao relay")
        t is IOException -> t
        else -> IOException(t.message, t)
    })

    private fun end(error: IOException?) {
        if (ended) return
        failure = error
        ended = true
        chunks.offer(END)
        opened.countDown()
    }

    override fun close() {
        if (!ended) socket?.close(1000, null)
        end(null)
    }

    companion object {
        /** Bytes per WebSocket message; the relay takes up to 64 KB. */
        const val FRAME = 16 * 1024
        private val END = ByteArray(0)

        // OkHttp checks the relay's certificate against the system CAs; the PC's own pin is checked inside.
        private val http by lazy {
            OkHttpClient.Builder()
                .connectTimeout(10, TimeUnit.SECONDS)
                .pingInterval(30, TimeUnit.SECONDS)
                .build()
        }

        /** The relay's close codes, in words for the screen; null for a normal close. */
        fun closeError(code: Int): IOException? = when (code) {
            1000, 1001 -> null
            4404 -> RelayException("O PC não está no relay: confira se a ilha está aberta e com internet")
            4429 -> RelayException("Aparelhos demais ligados a esse PC pelo relay")
            4400 -> RelayException("O relay recusou a conexão")
            else -> IOException("O relay fechou a conexão ($code)")
        }

        /** Opens the tunnel; throws if the relay can't be reached in [timeoutMs] or refuses it. */
        fun open(relay: String, room: String, key: String?, timeoutMs: Int, client: OkHttpClient = http): RelayWire {
            val wire = RelayWire()
            val request = Request.Builder().url("$relay/phone/$room")
                .apply { if (key != null) header("Authorization", "Bearer $key") }
                .build()
            wire.socket = client.newWebSocket(request, wire)
            val inTime = try {
                wire.opened.await(timeoutMs.toLong(), TimeUnit.MILLISECONDS)
            } catch (_: InterruptedException) {
                wire.socket?.cancel()
                throw InterruptedIOException()
            }
            if (!inTime) {
                wire.socket?.cancel()
                throw SocketTimeoutException("O relay não respondeu")
            }
            if (wire.ended) throw wire.failure ?: EOFException("O relay fechou a conexão")
            return wire
        }
    }
}

/**
 * TLS over any byte [Pipe] (the relay's WebSocket), with an [SSLEngine] set up exactly like the LAN socket:
 * same pinning trust manager, so the relay can't read or change anything it carries.
 * One thread reads; any thread may write.
 */
class TlsPipe(private val engine: SSLEngine, private val wire: Pipe) : Pipe {
    private val readLock = Any()
    private val writeLock = Any()
    private val raw = wire.input
    private val rawOut = wire.output
    private val chunk = ByteArray(engine.session.packetBufferSize)
    /** Network bytes not yet unwrapped (kept ready to read). */
    private var netIn: ByteBuffer = ByteBuffer.allocate(engine.session.packetBufferSize).apply { flip() }
    /** Plain bytes not yet handed out (kept ready to read). */
    private var appIn: ByteBuffer = ByteBuffer.allocate(engine.session.applicationBufferSize).apply { flip() }
    private var netOut: ByteBuffer = ByteBuffer.allocate(engine.session.packetBufferSize)

    override fun setReadTimeout(ms: Int) = wire.setReadTimeout(ms)

    /** Runs the whole handshake; the trust manager throws here if the certificate isn't the paired PC's. */
    fun handshake() {
        engine.beginHandshake()
        synchronized(readLock) {
            while (true) {
                when (engine.handshakeStatus) {
                    SSLEngineResult.HandshakeStatus.NEED_WRAP -> wrap(EMPTY)
                    SSLEngineResult.HandshakeStatus.NEED_UNWRAP ->
                        if (!unwrap()) throw EOFException("A conexão caiu durante o TLS")
                    SSLEngineResult.HandshakeStatus.NEED_TASK -> runTasks()
                    else -> return
                }
            }
        }
    }

    override val input: InputStream = object : InputStream() {
        override fun read(): Int {
            val one = ByteArray(1)
            return if (read(one, 0, 1) == -1) -1 else one[0].toInt() and 0xff
        }

        override fun read(b: ByteArray, off: Int, len: Int): Int {
            if (len == 0) return 0
            synchronized(readLock) {
                while (!appIn.hasRemaining()) {
                    if (!unwrap()) return -1
                    // after the handshake TLS 1.3 may still need to answer (key update) or run a task
                    while (true) when (engine.handshakeStatus) {
                        SSLEngineResult.HandshakeStatus.NEED_WRAP -> wrap(EMPTY)
                        SSLEngineResult.HandshakeStatus.NEED_TASK -> runTasks()
                        else -> break
                    }
                }
                val n = min(len, appIn.remaining())
                appIn.get(b, off, n)
                return n
            }
        }
    }

    override val output: OutputStream = object : OutputStream() {
        override fun write(b: Int) = write(byteArrayOf(b.toByte()), 0, 1)
        override fun write(b: ByteArray, off: Int, len: Int) = wrap(ByteBuffer.wrap(b, off, len))
        override fun flush() = rawOut.flush()
    }

    /** Unwraps one record; false when the PC or the relay closed the stream. */
    private fun unwrap(): Boolean {
        while (true) {
            appIn.compact()
            val result = try { engine.unwrap(netIn, appIn) } finally { appIn.flip() }
            // some engines answer "OK, nothing done" instead of underflow: that also means "read more"
            val idle = result.status == SSLEngineResult.Status.OK && result.bytesConsumed() == 0 &&
                result.bytesProduced() == 0 && engine.handshakeStatus.let {
                    it != SSLEngineResult.HandshakeStatus.NEED_WRAP && it != SSLEngineResult.HandshakeStatus.NEED_TASK
                }
            when (if (idle) SSLEngineResult.Status.BUFFER_UNDERFLOW else result.status) {
                SSLEngineResult.Status.OK -> return true
                SSLEngineResult.Status.CLOSED -> return false
                SSLEngineResult.Status.BUFFER_OVERFLOW ->
                    appIn = grow(appIn, engine.session.applicationBufferSize)
                SSLEngineResult.Status.BUFFER_UNDERFLOW -> {
                    if (netIn.limit() == netIn.capacity() && netIn.position() == 0)
                        netIn = grow(netIn, engine.session.packetBufferSize)
                    val n = raw.read(chunk)
                    if (n == -1) return false
                    netIn.compact()
                    if (netIn.remaining() < n) { netIn.flip(); netIn = grow(netIn, n); netIn.compact() }
                    netIn.put(chunk, 0, n)
                    netIn.flip()
                }
                else -> return false
            }
        }
    }

    private fun wrap(source: ByteBuffer) {
        synchronized(writeLock) {
            do {
                netOut.clear()
                val result = engine.wrap(source, netOut)
                when (result.status) {
                    SSLEngineResult.Status.BUFFER_OVERFLOW -> {
                        netOut = ByteBuffer.allocate(netOut.capacity() * 2)
                        continue
                    }
                    SSLEngineResult.Status.CLOSED -> if (result.bytesProduced() == 0) throw IOException("TLS fechado")
                    else -> {}
                }
                netOut.flip()
                if (netOut.hasRemaining()) rawOut.write(netOut.array(), netOut.arrayOffset(), netOut.remaining())
            } while (source.hasRemaining())
            rawOut.flush()
        }
    }

    private fun runTasks() {
        while (true) (engine.delegatedTask ?: return).run()
    }

    override fun close() {
        try {
            engine.closeOutbound()
            wrap(EMPTY)
        } catch (_: Exception) {}
        wire.close()
    }

    private companion object {
        val EMPTY: ByteBuffer = ByteBuffer.allocate(0)

        /** A bigger buffer with the same unread bytes, ready to read. */
        fun grow(buffer: ByteBuffer, extra: Int): ByteBuffer =
            ByteBuffer.allocate(buffer.capacity() + extra).put(buffer).apply { flip() }
    }
}
