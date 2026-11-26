package io.github.theuslpszbr15.dynamicisland

import android.os.Bundle
import android.view.WindowManager
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.awaitFirstDown
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.RowScope
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.Icon
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.focus.FocusRequester
import androidx.compose.ui.focus.focusRequester
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardCapitalization
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import java.util.concurrent.Executors
import java.util.concurrent.ScheduledFuture
import java.util.concurrent.TimeUnit
import kotlin.math.abs
import kotlin.math.hypot
import kotlin.math.min

/**
 * The phone as the PC's touchpad and keyboard, plus a movie and a slides remote. Moves and scrolls are summed and sent
 * at most every 16 ms; clicks and keys go on the same thread right after the move before them, so they land where the
 * pointer is. The PC types the text as keystrokes (its clipboard stays as it was).
 */
object Touchpad {
    internal val worker = Executors.newSingleThreadScheduledExecutor { Thread(it, "touchpad").apply { isDaemon = true } }
    private val lock = Any()
    private var dx = 0f
    private var dy = 0f
    private var wheel = 0f
    private var pump: ScheduledFuture<*>? = null

    /** Pixels of the PC per pixel of the phone, at slow speed; faster swipes go further (see [accelerate]). */
    const val SPEED = 1.1f
    /** Wheel units (120 = a notch) per pixel of two fingers. */
    const val SCROLL = 1.6f

    /** Faster finger, further pointer: 1× when slow, up to 2.5× on a quick swipe (per touch event, ~8 ms). */
    fun accelerate(x: Float, y: Float): Pair<Float, Float> {
        val gain = SPEED * (1f + min(hypot(x, y) / 24f, 1.5f))
        return x * gain to y * gain
    }

    /** What changed in the keyboard box: [backspaces] to erase, then [text] to type (autocorrect replaces a word). */
    data class Edit(val backspaces: Int, val text: String)

    fun diff(old: String, new: String): Edit {
        var same = 0
        while (same < old.length && same < new.length && old[same] == new[same]) same++
        return Edit(old.length - same, new.substring(same))
    }

    fun start() = synchronized(lock) {
        if (pump == null) pump = worker.scheduleWithFixedDelay(::flush, 16, 16, TimeUnit.MILLISECONDS)
    }

    fun stop() = synchronized(lock) {
        pump?.cancel(false)
        pump = null
        dx = 0f; dy = 0f; wheel = 0f
    }

    fun move(x: Float, y: Float) = synchronized(lock) { dx += x; dy += y }

    fun scroll(amount: Float) = synchronized(lock) { wheel += amount }

    fun click(button: String, act: String = "click") = later { c -> c.button(button, act) }

    fun key(name: String) = later { c -> c.key(name) }

    fun type(text: String) {
        if (text.isEmpty()) return
        text.chunked(200).forEach { part -> later { c -> c.text(part) } }
    }

    fun erase(count: Int) = repeat(count.coerceAtMost(200)) { key("backspace") }

    /** Media and volume through the remote control the app already has. */
    fun remote(action: String) = later { c -> c.remote(action) }

    private fun later(send: (LinkClient) -> Unit) {
        worker.execute {
            flush()
            val c = Link.client ?: return@execute
            try { send(c) } catch (_: Exception) {}
        }
    }

    /** Sends the whole pixels and wheel units summed so far; the fractions wait for the next turn. */
    private fun flush() {
        val (mx, my, sw) = synchronized(lock) {
            val x = dx.toInt(); val y = dy.toInt(); val w = wheel.toInt()
            dx -= x; dy -= y; wheel -= w
            Triple(x, y, w)
        }
        if (mx == 0 && my == 0 && sw == 0) return
        val c = Link.client ?: return
        try {
            if (mx != 0 || my != 0) c.mouse(mx, my)
            if (sw != 0) c.scroll(sw)
        } catch (_: Exception) {}
    }
}

