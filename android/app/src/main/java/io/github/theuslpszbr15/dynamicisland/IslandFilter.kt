package io.github.theuslpszbr15.dynamicisland

import android.content.Context
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow

/**
 * Which notices the person switched off on this phone's island, live. One flag per [Pill.Kind], all on by default.
 * The list is shared with the PC over the link: every change carries its time ([at]) and the newest change wins on
 * both sides, so a line bounced back (or a reconnection that repeats it) changes nothing.
 */
object IslandFilter {
    private const val PREFS = "island-filter"
    private val _off = MutableStateFlow<Set<Pill.Kind>>(emptySet())
    val off: StateFlow<Set<Pill.Kind>> = _off
    @Volatile private var loaded = false
    /** When the list last changed (Unix ms, 0 = never). */
    @Volatile var at: Long = 0L
        private set

    /** The kinds that can be switched off ([Pill.Kind.Idle] is the resting pill, not a notice). */
    val kinds: List<Pill.Kind> = Pill.Kind.values().filter { it != Pill.Kind.Idle }

    fun load(context: Context): Set<Pill.Kind> {
        if (loaded) return _off.value
        val p = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
        _off.value = kinds.filterNot { p.getBoolean(it.name, true) }.toSet()
        at = p.getLong(AT, 0L)
        loaded = true
        return _off.value
    }

    fun set(context: Context, kind: Pill.Kind, shown: Boolean) {
        load(context)
        val next = if (shown) _off.value - kind else _off.value + kind
        if (next == _off.value) return
        val stamp = maxOf(System.currentTimeMillis(), at + 1)
        store(context, next, stamp)
        Link.sendFilter(names(next), stamp)
    }

    /** The PC's list (or another phone's, through it). Applied only if it is newer than ours; true if it was. */
    fun fromPc(context: Context, off: List<String>, pcAt: Long): Boolean {
        load(context)
        if (!isNewer(at, pcAt)) return false
        store(context, parse(off), pcAt)
        return true
    }

    /** After (re)connecting: tell the PC what this phone has, so whichever side changed last wins. */
    fun announce(client: LinkClient) {
        if (at > 0) try { client.filter(names(_off.value), at) } catch (_: Exception) {}
    }

    private fun store(context: Context, off: Set<Pill.Kind>, stamp: Long) {
        _off.value = off
        at = stamp
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit().apply {
            kinds.forEach { putBoolean(it.name, it !in off) }
            putLong(AT, stamp)
        }.apply()
    }

    private const val AT = "at"

    /** The newest change wins; the same instant is no change. */
    fun isNewer(currentAt: Long, incomingAt: Long) = incomingAt > currentAt

    /** Kind names as they travel on the link, in a fixed order. */
    fun names(off: Set<Pill.Kind>): List<String> = kinds.filter { it in off }.map { it.name }

    /** Names from the link into kinds; anything unknown (a newer app's kind, the resting pill) is ignored. */
    fun parse(names: List<String>): Set<Pill.Kind> = kinds.filter { it.name in names }.toSet()
}
