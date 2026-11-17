package io.github.theuslpszbr15.dynamicisland

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.os.Handler
import android.os.Looper

/**
 * Clipboard sync. PC → phone is automatic (the PC sends what is copied there). Phone → PC: Android only lets an app
 * read the clipboard while it is in front, so it goes when the app opens, with "Enviar copiado" or the quick tile.
 */
object ClipSync {
    @Volatile private var lastFromPc: String? = null
    @Volatile private var lastSent: String? = null
    private val main by lazy { Handler(Looper.getMainLooper()) }

    private fun prefs(context: Context) = context.getSharedPreferences("clip", Context.MODE_PRIVATE)

    fun enabled(context: Context) = prefs(context).getBoolean("on", true)

    fun setEnabled(context: Context, on: Boolean) = prefs(context).edit().putBoolean("on", on).apply()

    fun fromPc(context: Context, text: String) {
        if (!enabled(context)) return
        lastFromPc = text
        lastSent = text
        val app = context.applicationContext
        main.post {
            try {
                app.getSystemService(ClipboardManager::class.java)?.setPrimaryClip(ClipData.newPlainText("Dynamic Island", text))
            } catch (_: Exception) {}
        }
    }

    /** Sends what the phone has copied, if it is new text; only works while an activity of the app has focus. */
    fun offer(context: Context, force: Boolean = false): Boolean {
        if ((!force && !enabled(context)) || Link.client == null) return false
        val text = try {
            context.getSystemService(ClipboardManager::class.java)?.primaryClip?.takeIf { it.itemCount > 0 }
                ?.getItemAt(0)?.coerceToText(context)?.toString()
        } catch (_: Exception) { null } ?: return false
        if (text.isEmpty() || text.length > LinkClient.MAX_CLIP) return false
        if (!force && (text == lastSent || text == lastFromPc)) return false
        lastSent = text
        Link.sendClip(text)
        return true
    }
}
