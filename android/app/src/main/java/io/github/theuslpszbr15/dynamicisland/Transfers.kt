package io.github.theuslpszbr15.dynamicisland

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
        /** The PC drops an offer after 10 minutes. */
        const val WINDOW_MS = 9 * 60_000L
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

        /** Through the relay a batch carries at most 10 MB in all (the relay has a daily budget); the PC uses the same rule. */
        const val RELAY_MAX_BYTES = 10L * 1024 * 1024
        const val RELAY_TOO_BIG = "Pela internet o limite é 10 MB por envio. No mesmo Wi-Fi do PC vai até 30 MB por arquivo."

        /** Whether a batch of [totalBytes] may go over [route]. */
        fun fitsRoute(totalBytes: Long, route: Route) = route != Route.Relay || totalBytes <= RELAY_MAX_BYTES

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
