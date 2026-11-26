package io.github.theuslpszbr15.dynamicisland

import android.content.Context
import android.view.HapticFeedbackConstants
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.awaitFirstDown
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Shape
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.IntOffset
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import org.json.JSONObject
import java.util.concurrent.ScheduledFuture
import java.util.concurrent.TimeUnit
import kotlin.math.hypot
import kotlin.math.roundToInt

/**
 * The phone as a game controller: the PC makes a virtual Xbox 360 or DualShock 4 pad out of these frames (the ViGEmBus
 * driver). A frame goes when something changed, at most every 16 ms, and again each second so a pad left untouched
 * isn't unplugged by the PC.
 */
object Gamepad {
    const val A = 1; const val B = 2; const val X = 4; const val Y = 8; const val LB = 16; const val RB = 32
    const val BACK = 64; const val START = 128; const val LS = 256; const val RS = 512
    const val UP = 1024; const val DOWN = 2048; const val LEFT = 4096; const val RIGHT = 8192; const val GUIDE = 16384

    @Volatile var ps4 = false
    /** The Jogo tab is open: the pad comes back when the app does. */
    @Volatile var wanted = false
    private val lock = Any()
    private var buttons = 0
    private var lx = 0f; private var ly = 0f; private var rx = 0f; private var ry = 0f
    private var lt = false; private var rt = false
    private var sent = ""
    private var sentAt = 0L
    private var pump: ScheduledFuture<*>? = null

    fun kind(context: Context): Boolean = context.getSharedPreferences("gamepad", Context.MODE_PRIVATE).getBoolean("ps4", false).also { ps4 = it }

    fun setKind(context: Context, ps4: Boolean) {
        this.ps4 = ps4
        context.getSharedPreferences("gamepad", Context.MODE_PRIVATE).edit().putBoolean("ps4", ps4).apply()
    }

    fun press(bit: Int, down: Boolean) = synchronized(lock) { buttons = if (down) buttons or bit else buttons and bit.inv() }

    fun trigger(left: Boolean, down: Boolean) = synchronized(lock) { if (left) lt = down else rt = down }

    /** x and y from −1 to 1, up positive. */
    fun stick(left: Boolean, x: Float, y: Float) = synchronized(lock) {
        if (left) { lx = x; ly = y } else { rx = x; ry = y }
    }

    fun frame(): JSONObject = synchronized(lock) {
        fun axis(v: Float) = (v.coerceIn(-1f, 1f) * 32767).roundToInt()
        JSONObject().put("type", "pad").put("kind", if (ps4) "ps4" else "xbox").put("b", buttons)
            .put("lx", axis(lx)).put("ly", axis(ly)).put("rx", axis(rx)).put("ry", axis(ry))
            .put("lt", if (lt) 255 else 0).put("rt", if (rt) 255 else 0)
    }

    fun start() = synchronized(lock) {
        if (pump == null) pump = Touchpad.worker.scheduleWithFixedDelay(::tick, 16, 16, TimeUnit.MILLISECONDS)
    }

    /** Lets go of everything (the game sees the buttons come up) and stops sending. */
    fun stop() {
        val wasOn = synchronized(lock) {
            buttons = 0; lx = 0f; ly = 0f; rx = 0f; ry = 0f; lt = false; rt = false
            val on = pump != null
            pump?.cancel(false)
            pump = null
            sent = ""
            on
        }
        if (wasOn) Touchpad.worker.execute { send(frame()) }
    }

    private fun tick() {
        val frame = frame()
        val text = frame.toString()
        val now = System.currentTimeMillis()
        if (text == sent && now - sentAt < 1000) return
        if (send(frame)) { sent = text; sentAt = now }
    }

    private fun send(frame: JSONObject): Boolean {
        val c = Link.client ?: return false
        return try { c.pad(frame); true } catch (_: Exception) { false }
    }
}

private val XboxColors = mapOf(Gamepad.A to Color(0xFF5CC85A), Gamepad.B to Color(0xFFE5534B), Gamepad.X to Color(0xFF4A90E2), Gamepad.Y to Color(0xFFF2C14E))
private val Ps4Colors = mapOf(Gamepad.A to Color(0xFF7FA7E8), Gamepad.B to Color(0xFFE8707A), Gamepad.X to Color(0xFFE59AD0), Gamepad.Y to Color(0xFF5FCFB3))

