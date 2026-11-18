package io.github.theuslpszbr15.dynamicisland

import android.app.KeyguardManager
import android.graphics.BitmapFactory
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.border
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
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items as gridItems
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Badge
import androidx.compose.material3.BadgedBox
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Switch
import androidx.compose.material3.SwitchDefaults
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
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import kotlinx.coroutines.delay

@Composable
fun AppTheme(content: @Composable () -> Unit) = MaterialTheme(
    colorScheme = darkColorScheme(
        primary = Ui.Text, onPrimary = Ui.OnAction,
        background = Ui.Bg, onBackground = Ui.Text,
        surface = Ui.Card, onSurface = Ui.Text,
        surfaceVariant = Ui.Raised, onSurfaceVariant = Ui.Muted,
        surfaceContainerHigh = Ui.Raised, surfaceContainer = Ui.Card,
        secondaryContainer = Ui.Raised, onSecondaryContainer = Ui.Text,
        surfaceContainerHighest = Ui.Raised,
        outline = Ui.Muted, outlineVariant = Ui.Line,
        error = Ui.Danger,
    ),
    content = content,
)

/** Switch in the app's colours: off-white when on, dark when off. Used by every card of settings. */
@Composable
fun uiSwitchColors() = SwitchDefaults.colors(
    checkedThumbColor = Ui.OnAction, checkedTrackColor = Ui.Text, checkedBorderColor = Ui.Text,
    uncheckedThumbColor = Ui.Muted, uncheckedTrackColor = Ui.Raised, uncheckedBorderColor = Ui.Raised,
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
    onRemote: (String, Int) -> Unit = { _, _ -> },
) {
    val context = LocalContext.current
    var connecting by remember { mutableStateOf(false) }
    var showSettings by remember { mutableStateOf(false) }
    var setupDone by remember { mutableStateOf(Setup.done(context)) }
    Box(Modifier.fillMaxSize().background(Ui.Bg).statusBarsPadding().navigationBarsPadding()) {
        when {
            // first run: the island needs Android's permissions before anything else
            !setupDone -> SetupScreen(onDone = { Setup.finish(context); setupDone = true })
            showSettings -> SettingsScreen(state, onBack = { showSettings = false }, onUnpair, onRetry, onPause)
            // No PC needed to open the app: the island works on its own, pairing is the extra
            state.pairing == null && !connecting -> LocalScreen(onConnect = { connecting = true }, onSettings = { showSettings = true })
            state.pairing == null -> PairScreen(onScan, onPaste, onBack = { connecting = false })
            else -> HomeScreen(state, focused, onAllow, onDeny, onOpen, onRetry, onScan, onFixNotifications, onRemote,
                onSettings = { showSettings = true })
        }
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

// ───────────────────────── shared pieces ─────────────────────────

@Composable
private fun IconButton(icon: Int, onClick: () -> Unit) {
    Box(Modifier.size(40.dp).clip(RoundedCornerShape(14.dp)).background(Ui.Card).clickable(onClick = onClick), contentAlignment = Alignment.Center) {
        Icon(painterResource(icon), contentDescription = null, tint = Ui.Text, modifier = Modifier.size(19.dp))
    }
}

/** Top of every screen: where you are in serif, what is connected under it, and the way to the settings. */
@Composable
private fun Header(
    title: String,
    modifier: Modifier = Modifier,
    onBack: (() -> Unit)? = null,
    onSettings: (() -> Unit)? = null,
    subtitle: @Composable () -> Unit = {},
) {
    Row(modifier.fillMaxWidth().padding(start = 16.dp, end = 16.dp, top = 16.dp, bottom = 8.dp), verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(12.dp)) {
        if (onBack != null) IconButton(R.drawable.ic_back, onBack)
        Column(Modifier.weight(1f)) {
            UiTitle(title, size = 26)
            subtitle()
        }
        if (onSettings != null) IconButton(R.drawable.ic_gear, onSettings)
    }
}

@Composable
private fun StatusLine(state: LinkUi) {
    val (color, text) = when (state.status) {
        LinkStatus.Connected -> Ui.Ok to if (state.viaRelay) "Ligado pela internet" else "Ligado"
        LinkStatus.Connecting -> Ui.Warn to "Procurando o PC…"
        LinkStatus.Offline -> Ui.Danger to (state.message ?: "Sem conexão")
        LinkStatus.AuthFailed -> Ui.Danger to "Pareamento recusado"
        LinkStatus.Paused -> Ui.Muted to "Parado"
        LinkStatus.Unpaired -> Ui.Muted to ""
    }
    Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.padding(top = 2.dp)) {
        Box(Modifier.size(8.dp).background(color, CircleShape))
        Text(text, color = Ui.Muted, fontSize = 13.sp, maxLines = 1, overflow = TextOverflow.Ellipsis, modifier = Modifier.padding(start = 6.dp))
    }
}

@Composable
private fun Notice(text: String, action: String, onAction: () -> Unit) {
    UiCard {
        Text(text, color = Ui.Text, fontSize = 14.sp)
        UiLink(action, onAction)
    }
}

// ───────────────────────── screens without a PC ─────────────────────────

/** The app without a PC: the floating island and its settings, and "Conectar ao PC" for the rest. */
@Composable
private fun LocalScreen(onConnect: () -> Unit, onSettings: () -> Unit) {
    val context = LocalContext.current
    val missing = remember { missingPermissions(context) }
    val on = IslandOverlay.isOn(context)
    Column(Modifier.fillMaxSize()) {
        Header("Dynamic Island", onSettings = onSettings)
        Column(Modifier.weight(1f).verticalScroll(rememberScrollState()).padding(horizontal = 16.dp, vertical = 8.dp),
            verticalArrangement = Arrangement.spacedBy(12.dp)) {
            UiStat("Ilha", if (on) "Ligada" else "Desligada", Modifier.fillMaxWidth(),
                note = if (missing == 0) "Permissões em dia" else "Faltam $missing permissões", icon = R.drawable.ic_agent)
            UiCard {
                UiRow("Ajustes da ilha", note = "Formato, posição, foco e avisos", icon = R.drawable.ic_gear, onClick = onSettings) {
                    Icon(painterResource(R.drawable.ic_chevron), null, tint = Ui.Faint, modifier = Modifier.size(18.dp))
                }
            }
            UiCard {
                UiRow("Conectar ao PC", icon = R.drawable.ic_pc)
                UiPrimary("Conectar", onConnect, Modifier.fillMaxWidth())
            }
        }
    }
}

@Composable
private fun PairScreen(onScan: () -> Unit, onPaste: (String) -> Unit, onBack: () -> Unit) {
    var link by remember { mutableStateOf("") }
    Column(Modifier.fillMaxSize()) {
        Header("Conectar ao PC", onBack = onBack)
        Column(Modifier.weight(1f).verticalScroll(rememberScrollState()).padding(horizontal = 16.dp, vertical = 8.dp),
            verticalArrangement = Arrangement.spacedBy(12.dp)) {
            UiCard(Modifier, content = {
                Steps(
                    "No PC, clique com o botão direito na ilha, abra Configurações › Celular e ligue “Ligação com o celular”.",
                    "Celular e PC na mesma rede Wi-Fi.",
                    "Escaneie o código que aparece no PC.",
                )
            })
            OutlinedTextField(
                value = link, onValueChange = { link = it }, singleLine = true,
                label = { Text("ou cole o link (dynamicisland://…)") }, modifier = Modifier.fillMaxWidth(),
                shape = Ui.ButtonShape,
            )
        }
        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
            UiPrimary("Escanear código", onScan, Modifier.fillMaxWidth())
            UiSecondary("Parear com o link", { onPaste(link) }, Modifier.fillMaxWidth(), enabled = link.isNotBlank())
        }
    }
}

