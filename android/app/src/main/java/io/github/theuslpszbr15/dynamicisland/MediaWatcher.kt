package io.github.theuslpszbr15.dynamicisland

import android.content.ComponentName
import android.content.Context
import android.graphics.Bitmap
import android.media.MediaMetadata
import android.media.session.MediaController
import android.media.session.MediaSessionManager
import android.media.session.PlaybackState
import android.os.Handler
import android.os.Looper
import android.os.SystemClock
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow

/**
 * The song playing in any app (Spotify, YouTube Music, Deezer…) through Android's media sessions, which only an
 * app with notification access may read (the same switch as the Discord call). Title, artist, cover, position and
 * the play / pause / skip buttons; nothing leaves the phone.
 */
object MediaWatcher {
    private val main by lazy { Handler(Looper.getMainLooper()) }
    private var manager: MediaSessionManager? = null
    private var controller: MediaController? = null
    private var component: ComponentName? = null
    private val _art = MutableStateFlow<Bitmap?>(null)
    /** The cover of [Island.media], scaled down. */
    val art: StateFlow<Bitmap?> = _art

    private val sessions = MediaSessionManager.OnActiveSessionsChangedListener { list -> pick(list.orEmpty()) }

    private val callback = object : MediaController.Callback() {
        override fun onPlaybackStateChanged(state: PlaybackState?) = publish()
        override fun onMetadataChanged(metadata: MediaMetadata?) = publish(newArt = true)
        override fun onSessionDestroyed() = main.post { pick(currentSessions()) }.let { }
    }

    /** From the notification listener once it is connected (the permission is in place then). */
    fun start(context: Context) = main.post {
        if (manager != null) return@post
        val m = context.getSystemService(MediaSessionManager::class.java) ?: return@post
        val me = ComponentName(context, DiscordListener::class.java)
        try {
            m.addOnActiveSessionsChangedListener(sessions, me, main)
            manager = m
            component = me
            pick(m.getActiveSessions(me))
        } catch (_: SecurityException) {
            manager = null
        }
    }.let { }

    fun stop() = main.post {
        try { manager?.removeOnActiveSessionsChangedListener(sessions) } catch (_: Exception) {}
        manager = null
        use(null)
    }.let { }

    private fun currentSessions(): List<MediaController> = try {
        manager?.getActiveSessions(component).orEmpty()
    } catch (_: Exception) { emptyList() }

    /** The one playing, else the most recent (Android lists them newest first). */
    private fun pick(list: List<MediaController>) {
        val playing = list.firstOrNull { it.playbackState?.state == PlaybackState.STATE_PLAYING }
        use(playing ?: list.firstOrNull())
    }

    private fun use(c: MediaController?) {
        if (c?.sessionToken == controller?.sessionToken && c != null) return
        controller?.unregisterCallback(callback)
        controller = c
        c?.registerCallback(callback, main)
        publish(newArt = true)
    }

    private fun publish(newArt: Boolean = false) {
        val c = controller
        val meta = c?.metadata
        val state = c?.playbackState
        val title = meta?.getString(MediaMetadata.METADATA_KEY_TITLE).orEmpty()
        if (c == null || meta == null || title.isEmpty()) {
            Island.media(null)
            _art.value = null
            return
        }
        // the session's position is from its own clock; turn it into wall-clock time once
        val read = state?.lastPositionUpdateTime?.takeIf { it > 0 } ?: SystemClock.elapsedRealtime()
        val at = System.currentTimeMillis() - (SystemClock.elapsedRealtime() - read)
        Island.media(NowPlaying(
            app = c.packageName.orEmpty(),
            title = DiscordEvent.clean(title, 80),
            artist = DiscordEvent.clean(meta.getString(MediaMetadata.METADATA_KEY_ARTIST)
                ?: meta.getString(MediaMetadata.METADATA_KEY_ALBUM_ARTIST).orEmpty(), 80),
            playing = state?.state == PlaybackState.STATE_PLAYING,
            positionMs = state?.position ?: 0L,
            durationMs = meta.getLong(MediaMetadata.METADATA_KEY_DURATION).coerceAtLeast(0),
            at = at,
            speed = state?.playbackSpeed?.takeIf { it > 0f } ?: 1f,
        ))
        if (newArt) {
            val bitmap = meta.getBitmap(MediaMetadata.METADATA_KEY_ALBUM_ART) ?: meta.getBitmap(MediaMetadata.METADATA_KEY_ART)
                ?: meta.getBitmap(MediaMetadata.METADATA_KEY_DISPLAY_ICON)
            _art.value = bitmap?.let { small(it) }
        }
    }

    private fun small(b: Bitmap): Bitmap {
        val max = 192
        if (b.width <= max && b.height <= max) return b
        val scale = max.toFloat() / maxOf(b.width, b.height)
        return try { Bitmap.createScaledBitmap(b, (b.width * scale).toInt().coerceAtLeast(1), (b.height * scale).toInt().coerceAtLeast(1), true) }
        catch (_: Exception) { b }
    }

    fun playPause() {
        val c = controller ?: return
        if (c.playbackState?.state == PlaybackState.STATE_PLAYING) c.transportControls.pause() else c.transportControls.play()
    }

    fun next() { controller?.transportControls?.skipToNext() }
    fun previous() { controller?.transportControls?.skipToPrevious() }
    fun seek(ms: Long) { controller?.transportControls?.seekTo(ms) }

    /** The song's own screen in its app (what a tap on the player in the shade opens); null if the app gave none. Android 14+ must be told the island may start it. */
    private fun openSession(context: Context, c: MediaController): Unit? {
        val intent = c.sessionActivity ?: return null
        val options = if (android.os.Build.VERSION.SDK_INT >= 34) android.app.ActivityOptions.makeBasic()
            .setPendingIntentBackgroundActivityStartMode(android.app.ActivityOptions.MODE_BACKGROUND_ACTIVITY_START_ALLOWED)
            .toBundle() else null
        intent.send(context, 0, null, null, null, null, options)
        return Unit
    }

    /** Opens the app that is playing, on the song's screen when the app has one. */
    fun openApp(context: Context) {
        val c = controller ?: return
        try {
            openSession(context, c) ?: context.packageManager.getLaunchIntentForPackage(c.packageName)
                ?.addFlags(android.content.Intent.FLAG_ACTIVITY_NEW_TASK)?.let(context::startActivity)
        } catch (_: Exception) {}
    }
}
