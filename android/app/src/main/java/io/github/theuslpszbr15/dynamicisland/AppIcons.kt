package io.github.theuslpszbr15.dynamicisland

import android.app.Notification
import android.content.Context
import android.graphics.Bitmap
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.graphics.drawable.Drawable
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.update

/**
 * The logo and name of the app a notification came from, kept small and in memory (an app's logo is the same every
 * time, so each is loaded once). The logo is the app's launcher icon; if Android does not let this app see the
 * package, the notification's own small icon on a disc of the app's colour stands in.
 */
object AppIcons {
    private const val SIZE = 96
    private const val MAX = 40

    private val _icons = MutableStateFlow<Map<String, Bitmap>>(emptyMap())
    val icons: StateFlow<Map<String, Bitmap>> = _icons
    private val labels = HashMap<String, String>()

    /** The app's name as the person knows it ("WhatsApp"), else the package name. */
    fun label(context: Context, pkg: String): String = synchronized(labels) {
        labels.getOrPut(pkg) {
            try {
                val pm = context.packageManager
                pm.getApplicationLabel(pm.getApplicationInfo(pkg, 0)).toString()
            } catch (_: Exception) { pkg.substringAfterLast('.').replaceFirstChar { it.uppercase() } }
        }
    }

    /** Loads the logo of [pkg] once; [n] is only used when the launcher icon cannot be read. */
    fun load(context: Context, pkg: String, n: Notification?) {
        if (pkg in _icons.value) return
        val bitmap = try { context.packageManager.getApplicationIcon(pkg).let(::bitmap) } catch (_: Exception) { null }
            ?: n?.let { fallback(context, it) } ?: return
        _icons.update { (it + (pkg to bitmap)).let { m -> if (m.size > MAX) m.entries.drop(m.size - MAX).associate { e -> e.key to e.value } else m } }
    }

    private fun fallback(context: Context, n: Notification): Bitmap? = try {
        val d = n.smallIcon?.loadDrawable(context) ?: return null
        val out = Bitmap.createBitmap(SIZE, SIZE, Bitmap.Config.ARGB_8888)
        val canvas = Canvas(out)
        canvas.drawCircle(SIZE / 2f, SIZE / 2f, SIZE / 2f, Paint(Paint.ANTI_ALIAS_FLAG).apply {
            color = if (n.color != 0) n.color else Color.parseColor("#48484A")
        })
        val pad = SIZE / 4
        d.setBounds(pad, pad, SIZE - pad, SIZE - pad)
        d.setTint(Color.WHITE)
        d.draw(canvas)
        out
    } catch (_: Exception) { null }

    private fun bitmap(d: Drawable): Bitmap {
        val out = Bitmap.createBitmap(SIZE, SIZE, Bitmap.Config.ARGB_8888)
        val canvas = Canvas(out)
        d.setBounds(0, 0, SIZE, SIZE)
        d.draw(canvas)
        return out
    }
}
