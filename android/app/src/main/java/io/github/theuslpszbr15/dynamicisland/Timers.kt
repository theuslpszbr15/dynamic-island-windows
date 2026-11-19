package io.github.theuslpszbr15.dynamicisland

import android.content.Context
import android.os.Build
import android.os.VibrationEffect
import android.os.Vibrator
import android.os.VibratorManager
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow

/** A countdown or a stopwatch on the island. Plain data and arithmetic, tested off the phone. */
data class TimerState(
    val mode: Mode = Mode.Idle,
    /** Wall clock when it (re)started, moved forward by the paused time on resume, so elapsed = now - startedAt. */
    val startedAt: Long = 0L,
    val durationMs: Long = 0L,
    /** Wall clock when it was paused (0: running). */
    val pausedAt: Long = 0L,
    /** Wall clock when a countdown reached zero (0: not yet). */
    val finishedAt: Long = 0L,
) {
    enum class Mode { Idle, Countdown, Stopwatch }

    val active get() = mode != Mode.Idle
    val paused get() = active && pausedAt > 0L && finishedAt == 0L
    val running get() = active && pausedAt == 0L && finishedAt == 0L

    fun elapsed(now: Long): Long = ((if (paused) pausedAt else now) - startedAt).coerceAtLeast(0)

    fun remaining(now: Long): Long = if (finishedAt > 0) 0 else (durationMs - elapsed(now)).coerceAtLeast(0)

    /** A countdown that reached zero and was not told yet. */
    fun reached(now: Long) = mode == Mode.Countdown && finishedAt == 0L && elapsed(now) >= durationMs

    /** "4:32" or "1:02:03": a countdown rounds up (so "0:00" only shows when it is over), a stopwatch counts up. */
    fun text(now: Long): String {
        val ms = if (mode == Mode.Countdown) (remaining(now) + 999) / 1000 * 1000 else elapsed(now)
        val s = ms / 1000
        return if (s >= 3600) "%d:%02d:%02d".format(s / 3600, s / 60 % 60, s % 60) else "%d:%02d".format(s / 60, s % 60)
    }

    fun pause(now: Long) = if (running) copy(pausedAt = now) else this
    fun resume(now: Long) = if (paused) copy(startedAt = startedAt + (now - pausedAt), pausedAt = 0L) else this
    fun finish(now: Long) = copy(finishedAt = now)

    companion object {
        fun countdown(ms: Long, now: Long) = TimerState(Mode.Countdown, now, ms)
        fun stopwatch(now: Long) = TimerState(Mode.Stopwatch, now)
    }
}

/** The island's one timer, kept across restarts of the app (a few numbers, nothing else). */
object Timers {
    private const val PREFS = "island-timer"
    private val _state = MutableStateFlow(TimerState())
    val state: StateFlow<TimerState> = _state
    @Volatile private var loaded = false

    fun load(context: Context): TimerState {
        if (loaded) return _state.value
        val p = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
        _state.value = TimerState(
            TimerState.Mode.values().getOrElse(p.getInt("m", 0)) { TimerState.Mode.Idle },
            p.getLong("s", 0), p.getLong("d", 0), p.getLong("p", 0), p.getLong("f", 0),
        )
        loaded = true
        return _state.value
    }

    private fun set(context: Context, s: TimerState) {
        _state.value = s
        loaded = true
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit()
            .putInt("m", s.mode.ordinal).putLong("s", s.startedAt).putLong("d", s.durationMs)
            .putLong("p", s.pausedAt).putLong("f", s.finishedAt).apply()
    }

    fun countdown(context: Context, minutes: Int, now: Long = System.currentTimeMillis()) = set(context, TimerState.countdown(minutes * 60_000L, now))
    fun stopwatch(context: Context, now: Long = System.currentTimeMillis()) = set(context, TimerState.stopwatch(now))
    fun pauseOrResume(context: Context, now: Long = System.currentTimeMillis()) =
        set(context, _state.value.let { if (it.paused) it.resume(now) else it.pause(now) })
    fun reset(context: Context) = set(context, TimerState())

    /** Called while the island ticks: a countdown that hit zero buzzes once and stays "Acabou" until it is cleared. */
    fun tick(context: Context, now: Long = System.currentTimeMillis()) {
        val s = load(context)
        if (!s.reached(now)) return
        set(context, s.finish(now))
        buzz(context)
    }

    @Suppress("DEPRECATION")
    private fun buzz(context: Context) {
        try {
            val v = if (Build.VERSION.SDK_INT >= 31) context.getSystemService(VibratorManager::class.java)?.defaultVibrator
            else context.getSystemService(Vibrator::class.java)
            v?.vibrate(VibrationEffect.createWaveform(longArrayOf(0, 300, 150, 300, 150, 500), -1))
        } catch (_: Exception) {}
    }
}