class TouchpadActivity : ComponentActivity() {
    companion object {
        /** Which tab opens: 0 Mouse, 1 Filme, 2 Slides, 3 Jogo. */
        const val MODE = "mode"
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        val mode = intent.getIntExtra(MODE, 0).coerceIn(0, 3)
        setContent {
            val state by Link.state.collectAsStateWithLifecycle()
            AppTheme {
                TouchpadScreen(connected = state.status == LinkStatus.Connected, viaRelay = state.viaRelay, padProblem = state.padProblem,
                    onClose = ::finish, initialMode = mode)
            }
        }
    }

    override fun onResume() {
        super.onResume()
        Touchpad.start()
        if (Gamepad.wanted) Gamepad.start()
    }

    override fun onDestroy() {
        Gamepad.wanted = false
        super.onDestroy()
    }

    override fun onPause() {
        Touchpad.stop()
        Gamepad.stop()
        super.onPause()
    }
}

/** Mouse (touchpad, buttons, keyboard), Filme, Slides and Jogo (a game controller in landscape, with its layout editor). */
@Composable
fun TouchpadScreen(connected: Boolean, viaRelay: Boolean, onClose: () -> Unit, initialMode: Int = 0, padProblem: String? = null,
                   initialEditing: Boolean = false, initialPad: PadLayout? = null) {
    var mode by androidx.compose.runtime.saveable.rememberSaveable { mutableIntStateOf(initialMode) }
    val context = androidx.compose.ui.platform.LocalContext.current
    val activity = context as? android.app.Activity
    var ps4 by remember { mutableStateOf(Gamepad.kind(context)) }
    var pad by remember(ps4) { mutableStateOf(initialPad ?: PadLayout.load(context, ps4)) }
    var editing by androidx.compose.runtime.saveable.rememberSaveable { mutableStateOf(initialEditing) }
    LaunchedEffect(mode) {
        activity?.requestedOrientation = if (mode == 3) android.content.pm.ActivityInfo.SCREEN_ORIENTATION_SENSOR_LANDSCAPE
            else android.content.pm.ActivityInfo.SCREEN_ORIENTATION_UNSPECIFIED
        if (mode == 3) Gamepad.start() else Gamepad.stop()
        Gamepad.wanted = mode == 3
    }
    Column(
        Modifier.fillMaxSize().background(Ui.Bg).statusBarsPadding().navigationBarsPadding().imePadding().padding(16.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        if (mode != 3) Row(verticalAlignment = Alignment.CenterVertically) {
            UiTitle("Controle do PC", Modifier.weight(1f), size = 24)
            UiLink("Fechar", onClose)
        }
        when {
            !connected -> UiTag("PC desconectado: os toques não chegam", Tone.Bad)
            viaRelay -> UiTag("Pela internet: o mouse pode atrasar um pouco", Tone.Warn)
            mode == 3 && padProblem != null && !editing -> UiTag(padProblem, Tone.Warn)
        }
        if (mode == 3 && editing) PadToolbar(pad, { pad = it }, onSave = {
            PadLayout.save(context, ps4, pad)
            editing = false
        })
        else Row(Modifier.fillMaxWidth().background(Ui.Card, Ui.ButtonShape).padding(4.dp), horizontalArrangement = Arrangement.spacedBy(4.dp),
            verticalAlignment = Alignment.CenterVertically) {
            listOf("Mouse", "Filme", "Slides", "Jogo").forEachIndexed { i, label ->
                Box(
                    Modifier.weight(1f).height(40.dp).clip(RoundedCorner).background(if (mode == i) Ui.Raised else Ui.Card).clickable { mode = i },
                    contentAlignment = Alignment.Center,
                ) { Text(label, color = if (mode == i) Ui.Text else Ui.Muted, fontWeight = FontWeight.SemiBold, fontSize = 14.sp) }
            }
            if (mode == 3) {
                listOf(false to "Xbox", true to "PS4").forEach { (kind, label) ->
                    Box(Modifier.width(64.dp).height(40.dp).clip(RoundedCorner).background(if (ps4 == kind) Ui.Raised else Ui.Card)
                        .clickable { ps4 = kind; Gamepad.setKind(context, kind) }, contentAlignment = Alignment.Center) {
                        Text(label, color = if (ps4 == kind) Ui.Text else Ui.Muted, fontWeight = FontWeight.SemiBold, fontSize = 14.sp)
                    }
                }
                UiLink("Editar layout", { editing = true }, Modifier.padding(horizontal = 8.dp))
                UiLink("Fechar", onClose, Modifier.padding(end = 8.dp))
            }
        }
        when (mode) {
            0 -> MouseMode(Modifier.weight(1f))
            1 -> MovieMode(Modifier.weight(1f))
            2 -> SlidesMode(Modifier.weight(1f))
            else -> GamepadMode(ps4, pad, editing, { pad = it }, Modifier.weight(1f))
        }
    }
}

/** While editing the pad: a layout to start from, size and see-through of everything, tilt steering, back to the default, save. */
@Composable
private fun PadToolbar(layout: PadLayout, onLayout: (PadLayout) -> Unit, onSave: () -> Unit) {
    var presets by remember { mutableStateOf(false) }
    Row(Modifier.fillMaxWidth().background(Ui.Card, Ui.ButtonShape).padding(6.dp), horizontalArrangement = Arrangement.spacedBy(10.dp),
        verticalAlignment = Alignment.CenterVertically) {
        Box {
            KeyButton("Layout ▾", Modifier.width(104.dp), 40) { presets = true }
            androidx.compose.material3.DropdownMenu(expanded = presets, onDismissRequest = { presets = false }) {
                PadLayout.Presets.forEach { (name, preset) ->
                    androidx.compose.material3.DropdownMenuItem(text = { Text(name) }, onClick = {
                        onLayout(preset.copy(scale = layout.scale, alpha = layout.alpha))
                        presets = false
                    })
                }
            }
        }
        Text("Tamanho", color = Ui.Muted, fontSize = 13.sp)
        val sliderColors = androidx.compose.material3.SliderDefaults.colors(thumbColor = Ui.Text, activeTrackColor = Ui.Text,
            inactiveTrackColor = Ui.Raised, activeTickColor = Ui.Text, inactiveTickColor = Ui.Raised)
        androidx.compose.material3.Slider(layout.scale, { onLayout(layout.copy(scale = it)) }, Modifier.width(110.dp),
            valueRange = PadLayout.MIN_SCALE..PadLayout.MAX_SCALE, colors = sliderColors)
        Text("Transparência", color = Ui.Muted, fontSize = 13.sp)
        androidx.compose.material3.Slider(1f - layout.alpha + PadLayout.MIN_ALPHA, { onLayout(layout.copy(alpha = 1f - it + PadLayout.MIN_ALPHA)) },
            Modifier.width(110.dp), valueRange = PadLayout.MIN_ALPHA..1f, colors = sliderColors)
        Box(
            Modifier.height(40.dp).clip(RoundedCorner).background(if (layout.gyro) Ui.Ok.copy(alpha = 0.18f) else Ui.Raised)
                .clickable { onLayout(layout.copy(gyro = !layout.gyro)) }.padding(horizontal = 12.dp),
            contentAlignment = Alignment.Center,
        ) { Text(if (layout.gyro) "Volante: ligado" else "Volante: desligado", color = if (layout.gyro) Ui.Ok else Ui.Text, fontWeight = FontWeight.SemiBold, fontSize = 14.sp) }
        Box(Modifier.weight(1f))
        KeyButton("Restaurar", Modifier.width(96.dp), 40) { onLayout(PadLayout.Default.copy(scale = 1f, alpha = 1f)) }
        UiPrimary("Salvar", onSave, Modifier.width(90.dp), height = 40.dp)
    }
}

private val RoundedCorner = androidx.compose.foundation.shape.RoundedCornerShape(12.dp)

@Composable
private fun MouseMode(modifier: Modifier) {
    var keyboard by remember { mutableStateOf(false) }
    Column(modifier, verticalArrangement = Arrangement.spacedBy(10.dp)) {
        Box(
            Modifier.weight(1f).fillMaxWidth().background(Ui.Card, Ui.CardShape).border(1.dp, Ui.Line, Ui.CardShape).pointerInput(Unit) {
                awaitEachGesture {
                    val first = awaitFirstDown(requireUnconsumed = false)
                    val start = first.uptimeMillis
                    var end = start
                    var travel = 0f
                    var fingers = 1
                    var scrolling = false
                    do {
                        val event = awaitPointerEvent()
                        val down = event.changes.filter { it.pressed }
                        fingers = maxOf(fingers, down.size)
                        if (down.size >= 2) {
                            scrolling = true
                            val dy = down.map { it.position.y - it.previousPosition.y }.average().toFloat()
                            travel += abs(dy)
                            Touchpad.scroll(dy * Touchpad.SCROLL)
                        } else if (down.size == 1 && !scrolling) {
                            val c = down[0]
                            val x = c.position.x - c.previousPosition.x
                            val y = c.position.y - c.previousPosition.y
                            travel += hypot(x, y)
                            val (mx, my) = Touchpad.accelerate(x, y)
                            Touchpad.move(mx, my)
                        }
                        event.changes.forEach { end = maxOf(end, it.uptimeMillis); it.consume() }
                    } while (event.changes.any { it.pressed })
                    // a tap is a click; two fingers tapping is the right button
                    if (travel < 24f && end - start < 260) Touchpad.click(if (fingers >= 2) "right" else "left")
                }
            },
            contentAlignment = Alignment.Center,
        ) {
            Text("Deslize para mover\nToque: clique · dois dedos: rolar\nToque com dois dedos: botão direito",
                color = Ui.Faint, fontSize = 14.sp, textAlign = TextAlign.Center, lineHeight = 20.sp)
        }
        Row(horizontalArrangement = Arrangement.spacedBy(10.dp)) {
            // held down: drag with the other finger on the touchpad
            Box(
                Modifier.weight(1f).height(56.dp).clip(Ui.ButtonShape).background(Ui.Raised).pointerInput(Unit) {
                    detectTapGestures(onPress = {
                        Touchpad.click("left", "down")
                        tryAwaitRelease()
                        Touchpad.click("left", "up")
                    })
                },
                contentAlignment = Alignment.Center,
            ) { Text("Esquerdo", color = Ui.Text, fontWeight = FontWeight.SemiBold, fontSize = 15.sp) }
            UiSecondary("Direito", { Touchpad.click("right") }, Modifier.weight(1f), height = 56.dp)
            Box(
                Modifier.weight(1f).height(56.dp).clip(Ui.ButtonShape).background(if (keyboard) Ui.Text else Ui.Raised)
                    .clickable { keyboard = !keyboard },
                contentAlignment = Alignment.Center,
            ) { Text("Teclado", color = if (keyboard) Ui.OnAction else Ui.Text, fontWeight = FontWeight.SemiBold, fontSize = 15.sp) }
        }
        if (keyboard) KeyboardBar()
    }
}

/** The phone's keyboard typing on the PC, and the keys a phone keyboard doesn't have. */
@Composable
private fun KeyboardBar() {
    var typed by remember { mutableStateOf("") }
    val focus = remember { FocusRequester() }
    LaunchedEffect(Unit) { focus.requestFocus() }
    Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
        Row(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
            listOf("esc" to "Esc", "tab" to "Tab", "left" to "←", "up" to "↑", "down" to "↓", "right" to "→", "backspace" to "⌫").forEach { (key, label) ->
                KeyButton(label, Modifier.weight(1f)) { Touchpad.key(key) }
            }
        }
        BasicTextField(
            value = typed,
            onValueChange = { new ->
                val edit = Touchpad.diff(typed, new)
                Touchpad.erase(edit.backspaces)
                Touchpad.type(edit.text)
                // a long line starts over (nothing is erased on the PC)
                typed = if (new.length > 300) "" else new
            },
            singleLine = true,
            textStyle = TextStyle(color = Ui.Text, fontSize = 16.sp),
            cursorBrush = SolidColor(Ui.Text),
            keyboardOptions = KeyboardOptions(capitalization = KeyboardCapitalization.Sentences, imeAction = ImeAction.Send),
            keyboardActions = KeyboardActions(onSend = { Touchpad.key("enter"); typed = "" }),
            modifier = Modifier.fillMaxWidth().background(Ui.Card, Ui.ButtonShape).padding(14.dp).focusRequester(focus),
            decorationBox = { inner ->
                if (typed.isEmpty()) Text("Digite: vai direto para o PC (Enviar = Enter)", color = Ui.Faint, fontSize = 15.sp)
                inner()
            },
        )
    }
}

