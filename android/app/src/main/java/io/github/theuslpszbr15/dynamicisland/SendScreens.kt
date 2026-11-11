package io.github.theuslpszbr15.dynamicisland

import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.widget.Toast
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.PickVisualMediaRequest
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.Icon
import androidx.compose.ui.res.painterResource
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.text.SimpleDateFormat
import java.util.Calendar
import java.util.Locale

private val SCard = Color(0xFF1C1C1E)
private val STile = Color(0xFF2C2C30)
private val SMuted = Color(0xFF8E8E93)
private val SBlue = Color(0xFF0A84FF)
private val SGreen = Color(0xFF30D158)
private val SOrange = Color(0xFFFF9F0A)
private val SRed = Color(0xFFFF453A)

/** Copies and offers on a worker thread (the PC asks first), then says how it went. */
private suspend fun offer(context: Context, work: () -> String): String =
    withContext(Dispatchers.IO) { try { work() } catch (e: Exception) { e.message ?: "Não deu para mandar." } }

/**
 * "Enviar": Arquivo, Foto/vídeo, Texto and Colar, like LocalSend's selection row, to the one paired PC (no list of
 * nearby devices). Everything goes through [FileSender]: the island asks before taking anything.
 */
@Composable
fun SendTab(state: LinkUi) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    val ready = state.status == LinkStatus.Connected
    var textOpen by rememberSaveable { mutableStateOf(false) }
    var text by rememberSaveable { mutableStateOf("") }
    var result by rememberSaveable { mutableStateOf<String?>(null) }

    fun sendFiles(uris: List<Uri>) {
        if (uris.isEmpty()) return
        result = "Copiando…"
        scope.launch { result = offer(context) { FileSender.offerFiles(context, uris) } }
    }
    fun sendText(value: String) {
        if (value.isBlank()) { result = "Escreva ou cole um texto primeiro."; return }
        scope.launch {
            val answer = offer(context) { FileSender.offerText(context, value) }
            result = answer
            if (answer.startsWith("Pedido enviado")) text = ""
        }
    }

    val files = rememberLauncherForActivityResult(ActivityResultContracts.OpenMultipleDocuments()) { sendFiles(it) }
    val media = rememberLauncherForActivityResult(ActivityResultContracts.PickMultipleVisualMedia(FileOffer.MAX_ITEMS)) { sendFiles(it) }

    Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(horizontal = 18.dp)) {
        PcCard(state)
        Text("Seleção", color = Color.White, fontSize = 17.sp, fontWeight = FontWeight.SemiBold,
            modifier = Modifier.padding(top = 18.dp, bottom = 10.dp))
        Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
            SendTile(R.drawable.ic_send_file, "Arquivo", ready, Modifier.weight(1f)) { files.launch(arrayOf("*/*")) }
            SendTile(R.drawable.ic_send_media, "Foto/vídeo", ready, Modifier.weight(1f)) {
                media.launch(PickVisualMediaRequest(ActivityResultContracts.PickVisualMedia.ImageAndVideo))
            }
        }
        Spacer(Modifier.height(10.dp))
        Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
            SendTile(R.drawable.ic_send_text, "Texto", ready, Modifier.weight(1f)) { textOpen = true }
            SendTile(R.drawable.ic_send_paste, "Colar", ready, Modifier.weight(1f)) {
                val clip = context.getSystemService(ClipboardManager::class.java)?.primaryClip
                val item = clip?.takeIf { it.itemCount > 0 }?.getItemAt(0)
                when {
                    item?.uri != null -> sendFiles(listOf(item.uri))
                    item?.text?.isNotBlank() == true -> sendText(item.text.toString())
                    else -> result = "Nada copiado para mandar."
                }
            }
        }
        Text(
            if (ready) "O PC pergunta antes de receber. Até ${FileOffer.MAX_ITEMS} de uma vez, 30 MB cada (vídeo 60 MB); " +
                "texto até 100 mil caracteres. De outros apps: Compartilhar → Mandar pro PC."
            else "Os botões funcionam quando o celular estiver ligado ao PC.",
            color = SMuted, fontSize = 13.sp, modifier = Modifier.padding(top = 12.dp),
        )
        result?.let {
            val good = it.startsWith("Pedido enviado") || it == "Copiando…"
            Text(it, color = if (good) SGreen else SOrange, fontSize = 14.sp, modifier = Modifier.padding(top = 10.dp))
        }
        if (textOpen) {
            Column(Modifier.padding(top = 18.dp).fillMaxWidth().background(SCard, RoundedCornerShape(18.dp)).padding(14.dp)) {
                Text("Texto", color = Color.White, fontWeight = FontWeight.SemiBold, fontSize = 15.sp)
                OutlinedTextField(
                    value = text, onValueChange = { if (it.length <= FileOffer.MAX_TEXT_CHARS) text = it },
                    placeholder = { Text("Escreva ou cole aqui") },
                    modifier = Modifier.fillMaxWidth().heightIn(min = 140.dp, max = 300.dp).padding(top = 8.dp),
                )
                Row(Modifier.padding(top = 8.dp), verticalAlignment = Alignment.CenterVertically) {
                    Text("${text.length} / ${FileOffer.MAX_TEXT_CHARS}", color = SMuted, fontSize = 12.sp, modifier = Modifier.weight(1f))
                    TextButton(onClick = { textOpen = false }) { Text("Fechar") }
                    Button(onClick = { sendText(text) }, enabled = ready && text.isNotBlank(), shape = RoundedCornerShape(12.dp)) {
                        Text("Enviar")
                    }
                }
            }
        }
        Spacer(Modifier.height(24.dp))
    }
}

