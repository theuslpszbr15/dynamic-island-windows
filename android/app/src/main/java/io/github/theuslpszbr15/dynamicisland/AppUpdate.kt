package io.github.theuslpszbr15.dynamicisland

import android.Manifest
import android.app.PendingIntent
import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.net.Uri
import android.widget.Toast
import androidx.core.app.NotificationCompat
import androidx.core.app.NotificationManagerCompat
import androidx.core.content.ContextCompat

/**
 * "Tem versão nova do app": the PC says which app version came with its island (and the download link). Older than
 * that, the app shows a notice and a notification, once per version.
 */
object AppUpdate {
    data class Available(val code: Int, val name: String, val url: String)

    private const val NOTIFICATION_ID = 7

    fun installed(context: Context): Int = try {
        context.packageManager.getPackageInfo(context.packageName, 0).longVersionCode.toInt()
    } catch (_: Exception) { 0 }

    fun heard(context: Context, code: Int, name: String, url: String) {
        if (code <= installed(context)) {
            Link.update { it.copy(update = null) }
            return
        }
        val update = Available(code, name, url)
        Link.update { it.copy(update = update) }
        val prefs = context.getSharedPreferences("update", Context.MODE_PRIVATE)
        if (prefs.getInt("notified", 0) >= code) return
        prefs.edit().putInt("notified", code).apply()
        notify(context, update)
    }

    private fun notify(context: Context, update: Available) {
        if (ContextCompat.checkSelfPermission(context, Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) return
        val download = PendingIntent.getActivity(context, NOTIFICATION_ID, browser(update.url),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE)
        val notification = NotificationCompat.Builder(context, Notifications.AGENT_CHANNEL)
            .setSmallIcon(R.drawable.ic_stat_bit)
            .setContentTitle("Tem versão nova do app (${update.name})")
            .setContentText("A ilha do PC é mais nova. Toque para baixar e instalar por cima.")
            .setAutoCancel(true)
            .setContentIntent(download)
            .build()
        NotificationManagerCompat.from(context).notify(NOTIFICATION_ID, notification)
    }

    fun open(context: Context, url: String) {
        try { context.startActivity(browser(url)) }
        catch (_: Exception) { Toast.makeText(context, "Não achei um navegador para baixar", Toast.LENGTH_SHORT).show() }
    }

    private fun browser(url: String) = Intent(Intent.ACTION_VIEW, Uri.parse(url)).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
}
