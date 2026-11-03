package io.github.theuslpszbr15.dynamicisland

import android.content.Context
import androidx.core.app.NotificationManagerCompat
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.update
import java.util.concurrent.Executors

enum class LinkStatus { Unpaired, Connecting, Connected, Offline, AuthFailed }

data class LinkUi(
    val pairing: Pairing? = null,
    val status: LinkStatus = LinkStatus.Unpaired,
    val desktop: String = "",
    val message: String? = null,
    val sessions: List<Session> = emptyList(),
    val approvals: List<Approval> = emptyList(),
    val tray: List<TrayItem> = emptyList(),
    /** Which tab opens first (screenshots). */
    val initialTab: Int = 0,
)

/** What the UI and the notifications show; the service feeds it, the UI answers through it. */
object Link {
    /** The PC stops waiting after 54 s; the phone stops offering a little before. */
    const val APPROVAL_WINDOW_MS = 50_000L

    private val _state = MutableStateFlow(LinkUi())
    val state: StateFlow<LinkUi> = _state

    @Volatile var client: LinkClient? = null
    private val io = Executors.newSingleThreadExecutor()

    fun update(change: (LinkUi) -> LinkUi) = _state.update(change)

    fun pruneExpired(context: Context) {
        val now = System.currentTimeMillis()
        val expired = _state.value.approvals.filter { now - it.receivedAt > APPROVAL_WINDOW_MS }
        if (expired.isEmpty()) return
        update { s -> s.copy(approvals = s.approvals - expired.toSet()) }
        expired.forEach { Notifications.cancelApproval(context, it.fingerprint) }
    }

    /** Allow must come through the screen lock check (see MainActivity); Deny needs nothing. */
    fun decide(context: Context, fingerprint: String, allow: Boolean) {
        val approval = _state.value.approvals.firstOrNull { it.fingerprint == fingerprint } ?: return
        if (allow && approval.truncated) return
        update { s -> s.copy(approvals = s.approvals.filterNot { it.fingerprint == fingerprint }) }
        NotificationManagerCompat.from(context).cancel(Notifications.approvalId(fingerprint))
        val c = client ?: return
        io.execute {
            try { c.decide(fingerprint, allow) } catch (_: Exception) {}
        }
    }

    fun open(item: TrayItem) {
        val c = client ?: return
        io.execute {
            try { c.open(item.id) } catch (_: Exception) {}
        }
    }

    fun ping() {
        val c = client ?: return
        io.execute {
            try { c.ping() } catch (_: Exception) {}
        }
    }
}
