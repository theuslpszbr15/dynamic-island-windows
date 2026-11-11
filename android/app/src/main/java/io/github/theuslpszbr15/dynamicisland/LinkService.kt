package io.github.theuslpszbr15.dynamicisland

import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.net.ConnectivityManager
import android.net.Network
import android.net.NetworkCapabilities
import android.os.Build
import android.os.IBinder
import androidx.core.app.NotificationManagerCompat
import androidx.core.content.ContextCompat
import java.util.concurrent.Executors
import java.util.concurrent.LinkedBlockingQueue
import java.util.concurrent.ScheduledExecutorService
import java.util.concurrent.TimeUnit

/**
 * Keeps the link to the PC open in the background so requests arrive as notifications. On Wi-Fi it tries the
 * PC's address, then asks the network where the PC went, then the relay (if the pairing has one); off Wi-Fi it
 * goes straight to the relay. While on the relay it keeps checking whether the PC is reachable directly again.
 */
class LinkService : Service() {
    @Volatile private var running = false
    @Volatile private var attempt = 0
    /** The last LAN try failed: the next one goes through the relay. */
    @Volatile private var relayNext = false
    /** The current client was closed on purpose to change route: reconnect at once. */
    @Volatile private var rerouting = false
    private val nudge = LinkedBlockingQueue<Unit>()
    private var worker: Thread? = null
    private var ticker: ScheduledExecutorService? = null
    private var networkCallback: ConnectivityManager.NetworkCallback? = null

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
                scheduleWithFixedDelay(::tryLanAgain, LAN_CHECK_MS, LAN_CHECK_MS, TimeUnit.MILLISECONDS)
            }
            watchNetwork()
        }
        return START_STICKY
    }

    private fun loop() {
        while (running) {
            if (PairingStore.isPaused(this)) break
            val pairing = PairingStore.load(this) ?: break
            val hasRelay = pairing.relay != null
            val route = if (hasRelay && (relayNext || !onLocalNetwork())) Route.Relay else Route.Lan
            relayNext = false
            rerouting = false
            Link.update { it.copy(pairing = pairing, status = LinkStatus.Connecting, message = null, viaRelay = false) }
            val client = LinkClient(pairing, "${Build.MANUFACTURER} ${Build.MODEL}".take(40), route,
                if (hasRelay) LinkClient.LAN_FIRST_TIMEOUT_MS else LinkClient.CONNECT_TIMEOUT_MS)
            Link.client = client
            try {
                client.run(listener)
                if (!running) break
                if (rerouting) continue
            } catch (e: AuthException) {
                Link.update { it.copy(status = LinkStatus.AuthFailed, message = e.message) }
                break
            } catch (e: Exception) {
                // stopped on purpose (pause / unpair): the UI already says so
                if (!running) break
                if (rerouting) continue
                Link.update { it.copy(status = LinkStatus.Offline, message = e.message) }
                if (route == Route.Lan) {
                    // The PC may just have a new address: ask the network before waiting to retry.
                    if (rediscover(pairing)) {
                        attempt = 0
                        continue
                    }
                    // not on this network (or not reachable on it): the relay, right away
                    if (hasRelay) {
                        relayNext = true
                        continue
                    }
                }
            } finally {
                Link.client = null
                client.close()
                // nothing can be answered without the PC
                Link.state.value.approvals.forEach { Notifications.cancelApproval(this, it.fingerprint) }
                Link.state.value.files.forEach { Notifications.cancelFile(this, it.id) }
                Link.update { it.copy(approvals = emptyList(), sessions = emptyList(), tray = emptyList(), files = emptyList()) }
            }
            showStatus("Sem conexão com ${pairing.name}. Tentando de novo…")
            val wait = BACKOFF_MS[minOf(attempt++, BACKOFF_MS.lastIndex)]
            // a network change cuts the wait short
            try { nudge.clear(); nudge.poll(wait, TimeUnit.MILLISECONDS) } catch (_: InterruptedException) { break }
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

    /** Wi-Fi or Ethernet: somewhere the PC could be on the same network. */
    private fun onLocalNetwork(): Boolean {
        val manager = getSystemService(ConnectivityManager::class.java) ?: return true
        val caps = manager.getNetworkCapabilities(manager.activeNetwork ?: return false) ?: return false
        return caps.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) || caps.hasTransport(NetworkCapabilities.TRANSPORT_ETHERNET)
    }

    /** On the relay but the PC answers on this network: drop the relay and connect directly. */
    private fun tryLanAgain() {
        val client = Link.client ?: return
        if (client.route != Route.Relay || !onLocalNetwork() || rerouting) return
        val pairing = PairingStore.load(this) ?: return
        val found = try { Discovery.find(pairing) } catch (_: Exception) { null } ?: return
        if (found.host != pairing.host || found.port != pairing.port) PairingStore.save(this, found)
        reroute(client)
    }

    private fun reroute(client: LinkClient) {
        if (Link.client !== client) return
        rerouting = true
        client.close()
    }

    /** A new network: leave a LAN link that can't work anymore, look for the PC at once instead of waiting. */
    private fun watchNetwork() {
        val manager = getSystemService(ConnectivityManager::class.java) ?: return
        val callback = object : ConnectivityManager.NetworkCallback() {
            override fun onAvailable(network: Network) = changed()
            override fun onLost(network: Network) = changed()
            override fun onCapabilitiesChanged(network: Network, caps: NetworkCapabilities) = changed()

            private fun changed() {
                nudge.offer(Unit)
                val client = Link.client ?: return
                val local = onLocalNetwork()
                if (client.route == Route.Lan && !local && PairingStore.load(this@LinkService)?.relay != null) reroute(client)
                else if (client.route == Route.Relay && local) ticker?.execute(::tryLanAgain)
            }
        }
        try {
            manager.registerDefaultNetworkCallback(callback)
            networkCallback = callback
        } catch (_: Exception) {}
    }

    private val listener = object : LinkClient.Listener {
        override fun onWelcome(desktop: String) {
            attempt = 0
            val viaRelay = Link.client?.route == Route.Relay
            Link.update { it.copy(status = LinkStatus.Connected, desktop = desktop, message = null, viaRelay = viaRelay) }
            showStatus(if (viaRelay) "Ligado a $desktop pela internet" else "Ligado a $desktop")
        }

        override fun onRelay(relay: String?, relayKey: String?) {
            val pairing = PairingStore.load(this@LinkService) ?: return
            if (pairing.relay == relay && pairing.relayKey == relayKey) return
            val updated = pairing.copy(relay = relay, relayKey = relayKey)
            PairingStore.save(this@LinkService, updated)
            Link.update { it.copy(pairing = updated) }
        }

        override fun onSessions(sessions: List<Session>) {
            val before = Link.state.value.sessions
            Link.update { it.copy(sessions = sessions) }
            SessionAlerts.settled(before, sessions).forEach { Notifications.cancelSessionAlert(this@LinkService, it) }
            SessionAlerts.changes(before, sessions).forEach { Notifications.showSessionAlert(this@LinkService, it) }
        }

        override fun onTray(items: List<TrayItem>) = Link.update { it.copy(tray = items) }

        override fun onFileOffer(offer: FileOffer) {
            if (Link.state.value.files.size >= MAX_FILE_OFFERS) return
            Link.update { s -> s.copy(files = s.files.filterNot { it.id == offer.id } + offer) }
            Notifications.showFileOffer(this@LinkService, offer)
        }

        override fun onFileCancel(id: String) {
            if (Link.state.value.files.none { it.id == id }) return
            Link.update { s -> s.copy(files = s.files.filterNot { it.id == id }) }
            Notifications.cancelFile(this@LinkService, id)
        }

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
        networkCallback?.let { cb ->
            try { getSystemService(ConnectivityManager::class.java)?.unregisterNetworkCallback(cb) } catch (_: Exception) {}
        }
        networkCallback = null
        Link.client?.close()
        worker?.interrupt()
        worker = null
        super.onDestroy()
    }

    companion object {
        private val BACKOFF_MS = longArrayOf(2_000, 5_000, 10_000, 30_000, 60_000)
        /** How often a relay link checks whether the PC is reachable directly again. */
        private const val LAN_CHECK_MS = 60_000L
        /** The PC keeps at most 5 offers at a time; more than that isn't a person dropping files. */
        private const val MAX_FILE_OFFERS = 5

        fun start(context: Context) =
            ContextCompat.startForegroundService(context, Intent(context, LinkService::class.java))

        fun stop(context: Context) = context.stopService(Intent(context, LinkService::class.java))
    }
}
