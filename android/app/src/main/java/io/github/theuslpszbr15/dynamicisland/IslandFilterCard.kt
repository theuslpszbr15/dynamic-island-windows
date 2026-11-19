package io.github.theuslpszbr15.dynamicisland

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp

private val CardColor = Color(0xFF202020)
private val MutedColor = Color(0xFFA19F9A)

private class Entry(val kind: Pill.Kind, val label: String, val note: String = "")

/** Title and the switches under it. The song has its own switch in the island card. */
private val groups: List<Pair<String, List<Entry>>> = listOf(
    "Do PC" to listOf(
        Entry(Pill.Kind.Approval, "Pedidos de permissão dos agentes", "Segurança: desligado, a ilha não avisa quando um agente espera sua resposta."),
        Entry(Pill.Kind.Offer, "Solicitações de envio recebidas"),
        Entry(Pill.Kind.Outgoing, "Esperando o PC aceitar um envio"),
    ),
    "Neste celular" to listOf(
        Entry(Pill.Kind.Claude, "Avisos do app do Claude"),
        Entry(Pill.Kind.Notification, "Notificações de todos os apps", "A ilha não acende quando chega uma notificação."),
        Entry(Pill.Kind.Pending, "Pendências dos agentes (esperando você)"),
        Entry(Pill.Kind.Ring, "Ligações e chamadas perdidas"),
        Entry(Pill.Kind.Live, "Progresso ao vivo (entregas, corridas, downloads)"),
        Entry(Pill.Kind.Timer, "Temporizador e cronômetro na pílula"),
        Entry(Pill.Kind.Battery, "Carregando, bateria baixa e cheia"),
        Entry(Pill.Kind.Recording, "Câmera ou microfone em uso (ponto vermelho)"),
        Entry(Pill.Kind.Transfer, "Transferências (andamento e fim)"),
    ),
    "Discord" to listOf(
        Entry(Pill.Kind.Discord, "Quem entra e sai da call"),
        Entry(Pill.Kind.Call, "Estou numa call"),
    ),
)

/** What the island may tell. Shared with the PC (Configurações → Celular), the newest change wins. */
@Composable
fun IslandFilterCard() {
    val context = LocalContext.current
    LaunchedEffect(Unit) { IslandFilter.load(context) }
    val off by IslandFilter.off.collectAsState()
    Column(Modifier.fillMaxWidth().background(CardColor, RoundedCornerShape(24.dp)).padding(16.dp)) {
        Text("Avisos da ilha", color = Color.White, fontSize = 17.sp, fontWeight = FontWeight.SemiBold)
        Text("Escolha o que aparece na ilha deste celular. Vale só aqui; a ilha do PC não muda.", color = MutedColor, fontSize = 13.sp)
        groups.forEach { (title, entries) ->
            Text(title, color = Color.White, fontSize = 14.5.sp, fontWeight = FontWeight.SemiBold,
                modifier = Modifier.padding(top = 16.dp, bottom = 4.dp))
            entries.forEach { e ->
                Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.padding(top = 4.dp)) {
                    Column(Modifier.weight(1f)) {
                        Text(e.label, color = Color.White, fontSize = 13.5.sp)
                        if (e.note.isNotEmpty() && e.kind in off) Text(e.note, color = Color(0xFFF5B544), fontSize = 12.sp)
                    }
                    Switch(checked = e.kind !in off, onCheckedChange = { IslandFilter.set(context, e.kind, it) },
                        modifier = Modifier.padding(start = 12.dp))
                }
            }
        }
    }
}
