package io.github.theuslpszbr15.dynamicisland

import androidx.compose.animation.core.Animatable
import androidx.compose.animation.core.FastOutSlowInEasing
import androidx.compose.animation.core.RepeatMode
import androidx.compose.animation.core.animateFloat
import androidx.compose.animation.core.infiniteRepeatable
import androidx.compose.animation.core.keyframes
import androidx.compose.animation.core.rememberInfiniteTransition
import androidx.compose.animation.core.tween
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.layout.size
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.runtime.staticCompositionLocalOf
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.StrokeJoin
import androidx.compose.ui.graphics.drawscope.DrawScope
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.graphics.drawscope.translate
import androidx.compose.ui.graphics.drawscope.withTransform
import androidx.compose.ui.graphics.lerp
import androidx.compose.ui.graphics.vector.PathParser
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlin.random.Random

enum class BitMood { Idle, Working, Waiting, Happy }

/** No animation (screenshots): an endless animation never lets a UI test go idle. */
val LocalStill = staticCompositionLocalOf { false }

private val Hoodie = Color(0xFF3D444D)
private val Face = Color(0xFF1F2328)
private val Rim = Color(0xFF5A636E)
private val Pink = Color(0xFFFF8FA3)
private val Ink = Color(0xFF1C1C1E)
private val Shade = Color(0xFF22262B)

private fun path(d: String) = PathParser().parsePathString(d).toPath()

private val Body = path("M6.2,18.6 C6.2,21.8 8.2,23.6 12,23.6 C15.8,23.6 17.8,21.8 17.8,18.6 Z")
private val Strings = path("M10.7,18.8 L10.5,21 M13.3,18.8 L13.5,21")
private val Ears = path("M3.6,10.4 L4.6,2.2 L10.4,6.4 Z M20.4,10.4 L19.4,2.2 L13.6,6.4 Z")
private val InnerEars = path("M5.4,4.6 L6.1,8 L8.4,6.4 Z M18.6,4.6 L17.9,8 L15.6,6.4 Z")
private val Hood = path("M12,4.4 C18.5,4.4 21.6,8.5 21.6,13.2 C21.6,17.7 17.7,20.2 12,20.2 C6.3,20.2 2.4,17.7 2.4,13.2 C2.4,8.5 5.5,4.4 12,4.4 Z")
private val FaceShape = path("M12,7.3 C16.9,7.3 19.2,10.1 19.2,13.4 C19.2,16.7 16.3,18.6 12,18.6 C7.7,18.6 4.8,16.7 4.8,13.4 C4.8,10.1 7.1,7.3 12,7.3 Z")
private val Nose = path("M11.35,15.95 L12.65,15.95 L12,16.7 Z")
private val Mouth = path("M10.9,17.1 Q11.45,17.7 12,17.1 Q12.55,17.7 13.1,17.1")
private val HappyEyes = path("M6.8,14.4 Q8.25,12.1 9.7,14.4 M14.3,14.4 Q15.75,12.1 17.2,14.4")

// PatPat, as on the PC (after the Minecraft mod): a pixel hand pats the head in 5 frames of 60 ms.
// Per frame: hand lift above the head, hand squash, body squash X / Y (about the feet).
private val PatHandLift = floatArrayOf(-3.4f, -1.8f, 0.5f, -0.8f, -2.8f)
private val PatHandSquash = floatArrayOf(1f, 0.95f, 0.8f, 0.9f, 1f)
private val PatBodyX = floatArrayOf(1f, 1.03f, 1.12f, 1.06f, 1f)
private val PatBodyY = floatArrayOf(1f, 0.96f, 0.82f, 0.9f, 1f)
private const val PatFrameMs = 60L
private const val PatEveryMs = 330L
private const val HeadTop = 4.4f
private const val HandPixel = 0.9f
private const val HeartPixel = 0.7f

