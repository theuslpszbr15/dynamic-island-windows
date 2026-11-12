package io.github.theuslpszbr15.dynamicisland

import android.graphics.BitmapFactory
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items as gridItems
import androidx.compose.material3.Badge
import androidx.compose.material3.BadgedBox
import androidx.compose.material3.Icon
import androidx.compose.material3.NavigationBar
import androidx.compose.material3.NavigationBarItem
import androidx.compose.material3.NavigationBarItemDefaults
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.res.painterResource
import androidx.compose.foundation.border
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
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.darkColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableLongStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import kotlinx.coroutines.delay

private val Ground = Color(0xFF0B0B0D)
private val Card = Color(0xFF1C1C1E)
private val Muted = Color(0xFF8E8E93)
private val Blue = Color(0xFF0A84FF)
private val Orange = Color(0xFFFF9F0A)
private val Green = Color(0xFF30D158)
private val Red = Color(0xFFFF453A)

@Composable
fun AppTheme(content: @Composable () -> Unit) = MaterialTheme(
    colorScheme = darkColorScheme(primary = Blue, onPrimary = Color.White, background = Ground, surface = Card),
    content = content,
)

@Composable
fun AppScreen(
    state: LinkUi,
    focused: String?,
    pending: Pairing?,
    onScan: () -> Unit,
    onPaste: (String) -> Unit,
    onConfirmPairing: (Pairing) -> Unit,
    onCancelPairing: () -> Unit,
    onAllow: (Approval) -> Unit,
    onDeny: (Approval) -> Unit,
    onOpen: (TrayItem) -> Unit,
    onUnpair: () -> Unit,
    onRetry: () -> Unit,
    onPause: () -> Unit,
    onFixNotifications: () -> Unit = {},
) {
    Box(Modifier.fillMaxSize().background(Ground).statusBarsPadding().navigationBarsPadding()) {
        if (state.pairing == null) PairScreen(onScan, onPaste)
        else HomeScreen(state, focused, onAllow, onDeny, onOpen, onUnpair, onRetry, onPause, onScan, onFixNotifications)
    }
    if (pending != null) {
        AlertDialog(
            onDismissRequest = onCancelPairing,
            title = { Text("Parear com ${pending.name}?") },
            text = { Text("O celular vai se ligar à ilha desse PC (${pending.host}) pela sua rede Wi-Fi.") },
            confirmButton = { TextButton(onClick = { onConfirmPairing(pending) }) { Text("Parear") } },
            dismissButton = { TextButton(onClick = onCancelPairing) { Text("Cancelar") } },
        )
    }
}