@Composable
private fun Steps(vararg steps: String) {
    Column(verticalArrangement = Arrangement.spacedBy(14.dp)) {
        steps.forEachIndexed { i, step ->
            if (i > 0) UiDivider()
            Row(horizontalArrangement = Arrangement.spacedBy(14.dp)) {
                Box(Modifier.size(26.dp).background(Ui.Raised, CircleShape), contentAlignment = Alignment.Center) {
                    Text("${i + 1}", color = Ui.Text, fontSize = 13.sp, fontWeight = FontWeight.SemiBold)
                }
                Text(step, color = Ui.Text, fontSize = 14.sp, modifier = Modifier.weight(1f))
            }
        }
    }
}

// ───────────────────────── settings ─────────────────────────

/** Everything that is a setting, behind the gear: the island, what it tells, updates and the PC link. */
@Composable
private fun SettingsScreen(
    state: LinkUi,
    onBack: () -> Unit,
    onUnpair: () -> Unit,
    onRetry: () -> Unit,
    onPause: () -> Unit,
) {
    var confirmUnpair by remember { mutableStateOf(false) }
    Column(Modifier.fillMaxSize()) {
        Header("Ajustes", onBack = onBack)
        Column(Modifier.weight(1f).verticalScroll(rememberScrollState()).padding(horizontal = 16.dp, vertical = 8.dp),
            verticalArrangement = Arrangement.spacedBy(12.dp)) {
            if (state.pairing != null) {
                UiCard {
                    UiRow(state.desktop.ifEmpty { state.pairing.name }, icon = R.drawable.ic_pc) { StatusLine(state) }
                    when (state.status) {
                        LinkStatus.Connecting, LinkStatus.Offline -> UiSecondary("Parar de procurar", onPause, Modifier.fillMaxWidth())
                        LinkStatus.Connected -> UiSecondary("Desconectar", onPause, Modifier.fillMaxWidth())
                        LinkStatus.Paused -> UiPrimary("Procurar o PC", onRetry, Modifier.fillMaxWidth())
                        else -> {}
                    }
                    UiSecondary("Desparear deste PC", { confirmUnpair = true }, Modifier.fillMaxWidth(), danger = true)
                }
            }
            IslandSettingsCard()
            IslandFilterCard()
            AppUpdateCard()
            Spacer(Modifier.height(8.dp))
        }
    }
    if (confirmUnpair) {
        AlertDialog(
            onDismissRequest = { confirmUnpair = false },
            title = { Text("Desparear deste PC?") },
            text = { Text("O celular esquece ${state.pairing?.name ?: "o PC"}. Para ligar de novo, escaneie o código nas Configurações do PC.") },
            confirmButton = { TextButton(onClick = { confirmUnpair = false; onUnpair() }) { Text("Desparear", color = Ui.Danger) } },
            dismissButton = { TextButton(onClick = { confirmUnpair = false }) { Text("Cancelar") } },
        )
    }
}

