package io.github.theuslpszbr15.dynamicisland

import android.app.Activity
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.provider.OpenableColumns
import android.widget.Toast
import java.io.File
import java.security.MessageDigest
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.Executors
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import org.json.JSONObject

/**
 * "Compartilhar → Dynamic Island" from any app: files, photos, videos or text go to the PC, which asks first.
 * The shared items are copied (and hashed) into the app's private cache right away, while the share is still
 * allowed to read them; they are sent only after the PC accepts, and the copies are deleted afterwards. Not answered
 * right away, the offer waits in Pendentes on both sides for up to a day (and is offered again on every connection).
 */
class ShareActivity : Activity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val intent = intent
        val uris = streams(intent)
        val text = intent.getCharSequenceExtra(Intent.EXTRA_TEXT)?.toString()
        Thread {
            val message = try {
                when {
                    uris.isNotEmpty() -> FileSender.offerFiles(this, uris)
                    !text.isNullOrBlank() -> FileSender.offerText(this, text)
                    else -> "Nada para mandar."
                }
            } catch (e: Exception) {
                e.message ?: "Não deu para mandar."
            }
            runOnUiThread {
                Toast.makeText(applicationContext, message, Toast.LENGTH_LONG).show()
                finish()
            }
        }.start()
    }

    @Suppress("DEPRECATION")
    private fun streams(intent: Intent): List<Uri> = when (intent.action) {
        Intent.ACTION_SEND -> listOfNotNull(
            if (Build.VERSION.SDK_INT >= 33) intent.getParcelableExtra(Intent.EXTRA_STREAM, Uri::class.java)
            else intent.getParcelableExtra(Intent.EXTRA_STREAM) as? Uri)
        Intent.ACTION_SEND_MULTIPLE ->
            (if (Build.VERSION.SDK_INT >= 33) intent.getParcelableArrayListExtra(Intent.EXTRA_STREAM, Uri::class.java)
            else intent.getParcelableArrayListExtra(Intent.EXTRA_STREAM)) ?: emptyList()
        else -> emptyList()
    }
}

object FileSender {
    /** The island shows an offer for 45 s and then keeps it in Pendentes; a PC that never says so (older ones)
     * gets the same treatment here a little later. */
    private const val ANSWER_WAIT_MS = 60_000L
    /** An accepted batch that couldn't be pushed is offered again on the next connections, a few times. */
    private const val MAX_PUSH_TRIES = 3
    private val io = Executors.newFixedThreadPool(2)
    private val main by lazy { Handler(Looper.getMainLooper()) }

    private class Pending(val offer: OutgoingOffer, val folder: File) {
        val files get() = offer.items.indices.map { File(folder, it.toString()) }
    }
    private val pending = ConcurrentHashMap<String, Pending>()
    private val tries = ConcurrentHashMap<String, Int>()
    @Volatile private var loaded = false

    private val _waiting = MutableStateFlow<List<OutgoingOffer>>(emptyList())
    /** What the phone offered and the PC hasn't taken yet, newest first (the "Pendentes" list). */
    val waiting: StateFlow<List<OutgoingOffer>> = _waiting

    private fun outbox(context: Context) = File(context.applicationContext.cacheDir, "outbox")

    /** Copies and checks the shared files, then asks the PC (or keeps it for when it connects). Returns what to tell the person. */
    fun offerFiles(context: Context, uris: List<Uri>): String {
        if (PairingStore.load(context) == null) return "Pareie com o PC primeiro."
        if (uris.size > FileOffer.MAX_ITEMS) return "No máximo ${FileOffer.MAX_ITEMS} de uma vez."
        load(context)
        if (pending.size >= MAX_WAITING) return "Já tem $MAX_WAITING envios esperando o PC. Cancele algum em Pendentes."
        val id = FileOffer.newId()
        val folder = File(outbox(context), id).apply { mkdirs() }
        val items = mutableListOf<OfferItem>()
        var total = 0L
        // Offers now wait in Pendentes even with no connection: check the 10 MB relay cap against the
        // current route when there is one (the PC checks again when the bytes actually go).
        val route = Link.client?.route ?: Route.Lan
        try {
            uris.forEachIndexed { i, uri ->
                val name = FileOffer.safeName(displayName(context, uri) ?: "arquivo")
                val limit = FileOffer.maxBytesFor(name)
                val copy = File(folder, i.toString())
                val digest = MessageDigest.getInstance("SHA-256")
                var size = 0L
                val input = context.contentResolver.openInputStream(uri) ?: throw IllegalStateException("Não deu para ler $name")
                input.use { source ->
                    copy.outputStream().use { out ->
                        val buffer = ByteArray(64 * 1024)
                        while (true) {
                            val n = source.read(buffer)
                            if (n == -1) break
                            size += n
                            total += n
                            if (size > limit) throw IllegalStateException("$name é grande demais (${FileOffer.limitText(name)}).")
                            if (!FileOffer.fitsRoute(total, route)) throw IllegalStateException(FileOffer.RELAY_TOO_BIG)
                            digest.update(buffer, 0, n)
                            out.write(buffer, 0, n)
                        }
                    }
                }
                items += OfferItem(name, size, LinkClient.hex(digest.digest()))
            }
        } catch (e: Exception) {
            folder.deleteRecursively()
            throw e
        }
        val label = if (items.size == 1) items[0].name else "${items.size} arquivos"
        return ask(context, Pending(OutgoingOffer(id, label, items, null, System.currentTimeMillis()), folder))
    }

