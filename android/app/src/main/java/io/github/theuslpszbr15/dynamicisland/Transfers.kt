package io.github.theuslpszbr15.dynamicisland

import org.json.JSONArray
import org.json.JSONObject
import java.security.SecureRandom

/** One thing in a batch: a file (name, size, SHA-256) or, with [isText], a long text that ends in the clipboard. */
data class OfferItem(val name: String, val size: Long, val sha256: String, val isText: Boolean = false)

/**
 * Files the PC offers, as one batch with one Aceitar. Only names, sizes and hashes come with the offer; the bytes
 * come on a connection of their own once the person accepts. Names are already safe to save (see [safeName]).
 * The same limits apply the other way (the PC checks them again).
 */
data class FileOffer(val id: String, val items: List<OfferItem>, val receivedAt: Long) {
    val totalBytes get() = items.sumOf { it.size }
    val isText get() = items.size == 1 && items[0].isText
    val label get() = when {
        isText -> "um texto"
        items.size == 1 -> items[0].name
        else -> "${items.size} arquivos"
    }

    companion object {
        /** 30 MB per file, 60 MB per video: a quick way to hand things over, not a sync. */
        const val MAX_BYTES = 30L * 1024 * 1024
        const val MAX_VIDEO_BYTES = 60L * 1024 * 1024
        const val MAX_ITEMS = 20
        /** Texts up to this many characters ride on the link; longer ones (up to [MAX_TEXT_CHARS]) go like a file. */
        const val INLINE_TEXT_CHARS = 8000
        const val MAX_TEXT_CHARS = 100_000
        const val MAX_TEXT_BYTES = 512 * 1024
        /** Offers either way wait at most a day ("Pendentes" on both sides). */
        const val PENDING_MS = 24 * 60 * 60_000L
        /** The PC drops its offers after [PENDING_MS]; the phone stops offering a little before. */
        const val WINDOW_MS = PENDING_MS - 10 * 60_000L
        private val ID = Regex("^[0-9a-f]{32}$")
        private val SHA = Regex("^[0-9a-f]{64}$")
        private val VIDEO = setOf("mp4", "mov", "m4v", "mkv", "webm", "3gp", "avi")
        private val random = SecureRandom()

        fun isId(id: String) = ID.matches(id)
        fun isSha(sha: String) = SHA.matches(sha)

        /** 128 random bits as lowercase hex, like the PC's. */
        fun newId(): String = ByteArray(16).also(random::nextBytes).joinToString("") { "%02x".format(it) }

        fun maxBytesFor(name: String) = if (name.substringAfterLast('.', "").lowercase() in VIDEO) MAX_VIDEO_BYTES else MAX_BYTES

        fun limitText(name: String) = if (maxBytesFor(name) == MAX_VIDEO_BYTES) "vídeo até 60 MB" else "até 30 MB por arquivo"

        fun parse(o: JSONObject, now: Long = System.currentTimeMillis()): FileOffer? {
            val id = o.optString("id")
            val list = o.optJSONArray("items") ?: return null
            if (!isId(id) || list.length() !in 1..MAX_ITEMS) return null
            val items = (0 until list.length()).map { i ->
                val e = list.optJSONObject(i) ?: return null
                val text = e.optBoolean("text")
                val name = if (text) "texto.txt" else safeName(e.optString("name"))
                val size = e.optLong("size", -1)
                val sha = e.optString("sha256")
                if (!isSha(sha) || size !in 0..(if (text) MAX_TEXT_BYTES.toLong() else maxBytesFor(name))) return null
                OfferItem(name, size, sha, text)
            }
            // a long text travels alone
            if (items.any { it.isText } && items.size > 1) return null
            return FileOffer(id, items, now)
        }

        /** Just a file name: no folders, no control, reserved or direction characters, no leading dots, at most 100 chars
         * (keeping the extension). The PC only sends names, but the phone doesn't take that on trust. */
        fun safeName(raw: String): String {
            var name = raw.substringAfterLast('/').substringAfterLast('\\')
                // also the invisible direction marks that can make "gpj.apk" read as "kpa.jpg"
                .filter { it >= ' ' && it != '\u007f' && it !in "<>:\"|?*" && it !in '‎'..'‏' &&
                    it !in '‪'..'‮' && it !in '⁦'..'⁩' }
                .trim().trimStart('.').trim()
            if (name.length > 100) {
                val dot = name.lastIndexOf('.')
                val ext = if (dot > 0 && name.length - dot <= 10) name.substring(dot) else ""
                name = name.take(100 - ext.length).trimEnd() + ext
            }
            return name.ifEmpty { "arquivo" }
        }
    }
}

