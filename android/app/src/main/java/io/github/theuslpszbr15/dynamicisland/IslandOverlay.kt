package io.github.theuslpszbr15.dynamicisland

import android.app.KeyguardManager
import android.content.Context
import android.content.Intent
import android.graphics.BitmapFactory
import android.graphics.PixelFormat
import android.net.Uri
import android.os.Handler
import android.os.Looper
import android.provider.Settings
import android.view.Gravity
import android.view.MotionEvent
import android.view.WindowManager
import android.widget.Toast
import androidx.compose.animation.animateContentSize
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.detectDragGestures
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableLongStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.ComposeView
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleOwner
import androidx.lifecycle.LifecycleRegistry
import androidx.lifecycle.setViewTreeLifecycleOwner
import androidx.savedstate.SavedStateRegistry
import androidx.savedstate.SavedStateRegistryController
import androidx.savedstate.SavedStateRegistryOwner
import androidx.savedstate.setViewTreeSavedStateRegistryOwner
import kotlinx.coroutines.delay
import kotlin.math.abs

private val Black = Color(0xFF000000)
private val Card = Color(0xFF1C1C1E)
private val Muted = Color(0xFF8E8E93)
private val Blue = Color(0xFF0A84FF)
private val Orange = Color(0xFFFF9F0A)
private val Green = Color(0xFF30D158)
private val Red = Color(0xFFFF453A)
private val Discord = Color(0xFF5865F2)

/**
 * The floating island: a small black pill at the top of the screen, over any app, that opens into a card with
 * Pendentes, Aprovações, Transferências and Discord. It is an ordinary overlay window (`SYSTEM_ALERT_WINDOW`, one
 * switch in Android's settings), so it sits just under the status bar, never shows on the lock screen and Android
 * hides it over its own screens (installer, permission prompts). The window is only as big as the pill or the card,
 * so it never takes touches from the app underneath. Lives while the link to the PC runs, or while a Discord call
 * is going on this phone.
 */
object IslandOverlay {
    private const val PREFS = "island"
    private const val KEY_ON = "on"
    private const val KEY_Y = "y"

    private val main by lazy { Handler(Looper.getMainLooper()) }
    private var window: Shown? = null
    @Volatile private var linkRunning = false