    fun offerText(context: Context, text: String): String {
        if (PairingStore.load(context) == null) return "Pareie com o PC primeiro."
        if (text.length > FileOffer.MAX_TEXT_CHARS) return "Texto grande demais (até 100 mil caracteres)."
        load(context)
        if (pending.size >= MAX_WAITING) return "Já tem $MAX_WAITING envios esperando o PC. Cancele algum em Pendentes."
        val id = FileOffer.newId()
        val now = System.currentTimeMillis()
        val folder = File(outbox(context), id).apply { mkdirs() }
        if (text.length <= FileOffer.INLINE_TEXT_CHARS)
            return ask(context, Pending(OutgoingOffer(id, "Texto", emptyList(), text, now), folder))
        val bytes = text.toByteArray(Charsets.UTF_8)
        if (bytes.size > FileOffer.MAX_TEXT_BYTES) { folder.deleteRecursively(); return "Texto grande demais." }
        File(folder, "0").writeBytes(bytes)
        val item = OfferItem("texto.txt", bytes.size.toLong(), LinkClient.sha256Hex(bytes), isText = true)
        return ask(context, Pending(OutgoingOffer(id, "Texto", listOf(item), text, now), folder))
    }

    private fun ask(context: Context, p: Pending): String {
        val app = context.applicationContext
        val client = Link.client
        val sent = client != null && Link.state.value.status == LinkStatus.Connected && try {
            offer(client, p.offer, again = false)
            true
        } catch (_: Exception) { false }
        val first = p.offer.copy(state = if (sent) OutgoingOffer.ASKING else OutgoingOffer.QUEUED)
        put(Pending(first, p.folder))
        History.add(app, TransferRecord(first.id, first.at, sent = true, title = first.label, bytes = first.bytes,
            count = maxOf(1, first.items.size), status = if (sent) "Esperando o PC" else "Na fila", text = first.text))
        if (!sent) return "Sem conexão agora: ficou nos Pendentes e vai quando o PC ligar."
        Notifications.showFileProgress(app, first.id, "Esperando o PC aceitar", first.label, -1)
        main.postDelayed({ movedToPending(app, first.id) }, ANSWER_WAIT_MS)
        return "Pedido enviado: aceite na ilha do PC."
    }

    private fun offer(client: LinkClient, o: OutgoingOffer, again: Boolean) {
        if (o.items.isEmpty()) client.sendText(o.id, o.text ?: "", again) else client.sendOffer(o.id, o.items, again)
    }

    /** Keeps it in memory and on disk (meta.json, written whole or not at all). */
    private fun put(p: Pending) {
        pending[p.offer.id] = p
        try {
            val temp = File(p.folder, "meta.tmp")
            temp.writeText(p.offer.toJson().toString())
            if (!temp.renameTo(File(p.folder, "meta.json"))) { File(p.folder, "meta.json").delete(); temp.renameTo(File(p.folder, "meta.json")) }
        } catch (_: Exception) {}
        publish()
    }

    private fun forget(id: String): Pending? = pending.remove(id)?.also {
        it.folder.deleteRecursively()
        tries.remove(id)
        publish()
    }

    private fun publish() {
        _waiting.value = pending.values.map { it.offer }.sortedByDescending { it.at }
    }

    /** sendPending from the PC (or nothing heard for a minute): it waits in the PC's Pendentes, for up to a day. */
    fun movedToPending(context: Context, id: String) {
        val p = pending[id] ?: return
        if (p.offer.state == OutgoingOffer.PENDING) return
        put(Pending(p.offer.copy(state = OutgoingOffer.PENDING), p.folder))
        History.setStatus(context.applicationContext, id, true, "Pendente no PC")
        Notifications.cancelFile(context.applicationContext, id)
    }

    /** On every connection: what is still waiting is offered again ("pending": the PC may know it already). */
    fun announce(client: LinkClient) {
        pending.values.sortedBy { it.offer.at }.forEach { p ->
            try { offer(client, p.offer, again = true) } catch (_: Exception) { return }
            if (p.offer.state == OutgoingOffer.QUEUED) put(Pending(p.offer.copy(state = OutgoingOffer.PENDING), p.folder))
        }
    }

    /** "Cancelar" in Pendentes: the copies go, and the PC stops showing it. */
    fun cancel(context: Context, id: String) {
        val app = context.applicationContext
        forget(id) ?: return
        History.setStatus(app, id, true, "Cancelado")
        Notifications.cancelFile(app, id)
        val client = Link.client ?: return
        io.execute { try { client.sendCancel(id) } catch (_: Exception) {} }
    }

