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
    LaunchedEffect(Unit) { IslandLook.load(context); while (true) { delay(1_000); tick++ } }
    val on = remember(tick) { IslandOverlay.isOn(context) }
    val canDraw = remember(tick) { IslandOverlay.canDraw(context) }
    val access = remember(tick) { DiscordListener.enabled(context) }
    val look by IslandLook.state.collectAsState()
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
                if (it && !IslandOverlay.canDraw(context)) context.startActivity(IslandOverlay.permissionIntent(context))
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
        Setting("Altura na tela", "${look.y} dp do topo", look.y.toFloat(), 0f..Look.MAX_Y.toFloat()) {
            change(look.copy(y = it.roundToInt()))
        }
        Setting("Opacidade", "${(look.opacity * 100).roundToInt()}%", look.opacity, 0.4f..1f) {
            change(look.copy(opacity = it))
        }
        TextButton(onClick = { change(Look(media = look.media)) }) { Text("Voltar ao padrão") }

        Section("Música e Discord")
        Row(verticalAlignment = Alignment.CenterVertically) {
            Text("Mostrar a música tocando (Spotify, YouTube Music…)", color = Color.White, fontSize = 13.5.sp, modifier = Modifier.weight(1f))
            Switch(checked = look.media, onCheckedChange = { change(look.copy(media = it)) })
        }
        Text(
            if (access) "Acesso a notificações ligado: a ilha lê a música tocando e a notificação de call do Discord. Nada sai do celular."
            else "Para a música e a call do Discord, ligue o acesso a notificações do Dynamic Island. Se o Android disser que está " +
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
            if (access) TextButton(onClick = { showSeen = !showSeen }) { Text("O que o Discord mandou") }
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