    /** The person turned it on in the app (off until then). */
    fun isOn(context: Context) = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).getBoolean(KEY_ON, false)

    fun setOn(context: Context, on: Boolean) {
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit().putBoolean(KEY_ON, on).apply()
        refresh(context)
    }

    fun canDraw(context: Context) = Settings.canDrawOverlays(context)

    /** Android's "Sobrepor a outros apps" screen for this app. */
    fun permissionIntent(context: Context) =
        Intent(Settings.ACTION_MANAGE_OVERLAY_PERMISSION, Uri.parse("package:${context.packageName}"))
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)

    /** The link service started or stopped. */
    fun linkRunning(context: Context, running: Boolean) {
        linkRunning = running
        refresh(context)
    }

    /** Shows or removes the window to match the switch, the permission and whether there is anything to serve. */
    fun refresh(context: Context) {
        val app = context.applicationContext
        main.post {
            val want = isOn(app) && canDraw(app) && (linkRunning || Island.call.value != null)
            if (want && window == null) window = try { Shown(app) } catch (_: Exception) { null }
            else if (!want) { window?.remove(); window = null }
        }
    }

    private fun savedY(context: Context, fallback: Int) =
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).getInt(KEY_Y, fallback)

    private fun saveY(context: Context, y: Int) =
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit().putInt(KEY_Y, y).apply()

    /** The window, its view and the lifecycle Compose needs outside an activity. */
    private class Shown(val context: Context) : LifecycleOwner, SavedStateRegistryOwner {
        private val registry = LifecycleRegistry(this)
        private val saved = SavedStateRegistryController.create(this)
        override val lifecycle: Lifecycle get() = registry
        override val savedStateRegistry: SavedStateRegistry get() = saved.savedStateRegistry

        private val manager = context.getSystemService(WindowManager::class.java)
        private val density = context.resources.displayMetrics.density
        private val params = WindowManager.LayoutParams(
            WindowManager.LayoutParams.WRAP_CONTENT,
            WindowManager.LayoutParams.WRAP_CONTENT,
            WindowManager.LayoutParams.TYPE_APPLICATION_OVERLAY,
            WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE or WindowManager.LayoutParams.FLAG_WATCH_OUTSIDE_TOUCH,
            PixelFormat.TRANSLUCENT,
        ).apply {
            gravity = Gravity.TOP or Gravity.CENTER_HORIZONTAL
            y = savedY(context, (statusBar() + 4 * density).toInt())
            title = "Dynamic Island"
        }
        private val expanded = mutableStateOf(false)
        private val view = ComposeView(context)

        init {
            saved.performRestore(null)
            registry.handleLifecycleEvent(Lifecycle.Event.ON_CREATE)
            view.setViewTreeLifecycleOwner(this)
            view.setViewTreeSavedStateRegistryOwner(this)
            view.setContent {
                IslandUi(
                    expanded = expanded.value,
                    onToggle = { expanded.value = !expanded.value },
                    onDrag = ::drag,
                    onDragEnd = ::dragEnd,
                )
            }
            // a tap anywhere else closes the card
            view.setOnTouchListener { _, e ->
                if (e.action == MotionEvent.ACTION_OUTSIDE) expanded.value = false
                false
            }
            manager.addView(view, params)
            registry.handleLifecycleEvent(Lifecycle.Event.ON_START)
            registry.handleLifecycleEvent(Lifecycle.Event.ON_RESUME)
        }

        private fun statusBar(): Int {
            val id = context.resources.getIdentifier("status_bar_height", "dimen", "android")
            return if (id > 0) context.resources.getDimensionPixelSize(id) else (24 * density).toInt()
        }

        private fun drag(dx: Float, dy: Float) {
            params.x += dx.toInt()
            params.y = (params.y + dy.toInt()).coerceIn(0, context.resources.displayMetrics.heightPixels - (60 * density).toInt())
            try { manager.updateViewLayout(view, params) } catch (_: Exception) {}
        }

        /** Back to the middle; thrown to a side it hides until something new happens. */
        private fun dragEnd() {
            val width = context.resources.displayMetrics.widthPixels
            if (abs(params.x) > width * 0.33f) Hidden.hide()
            params.x = 0
            saveY(context, params.y)
            try { manager.updateViewLayout(view, params) } catch (_: Exception) {}
        }

        fun remove() {
            registry.handleLifecycleEvent(Lifecycle.Event.ON_DESTROY)
            try { manager.removeView(view) } catch (_: Exception) {}
        }
    }

    /** Thrown aside: stays hidden while the pill keeps saying the same thing. */
    object Hidden {
        val hiddenPill = mutableStateOf<Pill?>(null)
        internal var current: Pill? = null
        fun hide() { hiddenPill.value = current ?: Pill(Pill.Kind.Idle, "") }
    }
}

