package io.github.theuslpszbr15.dynamicisland

import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.update
import org.json.JSONObject

/** Someone joined or left the Discord call, as the PC's island saw it (sent over the link, same TLS as the rest). */
data class DiscordEvent(
    val joined: Boolean,
    val name: String,
    val channel: String,
    val count: Int,
    /** A small PNG of their profile picture, or null. */
    val avatar: ByteArray?,
    val at: Long,
) {
    override fun equals(other: Any?) = other is DiscordEvent && other.joined == joined && other.name == name &&
        other.channel == channel && other.count == count && other.at == at
    override fun hashCode() = name.hashCode() * 31 + at.hashCode()

    companion object {
        /** The PC sends at most 64 px avatars; anything bigger than this is dropped (the event still shows). */
        const val MAX_AVATAR_BYTES = 24 * 1024

        /** `{"type":"discord","event":"join"|"leave","name","channel","count","avatar"?}`; null for anything off. */
        fun parse(o: JSONObject, now: Long = System.currentTimeMillis()): DiscordEvent? {
            val joined = when (o.optString("event")) {
                "join" -> true
                "leave" -> false
                else -> return null
            }
            val name = clean(o.optString("name"), 64)
            if (name.isEmpty()) return null
            val avatar = o.optString("avatar").takeIf { it.isNotEmpty() && it.length <= MAX_AVATAR_BYTES * 4 / 3 + 4 }?.let {
                try { java.util.Base64.getDecoder().decode(it) } catch (_: IllegalArgumentException) { null }
            }?.takeIf { it.size <= MAX_AVATAR_BYTES && isPng(it) }
            return DiscordEvent(joined, name, clean(o.optString("channel"), 64), o.optInt("count").coerceIn(0, 999), avatar, now)
        }

        private fun isPng(b: ByteArray) = b.size > 8 && b[0] == 0x89.toByte() && b[1] == 'P'.code.toByte() &&
            b[2] == 'N'.code.toByte() && b[3] == 'G'.code.toByte()

        /** One line of plain text: no control or direction characters (a name can't fake another layout). */
        fun clean(raw: String, max: Int): String = raw
            .filter { it >= ' ' && it != '\u007f' && it !in '‎'..'‏' && it !in '‪'..'‮' && it !in '⁦'..'⁩' }
            .trim().take(max)
    }
}

/** The Discord call notification on this phone (read only with "Acesso a notificações" on): channel, server, buttons. */
data class PhoneCall(val title: String, val text: String, val since: Long, val actions: List<String>)

/**
 * A notification of the Claude app on this phone (a decision card waiting, an answer ready): only its title and
 * text, kept in memory while it is in the shade. [at]: when it came.
 */
data class ClaudeNote(val key: String, val title: String, val text: String, val at: Long)

/**
 * Any other app's notification on this phone, as the island tells it: who sent it ([app], with its logo from [pkg]),
 * the title and the text. Held in memory while it is in the shade; never stored or sent anywhere.
 */
data class AppNote(
    val key: String,
    val pkg: String,
    val app: String,
    val title: String,
    val text: String,
    val at: Long,
    val category: String = "",
    /** The notification has a reply field (a message): the island can answer it without opening the app. */
    val canReply: Boolean = false,
)

/** An app's live notification (a delivery, a ride, a download): [percent] -1 when it has no bar. */
data class LiveNote(val key: String, val pkg: String, val app: String, val title: String, val text: String, val percent: Int, val at: Long)

/** How a notification arrives on the island: each one moves its own way, picked from what it is (not random). */
enum class NoteStyle {
    /** Pops in with a bounce: chat messages. */
    Bounce,
    /** Slides in from the side: e-mail and social. */
    Slide,
    /** Wiggles: calls, alarms, reminders, errors. */
    Shake,
    /** Swells and fades, twice: promotions, system, anything quiet. */
    Pulse,
    /** Swings like a bell: events. */
    Swing,
    /** One quick turn: progress and status. */
    Spin;

