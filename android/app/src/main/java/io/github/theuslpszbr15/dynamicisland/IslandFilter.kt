package io.github.theuslpszbr15.dynamicisland

import android.content.Context
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow

/**
 * Which notices the person switched off on this phone's island, live. Local only: nothing here goes to the PC.
 * One flag per [Pill.Kind], all on by default.
 */
object IslandFilter {
    private const val PREFS = "island-filter"
    private val _off = MutableStateFlow<Set<Pill.Kind>>(emptySet())
    val off: StateFlow<Set<Pill.Kind>> = _off
    @Volatile private var loaded = false

    /** The kinds that can be switched off ([Pill.Kind.Idle] is the resting pill, not a notice). */
    val kinds: List<Pill.Kind> = Pill.Kind.values().filter { it != Pill.Kind.Idle }

    fun load(context: Context): Set<Pill.Kind> {
        if (loaded) return _off.value
        val p = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
        _off.value = kinds.filterNot { p.getBoolean(it.name, true) }.toSet()
        loaded = true
        return _off.value
    }

    fun set(context: Context, kind: Pill.Kind, shown: Boolean) {
        load(context)
        _off.value = if (shown) _off.value - kind else _off.value + kind
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit().putBoolean(kind.name, shown).apply()
    }
}
