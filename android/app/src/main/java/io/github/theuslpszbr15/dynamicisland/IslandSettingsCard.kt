package io.github.theuslpszbr15.dynamicisland

import android.content.Intent
import android.net.Uri
import android.provider.Settings
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import kotlinx.coroutines.delay
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

/** "Ilha flutuante" in the Agentes tab: the switch, Android's overlay permission and the Discord call reading. */
@Composable
fun IslandSettingsCard() {
    val context = LocalContext.current
    // Android's settings change behind the app's back: look again every second while this is on screen
    var tick by remember { mutableIntStateOf(0) }
    LaunchedEffect(Unit) { while (true) { delay(1_000); tick++ } }
    val on = remember(tick) { IslandOverlay.isOn(context) }
    val canDraw = remember(tick) { IslandOverlay.canDraw(context) }
    val discord = remember(tick) { DiscordListener.enabled(context) }
    var showSeen by remember { mutableStateOf(false) }

    Column(Modifier.fillMaxWidth().background(Color(0xFF1C1C1E), RoundedCornerShape(18.dp)).padding(16.dp)) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Column(Modifier.weight(1f)) {
                Text("Ilha flutuante", color = Color.White, fontSize = 16.sp, fontWeight = FontWeight.SemiBold)
                Text("Uma pílula no topo da tela, por cima dos apps: pendentes, pedidos dos agentes, envios e a call do Discord. " +
                    "Arraste para mudar de altura; jogue para o lado para esconder até chegar algo novo.",
                    color = Color(0xFF8E8E93), fontSize = 13.sp)
            }
            Switch(checked = on, onCheckedChange = {
                IslandOverlay.setOn(context, it)
                if (it && !IslandOverlay.canDraw(context)) context.startActivity(IslandOverlay.permissionIntent(context))
                tick++
            }, modifier = Modifier.padding(start = 12.dp))
        }
        if (on && !canDraw) {
            Text("Falta permitir \"Sobrepor a outros apps\" para o Dynamic Island.", color = Color(0xFFFF9F0A), fontSize = 13.sp,
                modifier = Modifier.padding(top = 8.dp))
            TextButton(onClick = { context.startActivity(IslandOverlay.permissionIntent(context)) }) { Text("Permitir") }
        }

        Text("Call do Discord deste celular", color = Color.White, fontSize = 14.sp, fontWeight = FontWeight.SemiBold,
            modifier = Modifier.padding(top = 14.dp))
        Text(
            if (discord) "Ligado: a ilha mostra a call que você faz pelo Discord do celular (só a notificação de call é lida)."
            else "Opcional. Quem entra e sai já vem da ilha do PC; isto mostra também a call feita pelo celular. " +
                "Se o Android disser que a opção está bloqueada: Configurações › Apps › Dynamic Island › ⋮ › " +
                "Permitir configurações restritas, e tente de novo.",
            color = Color(0xFF8E8E93), fontSize = 13.sp,
        )
        Row {
            TextButton(onClick = {
                try { context.startActivity(DiscordListener.settingsIntent(context)) }
                catch (_: Exception) { context.startActivity(Intent(Settings.ACTION_NOTIFICATION_LISTENER_SETTINGS).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)) }
            }) { Text(if (discord) "Desligar" else "Ligar") }
            if (!discord) TextButton(onClick = {
                context.startActivity(Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, Uri.parse("package:${context.packageName}"))
                    .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
            }) { Text("Informações do app") }
            if (discord) TextButton(onClick = { showSeen = !showSeen }) { Text("O que o Discord mandou") }
        }
        if (showSeen) {
            val seen = remember(tick) { DiscordListener.lastSeen() }
            val time = SimpleDateFormat("HH:mm:ss", Locale.ROOT)
            if (seen.isEmpty()) Text("Nada do Discord ainda. Entre numa call pelo celular e abra de novo.", color = Color(0xFF8E8E93), fontSize = 12.sp)
            seen.forEach {
                Text("${time.format(Date(it.at))} · canal \"${it.channel}\" · ${it.category.ifEmpty { "sem categoria" }}" +
                    (if (it.ongoing) " · fixa: \"${it.title}\" / \"${it.text}\"" else " · comum") +
                    (if (it.actions.isNotEmpty()) " · botões: ${it.actions.joinToString()}" else ""),
                    color = Color(0xFF8E8E93), fontSize = 11.sp, modifier = Modifier.padding(top = 4.dp))
            }
        }
    }
}