/** Landscape: triggers and shoulders on top, left stick and D-pad on the left, face buttons and right stick on the right. */
@Composable
fun GamepadMode(problem: String?, modifier: Modifier = Modifier) {
    val context = LocalContext.current
    var ps4 by remember { mutableStateOf(Gamepad.kind(context)) }
    val face = if (ps4) Ps4Colors else XboxColors
    Row(modifier.fillMaxSize(), horizontalArrangement = Arrangement.spacedBy(12.dp)) {
        Column(Modifier.weight(1f).fillMaxHeight(), verticalArrangement = Arrangement.SpaceBetween) {
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                PadTrigger(if (ps4) "L2" else "LT", left = true, Modifier.weight(1f))
                PadButton(if (ps4) "L1" else "LB", Gamepad.LB, Modifier.weight(1f).height(44.dp), RoundedCornerShape(12.dp))
            }
            Stick(left = true, size = 128.dp)
            DPad(Modifier.align(Alignment.End))
        }
        Column(Modifier.width(150.dp).fillMaxHeight(), horizontalAlignment = Alignment.CenterHorizontally,
            verticalArrangement = Arrangement.spacedBy(10.dp, Alignment.CenterVertically)) {
            Row(Modifier.fillMaxWidth().background(Ui.Card, Ui.ButtonShape).padding(4.dp)) {
                listOf(false to "Xbox", true to "PS4").forEach { (kind, label) ->
                    Box(Modifier.weight(1f).height(34.dp).clip(RoundedCornerShape(12.dp)).background(if (ps4 == kind) Ui.Raised else Ui.Card)
                        .clickable { ps4 = kind; Gamepad.setKind(context, kind) }, contentAlignment = Alignment.Center) {
                        Text(label, color = if (ps4 == kind) Ui.Text else Ui.Muted, fontWeight = FontWeight.SemiBold, fontSize = 13.sp)
                    }
                }
            }
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                PadButton(if (ps4) "Share" else "View", Gamepad.BACK, Modifier.weight(1f).height(36.dp), RoundedCornerShape(18.dp), small = true)
                PadButton(if (ps4) "Options" else "Menu", Gamepad.START, Modifier.weight(1f).height(36.dp), RoundedCornerShape(18.dp), small = true)
            }
            PadButton(if (ps4) "PS" else "Xbox", Gamepad.GUIDE, Modifier.size(52.dp), CircleShape, small = true)
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                PadButton(if (ps4) "L3" else "LS", Gamepad.LS, Modifier.weight(1f).height(34.dp), RoundedCornerShape(17.dp), small = true)
                PadButton(if (ps4) "R3" else "RS", Gamepad.RS, Modifier.weight(1f).height(34.dp), RoundedCornerShape(17.dp), small = true)
            }
            if (problem != null) Text(problem, color = Ui.Warn, fontSize = 12.sp, lineHeight = 15.sp)
        }
        Column(Modifier.weight(1f).fillMaxHeight(), verticalArrangement = Arrangement.SpaceBetween) {
            Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                PadButton(if (ps4) "R1" else "RB", Gamepad.RB, Modifier.weight(1f).height(44.dp), RoundedCornerShape(12.dp))
                PadTrigger(if (ps4) "R2" else "RT", left = false, Modifier.weight(1f))
            }
            Box(Modifier.size(132.dp).align(Alignment.End)) {
                val size = 46.dp
                PadButton(if (ps4) "△" else "Y", Gamepad.Y, Modifier.size(size).align(Alignment.TopCenter), CircleShape, color = face[Gamepad.Y])
                PadButton(if (ps4) "□" else "X", Gamepad.X, Modifier.size(size).align(Alignment.CenterStart), CircleShape, color = face[Gamepad.X])
                PadButton(if (ps4) "○" else "B", Gamepad.B, Modifier.size(size).align(Alignment.CenterEnd), CircleShape, color = face[Gamepad.B])
                PadButton(if (ps4) "✕" else "A", Gamepad.A, Modifier.size(size).align(Alignment.BottomCenter), CircleShape, color = face[Gamepad.A])
            }
            Stick(left = false, size = 120.dp)
        }
    }
}

