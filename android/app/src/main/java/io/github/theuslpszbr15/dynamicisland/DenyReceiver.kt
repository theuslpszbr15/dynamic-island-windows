package io.github.theuslpszbr15.dynamicisland

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent

/** "Negar" and "Parar de procurar" straight from the notifications. Allowing always goes through the app and the screen lock. */
class DenyReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        if (intent.action == ACTION_PAUSE) {
            Link.pause(context)
            return
        }
        val fingerprint = intent.getStringExtra(Notifications.EXTRA_FINGERPRINT) ?: return
        Link.decide(context, fingerprint, allow = false)
    }

    companion object {
        const val ACTION_PAUSE = "io.github.theuslpszbr15.dynamicisland.PAUSE"
    }
}