@Composable
private fun IslandUi(expanded: Boolean, onToggle: () -> Unit, onDrag: (Float, Float) -> Unit, onDragEnd: () -> Unit) {
    val context = LocalContext.current
    val link by Link.state.collectAsState()
    val outgoing by FileSender.waiting.collectAsState()
    val transfers by Island.transfers.collectAsState()
    val discord by Island.discord.collectAsState()
    val call by Island.call.collectAsState()
    var now by remember { mutableLongStateOf(System.currentTimeMillis()) }
    LaunchedEffect(Unit) { while (true) { delay(1_000); now = System.currentTimeMillis() } }

    val pill = Island.pill(link.approvals.size, link.files.size, outgoing.size, transfers, discord, call, now)
    IslandOverlay.Hidden.current = pill
    val hidden = IslandOverlay.Hidden.hiddenPill.value
    if (hidden != null) {
        if (hidden == pill) return
        IslandOverlay.Hidden.hiddenPill.value = null
    }

    val drag = Modifier.pointerInput(Unit) {
        detectDragGestures(onDragEnd = onDragEnd, onDragCancel = onDragEnd) { change, amount ->
            change.consume()
            onDrag(amount.x, amount.y)
        }
    }
    val screen = LocalConfiguration.current.screenWidthDp.dp
    Column(
        Modifier
            .clip(RoundedCornerShape(if (expanded) 28.dp else 50.dp))
            .background(Black)
            .animateContentSize()
            .then(if (expanded) Modifier.width(minOf(screen - 24.dp, 380.dp)) else Modifier)
    ) {
        PillRow(pill, call, now, drag.clickable(onClick = onToggle))
        if (expanded) Panel(context, pill, link, outgoing, transfers, discord, call, now)
    }
}

