package io.github.theuslpszbr15.dynamicisland

import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.IBinder
import androidx.core.app.NotificationCompat
import androidx.core.content.ContextCompat

/**
 * Keeps the floating island alive when there is no PC link (the link service does it when there is one). Only a
 * silent, minimal notification; it does nothing else.
 */
class IslandService : Service() {
    override fun onBind(intent: Intent?): IBinder? = null

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        Notifications.createChannels(this)
        val open = PendingIntent.getActivity(this, 4, Intent(this, MainActivity::class.java)
            .addFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP), PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE)
        val notification = NotificationCompat.Builder(this, Notifications.LINK_CHANNEL)
            .setSmallIcon(R.drawable.ic_stat_bit)
            .setContentTitle("Ilha flutuante ligada")
            .setContentText("Toque para abrir o app")
            .setOngoing(true)
            .setSilent(true)
            .setContentIntent(open)
            .build()
        try {
            startForeground(ID, notification, ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE)
        } catch (_: Exception) {
            stopSelf()
            return START_NOT_STICKY
        }
        if (!IslandOverlay.isOn(this) || !IslandOverlay.canShow(this)) stopSelf()
        else IslandOverlay.refresh(this)
        return START_STICKY
    }

    companion object {
        private const val ID = 4
        @Volatile private var running = false

        fun start(context: Context) {
            if (running) return
            running = true
            try {
                ContextCompat.startForegroundService(context, Intent(context, IslandService::class.java))
            } catch (_: Exception) {
                running = false
            }
        }

        fun stop(context: Context) {
            if (!running) return
            running = false
            context.stopService(Intent(context, IslandService::class.java))
        }
    }
}
