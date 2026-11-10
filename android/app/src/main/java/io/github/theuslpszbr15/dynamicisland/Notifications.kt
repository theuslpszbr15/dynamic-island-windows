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
    const val AGENT_CHANNEL = "agents"
    const val LINK_ID = 1
    const val EXTRA_FINGERPRINT = "fingerprint"
    /** With [EXTRA_FINGERPRINT]: "Permitir" was tapped, so the app asks for the fingerprint / PIN right away. */
    const val EXTRA_ALLOW = "allow"

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
        manager.createNotificationChannel(
            NotificationChannel(AGENT_CHANNEL, "Agentes", NotificationManager.IMPORTANCE_DEFAULT).apply {
                description = "Quando um agente termina ou fica esperando sua resposta"
                lockscreenVisibility = Notification.VISIBILITY_PRIVATE
            }
        )
    }

    /** Off for the app or for the permission requests: requests would arrive without any sound or banner. */
    fun approvalsBlocked(context: Context): Boolean {
        if (ContextCompat.checkSelfPermission(context, Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) return true
        if (!NotificationManagerCompat.from(context).areNotificationsEnabled()) return true
        val channel = context.getSystemService(NotificationManager::class.java).getNotificationChannel(APPROVAL_CHANNEL)
        return channel != null && channel.importance == NotificationManager.IMPORTANCE_NONE
    }

    fun approvalId(fingerprint: String) = 1000 + (fingerprint.take(7).toIntOrNull(16) ?: 0)

    private fun openApp(context: Context, fingerprint: String?, allow: Boolean = false): PendingIntent {
        val intent = Intent(context, MainActivity::class.java)
            .addFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP or Intent.FLAG_ACTIVITY_CLEAR_TOP)
        if (fingerprint != null) intent.putExtra(EXTRA_FINGERPRINT, fingerprint)
        if (allow) intent.putExtra(EXTRA_ALLOW, true)
        // distinct request codes, or the allow intent would overwrite the plain "open" one
        val code = (fingerprint?.hashCode() ?: 0) * 2 + if (allow) 1 else 0
        return PendingIntent.getActivity(context, code, intent,
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
            // Permitir still goes through the fingerprint / PIN, just without the extra tap inside the app
            .addAction(0, if (approval.truncated) "Ver" else "Permitir", openApp(context, approval.fingerprint, allow = !approval.truncated))
            .build()
        NotificationManagerCompat.from(context).notify(approvalId(approval.fingerprint), notification)
    }

    fun cancelApproval(context: Context, fingerprint: String) =
        NotificationManagerCompat.from(context).cancel(approvalId(fingerprint))

    private fun sessionId(id: String) = 2000 + (id.take(7).toIntOrNull(16) ?: id.hashCode() and 0xFFFFFF)

    fun showSessionAlert(context: Context, alert: SessionAlert) {
        if (ContextCompat.checkSelfPermission(context, Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) return
        val s = alert.session
        val where = if (s.project.isBlank()) "" else " · ${s.project}"
        val done = alert.kind == SessionAlert.Kind.Done
        val title = if (done) "${s.agent} terminou$where" else "${s.agent} está esperando você$where"
        val text = s.detail.ifBlank { if (done) "Pronto para a próxima." else "Precisa da sua resposta no PC." }
        val public = NotificationCompat.Builder(context, AGENT_CHANNEL)
            .setSmallIcon(R.drawable.ic_stat_bit)
            .setContentTitle(if (done) "Um agente terminou" else "Um agente está esperando você")
            .build()
        val notification = NotificationCompat.Builder(context, AGENT_CHANNEL)
            .setSmallIcon(R.drawable.ic_stat_bit)
            .setColor(if (done) 0xFF30D158.toInt() else 0xFFFF9F0A.toInt())
            .setContentTitle(title)
            .setContentText(text)
            .setCategory(NotificationCompat.CATEGORY_STATUS)
            .setVisibility(NotificationCompat.VISIBILITY_PRIVATE)
            .setPublicVersion(public)
            .setAutoCancel(true)
            .setContentIntent(openApp(context, null))
            .build()
        NotificationManagerCompat.from(context).notify(sessionId(s.id), notification)
    }

    fun cancelSessionAlert(context: Context, id: String) =
        NotificationManagerCompat.from(context).cancel(sessionId(id))
}