@Composable
private fun KeyButton(label: String, modifier: Modifier = Modifier, height: Int = 44, onClick: () -> Unit) {
    Box(modifier.height(height.dp).clip(RoundedCorner).background(Ui.Raised).clickable(onClick = onClick), contentAlignment = Alignment.Center) {
        Text(label, color = Ui.Text, fontWeight = FontWeight.SemiBold, fontSize = 15.sp)
    }
}

@Composable
private fun BigKey(label: String, note: String, modifier: Modifier, main: Boolean = false, icon: Int? = null, onClick: () -> Unit) {
    Column(
        modifier.clip(Ui.CardShape).background(if (main) Ui.Text else Ui.Card).clickable(onClick = onClick).padding(12.dp),
        horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.Center,
    ) {
        val color = if (main) Ui.OnAction else Ui.Text
        if (icon != null) Icon(painterResource(icon), null, tint = color, modifier = Modifier.size(30.dp))
        else Text(label, color = color, fontWeight = FontWeight.SemiBold, fontSize = 22.sp)
        Text(note, color = if (main) Ui.OnAction.copy(alpha = 0.7f) else Ui.Muted, fontSize = 13.sp, modifier = Modifier.padding(top = 4.dp))
    }
}

@Composable
private fun KeyRow(modifier: Modifier, content: @Composable RowScope.() -> Unit) =
    Row(modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(10.dp), content = content)

