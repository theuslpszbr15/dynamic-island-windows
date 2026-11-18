package io.github.theuslpszbr15.dynamicisland

import android.app.Notification
import android.app.PendingIntent
import android.content.ComponentName
import android.content.Context
import android.provider.Settings
import android.service.notification.NotificationListenerService
import android.service.notification.StatusBarNotification

/**
 * Reads notifications for the island. The Claude app's (a decision card waiting, an answer ready): its title and
 * text light the island until it leaves the shade, and a tap opens it like the notification would. And the
 * Discord app's ongoing notification (the one it shows while you are in a voice call) so the island
 * can show the channel, how long the call has been going and its buttons (sair, mutar…). Every other app's notification
 * lights the island with that app's logo and sits in a card until it leaves the shade: only title and text, in memory,
 * never logged or sent anywhere (one switch in Avisos da ilha turns it off). Off until the person turns on
 * "Acesso a notificações" for the app (Android asks; on an APK from outside the Play Store, Android 13+ first wants
 * "Permitir configurações restritas" in the app's info screen).
 */
class DiscordListener : NotificationListenerService() {
    override fun onListenerConnected() {
        connected = true
        // the same permission lets the island read the song playing
        MediaWatcher.start(this)
        try { activeNotifications?.forEach { look(it, fresh = false) } } catch (_: Exception) {}
        if (current == null) Island.phoneCall(null)
    }

    override fun onListenerDisconnected() {
        connected = false
        MediaWatcher.stop()
        clear()
        if (listener === this) listener = null
        forgetNotes()
        forgetClaude()
    }

    override fun onNotificationPosted(sbn: StatusBarNotification) = look(sbn)

    override fun onNotificationRemoved(sbn: StatusBarNotification) {
        if (sbn.key == current) clear()
        if (sbn.key == claudeKey) forgetClaude()
        forgetNote(sbn.key)
    }

