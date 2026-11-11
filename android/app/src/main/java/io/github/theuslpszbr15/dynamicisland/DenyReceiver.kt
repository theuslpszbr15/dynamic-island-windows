package io.github.theuslpszbr15.dynamicisland

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent

/** "Negar", "Parar de procurar" and the file offer's "Aceitar" / "Recusar" straight from the notifications.
 * Allowing a command always goes through the app and the screen lock; accepting a file needs the phone unlocked. */
class DenyReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        when (intent.action) {
            ACTION_PAUSE -> return Link.pause(context)
            ACTION_ACCEPT_FILE -> return intent.getStringExtra(Notifications.EXTRA_FILE_ID)?.let { Link.acceptFile(context, it) } ?: Unit
            ACTION_DECLINE_FILE -> return intent.getStringExtra(Notifications.EXTRA_FILE_ID)?.let { Link.declineFile(context, it) } ?: Unit
        }
        val fingerprint = intent.getStringExtra(Notifications.EXTRA_FINGERPRINT) ?: return
        Link.decide(context, fingerprint, allow = false)
    }

    companion object {
        const val ACTION_PAUSE = "io.github.theuslpszbr15.dynamicisland.PAUSE"
        const val ACTION_ACCEPT_FILE = "io.github.theuslpszbr15.dynamicisland.ACCEPT_FILE"
        const val ACTION_DECLINE_FILE = "io.github.theuslpszbr15.dynamicisland.DECLINE_FILE"
    }
}