@Composable
private fun PcCard(state: LinkUi) {
    val (color, line) = when (state.status) {
        LinkStatus.Connected -> SGreen to if (state.viaRelay) "Conectado pela internet" else "Conectado pela rede local"
        LinkStatus.Connecting -> SOrange to "Procurando o PC…"
        LinkStatus.Paused -> SMuted to "Parado: toque em Procurar o PC lá em cima"
        else -> SRed to (state.message ?: "Sem conexão com o PC")
    }
    Row(Modifier.padding(top = 4.dp).fillMaxWidth().background(SCard, RoundedCornerShape(18.dp)).padding(16.dp),
        verticalAlignment = Alignment.CenterVertically) {
        Icon(painterResource(R.drawable.ic_pc), contentDescription = null, tint = Color.White,
            modifier = Modifier.padding(end = 14.dp).size(30.dp))
        Column(Modifier.weight(1f)) {
            Text("Para: " + state.desktop.ifEmpty { state.pairing?.name ?: "PC" }, color = Color.White, fontSize = 16.sp,
                fontWeight = FontWeight.SemiBold, maxLines = 1, overflow = TextOverflow.Ellipsis)
            Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.padding(top = 2.dp)) {
                Box(Modifier.size(8.dp).background(color, CircleShape))
                Text(line, color = SMuted, fontSize = 13.sp, modifier = Modifier.padding(start = 6.dp))
            }
        }
    }
}

@Composable
private fun SendTile(icon: Int, label: String, enabled: Boolean, modifier: Modifier, onClick: () -> Unit) {
    Column(
        modifier.height(92.dp).background(if (enabled) STile else STile.copy(alpha = 0.4f), RoundedCornerShape(18.dp))
            .clickable(enabled = enabled, onClick = onClick).padding(10.dp),
        horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.Center,
    ) {
        Icon(painterResource(icon), contentDescription = null, tint = if (enabled) Color.White else SMuted, modifier = Modifier.size(26.dp))
        Text(label, color = if (enabled) Color.White else SMuted, fontWeight = FontWeight.SemiBold, fontSize = 14.sp,
            modifier = Modifier.padding(top = 6.dp))
    }
}

/** "Histórico": Recebidos and Enviados, newest first; received files open from here, texts copy. */
@Composable
fun HistoryTab(records: List<TransferRecord>, ready: Boolean) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    var sent by rememberSaveable { mutableStateOf(false) }
    var confirmClear by rememberSaveable { mutableStateOf(false) }
    val shown = records.filter { it.sent == sent }
    Column(Modifier.fillMaxSize().padding(horizontal = 18.dp)) {
        Row(Modifier.padding(top = 4.dp, bottom = 10.dp).fillMaxWidth().background(SCard, RoundedCornerShape(14.dp)).padding(4.dp)) {
            listOf(false to "Recebidos", true to "Enviados").forEach { (side, label) ->
                Box(
                    Modifier.weight(1f).height(38.dp)
                        .background(if (sent == side) SBlue.copy(alpha = 0.3f) else Color.Transparent, RoundedCornerShape(11.dp))
                        .clickable { sent = side },
                    contentAlignment = Alignment.Center,
                ) { Text(label, color = Color.White, fontWeight = FontWeight.SemiBold, fontSize = 14.sp) }
            }
        }
        if (shown.isEmpty()) {
            Text(if (sent) "Nada enviado ainda. Use a aba Enviar ou Compartilhar → Mandar pro PC em qualquer app."
                else "Nada recebido ainda. No PC, solte arquivos no lado \"celular\" da ilha ou use a janela Celular.",
                color = SMuted, fontSize = 14.sp)
        }
        LazyColumn(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(10.dp)) {
            items(shown, key = { "${it.sent}-${it.id}" }) { record ->
                RecordRow(record, ready,
                    onOpen = { open(context, record) },
                    onCopy = { t ->
                        FileReceiver.copy(context, t)
                        Toast.makeText(context, "Texto copiado", Toast.LENGTH_SHORT).show()
                    },
                    onResend = { t ->
                        scope.launch {
                            val answer = offer(context) { FileSender.offerText(context, t) }
                            Toast.makeText(context, answer, Toast.LENGTH_LONG).show()
                        }
                    },
                    onRemove = { History.remove(context, record.id, record.sent) })
            }
            if (shown.isNotEmpty()) item {
                TextButton(onClick = { confirmClear = true }, modifier = Modifier.padding(vertical = 8.dp)) {
                    Text("Limpar a lista", color = SRed)
                }
            }
        }
    }
    if (confirmClear) {
        androidx.compose.material3.AlertDialog(
            onDismissRequest = { confirmClear = false },
            title = { Text("Limpar a lista?") },
            text = { Text("Os arquivos continuam onde estão; só a lista some.") },
            confirmButton = { TextButton(onClick = { confirmClear = false; History.clear(context, sent) }) { Text("Limpar", color = SRed) } },
            dismissButton = { TextButton(onClick = { confirmClear = false }) { Text("Cancelar") } },
        )
    }
}