/**
 * Something the phone asked the PC to take and that is still waiting, saved next to its copies (outbox/<id>/meta.json)
 * so it survives the app closing. [state]: [QUEUED] the PC hasn't heard of it yet (no connection), [ASKING] the island
 * is showing it, [PENDING] it moved to the PC's Pendentes. Offered again (with "pending") on every connection.
 */
data class OutgoingOffer(
    val id: String,
    val label: String,
    val items: List<OfferItem>,
    val text: String?,
    val at: Long,
    val state: String = QUEUED,
) {
    val isText get() = text != null
    val bytes get() = if (items.isEmpty()) text?.toByteArray(Charsets.UTF_8)?.size?.toLong() ?: 0L else items.sumOf { it.size }
    fun expired(now: Long) = now - at > FileOffer.PENDING_MS || at > now + 60_000L

    fun toJson(): JSONObject = JSONObject().put("id", id).put("label", label).put("at", at).put("state", state)
        .put("items", JSONArray().apply {
            items.forEach { put(JSONObject().put("name", it.name).put("size", it.size).put("sha256", it.sha256).put("text", it.isText)) }
        })
        .apply { if (text != null) put("text", text) }

    companion object {
        const val QUEUED = "fila"
        const val ASKING = "pedindo"
        const val PENDING = "pendente"

        /** Null for anything off (a hand-edited or half-written file is dropped with its copies). */
        fun parse(o: JSONObject): OutgoingOffer? {
            val id = o.optString("id")
            if (!FileOffer.isId(id)) return null
            val list = o.optJSONArray("items") ?: JSONArray()
            if (list.length() > FileOffer.MAX_ITEMS) return null
            val items = (0 until list.length()).map { i ->
                val e = list.optJSONObject(i) ?: return null
                val sha = e.optString("sha256")
                val size = e.optLong("size", -1)
                if (!FileOffer.isSha(sha) || size < 0) return null
                OfferItem(FileOffer.safeName(e.optString("name")), size, sha, e.optBoolean("text"))
            }
            val text = if (o.has("text")) o.optString("text") else null
            if (items.isEmpty() && text.isNullOrEmpty()) return null
            val state = o.optString("state").takeIf { it in setOf(QUEUED, ASKING, PENDING) } ?: QUEUED
            return OutgoingOffer(id, o.optString("label").ifEmpty { "Arquivo" }.take(120), items, text, o.optLong("at"), state)
        }
    }
}

/** A picture or video received from the PC, as the "Imagens" / "Vídeos" grids list it (gifs count as videos). */
data class MediaEntry(val uri: String, val mime: String, val at: Long, val title: String)

object Media {
    fun isVideo(mime: String) = mime.startsWith("video/") || mime == "image/gif"
    fun isImage(mime: String) = mime.startsWith("image/") && mime != "image/gif"

    /** Received files of one kind, newest first, each saved file once. */
    fun entries(records: List<TransferRecord>, videos: Boolean): List<MediaEntry> = records.filter { !it.sent }.flatMap { r ->
        r.uris.mapIndexedNotNull { i, uri ->
            val mime = r.mimes.getOrElse(i) { "" }.lowercase()
            if (if (videos) isVideo(mime) else isImage(mime)) MediaEntry(uri, mime, r.at, r.title) else null
        }
    }.distinctBy { it.uri }
}