// ───────────────────────── with a PC ─────────────────────────

@Composable
private fun HomeScreen(
    state: LinkUi,
    focused: String?,
    onAllow: (Approval) -> Unit,
    onDeny: (Approval) -> Unit,
    onOpen: (TrayItem) -> Unit,
    onRetry: () -> Unit,
    onScan: () -> Unit,
    onFixNotifications: () -> Unit,
    onRemote: (String, Int) -> Unit,
    onSettings: () -> Unit,
) {
    var tab by remember { mutableStateOf(state.initialTab) }
    val records by History.records.collectAsState()
    LaunchedEffect(focused) { if (focused != null) tab = 0 }
    val title = when (tab) { 2 -> "Enviar"; 3 -> "Histórico"; else -> state.desktop.ifEmpty { state.pairing?.name ?: "PC" } }
    Column(Modifier.fillMaxSize()) {
        Header(title, onSettings = onSettings) { StatusLine(state) }
        Box(Modifier.weight(1f)) {
            when (tab) {
                0 -> AgentsTab(state, focused, onAllow, onDeny, onRetry, onScan, onFixNotifications)
                2 -> SendTab(state)
                3 -> HistoryTab(records, state)
                else -> PcTab(state, onOpen, onRemote)
            }
        }
        NavPill(tab, state.approvals.size, onTab = { tab = it })
    }
}