/** Held while a finger is on it; several at once work (each button follows its own finger). */
@Composable
private fun PadButton(label: String, bit: Int, modifier: Modifier, shape: Shape, color: Color? = null, small: Boolean = false) {
    var down by remember { mutableStateOf(false) }
    val view = LocalView.current
    Box(
        modifier.clip(shape).background(if (down) Ui.Text else Ui.Raised).pointerInput(bit) {
            awaitEachGesture {
                awaitFirstDown(requireUnconsumed = false)
                down = true
                Gamepad.press(bit, true)
                view.performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP)
                do { val event = awaitPointerEvent() } while (event.changes.any { it.pressed })
                down = false
                Gamepad.press(bit, false)
            }
        },
        contentAlignment = Alignment.Center,
    ) {
        Text(label, color = if (down) Ui.OnAction else color ?: Ui.Text, fontWeight = FontWeight.Bold, fontSize = if (small) 12.sp else 17.sp)
    }
}

@Composable
private fun PadTrigger(label: String, left: Boolean, modifier: Modifier) {
    var down by remember { mutableStateOf(false) }
    val view = LocalView.current
    Box(
        modifier.height(44.dp).clip(RoundedCornerShape(topStart = 18.dp, topEnd = 18.dp, bottomStart = 8.dp, bottomEnd = 8.dp))
            .background(if (down) Ui.Text else Ui.Card).border(1.dp, Ui.Line, RoundedCornerShape(topStart = 18.dp, topEnd = 18.dp, bottomStart = 8.dp, bottomEnd = 8.dp))
            .pointerInput(left) {
                awaitEachGesture {
                    awaitFirstDown(requireUnconsumed = false)
                    down = true
                    Gamepad.trigger(left, true)
                    view.performHapticFeedback(HapticFeedbackConstants.KEYBOARD_TAP)
                    do { val event = awaitPointerEvent() } while (event.changes.any { it.pressed })
                    down = false
                    Gamepad.trigger(left, false)
                }
            },
        contentAlignment = Alignment.Center,
    ) { Text(label, color = if (down) Ui.OnAction else Ui.Text, fontWeight = FontWeight.Bold, fontSize = 15.sp) }
}

/** The finger anywhere on the ring sets the stick; letting go centres it. */
@Composable
private fun Stick(left: Boolean, size: Dp) {
    var knob by remember { mutableStateOf(Offset.Zero) }
    Box(
        Modifier.size(size).clip(CircleShape).background(Ui.Card).border(1.dp, Ui.Line, CircleShape).pointerInput(left) {
            val radius = this.size.width / 2f
            val center = Offset(radius, this.size.height / 2f)
            fun update(position: Offset) {
                var v = (position - center) / radius
                val length = hypot(v.x, v.y)
                if (length > 1f) v /= length
                knob = v
                Gamepad.stick(left, v.x, -v.y)
            }
            awaitEachGesture {
                val first = awaitFirstDown(requireUnconsumed = false)
                update(first.position)
                do {
                    val event = awaitPointerEvent()
                    event.changes.firstOrNull { it.id == first.id }?.let { if (it.pressed) update(it.position) }
                } while (event.changes.any { it.id == first.id && it.pressed })
                knob = Offset.Zero
                Gamepad.stick(left, 0f, 0f)
            }
        },
        contentAlignment = Alignment.Center,
    ) {
        val travel = size.value * 0.32f
        Box(Modifier.offset { IntOffset((knob.x * travel.dp.toPx()).roundToInt(), (knob.y * travel.dp.toPx()).roundToInt()) }
            .size(size * 0.42f).clip(CircleShape).background(Ui.Raised).border(1.dp, Ui.Faint, CircleShape))
    }
}

@Composable
private fun DPad(modifier: Modifier) {
    val key = 38.dp
    Box(modifier.size(key * 3)) {
        val arrow = RoundedCornerShape(8.dp)
        PadButton("▲", Gamepad.UP, Modifier.size(key).align(Alignment.TopCenter), arrow, small = true)
        PadButton("▼", Gamepad.DOWN, Modifier.size(key).align(Alignment.BottomCenter), arrow, small = true)
        PadButton("◀", Gamepad.LEFT, Modifier.size(key).align(Alignment.CenterStart), arrow, small = true)
        PadButton("▶", Gamepad.RIGHT, Modifier.size(key).align(Alignment.CenterEnd), arrow, small = true)
    }
}
