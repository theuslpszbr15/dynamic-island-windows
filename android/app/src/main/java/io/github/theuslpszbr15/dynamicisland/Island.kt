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
    enum class Kind { Idle, Approval, Offer, Transfer, Discord, Media, Call, Outgoing }
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

    private val _discord = MutableStateFlow<List<DiscordEvent>>(emptyList())
    val discord: StateFlow<List<DiscordEvent>> = _discord

    private val _call = MutableStateFlow<PhoneCall?>(null)
    val call: StateFlow<PhoneCall?> = _call

    private val _media = MutableStateFlow<NowPlaying?>(null)
    val media: StateFlow<NowPlaying?> = _media

    fun media(playing: NowPlaying?) { _media.value = playing }

    private val _transfers = MutableStateFlow<List<LiveTransfer>>(emptyList())
    val transfers: StateFlow<List<LiveTransfer>> = _transfers

    fun discordEvent(e: DiscordEvent) = _discord.update { (listOf(e) + it).take(MAX_EVENTS) }

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
     * Most urgent first: an agent waiting for an answer, a file to accept, something moving, someone who just joined
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
    ): Pill {
        if (approvals > 0) return Pill(Pill.Kind.Approval,
            if (approvals == 1) "Agente pedindo permissão" else "$approvals pedidos de permissão", "Toque para responder", approvals)
        if (incoming > 0) return Pill(Pill.Kind.Offer,
            if (incoming == 1) "Nova solicitação de envio" else "$incoming solicitações de envio", "Toque para aceitar ou rejeitar", incoming)
        transfers.firstOrNull { it.state == LiveTransfer.State.Running }?.let {
            return Pill(Pill.Kind.Transfer, it.title, if (it.percent >= 0) "${it.percent}%" else "Aguardando")
        }
        discord.firstOrNull()?.takeIf { now - it.at in 0..FLASH_MS }?.let {
            return Pill(Pill.Kind.Discord, if (it.joined) "${it.name} entrou na call" else "${it.name} saiu da call", it.channel)
        }
        transfers.firstOrNull()?.takeIf { now - it.at in 0..FLASH_MS }?.let {
            return Pill(Pill.Kind.Transfer, it.title, if (it.state == LiveTransfer.State.Done) "Pronto" else "Falhou")
        }
        if (media != null && media.playing) return Pill(Pill.Kind.Media, media.title, media.artist)
        if (call != null) return Pill(Pill.Kind.Call, call.title.ifEmpty { "Na call" }, "Call do Discord")
        if (outgoing > 0) return Pill(Pill.Kind.Outgoing, "Esperando o PC aceitar",
            if (outgoing == 1) "1 envio" else "$outgoing envios", outgoing)
        return Pill(Pill.Kind.Idle, "")
    }
}
