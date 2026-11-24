package io.github.theuslpszbr15.dynamicisland

import android.Manifest
import android.content.Context
import android.content.Intent
import android.os.Build
import android.provider.Settings
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Icon
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.core.app.NotificationManagerCompat
import kotlinx.coroutines.delay

/** One thing the island needs from Android, how to tell it is on, and how to turn it on. */
private class Need(
    val title: String,
    val icon: Int,
    val granted: (Context) -> Boolean,
    val turnOn: (Context) -> Unit,
)

private fun needs(): List<Need> = buildList {
    add(Need("Sobrepor a outros apps", R.drawable.ic_layers,
        { IslandOverlay.canShow(it) }, { it.startActivity(IslandOverlay.permissionIntent(it)) }))
    add(Need("Notificações", R.drawable.ic_bell,
        { NotificationManagerCompat.from(it).areNotificationsEnabled() },
        {
            if (Build.VERSION.SDK_INT >= 33) PermissionActivity.ask(it, arrayOf(Manifest.permission.POST_NOTIFICATIONS))
            else it.startActivity(Intent(Settings.ACTION_APP_NOTIFICATION_SETTINGS)
                .putExtra(Settings.EXTRA_APP_PACKAGE, it.packageName).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
        }))
    // a build without the listener declared has nothing to switch on here
    add(Need("Acesso às notificações", R.drawable.ic_bell,
        { !DiscordListener.available(it) || DiscordListener.enabled(it) },
        {
            try { it.startActivity(DiscordListener.settingsIntent(it)) }
            catch (_: Exception) { it.startActivity(Intent(Settings.ACTION_NOTIFICATION_LISTENER_SETTINGS).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)) }
        }))
    add(Need("Acessibilidade", R.drawable.ic_access,
        { !IslandAccessibility.available(it) || IslandAccessibility.enabled(it) }, { it.startActivity(IslandAccessibility.settingsIntent()) }))
    add(Need("Telefone e contatos", R.drawable.ic_phone,
        { PhoneRings.canSee(it) }, { PermissionActivity.ask(it, PhoneRings.permissions) }))
    add(Need("Agenda", R.drawable.ic_calendar,
        { Today.canReadCalendar(it) }, { PermissionActivity.ask(it, arrayOf(Manifest.permission.READ_CALENDAR)) }))
    add(Need("Localização aproximada", R.drawable.ic_pin,
        { Today.canLocate(it) }, { PermissionActivity.ask(it, arrayOf(Manifest.permission.ACCESS_COARSE_LOCATION)) }))
}

/** How many of the things the island needs from Android are still off. */
fun missingPermissions(context: Context): Int = needs().count { !it.granted(context) }

/** Seen once: after "Começar" (or "Pular") the app opens straight on its screens next time. */
object Setup {
    private fun prefs(context: Context) = context.getSharedPreferences("setup", Context.MODE_PRIVATE)
    fun done(context: Context) = prefs(context).getBoolean("done", false)
    fun finish(context: Context) = prefs(context).edit().putBoolean("done", true).apply()
}

/**
 * First screen: everything the island needs from Android, switched on one by one. Each button opens the right
 * Android screen; coming back, the row turns into a check. "Começar" waits for all of them.
 */
@Composable
fun SetupScreen(onDone: () -> Unit) {
    val context = LocalContext.current
    val list = remember { needs() }
    // Android's settings change behind the app's back: look again every second while this is on screen
    var tick by remember { mutableIntStateOf(0) }
    LaunchedEffect(Unit) { while (true) { delay(1_000); tick++ } }
    val state = remember(tick) { list.map { it.granted(context) } }
    val on = state.count { it }
    val all = on == list.size

    Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(horizontal = 16.dp, vertical = 24.dp),
        verticalArrangement = Arrangement.spacedBy(14.dp)) {
        Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(14.dp),
            modifier = Modifier.padding(top = 16.dp)) {
            Box(Modifier.size(44.dp).background(Ui.Raised, Ui.ChipShape), contentAlignment = Alignment.Center) {
                Icon(painterResource(R.drawable.ic_island), null, tint = Ui.Text, modifier = Modifier.size(26.dp))
            }
            UiTitle("Dynamic Island")
        }
        UiCard {
            UiCardTitle("Ative as permissões", "A ilha só funciona com todas ligadas.")
            Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                Box(Modifier.weight(1f).height(6.dp).background(Ui.Raised, RoundedCornerShape(3.dp))) {
                    Box(Modifier.fillMaxWidth(on / list.size.toFloat()).height(6.dp).background(Ui.Text, RoundedCornerShape(3.dp)))
                }
                Text("$on de ${list.size}", color = Ui.Muted, fontSize = 13.sp, fontWeight = FontWeight.Medium)
            }
        }
        UiCard {
            list.forEachIndexed { i, need ->
                if (i > 0) UiDivider()
                UiRow(need.title, icon = need.icon) {
                    if (state[i]) UiTag("Ativa", Tone.Ok) else UiPill("Ativar", { need.turnOn(context); tick++ })
                }
            }
        }
        Box(Modifier.height(4.dp))
        UiPrimary(if (all) "Começar" else "Ative tudo para continuar", onClick = onDone, enabled = all, modifier = Modifier.fillMaxWidth())
        if (!all) UiLink("Pular por enquanto", onDone, modifier = Modifier.align(Alignment.CenterHorizontally))
    }
}
