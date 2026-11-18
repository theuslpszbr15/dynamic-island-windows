package io.github.theuslpszbr15.dynamicisland

import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.ColumnScope
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Icon
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp

/**
 * The app's look, in one place: dark neutral greys, one off-white for what is active, and green / amber / red only to say
 * a status. One typeface, the system sans.
 */
object Ui {
    val Bg = Color(0xFF111113)
    val Card = Color(0xFF1E1E21)
    val Raised = Color(0xFF2B2B2F)
    val Line = Color(0xFF36363B)
    val Text = Color(0xFFF2F2F4)
    val Muted = Color(0xFF9B9BA3)
    val Faint = Color(0xFF6A6A72)
    val OnAction = Color(0xFF141416)
    val Ok = Color(0xFF4ADE80)
    val Warn = Color(0xFFF5B544)
    val Danger = Color(0xFFF26B63)

    val CardShape = RoundedCornerShape(24.dp)
    val ButtonShape = RoundedCornerShape(16.dp)
    val ChipShape = RoundedCornerShape(12.dp)
    val Display = FontFamily.Default
}

enum class Tone { Neutral, Ok, Warn, Bad }

@Composable
fun UiTitle(text: String, modifier: Modifier = Modifier, size: Int = 26) =
    Text(text, color = Ui.Text, fontFamily = Ui.Display, fontWeight = FontWeight.SemiBold, fontSize = size.sp, modifier = modifier)

@Composable
fun UiSection(text: String, modifier: Modifier = Modifier) =
    Text(text, color = Ui.Muted, fontSize = 13.sp, fontWeight = FontWeight.Medium, modifier = modifier.padding(start = 4.dp, top = 8.dp))

@Composable
fun UiCard(modifier: Modifier = Modifier, content: @Composable ColumnScope.() -> Unit) =
    Column(modifier.fillMaxWidth().background(Ui.Card, Ui.CardShape).padding(16.dp), verticalArrangement = Arrangement.spacedBy(10.dp), content = content)

@Composable
fun UiCardTitle(text: String, note: String? = null) {
    Column {
        Text(text, color = Ui.Text, fontFamily = Ui.Display, fontWeight = FontWeight.SemiBold, fontSize = 19.sp)
        if (note != null) Text(note, color = Ui.Muted, fontSize = 13.sp, modifier = Modifier.padding(top = 2.dp))
    }
}

/** The one main action: off-white, dark text. */
@Composable
fun UiPrimary(label: String, onClick: () -> Unit, modifier: Modifier = Modifier, enabled: Boolean = true, height: Dp = 48.dp) {
    Box(
        modifier.height(height).clip(Ui.ButtonShape)
            .background(if (enabled) Ui.Text else Ui.Raised)
            .clickable(enabled = enabled, onClick = onClick),
        contentAlignment = Alignment.Center,
    ) { Text(label, color = if (enabled) Ui.OnAction else Ui.Faint, fontWeight = FontWeight.SemiBold, fontSize = 15.sp) }
}

@Composable
fun UiSecondary(label: String, onClick: () -> Unit, modifier: Modifier = Modifier, danger: Boolean = false, enabled: Boolean = true, height: Dp = 48.dp) {
    Box(
        modifier.height(height).clip(Ui.ButtonShape).background(Ui.Raised).clickable(enabled = enabled, onClick = onClick),
        contentAlignment = Alignment.Center,
    ) {
        Text(label, color = if (!enabled) Ui.Faint else if (danger) Ui.Danger else Ui.Text, fontWeight = FontWeight.SemiBold, fontSize = 15.sp)
    }
}

/** A small pill button on a row ("Ativar"). */
@Composable
fun UiPill(label: String, onClick: () -> Unit, modifier: Modifier = Modifier) {
    Box(
        modifier.height(32.dp).clip(RoundedCornerShape(16.dp)).background(Ui.Text).clickable(onClick = onClick).padding(horizontal = 14.dp),
        contentAlignment = Alignment.Center,
    ) { Text(label, color = Ui.OnAction, fontWeight = FontWeight.SemiBold, fontSize = 13.sp) }
}

