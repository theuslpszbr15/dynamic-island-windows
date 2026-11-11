package io.github.theuslpszbr15.dynamicisland

import android.content.Context
import android.os.Handler
import android.os.Looper
import android.widget.Toast
import androidx.core.app.NotificationManagerCompat
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.update
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.Executors

enum class LinkStatus { Unpaired, Connecting, Connected, Offline, AuthFailed, Paused }

data class LinkUi(
    val pairing: Pairing? = null,
    val status: LinkStatus = LinkStatus.Unpaired,
    val desktop: String = "",
    val message: String? = null,
    val sessions: List<Session> = emptyList(),
    val approvals: List<Approval> = emptyList(),
    val tray: List<TrayItem> = emptyList(),
    /** Files the PC offered and the person hasn't answered yet. */
    val files: List<FileOffer> = emptyList(),
    /** Connected through the relay (away from the PC's network). */
    val viaRelay: Boolean = false,
    /** Notifications are off, so requests would arrive silently (the app shows how to turn them on). */
    val notificationsOff: Boolean = false,
    /** Which tab opens first (screenshots). */
    val initialTab: Int = 0,
)

/** What the UI and the notifications show; the service feeds it, the UI answers through it. */
object Link {
    /** The PC stops waiting after 54 s; the phone stops offering a little before. */
    const val APPROVAL_WINDOW_MS = 50_000L
    /** How long the PC has to say it applied an answer (it sends approvalResolved right away). */
    const val CONFIRM_WAIT_MS = 6_000L

    private val _state = MutableStateFlow(LinkUi())
    val state: StateFlow<LinkUi> = _state

    @Volatile var client: LinkClient? = null
    private val io = Executors.newSingleThreadExecutor()
    /** Answers sent and not yet confirmed by the PC: fingerprint → allow. */
    private val sent = ConcurrentHashMap<String, Boolean>()
    private val main by lazy { Handler(Looper.getMainLooper()) }

    fun update(change: (LinkUi) -> LinkUi) = _state.update(change)

    /** Stops looking for the PC (and stays stopped after a restart) but keeps the pairing. */
    fun pause(context: Context) {
        PairingStore.setPaused(context, true)
        LinkService.stop(context)
        _state.value.approvals.forEach { Notifications.cancelApproval(context, it.fingerprint) }
        _state.value.files.forEach { Notifications.cancelFile(context, it.id) }
        update { it.copy(status = LinkStatus.Paused, message = null, approvals = emptyList(), sessions = emptyList(), tray = emptyList(),
            files = emptyList()) }
    }

    fun resume(context: Context) {
        PairingStore.setPaused(context, false)
        update { it.copy(status = LinkStatus.Connecting, message = null) }
        LinkService.start(context)
    }

    fun pruneExpired(context: Context) {
        val now = System.currentTimeMillis()
        val stale = _state.value.files.filter { now - it.receivedAt > FileOffer.WINDOW_MS }
        if (stale.isNotEmpty()) {
            update { s -> s.copy(files = s.files - stale.toSet()) }
            stale.forEach { Notifications.cancelFile(context, it.id) }
        }
        val expired = _state.value.approvals.filter { now - it.receivedAt > APPROVAL_WINDOW_MS }
        if (expired.isEmpty()) return
        update { s -> s.copy(approvals = s.approvals - expired.toSet()) }
        expired.forEach { Notifications.cancelApproval(context, it.fingerprint) }
    }

    /** Allow must come through the screen lock check (see MainActivity); Deny needs nothing. */
    fun decide(context: Context, fingerprint: String, allow: Boolean) {
        val app = context.applicationContext
        val approval = _state.value.approvals.firstOrNull { it.fingerprint == fingerprint }
        if (approval == null) {
            NotificationManagerCompat.from(app).cancel(Notifications.approvalId(fingerprint))
            tell(app, "Esse pedido já expirou ou foi respondido no PC.")
            return
        }
        if (allow && approval.truncated) return
        update { s -> s.copy(approvals = s.approvals.filterNot { it.fingerprint == fingerprint }) }
        NotificationManagerCompat.from(app).cancel(Notifications.approvalId(fingerprint))
        val c = client ?: return tell(app, "Sem conexão com o PC: a resposta não chegou. Responda no terminal.")
        sent[fingerprint] = allow
        io.execute {
            try {
                c.decide(fingerprint, allow)
            } catch (_: Exception) {
                if (sent.remove(fingerprint) != null) tell(app, "A conexão caiu: a resposta não chegou ao PC. Responda no terminal.")
                return@execute
            }
            main.postDelayed({
                if (sent.remove(fingerprint) != null) tell(app, "O PC não confirmou a resposta (talvez o pedido tenha expirado). Confira no terminal.")
            }, CONFIRM_WAIT_MS)
        }
    }

    /** The PC dropped a request; if it was one we answered, our answer is what closed it. */
    fun confirmed(context: Context, fingerprint: String) {
        val allow = sent.remove(fingerprint) ?: return
        tell(context.applicationContext, if (allow) "Permitido no PC" else "Negado no PC")
    }

    private fun tell(context: Context, text: String) {
        main.post { Toast.makeText(context, text, Toast.LENGTH_SHORT).show() }
    }

    fun open(item: TrayItem) {
        val c = client ?: return
        io.execute {
            try { c.open(item.id) } catch (_: Exception) {}
        }
    }

    /** "Aceitar" on the notification (the phone must be unlocked): download on a connection of its own, the same
     * way the link reaches the PC right now (Wi-Fi directly, or the relay). */
    fun acceptFile(context: Context, id: String) {
        val app = context.applicationContext
        val offer = _state.value.files.firstOrNull { it.id == id }
        Notifications.cancelFile(app, id)
        if (offer == null) return tell(app, "Esse envio já expirou. Mande de novo pelo PC.")
        val c = client
        val pairing = _state.value.pairing
        if (c == null || pairing == null) return tell(app, "Sem conexão com o PC agora. Mande de novo quando ligar.")
        update { s -> s.copy(files = s.files.filterNot { it.id == id }) }
        FileReceiver.receive(app, pairing, c.route, offer)
    }

    /** Short texts from the PC, by id, until "Copiar" (kept only in memory, at most a few). */
    private val texts = object : LinkedHashMap<String, String>() {
        override fun removeEldestEntry(eldest: MutableMap.MutableEntry<String, String>?) = size > 5
    }

    fun textFromPc(context: Context, id: String, text: String) {
        synchronized(texts) { texts[id] = text }
        Notifications.showText(context, id, text)
    }

    fun copyText(context: Context, id: String) {
        val app = context.applicationContext
        Notifications.cancelFile(app, id)
        val text = synchronized(texts) { texts.remove(id) } ?: return tell(app, "Esse texto já não está mais aqui.")
        FileReceiver.copy(app, text)
        tell(app, "Texto copiado")
    }

    fun declineFile(context: Context, id: String) {
        Notifications.cancelFile(context.applicationContext, id)
        update { s -> s.copy(files = s.files.filterNot { it.id == id }) }
        val c = client ?: return
        io.execute {
            try { c.declineFile(id) } catch (_: Exception) {}
        }
    }

    fun fileDone(id: String, ok: Boolean) {
        val c = client ?: return
        io.execute {
            try { c.fileDone(id, ok) } catch (_: Exception) {}
        }
    }

    fun ping() {
        val c = client ?: return
        io.execute {
            try { c.ping() } catch (_: Exception) {}
        }
    }
}