    companion object {
        /** Same kind of notification, same animation; without a known category, from the app, so an app always moves alike. */
        fun of(category: String, pkg: String): NoteStyle = when (category) {
            "msg" -> Bounce
            "email", "social" -> Slide
            "call", "alarm", "reminder", "err" -> Shake
            "promo", "sys", "recommendation" -> Pulse
            "event" -> Swing
            "progress", "status", "service" -> Spin
            else -> values()[(pkg.hashCode() and 0x7fffffff) % values().size]
        }
    }
}

/** One frame of a notification's arrival: how its logo is drawn. [dx] is in logo widths, [rotation] in degrees. */
data class NoteFrame(val scale: Float = 1f, val rotation: Float = 0f, val dx: Float = 0f, val alpha: Float = 1f, val swingFromTop: Boolean = false)

/** Where the logo is [t] (0..1) into its arrival: every style ends at rest (scale 1, no turn, in place, opaque). */
fun NoteStyle.frame(t: Float): NoteFrame {
    val p = t.coerceIn(0f, 1f)
    val k = 1f - p
    return when (this) {
        NoteStyle.Bounce -> NoteFrame(scale = (1f - k * k * kotlin.math.cos(p * Math.PI.toFloat() * 3f)).coerceAtLeast(0f))
        NoteStyle.Slide -> NoteFrame(dx = -3f * k * k, alpha = p.coerceAtLeast(0.15f))
        NoteStyle.Shake -> NoteFrame(rotation = kotlin.math.sin(p * Math.PI.toFloat() * 8f) * k * 25f)
        NoteStyle.Pulse -> NoteFrame(scale = 1f + 0.35f * kotlin.math.sin(p * Math.PI.toFloat() * 4f) * k, alpha = 0.4f + 0.6f * p)
        NoteStyle.Swing -> NoteFrame(rotation = kotlin.math.sin(p * Math.PI.toFloat() * 5f) * k * 35f, swingFromTop = true)
        NoteStyle.Spin -> NoteFrame(rotation = 360f * (1f - k * k * k), scale = 0.6f + 0.4f * p)
    }
}

/** A transfer in progress or just finished, mirrored from its notification. [percent] -1: waiting. */
data class LiveTransfer(
    val id: String,
    val title: String,
    val text: String,
    val percent: Int,
    val state: State,
    val at: Long,
) {
    enum class State { Running, Done, Failed }
}

/** What the pill says right now: one short line and, under it, what a tap does (or a detail). */
data class Pill(val kind: Kind, val text: String, val hint: String = "", val count: Int = 0) {
    enum class Kind { Idle, Approval, Claude, Offer, Transfer, Discord, Media, Call, Outgoing, Notification, Pending, Timer, Battery, Recording, Ring, Live, Today }
}

/** The song playing in any app (Spotify, YouTube Music…), from its media session. Plain data, tested off the phone. */
data class NowPlaying(
    val app: String,
    val title: String,
    val artist: String,
    val playing: Boolean,
    val positionMs: Long,
    val durationMs: Long,
    /** When [positionMs] was read (elapsedRealtime of the session's playback state, turned into wall clock). */
    val at: Long,
    val speed: Float = 1f,
) {
    /** Where the song is now: the position moves on by itself while playing. */
    fun positionAt(now: Long): Long {
        val moved = if (playing) ((now - at).coerceAtLeast(0) * speed).toLong() else 0L
        val p = positionMs + moved
        return if (durationMs > 0) p.coerceIn(0, durationMs) else p.coerceAtLeast(0)
    }
}

/**
 * What the floating island shows. The link feeds approvals and offers through [Link.state]; this holds the rest:
 * the Discord events from the PC, the phone's own Discord call and the transfers.
 */
object Island {
    /** A Discord event or a finished transfer lights the pill this long. */
    const val FLASH_MS = 6_000L
    /** Finished transfers leave the Transferências tab after this. */
    const val KEEP_DONE_MS = 60_000L
    /** The last joins and leaves, newest first. */
    const val MAX_EVENTS = 20
    const val DUPLICATE_MS = 5_000L
    /** A new notification lights the pill this long (it stays in the card until it leaves the shade). */
    const val NOTE_MS = 7_000L
    const val MAX_NOTES = 20
    /** A Claude notification left unread stops lighting the pill after this (it stays in the shade). */
    const val CLAUDE_MS = 10 * 60_000L

