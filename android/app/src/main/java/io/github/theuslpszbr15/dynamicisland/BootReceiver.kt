package io.github.theuslpszbr15.dynamicisland

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent

/** Back on the link after the phone restarts (or the app is updated), without opening the app first. */
class BootReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        if (intent.action != Intent.ACTION_BOOT_COMPLETED && intent.action != Intent.ACTION_MY_PACKAGE_REPLACED) return
        if (PairingStore.isPaused(context) || PairingStore.load(context) == null) return
        try {
            LinkService.start(context)
        } catch (_: Exception) {
            // the system refused a background start; the link comes back the next time the app opens
        }
    }
}