    /** A day later what nobody took goes (called every second; cheap when nothing waits). */
    fun pruneExpired(context: Context) {
        if (pending.isEmpty()) return
        val now = System.currentTimeMillis()
        pending.values.filter { it.offer.expired(now) }.forEach {
            forget(it.offer.id)
            History.setStatus(context.applicationContext, it.offer.id, true, "Expirou")
        }
    }

    /** sendAccept / sendDecline from the PC. */
    fun answered(context: Context, id: String, accepted: Boolean) {
        val app = context.applicationContext
        val p = pending[id] ?: return
        if (!accepted) {
            forget(id)
            History.setStatus(app, id, true, "Recusado")
            Notifications.showFileFailed(app, id, "O PC recusou", p.offer.label)
            return
        }
        if (p.offer.items.isEmpty()) { // a short text: it went with the offer
            forget(id)
            History.setStatus(app, id, true, "No PC")
            Notifications.showTransferDone(app, id, "Texto copiado no PC", "", null, null)
            return
        }
        val client = Link.client
        val pairing = Link.state.value.pairing
        // the PC remembers it accepted: the next connection offers it again and hears "accepted" once more
        if (client == null || pairing == null) return
        pending.remove(id) ?: return // not twice at once
        publish()
        io.execute { push(app, pairing, client.route, p) }
    }

    private fun push(context: Context, pairing: Pairing, route: Route, p: Pending) {
        val o = p.offer
        val total = o.bytes
        var lastShown = 0L
        Notifications.showFileProgress(context, o.id, "Mandando pro PC", o.label, 0)
        History.setStatus(context, o.id, true, "Enviando")
        var done = false
        try {
            val files = p.files
            val outgoing = o.items.mapIndexed { i, item -> LinkClient.Outgoing(item) { files[i].inputStream() } }
            LinkClient(pairing, "${Build.MANUFACTURER} ${Build.MODEL}".take(40), route).push(o.id, outgoing) { sentBytes ->
                if (total > 0 && (sentBytes - lastShown) * 20 >= total) {
                    lastShown = sentBytes
                    Notifications.showFileProgress(context, o.id, "Mandando pro PC", o.label, (sentBytes * 100 / total).toInt())
                }
            }
            done = true
            History.setStatus(context, o.id, true, "No PC")
            Notifications.showTransferDone(context, o.id, if (o.items[0].isText) "Texto copiado no PC" else "Mandado pro PC",
                if (o.items[0].isText) "" else "${o.label} · Downloads\\Dynamic Island", null, null)
        } catch (e: Exception) {
            val tried = (tries[o.id] ?: 0) + 1
            if (e !is FileRefusedException && tried < MAX_PUSH_TRIES && !o.expired(System.currentTimeMillis())) {
                // kept: the next connection offers it again and the PC (which accepted) asks for it once more
                tries[o.id] = tried
                pending[o.id] = p
                publish()
                History.setStatus(context, o.id, true, "Vai de novo quando ligar")
                Notifications.showFileFailed(context, o.id, "Não deu para mandar ${o.label}", "Tenta de novo quando o PC ligar.")
                return
            }
            History.setStatus(context, o.id, true, "Falhou")
            Notifications.showFileFailed(context, o.id, "Não deu para mandar ${o.label}", e.message ?: "A conexão caiu.")
        } finally {
            if (pending[o.id] !== p) {
                p.folder.deleteRecursively()
                tries.remove(o.id)
            }
            if (done) publish()
        }
    }

    /** Reads back what was waiting when the app closed; copies with no (valid) meta, or a day old, are thrown away. */
    @Synchronized
    fun load(context: Context) {
        if (loaded) return
        loaded = true
        val now = System.currentTimeMillis()
        outbox(context).listFiles()?.forEach { folder ->
            if (folder.name in pending.keys) return@forEach
            val offer = try {
                OutgoingOffer.parse(JSONObject(File(folder, "meta.json").readText()))
            } catch (_: Exception) { null }
            val whole = offer != null && offer.id == folder.name && !offer.expired(now) &&
                offer.items.indices.all { i -> File(folder, i.toString()).length() == offer.items[i].size }
            if (whole) pending[offer!!.id] = Pending(offer, folder)
            else {
                folder.deleteRecursively()
                if (offer != null) History.setStatus(context.applicationContext, offer.id, true, "Expirou")
            }
        }
        publish()
    }

    /** At most this many things wait for the PC at once (the PC takes 20). */
    const val MAX_WAITING = 20

    private fun displayName(context: Context, uri: Uri): String? = try {
        context.contentResolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME), null, null, null)?.use {
            if (it.moveToFirst()) it.getString(0) else null
        }
    } catch (_: Exception) { null } ?: uri.lastPathSegment
}
