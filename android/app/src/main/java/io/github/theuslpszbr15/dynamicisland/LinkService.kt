package io.github.theuslpszbr15.dynamicisland

import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.Build
import android.os.IBinder
import androidx.core.app.NotificationManagerCompat
import androidx.core.content.ContextCompat
import java.util.concurrent.Executors
import java.util.concurrent.ScheduledExecutorService
import java.util.concurrent.TimeUnit

/** Keeps the link to the PC open in the background so requests arrive as notifications. */
class LinkService : Service() {
    @Volatile private var running = false
    @Volatile private var attempt = 0
    private var worker: Thread? = null
    private var ticker: ScheduledExecutorService? = null

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        Notifications.createChannels(this)
        startForeground(Notifications.LINK_ID, Notifications.link(this, "Procurando o PC…"),
            ServiceInfo.FOREGROUND_SERVICE_TYPE_CONNECTED_DEVICE)
        if (worker == null) {
            running = true
            worker = Thread(::loop, "link").apply { start() }
            ticker = Executors.newSingleThreadScheduledExecutor().apply {
                scheduleWithFixedDelay({ Link.ping() }, LinkClient.PING_EVERY_MS, LinkClient.PING_EVERY_MS, TimeUnit.MILLISECONDS)
                scheduleWithFixedDelay({ Link.pruneExpired(this@LinkService) }, 1, 1, TimeUnit.SECONDS)
            }
        }
        return START_STICKY
    }

    private fun loop() {
        while (running) {
            if (PairingStore.isPaused(this)) break
            val pairing = PairingStore.load(this) ?: break
            Link.update { it.copy(pairing = pairing, status = LinkStatus.Connecting, message = null) }
            val client = LinkClient(pairing, "${Build.MANUFACTURER} ${Build.MODEL}".take(40))
            Link.client = client
            try {
                client.run(listener)
                if (!running) break
            } catch (e: AuthException) {
                Link.update { it.copy(status = LinkStatus.AuthFailed, message = e.message) }
                break
            } catch (e: Exception) {
                // stopped on purpose (pause / unpair): the UI already says so
                if (!running) break
                Link.update { it.copy(status = LinkStatus.Offline, message = e.message) }
                // The PC may just have a new address: ask the network before waiting to retry.
                if (rediscover(pairing)) {
                    attempt = 0
                    continue
                }
            } finally {
                Link.client = null
                client.close()
                // nothing can be answered without the PC
                Link.state.value.approvals.forEach { Notifications.cancelApproval(this, it.fingerprint) }
                Link.update { it.copy(approvals = emptyList(), sessions = emptyList(), tray = emptyList()) }
            }
            showStatus("Sem conexão com ${pairing.name}. Tentando de novo…")
            val wait = BACKOFF_MS[minOf(attempt++, BACKOFF_MS.lastIndex)]
            try { Thread.sleep(wait) } catch (_: InterruptedException) { break }
        }
        stopSelf()
    }

    /** True when the paired PC answered from a different address (saved, so the next try goes there). */
    private fun rediscover(pairing: Pairing): Boolean {
        val found = try { Discovery.find(pairing) } catch (_: Exception) { null } ?: return false
        if (found.host == pairing.host && found.port == pairing.port) return false
        PairingStore.save(this, found)
        return true
    }

    private val listener = object : LinkClient.Listener {
        override fun onWelcome(desktop: String) {
            attempt = 0
            Link.update { it.copy(status = LinkStatus.Connected, desktop = desktop, message = null) }
            showStatus("Ligado a $desktop")
        }

        override fun onSessions(sessions: List<Session>) {
            val before = Link.state.value.sessions
            Link.update { it.copy(sessions = sessions) }
            SessionAlerts.settled(before, sessions).forEach { Notifications.cancelSessionAlert(this@LinkService, it) }
            SessionAlerts.changes(before, sessions).forEach { Notifications.showSessionAlert(this@LinkService, it) }
        }

        override fun onTray(items: List<TrayItem>) = Link.update { it.copy(tray = items) }

        override fun onApproval(approval: Approval) {
            Link.update { s -> s.copy(approvals = s.approvals.filterNot { it.fingerprint == approval.fingerprint } + approval) }
            Notifications.showApproval(this@LinkService, approval)
        }

        override fun onResolved(fingerprint: String) {
            Link.update { s -> s.copy(approvals = s.approvals.filterNot { it.fingerprint == fingerprint }) }
            Notifications.cancelApproval(this@LinkService, fingerprint)
            Link.confirmed(this@LinkService, fingerprint)
        }
    }

    private fun showStatus(text: String) =
        NotificationManagerCompat.from(this).notify(Notifications.LINK_ID, Notifications.link(this, text))

    override fun onDestroy() {
        running = false
        ticker?.shutdownNow()
        Link.client?.close()
        worker?.interrupt()
        worker = null
        super.onDestroy()
    }

    companion object {
        private val BACKOFF_MS = longArrayOf(2_000, 5_000, 10_000, 30_000, 60_000)

        fun start(context: Context) =
            ContextCompat.startForegroundService(context, Intent(context, LinkService::class.java))

        fun stop(context: Context) = context.stopService(Intent(context, LinkService::class.java))
    }
}
