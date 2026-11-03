package io.github.theuslpszbr15.dynamicisland

import androidx.compose.animation.core.FastOutSlowInEasing
import androidx.compose.animation.core.RepeatMode
import androidx.compose.animation.core.animateFloat
import androidx.compose.animation.core.infiniteRepeatable
import androidx.compose.animation.core.keyframes
import androidx.compose.animation.core.rememberInfiniteTransition
import androidx.compose.animation.core.tween
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.layout.size
import androidx.compose.runtime.Composable
import androidx.compose.runtime.staticCompositionLocalOf
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.ui.graphics.StrokeJoin
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.graphics.drawscope.translate
import androidx.compose.ui.graphics.drawscope.withTransform
import androidx.compose.ui.graphics.lerp
import androidx.compose.ui.graphics.vector.PathParser
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp

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

/** Bit, the hooded kitten from the PC's island (same shapes, 24×24 units). The hoodie takes the state's colour. */
@Composable
fun Bit(mood: BitMood, modifier: Modifier = Modifier, size: Dp = 96.dp) {
    val hoodie = when (mood) {
        BitMood.Working -> lerp(Shade, Color(0xFF0A84FF), 0.42f)
        BitMood.Waiting -> lerp(Shade, Color(0xFFFF9F0A), 0.42f)
        else -> Hoodie
    }
    val still = LocalStill.current
    val motion = rememberInfiniteTransition(label = "bit")
    val hop = if (still) 0f else motion.animateFloat(
        initialValue = 0f,
        targetValue = if (mood == BitMood.Waiting) -2.2f else -0.5f,
        animationSpec = infiniteRepeatable(
            tween(if (mood == BitMood.Waiting) 420 else 1600, easing = FastOutSlowInEasing), RepeatMode.Reverse),
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

    Canvas(modifier.size(size)) {
        val s = this.size.minDimension / 24f
        withTransform({ scale(s, s, pivot = Offset.Zero) }) {
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
                if (mood == BitMood.Happy) {
                    drawPath(HappyEyes, Color.White, style = Stroke(1.4f, cap = StrokeCap.Round))
                } else {
                    val grow = if (mood == BitMood.Waiting) 1.15f else 1f
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
    }
}
