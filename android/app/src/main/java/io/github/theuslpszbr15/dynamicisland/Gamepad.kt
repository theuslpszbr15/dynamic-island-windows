package io.github.theuslpszbr15.dynamicisland

import android.content.Context
import android.view.HapticFeedbackConstants
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.awaitFirstDown
import androidx.compose.foundation.gestures.detectDragGestures
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.runtime.DisposableEffect
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.drawBehind
import androidx.compose.ui.platform.LocalDensity
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
    /** Tilt steering (the left stick's X) while it is on; a finger on the left stick still wins. */
    private var tilt: Float? = null
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

    fun steering(x: Float?) = synchronized(lock) { tilt = x }

    fun frame(): JSONObject = synchronized(lock) {
        fun axis(v: Float) = (v.coerceIn(-1f, 1f) * 32767).roundToInt()
        val steer = tilt
        val x = if (steer != null && kotlin.math.abs(lx) < 0.05f) steer else lx
        JSONObject().put("type", "pad").put("kind", if (ps4) "ps4" else "xbox").put("b", buttons)
            .put("lx", axis(x)).put("ly", axis(ly)).put("rx", axis(rx)).put("ry", axis(ry))
            .put("lt", if (lt) 255 else 0).put("rt", if (rt) 255 else 0)
    }

    fun start() = synchronized(lock) {
        if (pump == null) pump = Touchpad.worker.scheduleWithFixedDelay(::tick, 16, 16, TimeUnit.MILLISECONDS)
    }

    /** Lets go of everything (the game sees the buttons come up) and stops sending. */
    fun stop() {
        val wasOn = synchronized(lock) {
            buttons = 0; lx = 0f; ly = 0f; rx = 0f; ry = 0f; lt = false; rt = false; tilt = null
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

/** The game pad as laid out in [layout] ([PadLayout]); [editing] turns it into the editor. [ps4] picks names and colours. */
@Composable
fun GamepadMode(ps4: Boolean, layout: PadLayout, editing: Boolean, onLayout: (PadLayout) -> Unit, modifier: Modifier = Modifier) {
    val face = if (ps4) Ps4Colors else XboxColors
    var selected by remember { mutableStateOf<String?>(null) }
    // the drag gestures outlive each change of the layout they make
    val current by androidx.compose.runtime.rememberUpdatedState(layout)
    val update by androidx.compose.runtime.rememberUpdatedState(onLayout)
    if (!editing) selected = null
    TiltSteering(on = layout.gyro && !editing)
    BoxWithConstraints(modifier.fillMaxSize().then(if (editing) Modifier.background(Ui.Bg).drawDots() else Modifier)) {
        val areaW = maxWidth
        val areaH = maxHeight
        val density = LocalDensity.current
        if (layout.gyro && !editing) Text("Volante: incline o celular", color = Ui.Ok, fontSize = 13.sp, fontWeight = FontWeight.SemiBold,
            modifier = Modifier.align(Alignment.TopCenter).background(Ui.Ok.copy(alpha = 0.14f), RoundedCornerShape(12.dp)).padding(horizontal = 12.dp, vertical = 6.dp))
        for (id in PadLayout.Ids) {
            val item = layout.item(id)
            if (item.hidden && !editing) continue
            val w = (item.w * layout.scale).dp
            val h = (item.h * layout.scale).dp
            Box(
                Modifier.offset(areaW * item.cx - w / 2, areaH * item.cy - h / 2).size(w, h)
                    .alpha(if (editing) (if (item.hidden) 0.3f else 1f) else layout.alpha),
            ) {
                PadControl(id, ps4, face, w, h)
                if (editing) {
                    val isSelected = selected == id
                    Box(
                        Modifier.matchParentSize()
                            .border(2.dp, Edit, if (id in PadLayout.Square) CircleShape else RoundedCornerShape(12.dp))
                            .background(if (isSelected) Edit.copy(alpha = 0.16f) else Color.Transparent)
                            .pointerInput(id) { detectTapGestures { selected = id } }
                            .pointerInput(id) {
                                detectDragGestures(onDragStart = { selected = id }, onDragEnd = {
                                    val now = current.item(id)
                                    update(current.with(id, now.copy(cx = snap(now.cx), cy = snap(now.cy))))
                                }) { change, drag ->
                                    change.consume()
                                    val now = current.item(id)
                                    val pxW = with(density) { areaW.toPx() }
                                    val pxH = with(density) { areaH.toPx() }
                                    update(current.with(id, now.copy(
                                        cx = (now.cx + drag.x / pxW).coerceIn(0f, 1f),
                                        cy = (now.cy + drag.y / pxH).coerceIn(0f, 1f),
                                    )))
                                }
                            },
                    )
                    if (isSelected) Box(
                        Modifier.align(Alignment.BottomEnd).offset(8.dp, 8.dp).size(22.dp).background(Edit, RoundedCornerShape(6.dp))
                            .pointerInput(id) {
                                detectDragGestures { change, drag ->
                                    change.consume()
                                    val layoutNow = current
                                    val now = layoutNow.item(id)
                                    var nw = (now.w + with(density) { drag.x.toDp().value } / layoutNow.scale).coerceIn(PadLayout.MIN_SIZE, PadLayout.MAX_SIZE)
                                    var nh = (now.h + with(density) { drag.y.toDp().value } / layoutNow.scale).coerceIn(PadLayout.MIN_SIZE, PadLayout.MAX_SIZE)
                                    if (id in PadLayout.Square) { val s = maxOf(nw, nh); nw = s; nh = s }
                                    update(layoutNow.with(id, now.copy(w = nw, h = nh)))
                                }
                            },
                    )
                }
            }
        }
        val chosen = selected
        if (editing && chosen != null) {
            val item = layout.item(chosen)
            Row(
                Modifier.align(Alignment.TopCenter).background(Ui.Card, RoundedCornerShape(14.dp)).border(1.dp, Edit, RoundedCornerShape(14.dp)).padding(6.dp),
                horizontalArrangement = Arrangement.spacedBy(6.dp), verticalAlignment = Alignment.CenterVertically,
            ) {
                Text(Names(ps4)[chosen] ?: chosen, color = Ui.Text, fontWeight = FontWeight.SemiBold, fontSize = 14.sp, modifier = Modifier.padding(horizontal = 8.dp))
                SmallChip("−") { onLayout(layout.with(chosen, item.copy(w = (item.w * 0.9f).coerceAtLeast(PadLayout.MIN_SIZE), h = (item.h * 0.9f).coerceAtLeast(PadLayout.MIN_SIZE)))) }
                SmallChip("+") { onLayout(layout.with(chosen, item.copy(w = (item.w * 1.1f).coerceAtMost(PadLayout.MAX_SIZE), h = (item.h * 1.1f).coerceAtMost(PadLayout.MAX_SIZE)))) }
                SmallChip(if (item.hidden) "Mostrar" else "Esconder") { onLayout(layout.with(chosen, item.copy(hidden = !item.hidden))) }
            }
        } else if (editing) Text("Arraste para mover · puxe o canto azul para o tamanho · toque para escolher", color = Ui.Muted, fontSize = 13.sp,
            modifier = Modifier.align(Alignment.TopCenter).padding(top = 60.dp))
    }
}

private val Edit = Color(0xFF5AA2FF)

/** Positions land on a grid of 1/48 of the area, so things line up. */
private fun snap(v: Float) = (v * 48).roundToInt() / 48f

private fun Modifier.drawDots() = drawBehind {
    val step = 24.dp.toPx()
    var x = step / 2
    while (x < size.width) {
        var y = step / 2
        while (y < size.height) { drawCircle(Color(0xFF2A2A29), 1.3.dp.toPx(), Offset(x, y)); y += step }
        x += step
    }
}

private fun Names(ps4: Boolean) = mapOf(
    "lt" to if (ps4) "L2" else "LT", "lb" to if (ps4) "L1" else "LB", "rb" to if (ps4) "R1" else "RB", "rt" to if (ps4) "R2" else "RT",
    "ls" to "Analógico esquerdo", "rs" to "Analógico direito", "dpad" to "Direcional", "face" to "Botões",
    "back" to if (ps4) "Share" else "View", "start" to if (ps4) "Options" else "Menu", "guide" to if (ps4) "PS" else "Xbox",
    "l3" to if (ps4) "L3" else "LS", "r3" to if (ps4) "R3" else "RS",
)

@Composable
private fun SmallChip(label: String, onClick: () -> Unit) {
    Box(Modifier.height(34.dp).clip(RoundedCornerShape(10.dp)).background(Ui.Raised).clickable(onClick = onClick).padding(horizontal = 12.dp),
        contentAlignment = Alignment.Center) { Text(label, color = Ui.Text, fontWeight = FontWeight.SemiBold, fontSize = 14.sp) }
}

/** One control at its size. */
@Composable
private fun PadControl(id: String, ps4: Boolean, face: Map<Int, Color>, w: Dp, h: Dp) {
    val names = Names(ps4)
    val full = Modifier.fillMaxSize()
    val pill = RoundedCornerShape(minOf(w, h) / 2)
    when (id) {
        "lt" -> PadTrigger(names.getValue(id), left = true, full)
        "rt" -> PadTrigger(names.getValue(id), left = false, full)
        "lb" -> PadButton(names.getValue(id), Gamepad.LB, full, RoundedCornerShape(12.dp))
        "rb" -> PadButton(names.getValue(id), Gamepad.RB, full, RoundedCornerShape(12.dp))
        "ls" -> Stick(left = true, size = w)
        "rs" -> Stick(left = false, size = w)
        "dpad" -> DPad(w)
        "face" -> FaceButtons(ps4, face, w)
        "back" -> PadButton(names.getValue(id), Gamepad.BACK, full, pill, small = true)
        "start" -> PadButton(names.getValue(id), Gamepad.START, full, pill, small = true)
        "guide" -> PadButton(names.getValue(id), Gamepad.GUIDE, full, CircleShape, small = true)
        "l3" -> PadButton(names.getValue(id), Gamepad.LS, full, pill, small = true)
        "r3" -> PadButton(names.getValue(id), Gamepad.RS, full, pill, small = true)
    }
}

@Composable
private fun FaceButtons(ps4: Boolean, face: Map<Int, Color>, size: Dp) {
    Box(Modifier.size(size)) {
        val b = size * 0.35f
        PadButton(if (ps4) "△" else "Y", Gamepad.Y, Modifier.size(b).align(Alignment.TopCenter), CircleShape, color = face[Gamepad.Y])
        PadButton(if (ps4) "□" else "X", Gamepad.X, Modifier.size(b).align(Alignment.CenterStart), CircleShape, color = face[Gamepad.X])
        PadButton(if (ps4) "○" else "B", Gamepad.B, Modifier.size(b).align(Alignment.CenterEnd), CircleShape, color = face[Gamepad.B])
        PadButton(if (ps4) "✕" else "A", Gamepad.A, Modifier.size(b).align(Alignment.BottomCenter), CircleShape, color = face[Gamepad.A])
    }
}

/** Tilting the phone like a steering wheel moves the left stick sideways, while [on]. */
@Composable
private fun TiltSteering(on: Boolean) {
    val context = LocalContext.current
    DisposableEffect(on) {
        val sensors = context.getSystemService(android.hardware.SensorManager::class.java)
        val gravity = sensors?.getDefaultSensor(android.hardware.Sensor.TYPE_GRAVITY)
            ?: sensors?.getDefaultSensor(android.hardware.Sensor.TYPE_ACCELEROMETER)
        val listener = object : android.hardware.SensorEventListener {
            override fun onSensorChanged(e: android.hardware.SensorEvent) {
                val (dx, dy) = e.values[0] to e.values[1]
                // the sensors use the phone's natural (portrait) axes; the screen may be turned
                val (sx, sy) = when ((context as? android.app.Activity)?.display?.rotation ?: android.view.Surface.ROTATION_0) {
                    android.view.Surface.ROTATION_90 -> -dy to dx
                    android.view.Surface.ROTATION_270 -> dy to -dx
                    android.view.Surface.ROTATION_180 -> -dx to -dy
                    else -> dx to dy
                }
                Gamepad.steering(PadLayout.steer(sx, sy))
            }
            override fun onAccuracyChanged(sensor: android.hardware.Sensor?, accuracy: Int) {}
        }
        if (on && gravity != null) sensors.registerListener(listener, gravity, android.hardware.SensorManager.SENSOR_DELAY_GAME)
        onDispose {
            sensors?.unregisterListener(listener)
            Gamepad.steering(null)
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
        modifier.clip(RoundedCornerShape(topStart = 18.dp, topEnd = 18.dp, bottomStart = 8.dp, bottomEnd = 8.dp))
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
private fun DPad(size: Dp) {
    val key = size / 3
    Box(Modifier.size(size)) {
        val arrow = RoundedCornerShape(8.dp)
        PadButton("▲", Gamepad.UP, Modifier.size(key).align(Alignment.TopCenter), arrow, small = true)
        PadButton("▼", Gamepad.DOWN, Modifier.size(key).align(Alignment.BottomCenter), arrow, small = true)
        PadButton("◀", Gamepad.LEFT, Modifier.size(key).align(Alignment.CenterStart), arrow, small = true)
        PadButton("▶", Gamepad.RIGHT, Modifier.size(key).align(Alignment.CenterEnd), arrow, small = true)
    }
}