@Composable
private fun PairScreen(onScan: () -> Unit, onPaste: (String) -> Unit) {
    var link by remember { mutableStateOf("") }
    Column(
        Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(28.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        Spacer(Modifier.height(40.dp))
        Bit(BitMood.Happy, size = 140.dp)
        Spacer(Modifier.height(18.dp))
        Text("Dynamic Island", color = Color.White, fontSize = 28.sp, fontWeight = FontWeight.Bold)
        Text(
            "Aprove os pedidos do Claude Code e acompanhe seus agentes pelo celular.",
            color = Muted, fontSize = 15.sp, textAlign = TextAlign.Center, modifier = Modifier.padding(top = 8.dp),
        )
        Spacer(Modifier.height(28.dp))
        Steps(
            "No PC, clique com o botão direito na ilha → Configurações → Celular e ligue “Ligação com o celular”.",
            "Celular e PC na mesma rede Wi-Fi.",
            "Toque abaixo e aponte a câmera para o código que aparece no PC.",
        )
        Spacer(Modifier.height(24.dp))
        Button(onClick = onScan, modifier = Modifier.fillMaxWidth().height(52.dp), shape = RoundedCornerShape(16.dp)) {
            Text("Escanear código do PC", fontSize = 16.sp, fontWeight = FontWeight.SemiBold)
        }
        Spacer(Modifier.height(20.dp))
        OutlinedTextField(
            value = link, onValueChange = { link = it }, singleLine = true,
            label = { Text("ou cole o link (dynamicisland://…)") }, modifier = Modifier.fillMaxWidth(),
        )
        TextButton(onClick = { onPaste(link) }, enabled = link.isNotBlank()) { Text("Parear com o link") }
    }
}

@Composable
private fun Steps(vararg steps: String) {
    Column(Modifier.fillMaxWidth().background(Card, RoundedCornerShape(18.dp)).padding(16.dp),
        verticalArrangement = Arrangement.spacedBy(10.dp)) {
        steps.forEachIndexed { i, step ->
            Row {
                Text("${i + 1}", color = Blue, fontWeight = FontWeight.Bold, modifier = Modifier.width(22.dp))
                Text(step, color = Color.White, fontSize = 14.sp)
            }
        }
    }
}

@Composable
private fun HomeScreen(
    state: LinkUi,
    focused: String?,
    onAllow: (Approval) -> Unit,
    onDeny: (Approval) -> Unit,
    onOpen: (TrayItem) -> Unit,
    onUnpair: () -> Unit,
    onRetry: () -> Unit,
    onPause: () -> Unit,
    onScan: () -> Unit,
    onFixNotifications: () -> Unit,
) {
    var tab by remember { mutableStateOf(state.initialTab) }
    val records by History.records.collectAsState()
    var confirmUnpair by remember { mutableStateOf(false) }
    LaunchedEffect(focused) { if (focused != null) tab = 0 }
    val mood = when {
        state.approvals.isNotEmpty() -> BitMood.Waiting
        state.sessions.any { it.state == "working" } -> BitMood.Working
        else -> BitMood.Idle
    }
    Column(Modifier.fillMaxSize()) {
        Row(Modifier.fillMaxWidth().padding(start = 18.dp, end = 10.dp, top = 18.dp, bottom = 8.dp),
            verticalAlignment = Alignment.CenterVertically) {
            Bit(mood, size = 56.dp)
            Column(Modifier.padding(start = 14.dp).weight(1f)) {
                Text(state.desktop.ifEmpty { state.pairing?.name ?: "" }, color = Color.White,
                    fontSize = 20.sp, fontWeight = FontWeight.Bold, maxLines = 1, overflow = TextOverflow.Ellipsis)
                StatusLine(state)
            }
            when (state.status) {
                LinkStatus.Connecting, LinkStatus.Offline ->
                    OutlinedButton(onClick = onPause, shape = RoundedCornerShape(12.dp)) { Text("Parar de procurar", fontSize = 13.sp) }
                LinkStatus.Connected ->
                    OutlinedButton(onClick = onPause, shape = RoundedCornerShape(12.dp)) { Text("Desconectar", fontSize = 13.sp) }
                LinkStatus.Paused ->
                    Button(onClick = onRetry, shape = RoundedCornerShape(12.dp)) { Text("Procurar o PC", fontSize = 13.sp) }
                else -> {}
            }
        }
        Box(Modifier.weight(1f)) {
            when (tab) {
                0 -> AgentsTab(state, focused, onAllow, onDeny, { confirmUnpair = true }, onRetry, onScan, onFixNotifications)
                2 -> SendTab(state)
                3 -> HistoryTab(records, state.status == LinkStatus.Connected)
                else -> TrayTab(state, onOpen)
            }
        }
        NavigationBar(containerColor = Card) {
            NavigationBarItem(
                selected = tab == 0, onClick = { tab = 0 },
                icon = {
                    BadgedBox(badge = { if (state.approvals.isNotEmpty()) Badge { Text("${state.approvals.size}") } }) {
                        Icon(painterResource(R.drawable.ic_tab_agents), contentDescription = null)
                    }
                },
                label = { Text("Agentes") },
                colors = NavigationBarItemDefaults.colors(indicatorColor = Blue.copy(alpha = 0.25f)),
            )
            NavigationBarItem(
                selected = tab == 2, onClick = { tab = 2 },
                icon = { Icon(painterResource(R.drawable.ic_tab_send), contentDescription = null) },
                label = { Text("Enviar") },
                colors = NavigationBarItemDefaults.colors(indicatorColor = Blue.copy(alpha = 0.25f)),
            )
            NavigationBarItem(
                selected = tab == 3, onClick = { tab = 3 },
                icon = { Icon(painterResource(R.drawable.ic_tab_history), contentDescription = null) },
                label = { Text("Histórico") },
                colors = NavigationBarItemDefaults.colors(indicatorColor = Blue.copy(alpha = 0.25f)),
            )
            NavigationBarItem(
                selected = tab == 1, onClick = { tab = 1 },
                icon = { Icon(painterResource(R.drawable.ic_tab_tray), contentDescription = null) },
                label = { Text("Bandeja") },
                colors = NavigationBarItemDefaults.colors(indicatorColor = Blue.copy(alpha = 0.25f)),
            )
        }
    }
    if (confirmUnpair) {
        AlertDialog(
            onDismissRequest = { confirmUnpair = false },
            title = { Text("Desparear deste PC?") },
            text = { Text("O celular esquece ${state.pairing?.name ?: "o PC"}. Para ligar de novo, escaneie o código nas Configurações do PC.") },
            confirmButton = { TextButton(onClick = { confirmUnpair = false; onUnpair() }) { Text("Desparear", color = Red) } },
            dismissButton = { TextButton(onClick = { confirmUnpair = false }) { Text("Cancelar") } },
        )
    }
}

@Composable
private fun AgentsTab(
    state: LinkUi,
    focused: String?,
    onAllow: (Approval) -> Unit,
    onDeny: (Approval) -> Unit,
    onUnpair: () -> Unit,
    onRetry: () -> Unit,
    onScan: () -> Unit,
    onFixNotifications: () -> Unit,
) {
    val approvals = state.approvals.sortedByDescending { it.fingerprint == focused }
    LazyColumn(Modifier.fillMaxSize().padding(horizontal = 18.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
        item { Spacer(Modifier.height(4.dp)) }
        if (state.notificationsOff) item {
            Notice("As notificações estão desligadas: os pedidos de permissão chegam sem aviso e só aparecem com o app aberto.",
                "Ativar notificações", onFixNotifications)
        }
        when (state.status) {
            LinkStatus.AuthFailed -> item {
                Notice("O PC trocou o código de pareamento. Escaneie o código novo.", "Escanear", onScan)
            }
            LinkStatus.Offline -> item {
                Notice((if (state.pairing?.relay != null)
                    "Sem conexão. Confira se o PC está ligado, com a ilha aberta e com internet."
                else "Sem conexão. Confira se o PC está ligado, com a ilha aberta, na mesma rede Wi-Fi " +
                    "(redes de visitante costumam isolar os aparelhos). Para conectar fora de casa, " +
                    "escaneie de novo o QR do PC.") +
                    (state.message?.let { "\n\nErro: $it" } ?: ""), "Tentar agora", onRetry)
            }
            LinkStatus.Paused -> item {
                Notice("Parado: o celular não está procurando o PC (nem gastando bateria com isso). " +
                    "O pareamento continua salvo.", "Procurar o PC", onRetry)
            }
            else -> {}
        }
        items(approvals, key = { it.fingerprint }) { approval ->
            ApprovalCard(approval, onAllow, onDeny)
        }
        if (state.status == LinkStatus.Connected) {
            item {
                Text("Agentes", color = Muted, fontSize = 13.sp, fontWeight = FontWeight.SemiBold,
                    modifier = Modifier.padding(top = 8.dp))
            }
            if (state.sessions.isEmpty()) item {
                Text("Nenhum agente rodando agora.", color = Muted, fontSize = 14.sp)
            }
            items(state.sessions, key = { it.id }) { SessionRow(it) }
        }
        item {
            TextButton(onClick = onUnpair, modifier = Modifier.padding(vertical = 18.dp)) {
                Text("Desparear deste PC", color = Red)
            }
        }
    }
}

@Composable
private fun TrayTab(state: LinkUi, onOpen: (TrayItem) -> Unit) {
    Column(Modifier.fillMaxSize().padding(horizontal = 18.dp)) {
        Text("O que está fixado na bandeja da ilha. Toque para abrir no PC.", color = Muted, fontSize = 13.sp,
            modifier = Modifier.padding(top = 4.dp, bottom = 12.dp))
        when {
            state.status != LinkStatus.Connected ->
                Text("Ligue-se ao PC para ver a bandeja.", color = Muted, fontSize = 14.sp)
            state.tray.isEmpty() ->
                Text("A bandeja está vazia (ou desligada para o celular nas Configurações do PC). " +
                    "Arraste arquivos ou apps até a ilha para fixá-los.", color = Muted, fontSize = 14.sp)
            else -> LazyVerticalGrid(
                columns = GridCells.Adaptive(104.dp),
                horizontalArrangement = Arrangement.spacedBy(10.dp),
                verticalArrangement = Arrangement.spacedBy(10.dp),
                modifier = Modifier.fillMaxSize(),
            ) {
                gridItems(state.tray, key = { it.id }) { TrayTile(it, onOpen) }
            }
        }
    }
}

@Composable
private fun TrayTile(item: TrayItem, onOpen: (TrayItem) -> Unit) {
    val icon = remember(item.id) {
        item.icon?.let { BitmapFactory.decodeByteArray(it, 0, it.size)?.asImageBitmap() }
    }
    Column(
        Modifier.fillMaxWidth().height(140.dp)
            .background(Card, RoundedCornerShape(18.dp))
            .clickable { onOpen(item) }
            .padding(10.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.Center,
    ) {
        if (icon != null) Image(icon, contentDescription = null, modifier = Modifier.size(44.dp))
        else Icon(painterResource(R.drawable.ic_tab_tray), contentDescription = null, tint = Muted, modifier = Modifier.size(40.dp))
        Text(item.name, color = Color.White, fontSize = 12.5.sp, maxLines = 2, overflow = TextOverflow.Ellipsis,
            textAlign = TextAlign.Center, modifier = Modifier.padding(top = 8.dp))
        Text(when (item.kind) { "app" -> "app"; "folder" -> "pasta"; else -> "arquivo" }, color = Muted, fontSize = 11.sp)
    }
}

@Composable
private fun StatusLine(state: LinkUi) {
    val (color, text) = when (state.status) {
        LinkStatus.Connected -> Green to if (state.viaRelay) "Ligado pela internet" else "Ligado"
        LinkStatus.Connecting -> Orange to "Procurando o PC…"
        LinkStatus.Offline -> Red to (state.message ?: "Sem conexão")
        LinkStatus.AuthFailed -> Red to "Pareamento recusado"
        LinkStatus.Paused -> Muted to "Parado"
        LinkStatus.Unpaired -> Muted to ""
    }
    Row(verticalAlignment = Alignment.CenterVertically) {
        Box(Modifier.size(8.dp).background(color, CircleShape))
        Text(text, color = Muted, fontSize = 13.sp, maxLines = 1, overflow = TextOverflow.Ellipsis,
            modifier = Modifier.padding(start = 6.dp))
    }
}

@Composable
private fun Notice(text: String, action: String, onAction: () -> Unit) {
    Column(Modifier.fillMaxWidth().background(Card, RoundedCornerShape(18.dp)).padding(16.dp)) {
        Text(text, color = Color.White, fontSize = 14.sp)
        TextButton(onClick = onAction) { Text(action) }
    }
}

@Composable
private fun ApprovalCard(approval: Approval, onAllow: (Approval) -> Unit, onDeny: (Approval) -> Unit) {
    var now by remember { mutableLongStateOf(System.currentTimeMillis()) }
    LaunchedEffect(approval.fingerprint) {
        while (true) { delay(1000); now = System.currentTimeMillis() }
    }
    val left = ((Link.APPROVAL_WINDOW_MS - (now - approval.receivedAt)) / 1000).coerceAtLeast(0)
    Column(
        Modifier.fillMaxWidth()
            .background(Card, RoundedCornerShape(22.dp))
            .border(1.dp, Orange.copy(alpha = 0.6f), RoundedCornerShape(22.dp))
            .padding(18.dp)
    ) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Text("${approval.agent} quer permissão", color = Orange, fontWeight = FontWeight.Bold, fontSize = 16.sp,
                modifier = Modifier.weight(1f))
            Text("${left}s", color = Muted, fontSize = 13.sp)
        }
        if (approval.project.isNotBlank()) Text(approval.project, color = Muted, fontSize = 13.sp)
        Text(
            approval.command,
            color = Color.White, fontFamily = FontFamily.Monospace, fontSize = 13.sp,
            modifier = Modifier.padding(top = 12.dp).fillMaxWidth().heightIn(max = 220.dp)
                .background(Color(0xFF0E0E10), RoundedCornerShape(12.dp)).padding(12.dp)
                .verticalScroll(rememberScrollState()),
        )
        if (approval.truncated) {
            Text("Comando longo demais para mostrar inteiro: só dá para permitir no PC.", color = Orange,
                fontSize = 12.sp, modifier = Modifier.padding(top = 8.dp))
        }
        Row(Modifier.padding(top = 14.dp), horizontalArrangement = Arrangement.spacedBy(10.dp)) {
            OutlinedButton(onClick = { onDeny(approval) }, modifier = Modifier.weight(1f).height(48.dp),
                shape = RoundedCornerShape(14.dp)) {
                Text("Negar", color = Red, fontWeight = FontWeight.SemiBold)
            }
            Button(onClick = { onAllow(approval) }, enabled = !approval.truncated,
                modifier = Modifier.weight(1f).height(48.dp), shape = RoundedCornerShape(14.dp),
                colors = ButtonDefaults.buttonColors(containerColor = Green, contentColor = Color.Black)) {
                Text("Permitir", fontWeight = FontWeight.SemiBold)
            }
        }
    }
}

@Composable
private fun SessionRow(session: Session) {
    val color = when (session.state) {
        "approval", "waiting" -> Orange
        "working" -> Blue
        "done" -> Green
        else -> Muted
    }
    Row(Modifier.fillMaxWidth().background(Card, RoundedCornerShape(16.dp)).padding(14.dp),
        verticalAlignment = Alignment.CenterVertically) {
        Box(Modifier.size(10.dp).background(color, CircleShape))
        Column(Modifier.padding(start = 12.dp)) {
            Text(
                if (session.project.isBlank()) session.agent else "${session.agent} · ${session.project}",
                color = Color.White, fontWeight = FontWeight.SemiBold, fontSize = 15.sp,
            )
            if (session.detail.isNotBlank())
                Text(session.detail, color = Muted, fontSize = 13.sp, maxLines = 2, overflow = TextOverflow.Ellipsis)
        }
    }
}