    private fun look(sbn: StatusBarNotification, fresh: Boolean = true) {
        if (sbn.packageName in CLAUDE) return claude(sbn)
        if (sbn.packageName !in PACKAGES) return note(sbn, fresh)
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
            return note(sbn, fresh)
        }
        current = sbn.key
        intents = n.actions.orEmpty().map { it.actionIntent }
        val since = if (n.`when` in 1..System.currentTimeMillis()) n.`when` else sbn.postTime
        Island.phoneCall(PhoneCall(DiscordEvent.clean(title, 60), DiscordEvent.clean(text, 80), since,
            actions.map { DiscordEvent.clean(it, 24) }))
        IslandOverlay.refresh(this)
    }

    private fun claude(sbn: StatusBarNotification) {
        val n = sbn.notification ?: return
        // a group's summary repeats its children; ongoing is the app working, not something for you
        if (n.flags and (Notification.FLAG_GROUP_SUMMARY or Notification.FLAG_ONGOING_EVENT) != 0) return
        val extras = n.extras
        val title = DiscordEvent.clean(extras?.getCharSequence(Notification.EXTRA_TITLE)?.toString().orEmpty(), 60)
        val text = DiscordEvent.clean((extras?.getCharSequence(Notification.EXTRA_BIG_TEXT)
            ?: extras?.getCharSequence(Notification.EXTRA_TEXT))?.toString().orEmpty().replace('\n', ' '), 120)
        if (title.isEmpty() && text.isEmpty()) return
        claudeKey = sbn.key
        claudeIntent = n.contentIntent
        claudeSeen = ClaudeSeen(System.currentTimeMillis(), n.channelId.orEmpty(), n.category.orEmpty(),
            n.actions.orEmpty().map { it.title?.toString().orEmpty().take(30) })
        Island.claude(ClaudeNote(sbn.key, title, text, sbn.postTime.takeIf { it > 0 } ?: System.currentTimeMillis()))
        listener = this
        IslandOverlay.refresh(this)
    }

    /** Any other app's notification: it lights the island with that app's logo and waits in the card until it leaves the shade. */
    private fun note(sbn: StatusBarNotification, fresh: Boolean) {
        val n = sbn.notification ?: return
        if (sbn.packageName == packageName || !isNote(n.flags, n.category)) return
        val extras = n.extras
        val title = DiscordEvent.clean(extras?.getCharSequence(Notification.EXTRA_TITLE)?.toString().orEmpty(), 60)
        val text = DiscordEvent.clean((extras?.getCharSequence(Notification.EXTRA_BIG_TEXT)
            ?: extras?.getCharSequence(Notification.EXTRA_TEXT))?.toString().orEmpty().replace('\n', ' '), 160)
        if (title.isEmpty() && text.isEmpty()) return
        AppIcons.load(this, sbn.packageName, n)
        val at = if (fresh) System.currentTimeMillis() else sbn.postTime.takeIf { it > 0 } ?: 0L
        n.contentIntent?.let { synchronized(noteIntents) { noteIntents[sbn.key] = it; if (noteIntents.size > 60) noteIntents.remove(noteIntents.keys.first()) } }
        listener = this
        Island.note(AppNote(sbn.key, sbn.packageName, AppIcons.label(this, sbn.packageName), title, text, at, n.category.orEmpty()))
        IslandOverlay.refresh(this)
    }

    private fun clear() {
        current = null
        intents = emptyList()
        Island.phoneCall(null)
    }

    /** How the last Claude notification was sent (channel, category, its buttons), for checking; never its text. */
    data class ClaudeSeen(val at: Long, val channel: String, val category: String, val actions: List<String>)

    /** What the last Discord notifications looked like (for "Ver o que o Discord mandou", in memory only). */
    data class Seen(val at: Long, val channel: String, val category: String, val ongoing: Boolean, val title: String,
                    val text: String, val actions: List<String>)

    companion object {
        /** Discord and its beta / alpha builds. */
        private val PACKAGES = setOf("com.discord", "com.discord.beta", "com.discord.alpha")
        /** The Claude app. */
        private val CLAUDE = setOf("com.anthropic.claude")

        private val noteIntents = LinkedHashMap<String, PendingIntent>()

        private fun forgetNote(key: String) {
            synchronized(noteIntents) { noteIntents.remove(key) }
            Island.noteGone(key)
        }

        private fun forgetNotes() {
            synchronized(noteIntents) { noteIntents.clear() }
            Island.clearNotes()
        }

        /** What a notification may be told as a notice: not a group's summary, not an app working (ongoing), not a player. */
        fun isNote(flags: Int, category: String?): Boolean =
            flags and (Notification.FLAG_GROUP_SUMMARY or Notification.FLAG_ONGOING_EVENT or Notification.FLAG_FOREGROUND_SERVICE) == 0 &&
                category != Notification.CATEGORY_TRANSPORT

        /** Opens what the notification is about, as a tap in the shade would, and takes it off the island and out of the shade. */
        fun openNote(context: Context, key: String): Boolean {
            val intent = synchronized(noteIntents) { noteIntents[key] }
            val ok = try {
                if (intent == null) false else {
                    val options = if (android.os.Build.VERSION.SDK_INT >= 34) android.app.ActivityOptions.makeBasic()
                        .setPendingIntentBackgroundActivityStartMode(android.app.ActivityOptions.MODE_BACKGROUND_ACTIVITY_START_ALLOWED)
                        .toBundle() else null
                    intent.send(context, 0, null, null, null, null, options)
                    true
                }
            } catch (_: Exception) { false }
            dismissNote(key)
            return ok
        }

        /** Takes it off the island and out of the shade. */
        fun dismissNote(key: String) {
            forgetNote(key)
            try { listener?.cancelNotification(key) } catch (_: Exception) {}
        }

        @Volatile private var claudeKey: String? = null
        @Volatile private var claudeIntent: PendingIntent? = null
        @Volatile private var listener: DiscordListener? = null
        @Volatile var claudeSeen: ClaudeSeen? = null
            private set

        private fun forgetClaude() {
            claudeKey = null
            claudeIntent = null
            Island.claude(null)
        }

        /** Opens what the Claude notification is about (the conversation), as tapping it in the shade would. */
        fun openClaude(context: Context): Boolean {
            val intent = claudeIntent
            val ok = try {
                if (intent != null) {
                    // Android 14+: the island is on screen, so it may open the app, but only if it says so
                    val options = if (android.os.Build.VERSION.SDK_INT >= 34) android.app.ActivityOptions.makeBasic()
                        .setPendingIntentBackgroundActivityStartMode(android.app.ActivityOptions.MODE_BACKGROUND_ACTIVITY_START_ALLOWED)
                        .toBundle() else null
                    intent.send(context, 0, null, null, null, null, options)
                    true
                } else {
                    context.packageManager.getLaunchIntentForPackage(CLAUDE.first())
                        ?.addFlags(android.content.Intent.FLAG_ACTIVITY_NEW_TASK)?.let(context::startActivity) != null
                }
            } catch (_: Exception) {
                false
            }
            dismissClaude()
            return ok
        }

        /** Takes it off the island and out of the shade. */
        fun dismissClaude() {
            val key = claudeKey
            forgetClaude()
            if (key != null) try { listener?.cancelNotification(key) } catch (_: Exception) {}
        }

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

        /** Whether this build declares the listener at all (the sideloaded APK doesn't, see AndroidManifest). */
        fun available(context: Context): Boolean = try {
            context.packageManager.getServiceInfo(ComponentName(context, DiscordListener::class.java), 0)
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