@Composable
private fun RecordRow(
    record: TransferRecord,
    ready: Boolean,
    onOpen: () -> Unit,
    onCopy: (String) -> Unit,
    onResend: (String) -> Unit,
    onRemove: () -> Unit,
) {
    val statusColor = when (record.status) {
        "No PC", "Recebido", "Copiado" -> SGreen
        "Esperando o PC", "Enviando" -> SOrange
        "Recusado", "Não aceito" -> SMuted
        else -> SRed
    }
    Column(Modifier.fillMaxWidth().background(SCard, RoundedCornerShape(16.dp)).padding(14.dp)) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Icon(painterResource(when {
                record.isText -> R.drawable.ic_send_text
                record.count > 1 -> R.drawable.ic_tab_tray
                else -> R.drawable.ic_send_file
            }), contentDescription = null, tint = SMuted, modifier = Modifier.padding(end = 12.dp).size(22.dp))
            Column(Modifier.weight(1f)) {
                Text(record.text?.let(::preview) ?: record.title, color = Color.White, fontWeight = FontWeight.SemiBold,
                    fontSize = 15.sp, maxLines = 2, overflow = TextOverflow.Ellipsis)
                Row {
                    Text(record.status, color = statusColor, fontSize = 12.5.sp)
                    Text(" · ${whenText(record.at)} · " + (record.text?.let { "${it.length} caracteres" } ?: sizeText(record.bytes)),
                        color = SMuted, fontSize = 12.5.sp, maxLines = 1, overflow = TextOverflow.Ellipsis)
                }
            }
        }
        Row(Modifier.padding(top = 6.dp)) {
            val text = record.text
            if (text != null) {
                TextButton(onClick = { onCopy(text) }) { Text("Copiar") }
                if (record.sent) TextButton(onClick = { onResend(text) }, enabled = ready) { Text("Mandar de novo") }
            } else if (!record.sent && record.uris.isNotEmpty()) {
                TextButton(onClick = onOpen) { Text(if (record.uris.size > 1) "Abrir o primeiro" else "Abrir") }
            }
            Spacer(Modifier.weight(1f))
            TextButton(onClick = onRemove) { Text("Tirar", color = SMuted) }
        }
    }
}

/** Opens a received file with whatever app handles it (only this app's own MediaStore entries are listed). */
private fun open(context: Context, record: TransferRecord) {
    val uri = record.uris.firstOrNull()?.let(Uri::parse) ?: return
    val intent = Intent(Intent.ACTION_VIEW).setDataAndType(uri, record.mimes.firstOrNull() ?: "*/*")
        .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_ACTIVITY_NEW_TASK)
    try { context.startActivity(intent) }
    catch (_: Exception) { Toast.makeText(context, "Esse arquivo não existe mais ou nenhum app abre ele.", Toast.LENGTH_LONG).show() }
}

private fun preview(text: String): String {
    val line = text.split(Regex("\\s+")).filter { it.isNotEmpty() }.joinToString(" ")
    return if (line.length > 90) line.take(90) + "…" else line
}

private fun sizeText(bytes: Long) =
    if (bytes < 1024 * 1024) "${maxOf(1, bytes / 1024)} KB" else String.format(Locale.US, "%.1f MB", bytes / (1024.0 * 1024.0))

private fun whenText(at: Long): String {
    val day = Calendar.getInstance().apply { timeInMillis = at }
    val today = Calendar.getInstance()
    val hour = SimpleDateFormat("HH:mm", Locale.getDefault()).format(at)
    fun same(a: Calendar, b: Calendar) = a.get(Calendar.YEAR) == b.get(Calendar.YEAR) && a.get(Calendar.DAY_OF_YEAR) == b.get(Calendar.DAY_OF_YEAR)
    if (same(day, today)) return "hoje $hour"
    today.add(Calendar.DAY_OF_YEAR, -1)
    if (same(day, today)) return "ontem $hour"
    return SimpleDateFormat("dd/MM HH:mm", Locale.getDefault()).format(at)
}
