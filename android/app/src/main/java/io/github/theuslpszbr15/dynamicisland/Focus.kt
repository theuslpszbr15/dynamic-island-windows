package io.github.theuslpszbr15.dynamicisland

import android.content.Context
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.update
import java.util.Calendar

/**
 * When the island stays quiet about other apps' notifications: switched on by hand, inside the quiet hours, or for the
 * apps the person muted. Calls, timers, agents and the rest of the island are never muted by it.
 */
data class FocusConfig(
    val manual: Boolean = false,
    val quiet: Boolean = false,
    /** Quiet hours, minutes since midnight; may cross midnight (22:00 to 07:00). */
    val fromMin: Int = 22 * 60,
    val toMin: Int = 7 * 60,
    val muted: Set<String> = emptySet(),
) {
    fun inQuietHours(minute: Int) = if (fromMin <= toMin) minute in fromMin until toMin else minute >= fromMin || minute < toMin

    fun silent(pkg: String, minute: Int) = manual || (quiet && inQuietHours(minute)) || pkg in muted

    /** Why nothing shows right now, for the card ("" when it is not silenced). */
    fun reason(minute: Int) = when {
        manual -> "Foco ligado"
        quiet && inQuietHours(minute) -> "Horário silencioso"
        else -> ""
    }
}

object Focus {
    private const val PREFS = "island-focus"
    private val _state = MutableStateFlow(FocusConfig())
    val state: StateFlow<FocusConfig> = _state
    @Volatile private var loaded = false

    /** Apps that sent a notification since the app started (package to name), for the "mute an app" list. */
    private val _seen = MutableStateFlow<Map<String, String>>(emptyMap())
    val seen: StateFlow<Map<String, String>> = _seen

    fun seen(pkg: String, label: String) { if (_seen.value[pkg] != label) _seen.update { it + (pkg to label) } }

    fun load(context: Context): FocusConfig {
        if (loaded) return _state.value
        val p = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
        val d = FocusConfig()
        _state.value = FocusConfig(p.getBoolean("manual", d.manual), p.getBoolean("quiet", d.quiet), p.getInt("from", d.fromMin),
            p.getInt("to", d.toMin), p.getStringSet("muted", emptySet()).orEmpty().toSet())
        loaded = true
        return _state.value
    }

    private fun set(context: Context, c: FocusConfig) {
        _state.value = c
        loaded = true
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit().putBoolean("manual", c.manual).putBoolean("quiet", c.quiet)
            .putInt("from", c.fromMin).putInt("to", c.toMin).putStringSet("muted", c.muted).apply()
    }

    fun setManual(context: Context, on: Boolean) = set(context, load(context).copy(manual = on))
    fun setQuiet(context: Context, on: Boolean) = set(context, load(context).copy(quiet = on))
    fun setHours(context: Context, from: Int, to: Int) = set(context, load(context).copy(fromMin = from.coerceIn(0, 1439), toMin = to.coerceIn(0, 1439)))
    fun toggleMute(context: Context, pkg: String) =
        set(context, load(context).let { it.copy(muted = if (pkg in it.muted) it.muted - pkg else it.muted + pkg) })

    fun minuteOfDay(now: Long = System.currentTimeMillis()): Int =
        Calendar.getInstance().apply { timeInMillis = now }.let { it.get(Calendar.HOUR_OF_DAY) * 60 + it.get(Calendar.MINUTE) }

    fun isSilent(context: Context, pkg: String, now: Long = System.currentTimeMillis()) = load(context).silent(pkg, minuteOfDay(now))

    /** "22:30" */
    fun clock(minute: Int) = "%02d:%02d".format(minute / 60, minute % 60)
}