private val HandRows = listOf(
    "......OOOOOOOCCO",
    ".....OSSSSSSSOCC",
    "...OOSSSSSSSSSOC",
    "..OSSSSSSSSSSSOC",
    ".OSSSSSSSSSSSDO.",
    "OSSDSSDSSDSSDO..",
    "OSSDSSDSSDSDO...",
    "OSSOSSOSSOSO....",
    ".OO.OO.OO.O.....",
)
private val HeartRows = listOf("XX.XX", "XXXXX", ".XXX.", "..X..")
private val HandColors = mapOf(
    'O' to Color(0xFF3B2A2A), 'S' to Color(0xFFFFD7B5), 'D' to Color(0xFFE3A884), 'C' to Color(0xFF4A90E2),
)
private val HeartColor = Color(0xFFFF4D6D)

/** A heart floating up from a pat; [progress] runs 0 to 1. */
private class PatHeart(val x: Float) {
    val progress = Animatable(0f)
}

private fun DrawScope.pixels(rows: List<String>, left: Float, top: Float, unit: Float, alpha: Float, color: (Char) -> Color?) {
    rows.forEachIndexed { r, row ->
        row.forEachIndexed { c, ch ->
            val paint = color(ch) ?: return@forEachIndexed
            // a hair larger than the cell so the scaled pixels leave no seams
            drawRect(paint.copy(alpha = alpha), Offset(left + c * unit, top + r * unit), Size(unit * 1.04f, unit * 1.04f))
        }
    }
}

/** Bit, the hooded kitten from the PC's island (same shapes, 24x24 units). The hoodie takes the state's colour.
 *  Touch it for a pat; keep holding and the hand keeps patting (Shift + click on the PC). */
