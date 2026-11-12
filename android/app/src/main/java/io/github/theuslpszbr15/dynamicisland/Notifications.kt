package io.github.theuslpszbr15.dynamicisland

import android.Manifest
import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.net.Uri
import androidx.core.app.NotificationCompat
import androidx.core.app.NotificationManagerCompat
import androidx.core.content.ContextCompat

object Notifications {
    const val LINK_CHANNEL = "link"
    const val APPROVAL_CHANNEL = "approvals"
    const val AGENT_CHANNEL = "agents"
    const val FILE_CHANNEL = "files"
    const val EXTRA_FILE_ID = "fileId"
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
            NotificationChannel(FILE_CHANNEL, "Arquivos e textos", NotificationManager.IMPORTANCE_HIGH).apply {
                description = "Arquivos e textos entre o PC e o celular (você aceita antes de receber)"
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

    // ───────────── files from the PC ─────────────

    private fun fileId(id: String) = 3000 + (id.take(6).toIntOrNull(16) ?: 0)

    private fun canNotify(context: Context) =
        ContextCompat.checkSelfPermission(context, Manifest.permission.POST_NOTIFICATIONS) == PackageManager.PERMISSION_GRANTED

    private fun fileAction(context: Context, action: String, id: String) = PendingIntent.getBroadcast(
        context, (action + id).hashCode(),
        Intent(context, DenyReceiver::class.java).setAction(action).putExtra(EXTRA_FILE_ID, id),
        PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
    )

    fun sizeText(bytes: Long): String = when {
        bytes < 1024 -> "$bytes B"
        bytes < 1024 * 1024 -> "${(bytes + 1023) / 1024} KB"
        else -> String.format(java.util.Locale.ROOT, "%.1f MB", bytes / (1024.0 * 1024.0)).replace('.', ',')
    }

    /** "O PC quer mandar foto.jpg (2,4 MB)" with Recusar / Aceitar. Nothing is downloaded before Aceitar, which
     * asks to unlock the phone first; the lock screen shows only that something is waiting, never its name. */
    fun showFileOffer(context: Context, offer: FileOffer) {
        if (!canNotify(context)) return
        val accept = NotificationCompat.Action.Builder(0, "Aceitar", fileAction(context, DenyReceiver.ACTION_ACCEPT_FILE, offer.id))
            .setAuthenticationRequired(true)
            .build()
        val title = if (offer.isText) "O PC quer mandar um texto longo" else "O PC quer mandar ${if (offer.items.size == 1) "um arquivo" else "${offer.items.size} arquivos"}"
        val names = offer.items.take(5).joinToString(", ") { it.name } + if (offer.items.size > 5) "…" else ""
        val public = NotificationCompat.Builder(context, FILE_CHANNEL)
            .setSmallIcon(R.drawable.ic_stat_bit)
            .setContentTitle("O PC quer mandar algo")
            .build()
        val notification = NotificationCompat.Builder(context, FILE_CHANNEL)
            .setSmallIcon(R.drawable.ic_stat_bit)
            .setColor(0xFF0A84FF.toInt())
            .setContentTitle(title)
            .setContentText(if (offer.isText) sizeText(offer.totalBytes) else "$names (${sizeText(offer.totalBytes)})")
            .setStyle(NotificationCompat.BigTextStyle().bigText(if (offer.isText) sizeText(offer.totalBytes) else "$names (${sizeText(offer.totalBytes)})"))
            .setCategory(NotificationCompat.CATEGORY_RECOMMENDATION)
            .setPriority(NotificationCompat.PRIORITY_HIGH)
            .setVisibility(NotificationCompat.VISIBILITY_PRIVATE)
            .setPublicVersion(public)
            .setTimeoutAfter(FileOffer.WINDOW_MS)
            .setContentIntent(openApp(context, null))
            .setDeleteIntent(fileAction(context, DenyReceiver.ACTION_DECLINE_FILE, offer.id))
            .addAction(0, "Recusar", fileAction(context, DenyReceiver.ACTION_DECLINE_FILE, offer.id))
            .addAction(accept)
            .build()
        NotificationManagerCompat.from(context).notify(fileId(offer.id), notification)
    }

    /** A short text from the PC: shown (privately) with "Copiar"; copying needs the phone unlocked. */
    fun showText(context: Context, id: String, text: String) {
        if (!canNotify(context)) return
        val copy = NotificationCompat.Action.Builder(0, "Copiar", fileAction(context, DenyReceiver.ACTION_COPY_TEXT, id))
            .setAuthenticationRequired(true)
            .build()
        val public = NotificationCompat.Builder(context, FILE_CHANNEL)
            .setSmallIcon(R.drawable.ic_stat_bit)
            .setContentTitle("O PC mandou um texto")
            .build()
        val notification = NotificationCompat.Builder(context, FILE_CHANNEL)
            .setSmallIcon(R.drawable.ic_stat_bit)
            .setColor(0xFF0A84FF.toInt())
            .setContentTitle("O PC mandou um texto")
            .setContentText(text.take(200))
            .setStyle(NotificationCompat.BigTextStyle().bigText(text.take(1000)))
            .setVisibility(NotificationCompat.VISIBILITY_PRIVATE)
            .setPublicVersion(public)
            .setTimeoutAfter(FileOffer.WINDOW_MS)
            .setAutoCancel(true)
            .setContentIntent(openApp(context, null))
            .addAction(copy)
            .build()
        NotificationManagerCompat.from(context).notify(fileId(id), notification)
    }

    /** [percent] -1: still waiting (no bar to fill yet). */
    fun showFileProgress(context: Context, id: String, title: String, text: String, percent: Int) {
        Island.transfer(id, title, text, percent, LiveTransfer.State.Running)
        if (!canNotify(context)) return
        val notification = NotificationCompat.Builder(context, FILE_CHANNEL)
            .setSmallIcon(if (title.startsWith("Receb")) android.R.drawable.stat_sys_download else android.R.drawable.stat_sys_upload)
            .setContentTitle(title)
            .setContentText(text)
            .setProgress(100, maxOf(percent, 0), percent < 0)
            .setOngoing(true)
            .setSilent(true)
            .setVisibility(NotificationCompat.VISIBILITY_PRIVATE)
            .build()
        NotificationManagerCompat.from(context).notify(fileId(id), notification)
    }

    /** Done; with [uri], tapping opens it with whatever app handles it (Android asks first for anything it would install). */
    fun showTransferDone(context: Context, id: String, title: String, text: String, uri: Uri?, mime: String?) {
        Island.transfer(id, title, text, 100, LiveTransfer.State.Done)
        if (!canNotify(context)) return
        val builder = NotificationCompat.Builder(context, FILE_CHANNEL)
            .setSmallIcon(R.drawable.ic_stat_bit)
            .setColor(0xFF30D158.toInt())
            .setContentTitle(title)
            .setContentText(text)
            .setSilent(true)
            .setVisibility(NotificationCompat.VISIBILITY_PRIVATE)
            .setAutoCancel(true)
            .setTimeoutAfter(if (uri == null) 8_000 else 0)
        if (uri != null) {
            val view = Intent(Intent.ACTION_VIEW).setDataAndType(uri, mime)
                .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_ACTIVITY_NEW_TASK)
            builder.setContentIntent(PendingIntent.getActivity(context, id.hashCode(), view,
                PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE))
        }
        NotificationManagerCompat.from(context).notify(fileId(id), builder.build())
    }

    fun showFileFailed(context: Context, id: String, title: String, reason: String) {
        Island.transfer(id, title, reason, -1, LiveTransfer.State.Failed)
        if (!canNotify(context)) return
        val notification = NotificationCompat.Builder(context, FILE_CHANNEL)
            .setSmallIcon(R.drawable.ic_stat_bit)
            .setColor(0xFFFF453A.toInt())
            .setContentTitle(title)
            .setContentText(reason)
            .setStyle(NotificationCompat.BigTextStyle().bigText(reason))
            .setVisibility(NotificationCompat.VISIBILITY_PRIVATE)
            .setAutoCancel(true)
            .build()
        NotificationManagerCompat.from(context).notify(fileId(id), notification)
    }

    fun cancelFile(context: Context, id: String) {
        Island.transferGone(id)
        NotificationManagerCompat.from(context).cancel(fileId(id))
    }
}