@Composable
private fun PillRow(pill: Pill, call: PhoneCall?, now: Long, modifier: Modifier) {
    val color = when (pill.kind) {
        Pill.Kind.Approval -> Orange
        Pill.Kind.Transfer -> Blue
        Pill.Kind.Discord, Pill.Kind.Call -> Discord
        Pill.Kind.Pending -> Green
        Pill.Kind.Idle -> Muted
    }
    Row(
        modifier.padding(horizontal = 14.dp, vertical = if (pill.kind == Pill.Kind.Idle) 6.dp else 9.dp).widthIn(min = 80.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.Center,
    ) {
        Box(Modifier.size(8.dp).background(color, CircleShape))
        if (pill.kind != Pill.Kind.Idle) {
            Text(pill.text, color = Color.White, fontSize = 13.sp, fontWeight = FontWeight.SemiBold, maxLines = 1,
                overflow = TextOverflow.Ellipsis, modifier = Modifier.padding(start = 8.dp).widthIn(max = 240.dp))
            if (pill.kind == Pill.Kind.Call && call != null)
                Text(elapsed(now - call.since), color = Muted, fontSize = 12.sp, modifier = Modifier.padding(start = 8.dp))
        }
    }
}

private enum class Tab(val label: String) { Pendentes("Pendentes"), Aprovacoes("Aprovações"), Transferencias("Transferências"), Discord("Discord") }

@Composable
private fun Panel(
    context: Context,
    pill: Pill,
    link: LinkUi,
    outgoing: List<OutgoingOffer>,
    transfers: List<LiveTransfer>,
    discord: List<DiscordEvent>,
    call: PhoneCall?,
    now: Long,
) {
    val first = when (pill.kind) {
        Pill.Kind.Approval -> Tab.Aprovacoes
        Pill.Kind.Transfer -> Tab.Transferencias
        Pill.Kind.Discord, Pill.Kind.Call -> Tab.Discord
        else -> Tab.Pendentes
    }
    var tab by remember { mutableIntStateOf(first.ordinal) }
    val counts = mapOf(
        Tab.Pendentes to link.files.size + outgoing.size,
        Tab.Aprovacoes to link.approvals.size,
        Tab.Transferencias to transfers.count { it.state == LiveTransfer.State.Running },
        Tab.Discord to if (call != null) 1 else 0,
    )
    Column(Modifier.padding(horizontal = 12.dp).padding(bottom = 12.dp)) {
        Row(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
            Tab.entries.forEach { t ->
                val selected = tab == t.ordinal
                val n = counts[t] ?: 0
                Text(
                    if (n > 0) "${t.label} $n" else t.label,
                    color = if (selected) Color.White else Muted,
                    fontSize = 12.sp, fontWeight = if (selected) FontWeight.SemiBold else FontWeight.Normal, maxLines = 1,
                    modifier = Modifier.clip(RoundedCornerShape(12.dp))
                        .background(if (selected) Card else Black)
                        .clickable { tab = t.ordinal }
                        .padding(horizontal = 9.dp, vertical = 6.dp),
                )
            }
        }
        Spacer(Modifier.size(8.dp))
        Column(Modifier.heightIn(max = 340.dp).verticalScroll(rememberScrollState()), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            when (Tab.entries[tab]) {
                Tab.Pendentes -> Pending(context, link, outgoing)
                Tab.Aprovacoes -> Approvals(context, link)
                Tab.Transferencias -> TransfersList(transfers)
                Tab.Discord -> DiscordTab(context, discord, call, now)
            }
        }
    }
}

@Composable
private fun Row2(title: String, detail: String, content: @Composable () -> Unit = {}) {
    Column(Modifier.clip(RoundedCornerShape(16.dp)).background(Card).padding(12.dp)) {
        Text(title, color = Color.White, fontSize = 14.sp, fontWeight = FontWeight.SemiBold, maxLines = 2, overflow = TextOverflow.Ellipsis)
        if (detail.isNotEmpty()) Text(detail, color = Muted, fontSize = 12.sp, maxLines = 3, overflow = TextOverflow.Ellipsis)
        content()
    }
}

@Composable
private fun Buttons(vararg buttons: Pair<String, () -> Unit>, colors: List<Color> = emptyList()) {
    Row(Modifier.padding(top = 8.dp), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
        buttons.forEachIndexed { i, (label, action) ->
            Text(label, color = colors.getOrNull(i) ?: Blue, fontSize = 13.sp, fontWeight = FontWeight.SemiBold,
                modifier = Modifier.clip(RoundedCornerShape(12.dp)).background(Black).clickable(onClick = action)
                    .padding(horizontal = 12.dp, vertical = 7.dp))
        }
    }
}

@Composable
private fun Empty(text: String) = Text(text, color = Muted, fontSize = 13.sp, modifier = Modifier.padding(4.dp))

@Composable
private fun Pending(context: Context, link: LinkUi, outgoing: List<OutgoingOffer>) {
    if (link.files.isEmpty() && outgoing.isEmpty()) return Empty("Nada esperando.")
    link.files.forEach { offer ->
        Row2("Do PC: ${if (offer.isText) "um texto longo" else offer.label}", Notifications.sizeText(offer.totalBytes)) {
            Buttons(
                "Aceitar" to { accept(context, offer.id) },
                "Recusar" to { Link.declineFile(context, offer.id) },
                colors = listOf(Green, Red),
            )
        }
    }
    outgoing.forEach { o ->
        Row2("Para o PC: ${o.label}", if (o.state == OutgoingOffer.QUEUED) "Na fila (sem conexão)" else "Esperando o PC aceitar") {
            Buttons("Cancelar" to { FileSender.cancel(context, o.id) }, colors = listOf(Red))
        }
    }
}

/** Receiving needs the phone unlocked, as from the notification. */
private fun accept(context: Context, id: String) {
    if (context.getSystemService(KeyguardManager::class.java)?.isKeyguardLocked == true) {
        Toast.makeText(context, "Desbloqueie o celular para aceitar", Toast.LENGTH_SHORT).show()
        return
    }
    Link.acceptFile(context, id)
}

@Composable
private fun Approvals(context: Context, link: LinkUi) {
    if (link.approvals.isEmpty()) return Empty("Nenhum agente esperando.")
    link.approvals.forEach { a ->
        Row2("${a.agent}${if (a.project.isBlank()) "" else " · ${a.project}"}", a.command) {
            Buttons(
                // "Permitir" always goes through the app and the screen lock, like the notification
                "Permitir…" to {
                    context.startActivity(Intent(context, MainActivity::class.java)
                        .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_SINGLE_TOP)
                        .putExtra(Notifications.EXTRA_FINGERPRINT, a.fingerprint)
                        .putExtra(Notifications.EXTRA_ALLOW, !a.truncated))
                },
                "Negar" to { Link.decide(context, a.fingerprint, allow = false) },
                colors = listOf(Green, Red),
            )
        }
    }
}