/** The floating bar: four places, the current one lit. */
@Composable
private fun NavPill(tab: Int, approvals: Int, onTab: (Int) -> Unit) {
    val items = listOf(
        Triple(0, "Agentes", R.drawable.ic_agent),
        Triple(2, "Enviar", R.drawable.ic_tab_send),
        Triple(3, "Histórico", R.drawable.ic_tab_history),
        Triple(1, "PC", R.drawable.ic_pc),
    )
    Box(Modifier.fillMaxWidth().padding(top = 8.dp, bottom = 12.dp), contentAlignment = Alignment.Center) {
        Row(Modifier.background(Ui.Card, RoundedCornerShape(30.dp)).padding(8.dp), horizontalArrangement = Arrangement.spacedBy(4.dp)) {
            items.forEach { (index, label, icon) ->
                val on = tab == index
                Column(
                    Modifier.width(72.dp).clip(RoundedCornerShape(22.dp))
                        .background(if (on) Color.White.copy(alpha = 0.11f) else Color.Transparent)
                        .clickable { onTab(index) }.padding(vertical = 8.dp),
                    horizontalAlignment = Alignment.CenterHorizontally,
                    verticalArrangement = Arrangement.spacedBy(3.dp),
                ) {
                    BadgedBox(badge = { if (index == 0 && approvals > 0) Badge { Text("$approvals") } }) {
                        Icon(painterResource(icon), contentDescription = null, tint = if (on) Ui.Text else Ui.Muted, modifier = Modifier.size(21.dp))
                    }
                    Text(label, color = if (on) Ui.Text else Ui.Muted, fontSize = 11.sp, fontWeight = if (on) FontWeight.SemiBold else FontWeight.Medium)
                }
            }
        }
    }
}

