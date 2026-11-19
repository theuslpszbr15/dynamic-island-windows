package io.github.theuslpszbr15.dynamicisland

import android.Manifest
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.content.pm.PackageManager
import android.net.Uri
import android.telecom.TelecomManager
import android.telephony.TelephonyManager
import androidx.core.content.ContextCompat
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow

/** A phone call ringing on this phone, or the one just missed. */
data class PhoneRing(val state: State, val name: String, val number: String, val at: Long) {
    enum class State { Ringing, Missed }

    val who get() = name.ifEmpty { number.ifEmpty { "Número desconhecido" } }

    companion object {
        /** A missed call lights the pill this long (it stays in the card until dismissed). */
        const val MISSED_MS = 10_000L
    }
}

/** What the next phone state does to the ring; [answered] is remembered between steps so a call picked up is not "missed". */
data class RingStep(val ring: PhoneRing?, val answered: Boolean)

/**
 * Calls on the island: who is calling (and the contact's name when the phone has the permissions), buttons to answer or
 * decline, and the missed ones. Reading the state needs the phone permission; answering and declining need
 * "Atender chamadas", which the island asks for the first time it is needed. Nothing leaves the phone.
 */
object PhoneRings {
    val permissions = arrayOf(
        Manifest.permission.READ_PHONE_STATE, Manifest.permission.READ_CALL_LOG, Manifest.permission.READ_CONTACTS,
        Manifest.permission.ANSWER_PHONE_CALLS,
    )

    // the documented values of TelephonyManager.EXTRA_STATE_*, spelled out so the logic tests without Android
    const val RINGING = "RINGING"
    const val OFFHOOK = "OFFHOOK"
    const val IDLE = "IDLE"

    private val _ring = MutableStateFlow<PhoneRing?>(null)
    val ring: StateFlow<PhoneRing?> = _ring
    private var answered = false
    private var receiver: BroadcastReceiver? = null

    /** Plain transition logic (tested off the phone). Android sends RINGING twice, without the number and then with it. */
    fun next(prev: PhoneRing?, answered: Boolean, state: String?, number: String, name: String, now: Long): RingStep = when (state) {
        RINGING ->
            if (prev?.state == PhoneRing.State.Ringing)
                RingStep(prev.copy(name = name.ifEmpty { prev.name }, number = number.ifEmpty { prev.number }), false)
            else RingStep(PhoneRing(PhoneRing.State.Ringing, name, number, now), false)
        OFFHOOK -> RingStep(if (prev?.state == PhoneRing.State.Ringing) null else prev, true)
        IDLE ->
            if (prev?.state == PhoneRing.State.Ringing && !answered) RingStep(prev.copy(state = PhoneRing.State.Missed, at = now), false)
            else RingStep(if (prev?.state == PhoneRing.State.Ringing) null else prev, false)
        else -> RingStep(prev, answered)
    }

    private fun granted(context: Context, p: String) = ContextCompat.checkSelfPermission(context, p) == PackageManager.PERMISSION_GRANTED

    /** The phone permission is there: the island can see calls ringing. */
    fun canSee(context: Context) = granted(context, Manifest.permission.READ_PHONE_STATE)

    /** The island may answer and decline. */
    fun canControl(context: Context) = granted(context, Manifest.permission.ANSWER_PHONE_CALLS)

    /** From the overlay when it starts and every time it refreshes: begins listening once the permission exists. */
    fun start(context: Context) {
        if (receiver != null || !canSee(context)) return
        val app = context.applicationContext
        val r = object : BroadcastReceiver() {
            override fun onReceive(c: Context, i: Intent) {
                @Suppress("DEPRECATION")
                val number = i.getStringExtra(TelephonyManager.EXTRA_INCOMING_NUMBER).orEmpty()
                val step = next(_ring.value, answered, i.getStringExtra(TelephonyManager.EXTRA_STATE), number,
                    if (number.isEmpty()) "" else Calls.contactName(c, number), System.currentTimeMillis())
                answered = step.answered
                _ring.value = step.ring
                if (step.ring != null) IslandOverlay.refresh(c)
            }
        }
        receiver = r
        try { ContextCompat.registerReceiver(app, r, IntentFilter(TelephonyManager.ACTION_PHONE_STATE_CHANGED), ContextCompat.RECEIVER_EXPORTED) }
        catch (_: Exception) { receiver = null }
    }

    /** Picks up. False if Android (or the missing permission) says no. */
    fun answer(context: Context): Boolean = try {
        context.getSystemService(TelecomManager::class.java)?.acceptRingingCall() != null
    } catch (_: SecurityException) { false } catch (_: Exception) { false }

    /** Hangs up the ringing call. */
    @Suppress("DEPRECATION")
    fun decline(context: Context): Boolean = try {
        context.getSystemService(TelecomManager::class.java)?.endCall() == true
    } catch (_: SecurityException) { false } catch (_: Exception) { false }

    fun dismissMissed() { if (_ring.value?.state == PhoneRing.State.Missed) _ring.value = null }

    /** The dialer with the number filled in (the person presses call). */
    fun callBack(context: Context, number: String) {
        if (number.isEmpty()) return
        try { context.startActivity(Intent(Intent.ACTION_DIAL, Uri.parse("tel:" + Uri.encode(number))).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)) } catch (_: Exception) {}
    }
}
