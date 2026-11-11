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

/**
 * "Compartilhar → Dynamic Island" from any app: files, photos, videos or text go to the PC, which asks first.
 * The shared items are copied (and hashed) into the app's private cache right away, while the share is still
 * allowed to read them; they are sent only after the PC accepts, and the copies are deleted afterwards.
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
    /** The PC answers within 45 s; after that the copies are thrown away. */
    private const val ANSWER_WAIT_MS = 60_000L
    private val io = Executors.newFixedThreadPool(2)
    private val main by lazy { Handler(Looper.getMainLooper()) }

    private class Pending(val id: String, val label: String, val items: List<OfferItem>, val files: List<File>, val folder: File?,
                          val text: String? = null)
    private val pending = ConcurrentHashMap<String, Pending>()

    private fun outbox(context: Context) = File(context.cacheDir, "outbox")

    /** Copies and checks the shared files, then asks the PC. Returns what to tell the person. */
    fun offerFiles(context: Context, uris: List<Uri>): String {
        val client = Link.client ?: return "Sem conexão com o PC agora."
        if (uris.size > FileOffer.MAX_ITEMS) return "No máximo ${FileOffer.MAX_ITEMS} de uma vez."
        val id = FileOffer.newId()
        val folder = File(outbox(context), id).apply { mkdirs() }
        val items = mutableListOf<OfferItem>()
        val files = mutableListOf<File>()
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
                            if (size > limit) throw IllegalStateException("$name é grande demais (${FileOffer.limitText(name)}).")
                            digest.update(buffer, 0, n)
                            out.write(buffer, 0, n)
                        }
                    }
                }
                items += OfferItem(name, size, LinkClient.hex(digest.digest()))
                files += copy
            }
        } catch (e: Exception) {
            folder.deleteRecursively()
            throw e
        }
        val label = if (items.size == 1) items[0].name else "${items.size} arquivos"
        return ask(context, client, Pending(id, label, items, files, folder)) { client.sendOffer(id, items) }
    }

    fun offerText(context: Context, text: String): String {
        val client = Link.client ?: return "Sem conexão com o PC agora."
        if (text.length > FileOffer.MAX_TEXT_CHARS) return "Texto grande demais (até 100 mil caracteres)."
        val id = FileOffer.newId()
        if (text.length <= FileOffer.INLINE_TEXT_CHARS)
            return ask(context, client, Pending(id, "Texto", emptyList(), emptyList(), null, text)) { client.sendText(id, text) }
        val folder = File(outbox(context), id).apply { mkdirs() }
        val bytes = text.toByteArray(Charsets.UTF_8)
        if (bytes.size > FileOffer.MAX_TEXT_BYTES) return "Texto grande demais."
        val copy = File(folder, "0").apply { writeBytes(bytes) }
        val item = OfferItem("texto.txt", bytes.size.toLong(), LinkClient.sha256Hex(bytes), isText = true)
        return ask(context, client, Pending(id, "Texto", listOf(item), listOf(copy), folder, text)) { client.sendOffer(id, listOf(item)) }
    }

    private fun ask(context: Context, client: LinkClient, p: Pending, send: () -> Unit): String {
        val app = context.applicationContext
        pending[p.id] = p
        try {
            send()
        } catch (e: Exception) {
            forget(p.id)
            return "Sem conexão com o PC agora."
        }
        Notifications.showFileProgress(app, p.id, "Esperando o PC aceitar", p.label, -1)
        History.add(app, TransferRecord(p.id, System.currentTimeMillis(), sent = true, title = p.label,
            bytes = p.text?.toByteArray(Charsets.UTF_8)?.size?.toLong() ?: p.items.sumOf { it.size },
            count = maxOf(1, p.items.size), status = "Esperando o PC", text = p.text))
        main.postDelayed({
            if (forget(p.id) != null) {
                History.setStatus(app, p.id, true, "Não aceito")
                Notifications.showFileFailed(app, p.id, "O PC não respondeu", "Ninguém aceitou ${p.label} na ilha.")
            }
        }, ANSWER_WAIT_MS)
        return "Pedido enviado: aceite na ilha do PC."
    }

    private fun forget(id: String): Pending? = pending.remove(id)?.also { it.folder?.deleteRecursively() }

    /** sendAccept / sendDecline from the PC. */
    fun answered(context: Context, id: String, accepted: Boolean) {
        val app = context.applicationContext
        val p = pending.remove(id) ?: return
        if (!accepted) {
            History.setStatus(app, id, true, "Recusado")
            p.folder?.deleteRecursively()
            Notifications.showFileFailed(app, id, "O PC recusou", p.label)
            return
        }
        if (p.items.isEmpty()) { // a short text: it went with the offer
            History.setStatus(app, id, true, "No PC")
            Notifications.showTransferDone(app, id, "Texto copiado no PC", "", null, null)
            return
        }
        val client = Link.client
        val pairing = Link.state.value.pairing
        if (client == null || pairing == null) {
            History.setStatus(app, id, true, "Falhou")
            p.folder?.deleteRecursively()
            Notifications.showFileFailed(app, id, "Não deu para mandar ${p.label}", "A conexão com o PC caiu.")
            return
        }
        io.execute { push(app, pairing, client.route, p) }
    }

    private fun push(context: Context, pairing: Pairing, route: Route, p: Pending) {
        val total = p.items.sumOf { it.size }
        var lastShown = 0L
        Notifications.showFileProgress(context, p.id, "Mandando pro PC", p.label, 0)
        History.setStatus(context, p.id, true, "Enviando")
        try {
            val outgoing = p.items.mapIndexed { i, item -> LinkClient.Outgoing(item) { p.files[i].inputStream() } }
            LinkClient(pairing, "${Build.MANUFACTURER} ${Build.MODEL}".take(40), route).push(p.id, outgoing) { done ->
                if (total > 0 && (done - lastShown) * 20 >= total) {
                    lastShown = done
                    Notifications.showFileProgress(context, p.id, "Mandando pro PC", p.label, (done * 100 / total).toInt())
                }
            }
            History.setStatus(context, p.id, true, "No PC")
            Notifications.showTransferDone(context, p.id, if (p.items[0].isText) "Texto copiado no PC" else "Mandado pro PC",
                if (p.items[0].isText) "" else "${p.label} · Downloads\\Dynamic Island", null, null)
        } catch (e: Exception) {
            History.setStatus(context, p.id, true, "Falhou")
            Notifications.showFileFailed(context, p.id, "Não deu para mandar ${p.label}", e.message ?: "A conexão caiu.")
        } finally {
            p.folder?.deleteRecursively()
        }
    }

    /** Copies left behind by a crash. */
    fun cleanUp(context: Context) {
        outbox(context).listFiles()?.forEach { if (it.name !in pending.keys) it.deleteRecursively() }
    }

    private fun displayName(context: Context, uri: Uri): String? = try {
        context.contentResolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME), null, null, null)?.use {
            if (it.moveToFirst()) it.getString(0) else null
        }
    } catch (_: Exception) { null } ?: uri.lastPathSegment
}
