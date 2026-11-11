package io.github.theuslpszbr15.dynamicisland

import android.content.ContentValues
import android.content.Context
import android.net.Uri
import android.os.Build
import android.os.Environment
import android.provider.MediaStore
import android.webkit.MimeTypeMap
import java.util.concurrent.Executors

/**
 * Saves an accepted file from the PC in Downloads/Dynamic Island. The file stays hidden (IS_PENDING) while it
 * comes in and is deleted if the size or SHA-256 don't match, so a half or altered file is never left behind.
 * No storage permission: the app only writes its own entries in Downloads.
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
        val values = ContentValues().apply {
            put(MediaStore.MediaColumns.DISPLAY_NAME, offer.name)
            put(MediaStore.MediaColumns.MIME_TYPE, mimeType(offer.name))
            put(MediaStore.MediaColumns.RELATIVE_PATH, "${Environment.DIRECTORY_DOWNLOADS}/$FOLDER")
            put(MediaStore.MediaColumns.IS_PENDING, 1)
        }
        var uri: Uri? = null
        var ok = false
        var error: String? = null
        Notifications.showFileProgress(context, offer, 0)
        try {
            uri = resolver.insert(MediaStore.Downloads.EXTERNAL_CONTENT_URI, values) ?: throw IllegalStateException("Sem espaço em Downloads")
            var lastShown = 0L
            resolver.openOutputStream(uri, "w")!!.use { out ->
                LinkClient(pairing, "${Build.MANUFACTURER} ${Build.MODEL}".take(40), route).fetch(offer, out) { done ->
                    // a notification update every ~5%, not one per chunk
                    if (offer.size > 0 && (done - lastShown) * 20 >= offer.size) {
                        lastShown = done
                        Notifications.showFileProgress(context, offer, (done * 100 / offer.size).toInt())
                    }
                }
            }
            resolver.update(uri, ContentValues().apply { put(MediaStore.MediaColumns.IS_PENDING, 0) }, null, null)
            ok = true
        } catch (e: FileRefusedException) {
            error = e.message
        } catch (e: Exception) {
            error = e.message ?: "Não deu para receber o arquivo."
        } finally {
            if (!ok) uri?.let { try { resolver.delete(it, null, null) } catch (_: Exception) {} }
        }
        if (ok) Notifications.showFileSaved(context, offer, uri!!, mimeType(offer.name))
        else Notifications.showFileFailed(context, offer, error ?: "Não deu para receber o arquivo.")
        Link.fileDone(offer.id, ok)
    }

    fun mimeType(name: String): String {
        val ext = name.substringAfterLast('.', "").lowercase()
        return MimeTypeMap.getSingleton().getMimeTypeFromExtension(ext) ?: "application/octet-stream"
    }
}
