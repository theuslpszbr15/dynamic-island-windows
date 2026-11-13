package io.github.theuslpszbr15.dynamicisland

import android.accessibilityservice.AccessibilityService
import android.content.ComponentName
import android.content.Context
import android.content.Intent
import android.provider.Settings
import android.view.accessibility.AccessibilityEvent

/**
 * Optional, turned on by the person in Android's Acessibilidade screen. An app's ordinary overlay always sits under
 * the status bar, which then takes the taps; an accessibility overlay is the only window an app can put above it.
 * With this on, the island is drawn that way: it can sit on the camera, still take taps there, and be docked to the
 * top edge. It reads nothing: it asks for no events it uses and no window content (see island_accessibility.xml).
 */
class IslandAccessibility : AccessibilityService() {
    override fun onServiceConnected() = IslandOverlay.accessibility(this, this)

    override fun onAccessibilityEvent(event: AccessibilityEvent?) {}

    override fun onInterrupt() {}

    override fun onUnbind(intent: Intent?): Boolean {
        IslandOverlay.accessibility(this, null)
        return super.onUnbind(intent)
    }

    override fun onDestroy() {
        IslandOverlay.accessibility(this, null)
        super.onDestroy()
    }

    companion object {
        /** The person turned it on in Android's settings (it may still be starting). */
        fun enabled(context: Context): Boolean {
            val list = Settings.Secure.getString(context.contentResolver, Settings.Secure.ENABLED_ACCESSIBILITY_SERVICES)
                ?: return false
            val me = ComponentName(context, IslandAccessibility::class.java)
            return list.split(':').any { ComponentName.unflattenFromString(it) == me }
        }

        fun settingsIntent(): Intent = Intent(Settings.ACTION_ACCESSIBILITY_SETTINGS).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
    }
}
