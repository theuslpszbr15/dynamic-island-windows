package io.github.theuslpszbr15.dynamicisland

import android.content.Context
import org.json.JSONArray
import org.json.JSONObject
import kotlin.math.abs
import kotlin.math.atan2
import kotlin.math.hypot

/** One control on the pad: its centre as a share of the play area (0–1), its size in dp (before [PadLayout.scale]). */
data class PadItem(val cx: Float, val cy: Float, val w: Float, val h: Float, val hidden: Boolean = false)

/**
 * Where each control of the phone's game pad sits, how big everything is, how see-through, and whether tilting the phone
 * steers (the left stick's X). Kept per kind (Xbox, PS4) in the app's own preferences.
 */
data class PadLayout(val items: Map<String, PadItem>, val scale: Float = 1f, val alpha: Float = 1f, val gyro: Boolean = false) {
    fun item(id: String): PadItem = items[id] ?: Default.items.getValue(id)

    fun with(id: String, item: PadItem) = copy(items = items + (id to item))

    fun toJson(): JSONObject = JSONObject().put("scale", scale.toDouble()).put("alpha", alpha.toDouble()).put("gyro", gyro).put("items",
        JSONObject().apply {
            items.forEach { (id, i) -> put(id, JSONArray(listOf(i.cx, i.cy, i.w, i.h, if (i.hidden) 1 else 0).map { it.toDouble() })) }
        })

