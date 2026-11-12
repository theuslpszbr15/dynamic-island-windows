package io.github.theuslpszbr15.dynamicisland

import android.app.Notification
import android.app.PendingIntent
import android.content.ComponentName
import android.content.Context
import android.provider.Settings
import android.service.notification.NotificationListenerService
import android.service.notification.StatusBarNotification

/**
 * Reads only the Discord app's ongoing notification (the one it shows while you are in a voice call) so the island
 * can show the channel, how long the call has been going and its buttons (sair, mutar…). Every other notification
 * is ignored on arrival: nothing is stored, logged or sent anywhere. Off until the person turns on
 * "Acesso a notificações" for the app (Android asks; on an APK from outside the Play Store, Android 13+ first wants
 * "Permitir configurações restritas" in the app's info screen).
 */
class DiscordListener : NotificationListenerService() {
    override fun onListenerConnected() {
        connected = true
        try { activeNotifications?.forEach(::look) } catch (_: Exception) {}
        if (current == null) Island.phoneCall(null)
    }

    override fun onListenerDisconnected() {
        connected = false
        clear()
    }

    override fun onNotificationPosted(sbn: StatusBarNotification) = look(sbn)

    override fun onNotificationRemoved(sbn: StatusBarNotification) {
        if (sbn.key == current) clear()
    }

    private fun look(sbn: StatusBarNotification) {
        if (sbn.packageName !in PACKAGES) return
        val n = sbn.notification ?: return
        val ongoing = n.flags and Notification.FLAG_ONGOING_EVENT != 0
        val extras = n.extras
        val title = extras?.getCharSequence(Notification.EXTRA_TITLE)?.toString().orEmpty()
        val text = extras?.getCharSequence(Notification.EXTRA_TEXT)?.toString().orEmpty()
        val actions = n.actions.orEmpty().map { it.title?.toString().orEmpty() }
        remember(Seen(System.currentTimeMillis(), n.channelId.orEmpty(), n.category.orEmpty(), ongoing,
            // only the call's own words, never a message's
            if (ongoing) title.take(80) else "", if (ongoing) text.take(80) else "", actions.map { it.take(30) }))
        if (!isCall(ongoing, n.category, n.channelId, n.flags)) {
            if (sbn.key == current) clear()
            return
        }
        current = sbn.key
        intents = n.actions.orEmpty().map { it.actionIntent }
        val since = if (n.`when` in 1..System.currentTimeMillis()) n.`when` else sbn.postTime
        Island.phoneCall(PhoneCall(DiscordEvent.clean(title, 60), DiscordEvent.clean(text, 80), since,
            actions.map { DiscordEvent.clean(it, 24) }))
        IslandOverlay.refresh(this)
    }

    private fun clear() {
        current = null
        intents = emptyList()
        Island.phoneCall(null)
    }

    /** What the last Discord notifications looked like (for "Ver o que o Discord mandou", in memory only). */
    data class Seen(val at: Long, val channel: String, val category: String, val ongoing: Boolean, val title: String,
                    val text: String, val actions: List<String>)

    companion object {
        /** Discord and its beta / alpha builds. */
        private val PACKAGES = setOf("com.discord", "com.discord.beta", "com.discord.alpha")

        @Volatile private var current: String? = null
        @Volatile private var intents: List<PendingIntent?> = emptyList()
        @Volatile var connected = false
            private set
        private val seen = ArrayDeque<Seen>()

        /** The voice call notification: ongoing, and not a download or an upload ("ongoing" alone could be one). */
        fun isCall(ongoing: Boolean, category: String?, channel: String?, flags: Int): Boolean {
            if (!ongoing || flags and Notification.FLAG_GROUP_SUMMARY != 0) return false
            if (category == Notification.CATEGORY_CALL || category == Notification.CATEGORY_SERVICE) return true
            val c = channel.orEmpty().lowercase()
            if (listOf("upload", "download", "media", "message").any { it in c }) return false
            return listOf("call", "voice", "rtc", "ongoing", "connection").any { it in c } || c.isEmpty()
        }

        private fun remember(s: Seen) = synchronized(seen) {
            seen.addFirst(s)
            while (seen.size > 5) seen.removeLast()
        }

        fun lastSeen(): List<Seen> = synchronized(seen) { seen.toList() }

        /** A button of the call notification, by position (the island shows them in the same order). */
        fun press(index: Int): Boolean = try {
            intents.getOrNull(index)?.send()
            true
        } catch (_: Exception) {
            false
        }

        fun enabled(context: Context): Boolean {
            val flat = Settings.Secure.getString(context.contentResolver, "enabled_notification_listeners") ?: return false
            val me = ComponentName(context, DiscordListener::class.java)
            return flat.split(':').any { ComponentName.unflattenFromString(it) == me }
        }

        /** Straight to this app's switch (Android 11+), else the list of apps. */
        fun settingsIntent(context: Context) =
            android.content.Intent(Settings.ACTION_NOTIFICATION_LISTENER_DETAIL_SETTINGS)
                .putExtra(Settings.EXTRA_NOTIFICATION_LISTENER_COMPONENT_NAME,
                    ComponentName(context, DiscordListener::class.java).flattenToString())
                .addFlags(android.content.Intent.FLAG_ACTIVITY_NEW_TASK)
    }
}
