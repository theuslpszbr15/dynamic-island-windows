package io.github.theuslpszbr15.dynamicisland

import android.content.ClipData
import android.content.ClipboardManager
import android.content.ContentValues
import android.content.Context
import android.net.Uri
import android.os.Build
import android.os.Environment
import android.provider.MediaStore
import android.webkit.MimeTypeMap
import androidx.core.content.FileProvider
import java.io.ByteArrayOutputStream
import java.io.File
import java.io.FileOutputStream
import java.io.OutputStream
import java.util.concurrent.Executors

/**
 * Saves an accepted batch from the PC: photos in Pictures/Dynamic Island, videos in Movies/Dynamic Island (so the
 * gallery shows them), everything else in Downloads/Dynamic Island. Each file stays hidden (IS_PENDING) while it
 * comes in and is deleted if its size or SHA-256 don't match, so a half or altered file is never left behind.
 * A long text goes to the clipboard instead. No storage permission: the app only writes its own entries.
 */
object FileReceiver {
    const val FOLDER = "Dynamic Island"
    private val io = Executors.newFixedThreadPool(2)
    private const val KEEP_COPIES_MS = 7 * 24 * 60 * 60_000L

    /** Default: files to the gallery/Downloads, a long text to the clipboard. Copy: a picture only to the clipboard
     * (kept in the app's cache, not in the gallery). Save: a text as a .txt in Downloads/Dynamic Island. */
    enum class Take { Default, Copy, Save }

    fun receive(context: Context, pairing: Pairing, route: Route, offer: FileOffer, take: Take = Take.Default) {
        val app = context.applicationContext
        io.execute { run(app, pairing, route, offer, take) }
    }

    private fun run(context: Context, pairing: Pairing, route: Route, offer: FileOffer, take: Take) {
        val resolver = context.contentResolver
        val toClipboard = take == Take.Copy && offer.isPicture
        val textToFile = take == Take.Save && offer.isText
        if (toClipboard) dropOldCopies(context)
        val saved = mutableListOf<Pair<Uri, String>>()
        val pending = mutableMapOf<Int, Uri>()
        val cached = mutableMapOf<Int, File>()
        val texts = mutableMapOf<Int, ByteArrayOutputStream>()
        var receivedText: String? = null
        var error: String? = null
        Notifications.showFileProgress(context, offer.id, "Recebendo do PC", offer.label, 0)
        var lastShown = 0L
        val sink = object : LinkClient.Sink {
            override fun open(index: Int, item: OfferItem): OutputStream {
                if (item.isText) return ByteArrayOutputStream().also { texts[index] = it }
                if (toClipboard) {
                    val folder = File(context.cacheDir, "received/${offer.id}").apply { mkdirs() }
                    return FileOutputStream(File(folder, item.name).also { cached[index] = it })
                }
                val mime = mimeType(item.name)
                val (collection, folder) = where(mime)
                val values = ContentValues().apply {
                    put(MediaStore.MediaColumns.DISPLAY_NAME, item.name)
                    put(MediaStore.MediaColumns.MIME_TYPE, mime)
                    put(MediaStore.MediaColumns.RELATIVE_PATH, "$folder/$FOLDER")
                    put(MediaStore.MediaColumns.IS_PENDING, 1)
                }
                val uri = resolver.insert(collection, values) ?: throw IllegalStateException("Sem espaço para salvar")
                pending[index] = uri
                return resolver.openOutputStream(uri, "w") ?: throw IllegalStateException("Não deu para salvar")
            }

            override fun keep(index: Int, item: OfferItem) {
                if (item.isText) {
                    val text = texts.remove(index)!!.toByteArray().toString(Charsets.UTF_8).take(FileOffer.MAX_TEXT_CHARS)
                    receivedText = text
                    if (textToFile) saveText(context, text)?.let { saved += it to "text/plain" } ?: throw IllegalStateException("Não deu para salvar")
                    else copy(context, text)
                    return
                }
                cached.remove(index)?.let { file ->
                    val uri = FileProvider.getUriForFile(context, "${context.packageName}.updates", file)
                    copyPicture(context, uri)
                    saved += uri to mimeType(item.name)
                    return
                }
                val uri = pending.remove(index) ?: return
                resolver.update(uri, ContentValues().apply { put(MediaStore.MediaColumns.IS_PENDING, 0) }, null, null)
                saved += uri to mimeType(item.name)
            }

            override fun drop(index: Int, item: OfferItem) {
                texts.remove(index)
                cached.remove(index)?.delete()
                pending.remove(index)?.let { try { resolver.delete(it, null, null) } catch (_: Exception) {} }
            }

            override fun progress(done: Long) {
                // a notification update every ~5%, not one per chunk
                if (offer.totalBytes > 0 && (done - lastShown) * 20 >= offer.totalBytes) {
                    lastShown = done
                    Notifications.showFileProgress(context, offer.id, "Recebendo do PC", offer.label, (done * 100 / offer.totalBytes).toInt())
                }
            }
        }
        try {
            LinkClient(pairing, "${Build.MANUFACTURER} ${Build.MODEL}".take(40), route).fetch(offer, sink)
        } catch (e: Exception) {
            error = e.message ?: "Não deu para receber."
        } finally {
            pending.values.forEach { try { resolver.delete(it, null, null) } catch (_: Exception) {} }
            cached.values.forEach { it.delete() }
        }
        val ok = error == null
        History.add(context, TransferRecord(offer.id, System.currentTimeMillis(), sent = false,
            title = if (offer.isText) "Texto" else offer.label, bytes = offer.totalBytes, count = offer.items.size,
            status = when {
                !ok && saved.isNotEmpty() -> "Incompleto (${saved.size} de ${offer.items.size})"
                !ok -> "Falhou"
                textToFile -> "Salvo"
                offer.isText || toClipboard -> "Copiado"
                else -> "Recebido"
            },
            uris = saved.map { it.first.toString() }, mimes = saved.map { it.second }, text = receivedText))
        when {
            !ok -> Notifications.showFileFailed(context, offer.id, "Não deu para receber ${offer.label}", error!!)
            textToFile -> Notifications.showTransferDone(context, offer.id, "Texto do PC salvo", "Downloads/$FOLDER", saved[0].first, "text/plain")
            offer.isText -> Notifications.showTransferDone(context, offer.id, "Texto do PC copiado", "Já está na área de transferência", null, null)
            toClipboard -> Notifications.showTransferDone(context, offer.id, "Imagem do PC copiada", "É só colar. Para guardar na galeria, use Baixar no app.", null, null)
            else -> {
                val (uri, mime) = saved.first()
                val where = if (saved.size == 1) offer.items[0].name else "${saved.size} arquivos"
                Notifications.showTransferDone(context, offer.id, "Recebido do PC", "$where · ${placeOf(mime)}", uri, mime)
            }
        }
        Link.fileDone(offer.id, ok)
    }