/** YouTube, Netflix, players: arrows seek, F is full screen; play and volume go through the PC's media controls. */
@Composable
private fun MovieMode(modifier: Modifier) {
    Column(modifier, verticalArrangement = Arrangement.spacedBy(10.dp)) {
        BigKey("", "tocar / pausar", Modifier.weight(1.4f).fillMaxWidth(), main = true, icon = R.drawable.ic_play) { Touchpad.remote("play") }
        KeyRow(Modifier.weight(1f)) {
            BigKey("−10 s", "voltar", Modifier.weight(1f).fillMaxSize()) { Touchpad.key("left") }
            BigKey("+10 s", "avançar", Modifier.weight(1f).fillMaxSize()) { Touchpad.key("right") }
        }
        KeyRow(Modifier.weight(1f)) {
            BigKey("", "volume", Modifier.weight(1f).fillMaxSize(), icon = R.drawable.ic_minus) { Touchpad.remote("voldown") }
            BigKey("", "mudo", Modifier.weight(1f).fillMaxSize(), icon = R.drawable.ic_mute) { Touchpad.remote("mute") }
            BigKey("", "volume", Modifier.weight(1f).fillMaxSize(), icon = R.drawable.ic_plus) { Touchpad.remote("volup") }
        }
        KeyRow(Modifier.height(56.dp)) {
            KeyButton("Tela cheia", Modifier.weight(1f), 56) { Touchpad.key("f") }
            KeyButton("Esc", Modifier.weight(1f), 56) { Touchpad.key("esc") }
            KeyButton("Espaço", Modifier.weight(1f), 56) { Touchpad.key("space") }
        }
    }
}

/** PowerPoint, Google Slides, PDFs: the arrows turn the page; F5 starts, B blacks the screen. */
@Composable
private fun SlidesMode(modifier: Modifier) {
    Column(modifier, verticalArrangement = Arrangement.spacedBy(10.dp)) {
        BigKey("Próximo", "→", Modifier.weight(2f).fillMaxWidth(), main = true) { Touchpad.key("right") }
        BigKey("Anterior", "←", Modifier.weight(1f).fillMaxWidth()) { Touchpad.key("left") }
        KeyRow(Modifier.height(56.dp)) {
            KeyButton("Começar", Modifier.weight(1f), 56) { Touchpad.key("f5") }
            KeyButton("Daqui", Modifier.weight(1f), 56) { Touchpad.key("shift+f5") }
        }
        KeyRow(Modifier.height(56.dp)) {
            KeyButton("Tela preta", Modifier.weight(1f), 56) { Touchpad.key("b") }
            KeyButton("Sair", Modifier.weight(1f), 56) { Touchpad.key("esc") }
        }
    }
}
