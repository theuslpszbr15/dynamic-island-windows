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
import java.io.ByteArrayOutputStream
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

    fun receive(context: Context, pairing: Pairing, route: Route, offer: FileOffer) {
        val app = context.applicationContext
        io.execute { run(app, pairing, route, offer) }
    }

    private fun run(context: Context, pairing: Pairing, route: Route, offer: FileOffer) {
        val resolver = context.contentResolver
        val saved = mutableListOf<Pair<Uri, String>>()
        val pending = mutableMapOf<Int, Uri>()
        val texts = mutableMapOf<Int, ByteArrayOutputStream>()
        var error: String? = null
        Notifications.showFileProgress(context, offer.id, "Recebendo do PC", offer.label, 0)
        var lastShown = 0L
        val sink = object : LinkClient.Sink {
            override fun open(index: Int, item: OfferItem): OutputStream {
                if (item.isText) return ByteArrayOutputStream().also { texts[index] = it }
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
                    val text = texts.remove(index)!!.toByteArray().toString(Charsets.UTF_8)
                    copy(context, text.take(FileOffer.MAX_TEXT_CHARS))
                    return
                }
                val uri = pending.remove(index) ?: return
                resolver.update(uri, ContentValues().apply { put(MediaStore.MediaColumns.IS_PENDING, 0) }, null, null)
                saved += uri to mimeType(item.name)
            }

            override fun drop(index: Int, item: OfferItem) {
                texts.remove(index)
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
        }
        val ok = error == null
        when {
            !ok -> Notifications.showFileFailed(context, offer.id, "Não deu para receber ${offer.label}", error!!)
            offer.isText -> Notifications.showTransferDone(context, offer.id, "Texto do PC copiado", "Já está na área de transferência", null, null)
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

    fun mimeType(name: String): String {
        val ext = name.substringAfterLast('.', "").lowercase()
        return MimeTypeMap.getSingleton().getMimeTypeFromExtension(ext) ?: "application/octet-stream"
    }
}