@Composable
fun Bit(mood: BitMood, modifier: Modifier = Modifier, size: Dp = 96.dp, previewPatFrame: Int = -1) {
    val scope = rememberCoroutineScope()
    var patFrame by remember { mutableIntStateOf(-1) }
    var patted by remember { mutableStateOf(false) }
    var patJob by remember { mutableStateOf<Job?>(null) }
    val handAlpha = remember { Animatable(0f) }
    val hearts = remember { mutableStateListOf<PatHeart>() }

    fun pat() {
        patJob?.cancel()
        patted = true
        val heart = PatHeart(4f + Random.nextFloat() * 14f)
        hearts.add(heart)
        scope.launch {
            heart.progress.animateTo(1f, tween(700, easing = FastOutSlowInEasing))
            hearts.remove(heart)
        }
        patJob = scope.launch {
            handAlpha.snapTo(1f)
            for (i in PatBodyY.indices) {
                patFrame = i
                delay(PatFrameMs)
            }
            patFrame = -1
            handAlpha.animateTo(0f, tween(220, delayMillis = 60))
            delay(700)
            patted = false
        }
    }

    val shown = if (patted || previewPatFrame >= 0) BitMood.Happy else mood
    val hoodie = when (shown) {
        BitMood.Working -> lerp(Shade, Color(0xFF0A84FF), 0.42f)
        BitMood.Waiting -> lerp(Shade, Color(0xFFFF9F0A), 0.42f)
        else -> Hoodie
    }
    val still = LocalStill.current
    val motion = rememberInfiniteTransition(label = "bit")
    val hop = if (still) 0f else motion.animateFloat(
        initialValue = 0f,
        targetValue = if (shown == BitMood.Waiting) -2.2f else -0.5f,
        animationSpec = infiniteRepeatable(
            tween(if (shown == BitMood.Waiting) 420 else 1600, easing = FastOutSlowInEasing), RepeatMode.Reverse),
        label = "hop",
    ).value
    val blink = if (still) 1f else motion.animateFloat(
        initialValue = 1f,
        targetValue = 1f,
        animationSpec = infiniteRepeatable(keyframes {
            durationMillis = 3800
            1f at 3500
            0.1f at 3600
            1f at 3700
        }),
        label = "blink",
    ).value

    // previewPatFrame freezes one pat frame (screenshots)
    val f = if (previewPatFrame >= 0) previewPatFrame else patFrame
    Canvas(
        modifier.size(size).pointerInput(Unit) {
            detectTapGestures(onPress = {
                pat()
                val hold = scope.launch {
                    while (true) {
                        delay(PatEveryMs)
                        pat()
                    }
                }
                tryAwaitRelease()
                hold.cancel()
            })
        },
    ) {
        val s = this.size.minDimension / 24f
        withTransform({ scale(s, s, pivot = Offset.Zero) }) {
            // the body squashes towards its feet while the hand lands
            withTransform({ if (f >= 0) scale(PatBodyX[f], PatBodyY[f], pivot = Offset(12f, 23.5f)) }) {
            translate(top = hop) {
                drawPath(Body, hoodie)
                drawPath(Strings, Color.White.copy(alpha = 0.85f), style = Stroke(0.55f, cap = StrokeCap.Round))
                drawPath(Ears, hoodie)
                drawPath(Ears, hoodie, style = Stroke(1.4f, join = StrokeJoin.Round))
                drawPath(InnerEars, Pink)
                drawPath(Hood, hoodie)
                drawPath(Hood, Rim, style = Stroke(0.45f))
                drawPath(FaceShape, Face)
                drawPath(FaceShape, Rim, style = Stroke(0.35f))
                drawOval(Pink.copy(alpha = 0.45f), Offset(5.6f, 16.2f), Size(2.2f, 1.2f))
                drawOval(Pink.copy(alpha = 0.45f), Offset(16.2f, 16.2f), Size(2.2f, 1.2f))
                if (shown == BitMood.Happy) {
                    drawPath(HappyEyes, Color.White, style = Stroke(1.4f, cap = StrokeCap.Round))
                } else {
                    val grow = if (shown == BitMood.Waiting) 1.15f else 1f
                    for (x in floatArrayOf(8.25f, 15.75f)) {
                        val inward = if (x < 12) 0.35f else -0.35f
                        val w = 4.2f * grow
                        val h = 4.8f * grow * blink
                        drawOval(Color.White, Offset(x - w / 2, 13.6f - h / 2), Size(w, h))
                        if (blink > 0.5f) {
                            drawOval(Ink, Offset(x - 1.05f + inward, 12.5f), Size(2.1f, 2.7f))
                            drawOval(Color.White, Offset(x - 0.6f + inward, 12.75f), Size(0.9f, 0.9f))
                        }
                    }
                }
                drawPath(Nose, Pink)
                drawPath(Mouth, Rim, style = Stroke(0.55f, cap = StrokeCap.Round))
            }
            }
            if (handAlpha.value > 0.01f || previewPatFrame >= 0) {
                val alpha = if (previewPatFrame >= 0) 1f else handAlpha.value
                val handW = 16 * HandPixel
                val handH = 9 * HandPixel
                val left = 12f - handW / 2 + 0.8f
                val top = HeadTop - handH + 0.6f
                // the head comes down with the squash, and the hand follows it
                val lift = if (f >= 0) (23.5f - HeadTop) * (1f - PatBodyY[f]) + PatHandLift[f] else 0f
                withTransform({
                    translate(top = lift)
                    if (f >= 0) scale(1f, PatHandSquash[f], pivot = Offset(left + handW / 2, top + handH))
                }) {
                    pixels(HandRows, left, top, HandPixel, alpha) { HandColors[it] }
                }
            }
            if (previewPatFrame >= 0) {
                pixels(HeartRows, 14f, HeadTop - 1.5f - 3f, HeartPixel, 0.8f) { if (it == 'X') HeartColor else null }
            }
            for (heart in hearts) {
                val p = heart.progress.value
                pixels(HeartRows, heart.x, HeadTop - 1.5f - 7f * p, HeartPixel, 1f - p) { if (it == 'X') HeartColor else null }
            }
        }
    }
}