    private val _discord = MutableStateFlow<List<DiscordEvent>>(emptyList())
    val discord: StateFlow<List<DiscordEvent>> = _discord

    private val _call = MutableStateFlow<PhoneCall?>(null)
    val call: StateFlow<PhoneCall?> = _call

    private val _media = MutableStateFlow<NowPlaying?>(null)
    val media: StateFlow<NowPlaying?> = _media

    fun media(playing: NowPlaying?) { _media.value = playing }

    private val _claude = MutableStateFlow<ClaudeNote?>(null)
    val claude: StateFlow<ClaudeNote?> = _claude

    fun claude(note: ClaudeNote?) { _claude.value = note }

    private val _notes = MutableStateFlow<List<AppNote>>(emptyList())
    val notes: StateFlow<List<AppNote>> = _notes

    fun note(n: AppNote) = _notes.update { with(it, n) }

    /** It left the shade. A pinned one stays on the island until the person dismisses it. */
    fun noteGone(key: String) = _notes.update { list -> list.filterNot { it.key == key && key !in _pinned.value } }

    private val _pinned = MutableStateFlow<Set<String>>(emptySet())
    val pinned: StateFlow<Set<String>> = _pinned

    /** Long press: pinned notices stay at the top and survive "Limpar" and the shade. */
    fun togglePin(key: String) = _pinned.update { if (key in it) it - key else it + key }

    /** The person's own dismissal: off the island for good (and unpinned). */
    fun dismissNote(key: String) { _pinned.update { it - key }; noteGone(key) }

    /** Pinned first, each group newest first. */
    fun ordered(list: List<AppNote>, pinned: Set<String>): List<AppNote> = list.sortedByDescending { it.key in pinned }

    private val _live = MutableStateFlow<List<LiveNote>>(emptyList())
    val live: StateFlow<List<LiveNote>> = _live

    /** A live notification arrives or moves on (the same key replaces; no bar -1). */
    fun live(n: LiveNote) = _live.update { list -> (listOf(n) + list.filterNot { it.key == n.key }).take(6) }

    fun liveGone(key: String) = _live.update { list -> list.filterNot { it.key == key } }

    fun clearLive() { _live.value = emptyList() }


    fun clearNotes() { _notes.value = emptyList(); _pinned.value = emptySet() }

    /** Newest first. The same key replaces (an app updating its notification); the same words from the same app, right after, are one. */
    fun with(list: List<AppNote>, n: AppNote): List<AppNote> {
        // an app re-posting the same notification (a progress tick, a re-sort) is not news
        if (list.any { it.key == n.key && it.title == n.title && it.text == n.text }) return list
        val rest = list.filterNot { it.key == n.key }
        val twin = rest.firstOrNull { it.pkg == n.pkg && it.title == n.title && it.text == n.text && n.at - it.at in 0..DUPLICATE_MS }
        return if (twin != null) list else (listOf(n) + rest).take(MAX_NOTES)
    }

    /** The pages of the opened island, in swipe order: always Notificações and Esperando você, and the player while a song is around. */
    fun hubPages(hasMedia: Boolean): List<Pill.Kind> =
        listOf(Pill.Kind.Notification, Pill.Kind.Pending, Pill.Kind.Timer, Pill.Kind.Today) + if (hasMedia) listOf(Pill.Kind.Media) else emptyList()

    /** The page [dir] (+1 or -1) from [from], wrapping round like the PC's. A page that is not in the list starts at the first. */
    fun hubStep(pages: List<Pill.Kind>, from: Pill.Kind, dir: Int): Pill.Kind {
        val i = pages.indexOf(from)
        return if (i < 0) pages.first() else pages[((i + dir) % pages.size + pages.size) % pages.size]
    }

    /** Agent alerts the person already dealt with on the phone: session id to the update they dealt with. */
    private val _handled = MutableStateFlow<Map<String, Long>>(emptyMap())
    val handled: StateFlow<Map<String, Long>> = _handled

    /** The person dismissed or opened this agent's pending line: it stays gone until the session changes again. */
    fun handle(session: Session) = _handled.update { it + (session.id to session.updatedAt) }

    /** Drops what no longer exists, so the map stays small. */
    fun forget(live: List<Session>) = _handled.update { m -> m.filterKeys { id -> live.any { it.id == id } } }