/** A text link-like action inside a card ("Tentar agora"). */
@Composable
fun UiLink(label: String, onClick: () -> Unit, modifier: Modifier = Modifier, danger: Boolean = false) =
    Text(label, color = if (danger) Ui.Danger else Ui.Text, fontWeight = FontWeight.SemiBold, fontSize = 14.sp,
        modifier = modifier.clip(RoundedCornerShape(10.dp)).clickable(onClick = onClick).padding(vertical = 8.dp, horizontal = 2.dp))

@Composable
fun UiTag(text: String, tone: Tone = Tone.Neutral, modifier: Modifier = Modifier) {
    val (bg, fg) = when (tone) {
        Tone.Ok -> Ui.Ok.copy(alpha = 0.14f) to Ui.Ok
        Tone.Warn -> Ui.Warn.copy(alpha = 0.16f) to Ui.Warn
        Tone.Bad -> Ui.Danger.copy(alpha = 0.14f) to Ui.Danger
        Tone.Neutral -> Ui.Raised to Ui.Muted
    }
    Text(text, color = fg, fontSize = 12.sp, fontWeight = FontWeight.SemiBold, maxLines = 1,
        modifier = modifier.background(bg, RoundedCornerShape(10.dp)).padding(horizontal = 9.dp, vertical = 3.dp))
}

/** A square holding an icon: how every row says what it is about. */
@Composable
fun UiChip(icon: Int, tint: Color = Ui.Text, size: Dp = 36.dp, background: Color = Ui.Raised) {
    Box(Modifier.size(size).background(background, Ui.ChipShape), contentAlignment = Alignment.Center) {
        Icon(painterResource(icon), contentDescription = null, tint = tint, modifier = Modifier.size(size * 0.5f))
    }
}

/** A big number with its label: the bento tiles on the first screens. */
@Composable
fun UiStat(label: String, value: String, modifier: Modifier = Modifier, unit: String? = null, note: String? = null, icon: Int? = null) {
    Column(modifier.background(Ui.Card, Ui.CardShape).padding(16.dp), verticalArrangement = Arrangement.spacedBy(6.dp)) {
        Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            if (icon != null) Icon(painterResource(icon), contentDescription = null, tint = Ui.Muted, modifier = Modifier.size(16.dp))
            Text(label, color = Ui.Muted, fontSize = 13.sp, fontWeight = FontWeight.Medium, maxLines = 1)
        }
        Row(verticalAlignment = Alignment.Bottom) {
            Text(value, color = Ui.Text, fontSize = 38.sp, fontWeight = FontWeight.SemiBold)
            if (unit != null) Text(unit, color = Ui.Muted, fontSize = 15.sp, modifier = Modifier.padding(start = 4.dp, bottom = 5.dp))
        }
        if (note != null) Text(note, color = Ui.Muted, fontSize = 13.sp)
    }
}

/** A row of a grouped list: icon square, title and optional line, then whatever sits on the right. */
@Composable
fun UiRow(
    title: String,
    modifier: Modifier = Modifier,
    note: String? = null,
    icon: Int? = null,
    onClick: (() -> Unit)? = null,
    trailing: @Composable () -> Unit = {},
) {
    Row(
        modifier.fillMaxWidth().let { if (onClick != null) it.clickable(onClick = onClick) else it }.padding(vertical = 10.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        if (icon != null) UiChip(icon)
        Column(Modifier.weight(1f)) {
            Text(title, color = Ui.Text, fontWeight = FontWeight.Medium, fontSize = 15.sp)
            if (note != null) Text(note, color = Ui.Muted, fontSize = 13.sp)
        }
        trailing()
    }
}

@Composable
fun UiDivider() = Box(Modifier.fillMaxWidth().height(1.dp).background(Ui.Line))
