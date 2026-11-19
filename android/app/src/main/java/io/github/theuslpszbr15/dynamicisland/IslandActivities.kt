package io.github.theuslpszbr15.dynamicisland

import android.app.Activity
import android.content.Context
import android.content.Intent
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp

/**
 * Asks Android for permissions the island needs the first time a feature wants them (answering calls, calendar,
 * location), from a transparent window over whatever is on screen, and closes. Nothing is asked until a button of the
 * island needs it.
 */
class PermissionActivity : Activity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val wanted = intent.getStringArrayExtra(EXTRA) ?: return finish()
        requestPermissions(wanted, 1)
    }

    override fun onRequestPermissionsResult(requestCode: Int, permissions: Array<out String>, grantResults: IntArray) {
        IslandOverlay.refresh(this)
        finish()
    }

    companion object {
        private const val EXTRA = "permissions"

        fun ask(context: Context, permissions: Array<String>) {
            context.startActivity(Intent(context, PermissionActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
                .putExtra(EXTRA, permissions))
        }
    }
}

/** A small window over the current app with a text field: answers a message notification without opening its app. */
class ReplyActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val key = intent.getStringExtra(KEY) ?: return finish()
        val app = intent.getStringExtra(APP).orEmpty()
        setContent { ReplyBox(app, onCancel = { finish() }) { text ->
            if (DiscordListener.reply(this, key, text)) { Island.dismissNote(key); finish() }
            else android.widget.Toast.makeText(this, "Não deu para enviar. Responda pelo app.", android.widget.Toast.LENGTH_SHORT).show()
        } }
    }

    companion object {
        private const val KEY = "key"
        private const val APP = "app"

        fun open(context: Context, note: AppNote) {
            context.startActivity(Intent(context, ReplyActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
                .putExtra(KEY, note.key).putExtra(APP, note.app))
        }
    }
}

@Composable
private fun ReplyBox(app: String, onCancel: () -> Unit, onSend: (String) -> Unit) {
    var text by remember { mutableStateOf("") }
    Box(Modifier.fillMaxSize().background(Color(0x99000000)).clickable(onClick = onCancel).padding(20.dp), contentAlignment = Alignment.Center) {
        Column(Modifier.fillMaxWidth().clip(RoundedCornerShape(22.dp)).background(Color(0xFF1C1C1E)).clickable(enabled = false) {}.padding(16.dp)) {
            Text("Responder · $app", color = Color.White, fontSize = 15.sp, fontWeight = FontWeight.SemiBold)
            OutlinedTextField(text, { text = it }, Modifier.fillMaxWidth().padding(top = 10.dp), placeholder = { Text("Sua resposta") })
            Row(Modifier.fillMaxWidth().padding(top = 12.dp), horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                Text("Cancelar", color = Color.White, fontSize = 14.sp, modifier = Modifier.weight(1f).clip(RoundedCornerShape(50))
                    .background(Color(0xFF2C2C2E)).clickable(onClick = onCancel).padding(vertical = 10.dp), textAlign = androidx.compose.ui.text.style.TextAlign.Center)
                Text("Enviar", color = Color.Black, fontSize = 14.sp, fontWeight = FontWeight.SemiBold, modifier = Modifier.weight(1f).clip(RoundedCornerShape(50))
                    .background(Color(0xFF30D158)).clickable(enabled = text.isNotBlank()) { onSend(text.trim()) }.padding(vertical = 10.dp),
                    textAlign = androidx.compose.ui.text.style.TextAlign.Center)
            }
        }
    }
}