    /**
     * What the agents still wait for: sessions waiting for the person and not yet dealt with. A permission request has
     * its own line, and a session already handled stays out until it changes, so the same thing is never told twice
     * (here, in the shade, and on the PC, which sends the session's new state when it moves on).
     */
    fun pending(sessions: List<Session>, handled: Map<String, Long>): List<Session> =
        sessions.filter { it.state == "waiting" && handled[it.id] != it.updatedAt }

    private val _transfers = MutableStateFlow<List<LiveTransfer>>(emptyList())
    val transfers: StateFlow<List<LiveTransfer>> = _transfers

    fun discordEvent(e: DiscordEvent) = _discord.update { with(it, e) }

    /**
     * The same person joining (or leaving) the same call twice in a few seconds is one notice: the PC can reach the
     * phone over the LAN and the relay at once, and a reconnection repeats what was last said.
     */
    fun with(list: List<DiscordEvent>, e: DiscordEvent): List<DiscordEvent> {
        val same = list.firstOrNull { it.name == e.name }
        if (same != null && same.joined == e.joined && same.channel == e.channel && e.at - same.at in 0..DUPLICATE_MS) return list
        return (listOf(e) + list).take(MAX_EVENTS)
    }

    fun clearDiscord() { _discord.value = emptyList() }

    fun phoneCall(call: PhoneCall?) { _call.value = call }

    fun transfer(id: String, title: String, text: String, percent: Int, state: LiveTransfer.State, now: Long = System.currentTimeMillis()) =
        _transfers.update { list -> Transfers.put(list, LiveTransfer(id, title, text, percent, state, now), now) }

    fun transferGone(id: String) = _transfers.update { list -> list.filterNot { it.id == id && it.state == LiveTransfer.State.Running } }

    /** Plain list logic, tested off the phone. */
    object Transfers {
        fun put(list: List<LiveTransfer>, t: LiveTransfer, now: Long): List<LiveTransfer> =
            (listOf(t) + list.filterNot { it.id == t.id })
                .filter { it.state == LiveTransfer.State.Running || now - it.at <= KEEP_DONE_MS }
                .take(10)
    }