@Composable
private fun TransfersList(transfers: List<LiveTransfer>) {
    if (transfers.isEmpty()) return Empty("Nada indo ou vindo agora.")
    transfers.forEach { t ->
        Row2(t.title, t.text) {
            when (t.state) {
                LiveTransfer.State.Running -> if (t.percent >= 0)
                    LinearProgressIndicator(progress = { t.percent / 100f }, color = Blue, trackColor = Black,
                        modifier = Modifier.padding(top = 8.dp).width(260.dp))
                else LinearProgressIndicator(color = Blue, trackColor = Black, modifier = Modifier.padding(top = 8.dp).width(260.dp))
                LiveTransfer.State.Done -> Text("Pronto", color = Green, fontSize = 12.sp)
                LiveTransfer.State.Failed -> Text("Falhou", color = Red, fontSize = 12.sp)
            }
        }
    }
}

@Composable
private fun DiscordTab(context: Context, events: List<DiscordEvent>, call: PhoneCall?, now: Long) {
    if (call != null) {
        Row2(call.title.ifEmpty { "Na call do Discord" }, listOf(call.text, elapsed(now - call.since)).filter { it.isNotEmpty() }.joinToString(" · ")) {
            if (call.actions.isNotEmpty())
                Buttons(*call.actions.mapIndexed { i, label -> label to { DiscordListener.press(i); Unit } }.toTypedArray())
        }
    }
    if (events.isEmpty() && call == null)
        Empty("Quando alguém entrar ou sair da sua call, aparece aqui (a ilha do PC manda, com o Discord aberto lá).")
    events.forEach { e ->
        Row(Modifier.clip(RoundedCornerShape(16.dp)).background(Card).padding(10.dp), verticalAlignment = Alignment.CenterVertically) {
            Avatar(e)
            Column(Modifier.padding(start = 10.dp)) {
                Text(if (e.joined) "${e.name} entrou" else "${e.name} saiu", color = Color.White, fontSize = 14.sp,
                    fontWeight = FontWeight.SemiBold, maxLines = 1, overflow = TextOverflow.Ellipsis)
                Text(listOf(e.channel, if (e.joined && e.count > 0) "${e.count} na call" else "", ago(now - e.at))
                    .filter { it.isNotEmpty() }.joinToString(" · "), color = Muted, fontSize = 12.sp, maxLines = 1)
            }
        }
    }
    if (!DiscordListener.enabled(context))
        Row2("Call deste celular", "Para ver aqui a call que você faz pelo Discord do celular, ligue o acesso a notificações " +
            "(o app só lê a notificação de call do Discord).") {
            Buttons("Ligar" to {
                try { context.startActivity(DiscordListener.settingsIntent(context)) } catch (_: Exception) {}
            })
        }
}

@Composable
private fun Avatar(e: DiscordEvent) {
    val bitmap = remember(e) { e.avatar?.let { try { BitmapFactory.decodeByteArray(it, 0, it.size) } catch (_: Exception) { null } } }
    if (bitmap != null)
        Image(bitmap.asImageBitmap(), contentDescription = null, modifier = Modifier.size(36.dp).clip(CircleShape))
    else
        Box(Modifier.size(36.dp).background(Discord, CircleShape), contentAlignment = Alignment.Center) {
            Text(e.name.take(1).uppercase(), color = Color.White, fontSize = 15.sp, fontWeight = FontWeight.Bold)
        }
}

private fun elapsed(ms: Long): String {
    val s = (ms / 1000).coerceAtLeast(0)
    return if (s >= 3600) "%d:%02d:%02d".format(s / 3600, s / 60 % 60, s % 60) else "%d:%02d".format(s / 60, s % 60)
}

private fun ago(ms: Long): String {
    val s = ms / 1000
    return when {
        s < 60 -> "agora"
        s < 3600 -> "há ${s / 60} min"
        else -> "há ${s / 3600} h"
    }
}
