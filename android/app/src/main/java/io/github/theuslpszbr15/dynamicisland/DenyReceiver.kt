package io.github.theuslpszbr15.dynamicisland

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent

/** "Negar" straight from the notification. Allowing always goes through the app and the screen lock. */
class DenyReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        val fingerprint = intent.getStringExtra(Notifications.EXTRA_FINGERPRINT) ?: return
        Link.decide(context, fingerprint, allow = false)
    }
}