@Composable
private fun AgentsTab(
    state: LinkUi,
    focused: String?,
    onAllow: (Approval) -> Unit,
    onDeny: (Approval) -> Unit,
    onRetry: () -> Unit,
    onScan: () -> Unit,
    onFixNotifications: () -> Unit,
) {
    val approvals = state.approvals.sortedByDescending { it.fingerprint == focused }
    val connected = state.status == LinkStatus.Connected
    val live = state.sessions.count { it.state == "working" || it.state == "approval" || it.state == "waiting" }
    val waiting = state.sessions.count { it.state == "approval" || it.state == "waiting" }
    LazyColumn(Modifier.fillMaxSize().padding(horizontal = 16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
        item { Spacer(Modifier.height(0.dp)) }
        if (state.notificationsOff) item {
            Notice("As notificações estão desligadas: os pedidos de permissão chegam sem aviso e só aparecem com o app aberto.",
                "Ativar notificações", onFixNotifications)
        }
        state.update?.let { update ->
            item {
                val context = LocalContext.current
                Notice("Tem versão nova do app (${update.name}). Atualize para tudo funcionar igual à ilha do PC.", "Atualizar") {
                    AppUpdate.open(context, update.url)
                }
            }
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
        if (connected) item {
            Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                UiStat("Agentes ativos", "$live", Modifier.weight(1f).heightIn(min = 124.dp),
                    note = if (waiting > 0) "$waiting esperando você" else null, icon = R.drawable.ic_agent)
                UiStat("Pedidos", "${approvals.size}", Modifier.weight(1f).heightIn(min = 124.dp),
                    unit = if (approvals.isNotEmpty()) "agora" else null, icon = R.drawable.ic_bell)
            }
        }
        items(approvals, key = { it.fingerprint }) { approval ->
            ApprovalCard(approval, onAllow, onDeny)
        }
        if (connected) {
            item { UiSection("Em andamento") }
            if (state.sessions.isEmpty()) item {
                Text("Nenhum agente rodando agora.", color = Ui.Muted, fontSize = 14.sp, modifier = Modifier.padding(start = 4.dp))
            } else item {
                UiCard {
                    state.sessions.forEachIndexed { i, session ->
                        if (i > 0) UiDivider()
                        SessionRow(session)
                    }
                }
            }
        }
        item { Spacer(Modifier.height(8.dp)) }
    }
}

@Composable
private fun PcTab(state: LinkUi, onOpen: (TrayItem) -> Unit, onRemote: (String, Int) -> Unit) {
    val context = LocalContext.current
    val connected = state.status == LinkStatus.Connected
    var clipOn by remember { mutableStateOf(ClipSync.enabled(context)) }
    var callsOn by remember { mutableStateOf(Calls.enabled(context)) }
    val askCalls = androidx.activity.compose.rememberLauncherForActivityResult(
        androidx.activity.result.contract.ActivityResultContracts.RequestMultiplePermissions()) { result ->
        val ok = result[android.Manifest.permission.READ_PHONE_STATE] == true
        Calls.setEnabled(context, ok)
        callsOn = Calls.enabled(context)
        if (ok) LinkService.start(context)
    }
    LazyVerticalGrid(
        columns = GridCells.Adaptive(104.dp),
        horizontalArrangement = Arrangement.spacedBy(10.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp),
        modifier = Modifier.fillMaxSize().padding(horizontal = 16.dp),
    ) {
        val full: androidx.compose.foundation.lazy.grid.LazyGridItemSpanScope.() -> androidx.compose.foundation.lazy.grid.GridItemSpan =
            { androidx.compose.foundation.lazy.grid.GridItemSpan(maxLineSpan) }
        if (!connected) item(span = full) { WakeCard() }
        if (connected) item(span = full) { RemoteCard(state.pc, onRemote) }
        item(span = full) {
            UiCard {
                UiRow("Área de transferência", note = "Copiou num lado, aparece no outro.") {
                    Switch(checked = clipOn, onCheckedChange = { ClipSync.setEnabled(context, it); clipOn = it }, colors = uiSwitchColors())
                }
                UiDivider()
                UiRow("Ligações na ilha do PC", note = "Pede acesso ao telefone e aos contatos.") {
                    Switch(checked = callsOn, onCheckedChange = {
                        if (it) askCalls.launch(Calls.permissions) else { Calls.setEnabled(context, false); callsOn = false }
                    }, colors = uiSwitchColors())
                }
                UiDivider()
                UiLink("Enviar copiado agora", {
                    val sent = ClipSync.offer(context, force = true)
                    android.widget.Toast.makeText(context, if (sent) "Copiado enviado para o PC" else "Nada de texto copiado para enviar",
                        android.widget.Toast.LENGTH_SHORT).show()
                })
            }
        }
        item(span = full) { UiSection("Fixados na ilha") }
        if (connected && state.tray.isEmpty()) item(span = full) {
            Text("A bandeja está vazia (ou desligada para o celular nas Configurações do PC). " +
                "Arraste arquivos ou apps até a ilha para fixá-los.", color = Ui.Muted, fontSize = 14.sp, modifier = Modifier.padding(start = 4.dp))
        }
        gridItems(state.tray, key = { it.id }) { TrayTile(it, onOpen) }
        item(span = full) { Spacer(Modifier.height(8.dp)) }
    }
}

@Composable
private fun WakeCard() {
    val context = LocalContext.current
    val mac = remember { Wake.mac(context) }
    var external by remember { mutableStateOf(Wake.external(context)) }
    UiCard {
        UiCardTitle("O PC está desligado?",
            if (mac == null) "Para ligar o PC pelo celular, conecte uma vez com ele ligado: o app guarda o endereço da placa de rede. " +
                "No PC: Configurações › Celular › Ligar o PC pelo celular."
            else "Com o celular no Wi-Fi de casa, o botão liga o PC (Wake-on-LAN, placa $mac). " +
                "De fora de casa, só se o roteador repassar o pacote para o PC.")
        UiPrimary("Ligar o PC", {
            Wake.send(context) { sent ->
                android.os.Handler(android.os.Looper.getMainLooper()).post {
                    android.widget.Toast.makeText(context,
                        if (sent > 0) "Pacote enviado: o PC deve ligar em até um minuto" else "Não consegui enviar (sem rede?)",
                        android.widget.Toast.LENGTH_LONG).show()
                }
            }
        }, Modifier.fillMaxWidth(), enabled = mac != null)
        if (mac != null) OutlinedTextField(
            value = external, onValueChange = { external = it; Wake.setExternal(context, it) }, singleLine = true,
            label = { Text("Fora de casa: endereço do roteador (opcional)") },
            placeholder = { Text("meupc.ddns.net:9") },
            modifier = Modifier.fillMaxWidth(), shape = Ui.ButtonShape,
        )
    }
}

@Composable
private fun RemoteCard(pc: PcState?, onRemote: (String, Int) -> Unit) {
    Column(verticalArrangement = Arrangement.spacedBy(12.dp)) {
        Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
            Column(Modifier.weight(1f).background(Ui.Card, Ui.CardShape).padding(16.dp).heightIn(min = 112.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
                Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    Icon(painterResource(R.drawable.ic_music), null, tint = Ui.Muted, modifier = Modifier.size(16.dp))
                    Text("Tocando", color = Ui.Muted, fontSize = 13.sp, fontWeight = FontWeight.Medium)
                }
                if (pc != null && pc.title.isNotEmpty()) {
                    Text(pc.title, color = Ui.Text, fontSize = 16.sp, fontWeight = FontWeight.SemiBold, maxLines = 2, overflow = TextOverflow.Ellipsis)
                    if (pc.artist.isNotEmpty()) Text(pc.artist, color = Ui.Muted, fontSize = 13.sp, maxLines = 1, overflow = TextOverflow.Ellipsis)
                } else Text("Nada tocando no PC", color = Ui.Muted, fontSize = 14.sp)
            }
            Column(Modifier.weight(1f).background(Ui.Card, Ui.CardShape).padding(16.dp).heightIn(min = 112.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
                Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    Icon(painterResource(R.drawable.ic_volume), null, tint = Ui.Muted, modifier = Modifier.size(16.dp))
                    Text("Volume", color = Ui.Muted, fontSize = 13.sp, fontWeight = FontWeight.Medium)
                }
                Text(when { pc == null -> "—"; pc.muted -> "Mudo"; else -> "${pc.volume}%" }, color = Ui.Text, fontSize = 32.sp, fontWeight = FontWeight.SemiBold)
            }
        }
        UiCard {
            Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                RemoteKey(R.drawable.ic_prev, Modifier.weight(1f)) { onRemote("prev", 0) }
                RemoteKey(if (pc?.playing == true) R.drawable.ic_pause else R.drawable.ic_play, Modifier.weight(1f), main = true) { onRemote("play", 0) }
                RemoteKey(R.drawable.ic_next, Modifier.weight(1f)) { onRemote("next", 0) }
            }
            Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                RemoteKey(R.drawable.ic_minus, Modifier.weight(1f)) { onRemote("voldown", 0) }
                RemoteKey(R.drawable.ic_plus, Modifier.weight(1f)) { onRemote("volup", 0) }
                RemoteKey(if (pc?.muted == true) R.drawable.ic_volume else R.drawable.ic_mute, Modifier.weight(1f)) { onRemote("mute", 0) }
            }
        }
        UiSection("Energia")
        UiCard {
            val shutdownAt = pc?.shutdownAt ?: 0L
            if (shutdownAt > System.currentTimeMillis()) {
                val at = java.text.SimpleDateFormat("HH:mm", java.util.Locale.ROOT).format(java.util.Date(shutdownAt))
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Text("O PC desliga às $at", color = Ui.Warn, fontSize = 14.sp, modifier = Modifier.weight(1f))
                    UiLink("Cancelar", { onRemote("cancelShutdown", 0) })
                }
            }
            UiSecondary("Bloquear o PC", { onRemote("lock", 0) }, Modifier.fillMaxWidth())
            Text("Desligar daqui a", color = Ui.Muted, fontSize = 13.sp)
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                listOf(15 to "15 min", 30 to "30 min", 60 to "1 h", 120 to "2 h").forEach { (minutes, label) ->
                    UiSecondary(label, { onRemote("shutdown", minutes) }, Modifier.weight(1f), height = 44.dp)
                }
            }
        }
    }
}

