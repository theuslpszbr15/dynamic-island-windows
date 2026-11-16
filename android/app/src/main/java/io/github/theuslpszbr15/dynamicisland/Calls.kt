package io.github.theuslpszbr15.dynamicisland

import android.Manifest
import android.app.Activity
import android.app.PendingIntent
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.provider.ContactsContract
import android.service.quicksettings.TileService
import android.telephony.TelephonyManager
import android.widget.Toast
import androidx.core.content.ContextCompat

/** Incoming calls go to the PC's island, with the contact's name when the phone knows it. Off until the person turns
 * it on (and grants the phone and contacts permissions). */
object Calls {
    val permissions = arrayOf(Manifest.permission.READ_PHONE_STATE, Manifest.permission.READ_CALL_LOG, Manifest.permission.READ_CONTACTS)

    private fun prefs(context: Context) = context.getSharedPreferences("calls", Context.MODE_PRIVATE)

    fun enabled(context: Context) = prefs(context).getBoolean("on", false) && granted(context, Manifest.permission.READ_PHONE_STATE)

    fun setEnabled(context: Context, on: Boolean) = prefs(context).edit().putBoolean("on", on).apply()

    private fun granted(context: Context, permission: String) =
        ContextCompat.checkSelfPermission(context, permission) == PackageManager.PERMISSION_GRANTED

    fun contactName(context: Context, number: String): String {
        if (number.isEmpty() || !granted(context, Manifest.permission.READ_CONTACTS)) return ""
        return try {
            context.contentResolver.query(
                Uri.withAppendedPath(ContactsContract.PhoneLookup.CONTENT_FILTER_URI, Uri.encode(number)),
                arrayOf(ContactsContract.PhoneLookup.DISPLAY_NAME), null, null, null,
            )?.use { if (it.moveToFirst()) it.getString(0) ?: "" else "" } ?: ""
        } catch (_: Exception) { "" }
    }

    /** Registered by LinkService while the link runs. */
    fun receiver() = object : BroadcastReceiver() {
        override fun onReceive(context: Context, intent: Intent) {
            if (!enabled(context)) return
            @Suppress("DEPRECATION")
            val number = intent.getStringExtra(TelephonyManager.EXTRA_INCOMING_NUMBER).orEmpty()
            when (intent.getStringExtra(TelephonyManager.EXTRA_STATE)) {
                // Android sends RINGING twice: without the number, then with it (READ_CALL_LOG)
                TelephonyManager.EXTRA_STATE_RINGING -> Link.call("ringing", contactName(context, number), number)
                TelephonyManager.EXTRA_STATE_OFFHOOK -> Link.call("offhook", "", "")
                TelephonyManager.EXTRA_STATE_IDLE -> Link.call("idle", "", "")
            }
        }
    }

    fun register(context: Context, receiver: BroadcastReceiver) =
        ContextCompat.registerReceiver(context, receiver, IntentFilter(TelephonyManager.ACTION_PHONE_STATE_CHANGED),
            ContextCompat.RECEIVER_EXPORTED)
}

/** Quick settings tile "Copiado → PC": opens [ClipActivity] for a blink, the only way Android lets the app read
 * the clipboard. */
class ClipTileService : TileService() {
    override fun onClick() {
        val intent = Intent(this, ClipActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        if (Build.VERSION.SDK_INT >= 34)
            startActivityAndCollapse(PendingIntent.getActivity(this, 0, intent, PendingIntent.FLAG_IMMUTABLE))
        else @Suppress("DEPRECATION") startActivityAndCollapse(intent)
    }
}

/** Invisible: reads the clipboard as soon as it has focus, sends it to the PC and closes. */
class ClipActivity : Activity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        window.setDimAmount(0f)
    }

    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (!hasFocus) return
        val text = when {
            Link.client == null -> "Sem conexão com o PC agora"
            ClipSync.offer(this, force = true) -> "Copiado enviado para o PC"
            else -> "Nada de texto copiado para enviar"
        }
        Toast.makeText(this, text, Toast.LENGTH_SHORT).show()
        finish()
    }
}
