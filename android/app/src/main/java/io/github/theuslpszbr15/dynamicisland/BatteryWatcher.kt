package io.github.theuslpszbr15.dynamicisland

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.os.BatteryManager
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow

/** The battery as the island tells it: the level, whether it charges, and the last thing that happened to it. */
data class BatteryInfo(val percent: Int, val charging: Boolean, val at: Long, val event: Event = Event.None) {
    enum class Event { None, Plugged, Unplugged, Low, Critical, Full }
}

/** Plug in: an animation; going low (20%, 10%) and full: a notice. Reads Android's sticky battery broadcast; nothing leaves the phone. */
object BatteryWatcher {
    const val LOW = 20
    const val CRITICAL = 10
    /** A battery event lights the pill this long. */
    const val FLASH_MS = 6_000L

    private val _state = MutableStateFlow<BatteryInfo?>(null)
    val state: StateFlow<BatteryInfo?> = _state
    private var receiver: BroadcastReceiver? = null

    /** What changed from [prev] to the new reading (the first reading says nothing, so opening the app never "announces" the battery). */
    fun next(prev: BatteryInfo?, percent: Int, charging: Boolean, now: Long): BatteryInfo {
        val p = percent.coerceIn(0, 100)
        val event = when {
            prev == null -> BatteryInfo.Event.None
            charging && !prev.charging -> BatteryInfo.Event.Plugged
            !charging && prev.charging -> BatteryInfo.Event.Unplugged
            charging && p == 100 && prev.percent < 100 -> BatteryInfo.Event.Full
            !charging && p <= CRITICAL && prev.percent > CRITICAL -> BatteryInfo.Event.Critical
            !charging && p <= LOW && prev.percent > LOW -> BatteryInfo.Event.Low
            else -> BatteryInfo.Event.None
        }
        // a quiet change keeps the last event and its time, so the notice fades by itself
        return if (event == BatteryInfo.Event.None && prev != null) BatteryInfo(p, charging, prev.at, prev.event)
        else BatteryInfo(p, charging, now, event)
    }

    fun start(context: Context) {
        if (receiver != null) return
        val app = context.applicationContext
        val r = object : BroadcastReceiver() {
            override fun onReceive(c: Context, i: Intent) = read(i)
        }
        receiver = r
        // ACTION_BATTERY_CHANGED is sticky: registering returns the current reading at once
        try { app.registerReceiver(r, IntentFilter(Intent.ACTION_BATTERY_CHANGED))?.let(::read) } catch (_: Exception) { receiver = null }
    }

    private fun read(i: Intent) {
        val level = i.getIntExtra(BatteryManager.EXTRA_LEVEL, -1)
        val scale = i.getIntExtra(BatteryManager.EXTRA_SCALE, 100)
        if (level < 0 || scale <= 0) return
        val status = i.getIntExtra(BatteryManager.EXTRA_STATUS, -1)
        val charging = status == BatteryManager.BATTERY_STATUS_CHARGING || status == BatteryManager.BATTERY_STATUS_FULL
        _state.value = next(_state.value, level * 100 / scale, charging, System.currentTimeMillis())
    }
}
