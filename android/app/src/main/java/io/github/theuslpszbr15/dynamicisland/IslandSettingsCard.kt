package io.github.theuslpszbr15.dynamicisland

import android.content.Intent
import android.net.Uri
import android.provider.Settings
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Slider
import androidx.compose.material3.SliderDefaults
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import kotlinx.coroutines.delay
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale
import kotlin.math.roundToInt

private val CardColor = Color(0xFF1C1C1E)
private val MutedColor = Color(0xFF8E8E93)
private val BlueColor = Color(0xFF0A84FF)

/** The island's switch, Android's permissions, its size and place (live) and the music / Discord reading. */
@Composable
fun IslandSettingsCard() {
    val context = LocalContext.current
    // Android's settings change behind the app's back: look again every second while this is on screen
    var tick by remember { mutableIntStateOf(0) }
    LaunchedEffect(Unit) { Focus.load(context); IslandLook.load(context); while (true) { delay(1_000); tick++ } }
    val on = remember(tick) { IslandOverlay.isOn(context) }
    val canDraw = remember(tick) { IslandOverlay.canShow(context) }
    val above = remember(tick) { IslandOverlay.isAbove() }
    val aboveOn = remember(tick) { IslandAccessibility.enabled(context) }
    val access = remember(tick) { DiscordListener.enabled(context) }
    val look by IslandLook.state.collectAsState()
    val focus by Focus.state.collectAsState()
    val seen by Focus.seen.collectAsState()
    var showSeen by remember { mutableStateOf(false) }
    fun change(l: Look) = IslandLook.set(context, l)

    Column(Modifier.fillMaxWidth().background(CardColor, RoundedCornerShape(18.dp)).padding(16.dp)) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Column(Modifier.weight(1f)) {
                Text("Ilha", color = Color.White, fontSize = 17.sp, fontWeight = FontWeight.SemiBold)
                Text("A pílula no topo da tela, por cima dos apps. Ela só avisa; um toque abre o que fazer.",
                    color = MutedColor, fontSize = 13.sp)
            }
            Switch(checked = on, onCheckedChange = {
                IslandOverlay.setOn(context, it)
                if (it && !IslandOverlay.canShow(context)) context.startActivity(IslandOverlay.permissionIntent(context))
                tick++
            }, modifier = Modifier.padding(start = 12.dp))
        }
        if (on && !canDraw) {
            Text("Falta permitir \"Sobrepor a outros apps\" para o Dynamic Island.", color = Color(0xFFFF9F0A), fontSize = 13.sp,
                modifier = Modifier.padding(top = 8.dp))
            TextButton(onClick = { context.startActivity(IslandOverlay.permissionIntent(context)) }) { Text("Permitir") }
        }

        Section("Formato")
        Row(Modifier.horizontalScroll(rememberScrollState()), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            Look.presets.forEach { (name, apply) ->
                Text(name, color = Color.White, fontSize = 12.5.sp,
                    modifier = Modifier.clip(RoundedCornerShape(50)).background(Color(0xFF2C2C2E))
                        .clickable { change(apply(look)) }.padding(horizontal = 12.dp, vertical = 7.dp))
            }
        }
        Setting("Largura", "${look.width} dp", look.width.toFloat(), Look.MIN_W.toFloat()..Look.MAX_W.toFloat()) {
            change(look.copy(width = it.roundToInt()))
        }
        Setting("Altura", "${look.height} dp", look.height.toFloat(), Look.MIN_H.toFloat()..Look.MAX_H.toFloat()) {
            change(look.copy(height = it.roundToInt()))
        }
        Setting("Arredondamento", "${(look.corner * 100).roundToInt()}%", look.corner, 0f..1f) {
            change(look.copy(corner = it))
        }

        Section("Posição")
        Setting("Para os lados", if (look.x == 0) "centro" else "${look.x} dp", look.x.toFloat(), -Look.MAX_X.toFloat()..Look.MAX_X.toFloat()) {
            // snaps to the middle near it
            val v = it.roundToInt()
            change(look.copy(x = if (kotlin.math.abs(v) < 6) 0 else v))
        }
        if (look.dock && above) Text("Altura na tela: presa na borda de cima", color = MutedColor, fontSize = 13.sp,
            modifier = Modifier.padding(vertical = 8.dp))
        else Setting("Altura na tela", "${look.y} dp do topo", look.y.toFloat(), 0f..Look.MAX_Y.toFloat()) {
            change(look.copy(y = it.roundToInt()))
        }
        Setting("Opacidade", "${(look.opacity * 100).roundToInt()}%", look.opacity, 0.4f..1f) {
            change(look.copy(opacity = it))
        }
        Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.padding(top = 4.dp)) {
            Column(Modifier.weight(1f)) {
                Text("Esconder quando parada", color = Color.White, fontSize = 13.5.sp)
                Text("Sem nada para mostrar, ela vira uma bolinha e some. Quando chega algo, a bolinha aparece e se abre na pílula.",
                    color = MutedColor, fontSize = 12.sp)
            }
            Switch(checked = look.hide, onCheckedChange = { change(look.copy(hide = it)) }, modifier = Modifier.padding(start = 12.dp))
        }
        Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.padding(top = 4.dp)) {
            Column(Modifier.weight(1f)) {
                Text("Rolagem na ilha", color = Color.White, fontSize = 13.5.sp)
                Text("Textos longos deslizam na pílula e as listas dos cartões rolam com o dedo. Desligado, o que não cabe é cortado.",
                    color = MutedColor, fontSize = 12.sp)
            }
            Switch(checked = look.scroll, onCheckedChange = { change(look.copy(scroll = it)) }, modifier = Modifier.padding(start = 12.dp))
        }
        TextButton(onClick = { change(Look(media = look.media, hide = look.hide, dock = look.dock, scroll = look.scroll)) }) { Text("Voltar ao padrão") }

        Section("Por cima da barra de notificações")
        Text(
            if (above) "Ligado: a ilha fica na câmera, por cima da barra, e dá para tocar nela lá em cima."
            else "Desligado: a ilha fica logo abaixo da barra para continuar clicável. Para ela ficar na câmera, ligue " +
                "\"Ilha por cima da barra\" em Acessibilidade › Apps instalados. Se o Android disser que está bloqueado: " +
                "Configurações › Apps › Dynamic Island › ⋮ › Permitir configurações restritas, e tente de novo. Ela não lê nada da tela.",
            color = MutedColor, fontSize = 13.sp,
        )
        TextButton(onClick = { context.startActivity(IslandAccessibility.settingsIntent()) }) {
            Text(if (aboveOn) "Desligar na acessibilidade" else "Ligar na acessibilidade")
        }
        Row(verticalAlignment = Alignment.CenterVertically) {
            Column(Modifier.weight(1f)) {
                Text("Presa na borda de cima", color = Color.White, fontSize = 13.5.sp)
                Text(if (above) "Como um notch: reta em cima, redonda embaixo, saindo da borda da tela."
                    else "Só aparece com \"Por cima da barra\" ligado.", color = MutedColor, fontSize = 12.sp)
            }
            Switch(checked = look.dock, onCheckedChange = { change(look.copy(dock = it)) }, modifier = Modifier.padding(start = 12.dp))
        }

        Section("Foco")
        Text("Silencia só as notificações de outros apps na ilha. Ligações, temporizador, agentes e o resto continuam. " +
            "Para silenciar um app, toque em \"Silenciar\" na notificação dele, no cartão Notificações.", color = MutedColor, fontSize = 13.sp)
        Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.padding(top = 6.dp)) {
            Text("Foco ligado agora", color = Color.White, fontSize = 13.5.sp, modifier = Modifier.weight(1f))
            Switch(checked = focus.manual, onCheckedChange = { Focus.setManual(context, it) })
        }
        Row(verticalAlignment = Alignment.CenterVertically) {
            Text("Horário silencioso: ${Focus.clock(focus.fromMin)} às ${Focus.clock(focus.toMin)}", color = Color.White, fontSize = 13.5.sp, modifier = Modifier.weight(1f))
            Switch(checked = focus.quiet, onCheckedChange = { Focus.setQuiet(context, it) })
        }
        if (focus.quiet) {
            Setting("Começa às", Focus.clock(focus.fromMin), focus.fromMin.toFloat(), 0f..1410f) { Focus.setHours(context, (it / 30).roundToInt() * 30, focus.toMin) }
            Setting("Termina às", Focus.clock(focus.toMin), focus.toMin.toFloat(), 0f..1410f) { Focus.setHours(context, focus.fromMin, (it / 30).roundToInt() * 30) }
        }
        val apps = (focus.muted + seen.keys).distinct().sortedBy { (seen[it] ?: it).lowercase() }
        if (apps.isNotEmpty()) {
            Text("Apps", color = Color.White, fontSize = 13.5.sp, modifier = Modifier.padding(top = 8.dp))
            apps.forEach { pkg ->
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Text(seen[pkg] ?: pkg, color = Color.White, fontSize = 13.sp, modifier = Modifier.weight(1f), maxLines = 1)
                    Switch(checked = pkg !in focus.muted, onCheckedChange = { Focus.toggleMute(context, pkg) })
                }
            }
        }

        Section("Ligações")
        Text(if (PhoneRings.canSee(context)) "A ilha mostra quem está ligando e as chamadas perdidas." + (if (PhoneRings.canControl(context)) " Atender e recusar estão liberados." else " Para atender e recusar, ela pede a permissão na primeira vez.")
            else "Para mostrar ligações na ilha, permita o acesso ao telefone. O nome vem dos seus contatos e nada sai do celular.",
            color = MutedColor, fontSize = 13.sp)
        if (!PhoneRings.canSee(context)) TextButton(onClick = { PermissionActivity.ask(context, PhoneRings.permissions) }) { Text("Permitir") }

        // a build without the listener declared (see AndroidManifest) has nothing to switch on here
        if (DiscordListener.available(context)) {
        Section("Música, Discord e Claude")
        Row(verticalAlignment = Alignment.CenterVertically) {
            Text("Mostrar a música tocando (Spotify, YouTube Music…)", color = Color.White, fontSize = 13.5.sp, modifier = Modifier.weight(1f))
            Switch(checked = look.media, onCheckedChange = { change(look.copy(media = it)) })
        }
        Text(
            if (access) "Acesso a notificações ligado: a ilha lê a música tocando, a notificação de call do Discord e os avisos do " +
                "app do Claude (como um cartão esperando sua decisão). Nada sai do celular."
            else "Para a música, a call do Discord e os avisos do app do Claude, ligue o acesso a notificações do Dynamic Island. Se o Android disser que está " +
                "bloqueado: Configurações › Apps › Dynamic Island › ⋮ › Permitir configurações restritas, e tente de novo.",
            color = MutedColor, fontSize = 13.sp, modifier = Modifier.padding(top = 6.dp),
        )
        Row {
            TextButton(onClick = {
                try { context.startActivity(DiscordListener.settingsIntent(context)) }
                catch (_: Exception) { context.startActivity(Intent(Settings.ACTION_NOTIFICATION_LISTENER_SETTINGS).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)) }
            }) { Text(if (access) "Desligar acesso" else "Ligar acesso") }
            if (!access) TextButton(onClick = {
                context.startActivity(Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS, Uri.parse("package:${context.packageName}"))
                    .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
            }) { Text("Informações do app") }
            if (access) TextButton(onClick = { showSeen = !showSeen }) { Text("O que chegou") }
        }
        if (showSeen) {
            val seen = remember(tick) { DiscordListener.lastSeen() }
            val time = SimpleDateFormat("HH:mm:ss", Locale.ROOT)
            if (seen.isEmpty()) Text("Nada do Discord ainda. Entre numa call pelo celular e abra de novo.", color = MutedColor, fontSize = 12.sp)
            seen.forEach {
                Text("${time.format(Date(it.at))} · canal \"${it.channel}\" · ${it.category.ifEmpty { "sem categoria" }}" +
                    (if (it.ongoing) " · fixa: \"${it.title}\" / \"${it.text}\"" else " · comum") +
                    (if (it.actions.isNotEmpty()) " · botões: ${it.actions.joinToString()}" else ""),
                    color = MutedColor, fontSize = 11.sp, modifier = Modifier.padding(top = 4.dp))
            }
            val claude = remember(tick) { DiscordListener.claudeSeen }
            Text(if (claude == null) "Nada do Claude ainda. Quando o app do Claude avisar algo, abra de novo."
                else "${time.format(Date(claude.at))} · Claude · canal \"${claude.channel}\" · ${claude.category.ifEmpty { "sem categoria" }}" +
                    (if (claude.actions.isNotEmpty()) " · botões: ${claude.actions.joinToString()}" else ""),
                color = MutedColor, fontSize = 11.sp, modifier = Modifier.padding(top = 4.dp))
        }
        }
    }
}

@Composable
private fun Section(title: String) =
    Text(title, color = Color.White, fontSize = 14.5.sp, fontWeight = FontWeight.SemiBold, modifier = Modifier.padding(top = 16.dp, bottom = 4.dp))

@Composable
private fun Setting(label: String, value: String, current: Float, range: ClosedFloatingPointRange<Float>, onChange: (Float) -> Unit) =
    Column {
        Row {
            Text(label, color = Color.White, fontSize = 13.sp, modifier = Modifier.weight(1f))
            Text(value, color = MutedColor, fontSize = 12.sp)
        }
        Slider(value = current.coerceIn(range), onValueChange = onChange, valueRange = range,
            colors = SliderDefaults.colors(thumbColor = Color.White, activeTrackColor = BlueColor))
    }