    /** Photos and videos where the gallery looks; the rest in Downloads. */
    private fun where(mime: String): Pair<Uri, String> = when {
        mime.startsWith("image/") -> MediaStore.Images.Media.EXTERNAL_CONTENT_URI to Environment.DIRECTORY_PICTURES
        mime.startsWith("video/") -> MediaStore.Video.Media.EXTERNAL_CONTENT_URI to Environment.DIRECTORY_MOVIES
        else -> MediaStore.Downloads.EXTERNAL_CONTENT_URI to Environment.DIRECTORY_DOWNLOADS
    }

    private fun placeOf(mime: String) = when {
        mime.startsWith("image/") || mime.startsWith("video/") -> "Galeria"
        else -> "Downloads/$FOLDER"
    }

    fun copy(context: Context, text: String) {
        val clipboard = context.getSystemService(ClipboardManager::class.java) ?: return
        clipboard.setPrimaryClip(ClipData.newPlainText("Dynamic Island", text))
    }

    /** The picture itself on the clipboard (the app that pastes gets to read it). */
    fun copyPicture(context: Context, uri: Uri) {
        val clipboard = context.getSystemService(ClipboardManager::class.java) ?: return
        clipboard.setPrimaryClip(ClipData.newUri(context.contentResolver, "Imagem do PC", uri))
    }

    /** Saved in the gallery or Downloads (not only in the app's cache, for the clipboard). */
    fun inGallery(uri: String) = Uri.parse(uri).authority == MediaStore.AUTHORITY

    /** A copy in the gallery of a picture that was kept only for the clipboard. */
    fun saveCopy(context: Context, source: Uri, name: String, mime: String): Uri? =
        saveNew(context, name, mime) { out -> context.contentResolver.openInputStream(source)!!.use { it.copyTo(out) } }

    /** A text as "texto do PC <data>.txt" in Downloads/Dynamic Island. */
    fun saveText(context: Context, text: String): Uri? {
        val stamp = java.text.SimpleDateFormat("dd-MM-yyyy HH'h'mm", java.util.Locale.ROOT).format(System.currentTimeMillis())
        return saveNew(context, "texto do PC $stamp.txt", "text/plain") { it.write(text.toByteArray(Charsets.UTF_8)) }
    }

    private fun saveNew(context: Context, name: String, mime: String, write: (OutputStream) -> Unit): Uri? {
        val resolver = context.contentResolver
        val (collection, folder) = where(mime)
        val uri = resolver.insert(collection, ContentValues().apply {
            put(MediaStore.MediaColumns.DISPLAY_NAME, name)
            put(MediaStore.MediaColumns.MIME_TYPE, mime)
            put(MediaStore.MediaColumns.RELATIVE_PATH, "$folder/$FOLDER")
            put(MediaStore.MediaColumns.IS_PENDING, 1)
        }) ?: return null
        return try {
            resolver.openOutputStream(uri, "w")!!.use(write)
            resolver.update(uri, ContentValues().apply { put(MediaStore.MediaColumns.IS_PENDING, 0) }, null, null)
            uri
        } catch (_: Exception) {
            try { resolver.delete(uri, null, null) } catch (_: Exception) {}
            null
        }
    }

    /** Pictures copied but never saved stay a week in the cache. */
    private fun dropOldCopies(context: Context) {
        val now = System.currentTimeMillis()
        File(context.cacheDir, "received").listFiles()?.forEach { if (now - it.lastModified() > KEEP_COPIES_MS) it.deleteRecursively() }
    }

    fun mimeType(name: String): String {
        val ext = name.substringAfterLast('.', "").lowercase()
        return MimeTypeMap.getSingleton().getMimeTypeFromExtension(ext) ?: "application/octet-stream"
    }
}