@Composable
private fun RemoteKey(icon: Int, modifier: Modifier, main: Boolean = false, onClick: () -> Unit) {
    Box(modifier.height(48.dp).clip(Ui.ButtonShape).background(if (main) Ui.Text else Ui.Raised).clickable(onClick = onClick),
        contentAlignment = Alignment.Center) {
        Icon(painterResource(icon), contentDescription = null, tint = if (main) Ui.OnAction else Ui.Text, modifier = Modifier.size(20.dp))
    }
}

@Composable
private fun TrayTile(item: TrayItem, onOpen: (TrayItem) -> Unit) {
    val icon = remember(item.id) {
        item.icon?.let { BitmapFactory.decodeByteArray(it, 0, it.size)?.asImageBitmap() }
    }
    Column(
        Modifier.fillMaxWidth().height(132.dp)
            .background(Ui.Card, RoundedCornerShape(20.dp))
            .clickable { onOpen(item) }
            .padding(10.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.Center,
    ) {
        if (icon != null) Image(icon, contentDescription = null, modifier = Modifier.size(44.dp))
        else Icon(painterResource(R.drawable.ic_tab_tray), contentDescription = null, tint = Ui.Muted, modifier = Modifier.size(40.dp))
        Text(item.name, color = Ui.Text, fontSize = 12.5.sp, maxLines = 2, overflow = TextOverflow.Ellipsis,
            textAlign = TextAlign.Center, modifier = Modifier.padding(top = 8.dp))
        Text(when (item.kind) { "app" -> "app"; "folder" -> "pasta"; else -> "arquivo" }, color = Ui.Muted, fontSize = 11.sp)
    }
}

@Composable
private fun ApprovalCard(approval: Approval, onAllow: (Approval) -> Unit, onDeny: (Approval) -> Unit) {
    var now by remember { mutableLongStateOf(System.currentTimeMillis()) }
    LaunchedEffect(approval.fingerprint) {
        while (true) { delay(1000); now = System.currentTimeMillis() }
    }
    val left = ((Link.APPROVAL_WINDOW_MS - (now - approval.receivedAt)) / 1000).coerceAtLeast(0)
    // The app also shows over the lock screen (for the agents' requests), so the command is held back there:
    // only who is asking and what for; the command itself waits for an unlock (same rule as the media viewer).
    val context = LocalContext.current
    val keyguard = remember { context.getSystemService(KeyguardManager::class.java) }
    var unlocked by remember(approval.fingerprint) { mutableStateOf(false) }
    val locked = keyguard?.isKeyguardLocked == true && !unlocked
    Column(
        Modifier.fillMaxWidth()
            .background(Ui.Card, Ui.CardShape)
            .border(1.dp, Ui.Warn.copy(alpha = 0.7f), Ui.CardShape)
            .padding(16.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(12.dp)) {
            UiChip(R.drawable.ic_agent, tint = Ui.Warn)
            Column(Modifier.weight(1f)) {
                Text("${approval.agent} pede permissão", color = Ui.Text, fontWeight = FontWeight.SemiBold, fontSize = 16.sp)
                if (approval.project.isNotBlank()) Text(approval.project, color = Ui.Muted, fontSize = 13.sp)
            }
            UiTag("${left} s", Tone.Warn)
        }
        if (locked) {
            Text("O celular está bloqueado: desbloqueie para ler o comando antes de permitir.", color = Ui.Warn, fontSize = 13.sp)
            UiLink("Desbloquear", { whenUnlocked(context) { unlocked = true } })
        } else {
            Text(
                approval.command,
                color = Ui.Text, fontFamily = FontFamily.Monospace, fontSize = 13.sp,
                modifier = Modifier.fillMaxWidth().heightIn(max = 220.dp)
                    .background(Ui.Bg, RoundedCornerShape(14.dp)).padding(12.dp)
                    .verticalScroll(rememberScrollState()),
            )
            if (approval.truncated) {
                Text("Comando longo demais para mostrar inteiro: só dá para permitir no PC.", color = Ui.Warn, fontSize = 13.sp)
            }
            Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                UiSecondary("Negar", { onDeny(approval) }, Modifier.weight(1f), danger = true)
                UiPrimary("Permitir", { onAllow(approval) }, Modifier.weight(1f), enabled = !approval.truncated)
            }
        }
    }
}

@Composable
private fun SessionRow(session: Session) {
    val (label, tone) = when (session.state) {
        "approval", "waiting" -> "Aguardando" to Tone.Warn
        "working" -> "Trabalhando" to Tone.Neutral
        "done" -> "Concluído" to Tone.Ok
        else -> "Parado" to Tone.Neutral
    }
    UiRow(
        title = session.agent,
        note = listOf(session.project, session.detail).filter { it.isNotBlank() }.joinToString(" · ").ifEmpty { null },
        icon = R.drawable.ic_agent,
    ) { UiTag(label, tone) }
}
