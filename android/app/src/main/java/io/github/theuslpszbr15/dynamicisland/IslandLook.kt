package io.github.theuslpszbr15.dynamicisland

import android.content.Context
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow

/**
 * How the island looks and where it sits, in dp (except [corner], 0..1 of the round end, and [opacity], 0.4..1).
 * [width] and [height] are the resting pill; it grows from there for a notice and opens into a card.
 */
data class Look(
    val width: Int = 110,
    val height: Int = 32,
    val corner: Float = 1f,
    val x: Int = 0,
    val y: Int = 8,
    val opacity: Float = 1f,
    /** The song playing shows on the island. */
    val media: Boolean = true,
    /** With nothing to show, the island shrinks into a dot and goes; it comes back the same way when something arrives. */
    val hide: Boolean = true,
    /**
     * Docked to the top edge, like a notch: flat on top, round below, curving into the edge. Only drawn so when the
     * island can go above the status bar ([IslandAccessibility]); [y] is then ignored.
     */
    val dock: Boolean = false,
) {
    /** Always inside what the screen can show. */
    fun clamped() = copy(
        width = width.coerceIn(MIN_W, MAX_W), height = height.coerceIn(MIN_H, MAX_H), corner = corner.coerceIn(0f, 1f),
        x = x.coerceIn(-MAX_X, MAX_X), y = y.coerceIn(0, MAX_Y), opacity = opacity.coerceIn(0.4f, 1f),
    )

    companion object {
        const val MIN_W = 60
        const val MAX_W = 220
        const val MIN_H = 22
        const val MAX_H = 48
        const val MAX_X = 170
        const val MAX_Y = 120

        /** Ready-made shapes: name to look (keeping the person's other choices). */
        val presets: List<Pair<String, (Look) -> Look>> = listOf(
            "Câmera no centro" to { l -> l.copy(width = 110, height = 32, corner = 1f, x = 0, y = 8, dock = false) },
            "Câmera no canto" to { l -> l.copy(width = 90, height = 30, corner = 1f, x = -125, y = 8, dock = false) },
            "Presa no topo" to { l -> l.copy(width = 175, height = 35, corner = 1f, x = 0, y = 0, dock = true) },
            "Pílula larga" to { l -> l.copy(width = 170, height = 36, corner = 1f, x = 0, y = 10, dock = false) },
            "Compacta" to { l -> l.copy(width = 72, height = 26, corner = 1f, x = 0, y = 10, dock = false) },
        )
    }
}

/** The saved [Look], live: the island redraws as a slider moves. */
object IslandLook {
    private const val PREFS = "island-look"
    private val _state = MutableStateFlow(Look())
    val state: StateFlow<Look> = _state
    @Volatile private var loaded = false

    fun load(context: Context): Look {
        if (loaded) return _state.value
        val p = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
        val d = Look()
        _state.value = Look(
            p.getInt("w", d.width), p.getInt("h", d.height), p.getFloat("c", d.corner),
            p.getInt("x", d.x), p.getInt("y", d.y), p.getFloat("o", d.opacity), p.getBoolean("m", d.media),
            p.getBoolean("hd", d.hide), p.getBoolean("dk", d.dock),
        ).clamped()
        loaded = true
        return _state.value
    }

    fun set(context: Context, look: Look) {
        val l = look.clamped()
        _state.value = l
        loaded = true
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit()
            .putInt("w", l.width).putInt("h", l.height).putFloat("c", l.corner)
            .putInt("x", l.x).putInt("y", l.y).putFloat("o", l.opacity).putBoolean("m", l.media)
            .putBoolean("hd", l.hide).putBoolean("dk", l.dock).apply()
        IslandOverlay.place(context)
    }
}
