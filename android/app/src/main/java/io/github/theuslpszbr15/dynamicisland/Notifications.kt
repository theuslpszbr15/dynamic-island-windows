package io.github.theuslpszbr15.dynamicisland

import android.Manifest
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import androidx.core.app.NotificationCompat
import androidx.core.app.NotificationManagerCompat
import androidx.core.content.ContextCompat

object Notifications {
    const val LINK_CHANNEL = "link"
    const val APPROVAL_CHANNEL = "approvals"
    const val LINK_ID = 1
    const val EXTRA_FINGERPRINT = "fingerprint"

    fun createChannels(context: Context) {
        val manager = context.getSystemService(NotificationManager::class.java)
        manager.createNotificationChannel(
            NotificationChannel(LINK_CHANNEL, "Ligação com o PC", NotificationManager.IMPORTANCE_MIN).apply {
                description = "Mantém o celular ligado à ilha do PC"
                setShowBadge(false)
            }
        )
        manager.createNotificationChannel(
            NotificationChannel(APPROVAL_CHANNEL, "Pedidos de permissão", NotificationManager.IMPORTANCE_HIGH).apply {
                description = "Quando um agente pede para rodar algo no PC"
                enableVibration(true)
                lockscreenVisibility = Notification.VISIBILITY_PRIVATE
            }
        )
    }

    fun approvalId(fingerprint: String) = 1000 + (fingerprint.take(7).toIntOrNull(16) ?: 0)

    private fun openApp(context: Context, fingerprint: String?): PendingIntent {
        val intent = Intent(context, MainActivity::class.java)
            .addFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP or Intent.FLAG_ACTIVITY_CLEAR_TOP)
        if (fingerprint != null) intent.putExtra(EXTRA_FINGERPRINT, fingerprint)
        return PendingIntent.getActivity(context, fingerprint?.hashCode() ?: 0, intent,
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE)
    }

    fun link(context: Context, text: String): Notification {
        val pause = PendingIntent.getBroadcast(
            context, 1, Intent(context, DenyReceiver::class.java).setAction(DenyReceiver.ACTION_PAUSE),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
        )
        return NotificationCompat.Builder(context, LINK_CHANNEL)
            .setSmallIcon(R.drawable.ic_stat_bit)
            .setContentTitle("Dynamic Island")
            .setContentText(text)
            .setOngoing(true)
            .setSilent(true)
            .setContentIntent(openApp(context, null))
            .addAction(0, "Parar de procurar", pause)
            .build()
    }

    fun showApproval(context: Context, approval: Approval) {
        if (ContextCompat.checkSelfPermission(context, Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) return
        val deny = PendingIntent.getBroadcast(
            context, approval.fingerprint.hashCode(),
            Intent(context, DenyReceiver::class.java).putExtra(EXTRA_FINGERPRINT, approval.fingerprint),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
        )
        val where = if (approval.project.isBlank()) "" else " · ${approval.project}"
        // On the lock screen: only who is asking, never the command.
        val public = NotificationCompat.Builder(context, APPROVAL_CHANNEL)
            .setSmallIcon(R.drawable.ic_stat_bit)
            .setContentTitle("Aprovação pendente")
            .setContentText("${approval.agent} está esperando seu OK")
            .build()
        val notification = NotificationCompat.Builder(context, APPROVAL_CHANNEL)
            .setSmallIcon(R.drawable.ic_stat_bit)
            .setColor(0xFFFF9F0A.toInt())
            .setContentTitle("${approval.agent} quer permissão$where")
            .setContentText(approval.command)
            .setStyle(NotificationCompat.BigTextStyle().bigText(approval.command))
            .setCategory(NotificationCompat.CATEGORY_REMINDER)
            .setPriority(NotificationCompat.PRIORITY_HIGH)
            .setVisibility(NotificationCompat.VISIBILITY_PRIVATE)
            .setPublicVersion(public)
            .setTimeoutAfter(Link.APPROVAL_WINDOW_MS)
            .setAutoCancel(true)
            .setContentIntent(openApp(context, approval.fingerprint))
            .addAction(0, "Negar", deny)
            .addAction(0, if (approval.truncated) "Ver" else "Permitir…", openApp(context, approval.fingerprint))
            .build()
        NotificationManagerCompat.from(context).notify(approvalId(approval.fingerprint), notification)
    }

    fun cancelApproval(context: Context, fingerprint: String) =
        NotificationManagerCompat.from(context).cancel(approvalId(fingerprint))
}