    companion object {
        /** Controls that keep a square shape when resized (sticks, round buttons, the D-pad and the face buttons). */
        val Square = setOf("ls", "rs", "dpad", "face", "guide")

        val Ids = listOf("lt", "lb", "rb", "rt", "ls", "rs", "dpad", "face", "back", "start", "guide", "l3", "r3")

        private fun layout(vararg items: Pair<String, PadItem>, gyro: Boolean = false) = PadLayout(items.toMap(), gyro = gyro)

        val Default = layout(
            "lt" to PadItem(0.08f, 0.08f, 120f, 44f), "lb" to PadItem(0.235f, 0.08f, 120f, 44f),
            "rb" to PadItem(0.765f, 0.08f, 120f, 44f), "rt" to PadItem(0.92f, 0.08f, 120f, 44f),
            "ls" to PadItem(0.14f, 0.47f, 128f, 128f), "dpad" to PadItem(0.31f, 0.8f, 114f, 114f),
            "face" to PadItem(0.86f, 0.47f, 132f, 132f), "rs" to PadItem(0.69f, 0.8f, 120f, 120f),
            "back" to PadItem(0.43f, 0.32f, 72f, 36f), "start" to PadItem(0.57f, 0.32f, 72f, 36f),
            "guide" to PadItem(0.5f, 0.52f, 52f, 52f), "l3" to PadItem(0.44f, 0.74f, 56f, 34f), "r3" to PadItem(0.56f, 0.74f, 56f, 34f),
        )

        /** Pedals for braking and accelerating at the edges; steering by tilting the phone. */
        val Racing = layout(
            "lt" to PadItem(0.11f, 0.62f, 150f, 190f), "rt" to PadItem(0.89f, 0.62f, 150f, 190f),
            "lb" to PadItem(0.09f, 0.08f, 110f, 44f), "rb" to PadItem(0.91f, 0.08f, 110f, 44f),
            "face" to PadItem(0.68f, 0.55f, 128f, 128f), "dpad" to PadItem(0.32f, 0.55f, 110f, 110f, hidden = true),
            "ls" to PadItem(0.32f, 0.55f, 120f, 120f, hidden = true), "rs" to PadItem(0.7f, 0.8f, 110f, 110f, hidden = true),
            "back" to PadItem(0.44f, 0.9f, 72f, 36f), "start" to PadItem(0.56f, 0.9f, 72f, 36f), "guide" to PadItem(0.5f, 0.7f, 48f, 48f),
            "l3" to PadItem(0.44f, 0.74f, 56f, 34f, hidden = true), "r3" to PadItem(0.56f, 0.74f, 56f, 34f, hidden = true),
            gyro = true,
        )

        /** A big D-pad and big face buttons, the shoulders within reach. */
        val Fighting = layout(
            "dpad" to PadItem(0.17f, 0.56f, 160f, 160f), "face" to PadItem(0.83f, 0.56f, 170f, 170f),
            "lb" to PadItem(0.36f, 0.1f, 110f, 44f), "rb" to PadItem(0.64f, 0.1f, 110f, 44f),
            "lt" to PadItem(0.36f, 0.26f, 110f, 44f), "rt" to PadItem(0.64f, 0.26f, 110f, 44f),
            "ls" to PadItem(0.14f, 0.47f, 128f, 128f, hidden = true), "rs" to PadItem(0.69f, 0.8f, 120f, 120f, hidden = true),
            "back" to PadItem(0.44f, 0.9f, 72f, 36f), "start" to PadItem(0.56f, 0.9f, 72f, 36f), "guide" to PadItem(0.5f, 0.58f, 48f, 48f),
            "l3" to PadItem(0.44f, 0.74f, 56f, 34f, hidden = true), "r3" to PadItem(0.56f, 0.74f, 56f, 34f, hidden = true),
        )

        /** Stick to walk, big jump buttons. */
        val Platform = layout(
            "ls" to PadItem(0.15f, 0.58f, 150f, 150f), "face" to PadItem(0.85f, 0.58f, 160f, 160f),
            "lt" to PadItem(0.08f, 0.08f, 120f, 44f), "lb" to PadItem(0.235f, 0.08f, 120f, 44f),
            "rb" to PadItem(0.765f, 0.08f, 120f, 44f), "rt" to PadItem(0.92f, 0.08f, 120f, 44f),
            "dpad" to PadItem(0.31f, 0.8f, 114f, 114f, hidden = true), "rs" to PadItem(0.69f, 0.8f, 120f, 120f, hidden = true),
            "back" to PadItem(0.43f, 0.32f, 72f, 36f), "start" to PadItem(0.57f, 0.32f, 72f, 36f), "guide" to PadItem(0.5f, 0.52f, 52f, 52f),
            "l3" to PadItem(0.44f, 0.74f, 56f, 34f, hidden = true), "r3" to PadItem(0.56f, 0.74f, 56f, 34f, hidden = true),
        )

        val Presets = listOf("Padrão" to Default, "Corrida" to Racing, "Luta" to Fighting, "Plataforma" to Platform)

        fun fromJson(o: JSONObject): PadLayout {
            val items = mutableMapOf<String, PadItem>()
            val list = o.optJSONObject("items") ?: JSONObject()
            for (id in Ids) {
                val a = list.optJSONArray(id) ?: continue
                if (a.length() < 5) continue
                items[id] = PadItem(a.getDouble(0).toFloat().coerceIn(0f, 1f), a.getDouble(1).toFloat().coerceIn(0f, 1f),
                    a.getDouble(2).toFloat().coerceIn(MIN_SIZE, MAX_SIZE), a.getDouble(3).toFloat().coerceIn(MIN_SIZE, MAX_SIZE), a.getInt(4) == 1)
            }
            return PadLayout(Default.items + items, o.optDouble("scale", 1.0).toFloat().coerceIn(MIN_SCALE, MAX_SCALE),
                o.optDouble("alpha", 1.0).toFloat().coerceIn(MIN_ALPHA, 1f), o.optBoolean("gyro"))
        }

        private fun prefs(context: Context) = context.getSharedPreferences("gamepad", Context.MODE_PRIVATE)

        fun load(context: Context, ps4: Boolean): PadLayout =
            prefs(context).getString(if (ps4) "layout_ps4" else "layout_xbox", null)
                ?.let { runCatching { fromJson(JSONObject(it)) }.getOrNull() } ?: Default

        fun save(context: Context, ps4: Boolean, layout: PadLayout) =
            prefs(context).edit().putString(if (ps4) "layout_ps4" else "layout_xbox", layout.toJson().toString()).apply()

        const val MIN_SIZE = 32f
        const val MAX_SIZE = 320f
        const val MIN_SCALE = 0.7f
        const val MAX_SCALE = 1.4f
        const val MIN_ALPHA = 0.25f

        /** Steering wheel: the phone turned within its own plane, from gravity in screen coordinates (x right, y up).
         * Upright = 0; turned clockwise = right (positive). ±[MAX_TILT] degrees is full lock; flat on a table, nothing. */
        fun steer(sx: Float, sy: Float): Float {
            if (hypot(sx, sy) < 3f) return 0f
            val degrees = Math.toDegrees(-atan2(sx.toDouble(), sy.toDouble())).toFloat()
            if (abs(degrees) < DEAD_TILT) return 0f
            return ((degrees - Math.signum(degrees) * DEAD_TILT) / (MAX_TILT - DEAD_TILT)).coerceIn(-1f, 1f)
        }

        const val MAX_TILT = 35f
        const val DEAD_TILT = 3f
    }
}