    /**
     * Most urgent first: an agent waiting for an answer, the Claude app waiting for you, a file to accept, something moving, someone who just joined
     * the call, the song playing, the phone's call, what the PC still has to accept. Nothing: the resting pill.
     * The phone is small, so the pill only tells; a tap opens what to do.
     */
    fun pill(
        approvals: Int,
        incoming: Int,
        outgoing: Int,
        transfers: List<LiveTransfer>,
        discord: List<DiscordEvent>,
        call: PhoneCall?,
        media: NowPlaying?,
        now: Long,
        claude: ClaudeNote? = null,
        /** Kinds the person switched off (see [IslandFilter]): skipped, the next one in line shows instead. */
        off: Set<Pill.Kind> = emptySet(),
        notes: List<AppNote> = emptyList(),
        pending: List<Session> = emptyList(),
        timer: TimerState = TimerState(),
        battery: BatteryInfo? = null,
        recording: Recording = Recording(),
        ring: PhoneRing? = null,
        live: List<LiveNote> = emptyList(),
    ): Pill {
        if (approvals > 0 && Pill.Kind.Approval !in off) return Pill(Pill.Kind.Approval,
            if (approvals == 1) "Agente pedindo permissão" else "$approvals pedidos de permissão", "Toque para responder", approvals)
        ring?.takeIf { Pill.Kind.Ring !in off && it.state == PhoneRing.State.Ringing }?.let {
            return Pill(Pill.Kind.Ring, it.who, "Ligação · toque para atender")
        }
        ring?.takeIf { Pill.Kind.Ring !in off && it.state == PhoneRing.State.Missed && now - it.at in 0..PhoneRing.MISSED_MS }?.let {
            return Pill(Pill.Kind.Ring, "Chamada perdida", it.who)
        }
        claude?.takeIf { Pill.Kind.Claude !in off && now - it.at in 0..CLAUDE_MS }?.let {
            return Pill(Pill.Kind.Claude, it.title.ifEmpty { "Claude" }, it.text.ifEmpty { "Toque para abrir no Claude" })
        }
        recording.takeIf { Pill.Kind.Recording !in off && it.any && now - it.since in 0..FLASH_MS }?.let {
            return Pill(Pill.Kind.Recording, it.text, "Algum app está gravando")
        }
        notes.firstOrNull()?.takeIf { Pill.Kind.Notification !in off && now - it.at in 0..NOTE_MS }?.let {
            return Pill(Pill.Kind.Notification, it.title.ifEmpty { it.app }, it.text.ifEmpty { it.app })
        }
        if (incoming > 0 && Pill.Kind.Offer !in off) return Pill(Pill.Kind.Offer,
            if (incoming == 1) "Nova solicitação de envio" else "$incoming solicitações de envio", "Toque para aceitar ou rejeitar", incoming)
        transfers.firstOrNull { Pill.Kind.Transfer !in off && it.state == LiveTransfer.State.Running }?.let {
            return Pill(Pill.Kind.Transfer, it.title, if (it.percent >= 0) "${it.percent}%" else "Aguardando")
        }
        discord.firstOrNull()?.takeIf { Pill.Kind.Discord !in off && now - it.at in 0..FLASH_MS }?.let {
            return Pill(Pill.Kind.Discord, if (it.joined) "${it.name} entrou na call" else "${it.name} saiu da call", it.channel)
        }
        transfers.firstOrNull()?.takeIf { Pill.Kind.Transfer !in off && now - it.at in 0..FLASH_MS }?.let {
            return Pill(Pill.Kind.Transfer, it.title, if (it.state == LiveTransfer.State.Done) "Pronto" else "Falhou")
        }
        battery?.takeIf { Pill.Kind.Battery !in off && it.event != BatteryInfo.Event.None && now - it.at in 0..BatteryWatcher.FLASH_MS }?.let {
            return when (it.event) {
                BatteryInfo.Event.Plugged -> Pill(Pill.Kind.Battery, "Carregando · ${it.percent}%", "Carregador conectado")
                BatteryInfo.Event.Unplugged -> Pill(Pill.Kind.Battery, "Desconectado · ${it.percent}%", "Carregador removido")
                BatteryInfo.Event.Full -> Pill(Pill.Kind.Battery, "Bateria cheia", "100%")
                BatteryInfo.Event.Critical -> Pill(Pill.Kind.Battery, "Bateria em ${it.percent}%", "Conecte o carregador")
                else -> Pill(Pill.Kind.Battery, "Bateria baixa · ${it.percent}%", "Conecte o carregador")
            }
        }
        if (timer.active && Pill.Kind.Timer !in off) return if (timer.finishedAt > 0) Pill(Pill.Kind.Timer, "Acabou!", "Temporizador")
            else Pill(Pill.Kind.Timer, timer.text(now), if (timer.mode == TimerState.Mode.Countdown) (if (timer.paused) "Pausado" else "Temporizador") else (if (timer.paused) "Pausado" else "Cronômetro"))
        live.firstOrNull()?.takeIf { Pill.Kind.Live !in off }?.let {
            return Pill(Pill.Kind.Live, it.title.ifEmpty { it.app }, if (it.percent >= 0) "${it.percent}%" + (if (it.text.isNotEmpty()) " · ${it.text}" else "") else it.text.ifEmpty { it.app }, live.size)
        }
        if (media != null && media.playing && Pill.Kind.Media !in off) return Pill(Pill.Kind.Media, media.title, media.artist)
        if (pending.isNotEmpty() && Pill.Kind.Pending !in off) return Pill(Pill.Kind.Pending,
            if (pending.size == 1) "${pending[0].agent} está esperando você" else "${pending.size} agentes esperando você",
            pending[0].detail.ifEmpty { "Toque para ver" }, pending.size)
        if (call != null && Pill.Kind.Call !in off) return Pill(Pill.Kind.Call, call.title.ifEmpty { "Na call" }, "Call do Discord")
        if (outgoing > 0 && Pill.Kind.Outgoing !in off) return Pill(Pill.Kind.Outgoing, "Esperando o PC aceitar",
            if (outgoing == 1) "1 envio" else "$outgoing envios", outgoing)
        return Pill(Pill.Kind.Idle, "")
    }
}
