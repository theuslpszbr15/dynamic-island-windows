package io.github.theuslpszbr15.dynamicisland

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Button
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.remember
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp

/** "Verificar atualização": the app's own version, a button, and what is happening. */
@Composable
fun AppUpdateCard() {
    val context = LocalContext.current
    val state by AppUpdater.state.collectAsState()
    val version = remember(context) {
        try { context.packageManager.getPackageInfo(context.packageName, 0).versionName.orEmpty() } catch (_: Exception) { "" }
    }
    Column(Modifier.fillMaxWidth().background(Color(0xFF1E1E21), RoundedCornerShape(24.dp)).padding(16.dp)) {
        Text("Atualização do app", color = Color.White, fontSize = 17.sp, fontWeight = FontWeight.SemiBold)
        Text("Versão instalada: $version (${AppUpdate.installed(context)})", color = Color(0xFF9B9BA3), fontSize = 13.sp)
        val (text, warn) = when (val s = state) {
            UpdateState.Idle -> "" to false
            UpdateState.Checking -> "Verificando…" to false
            UpdateState.UpToDate -> "Você já está na última versão." to false
            is UpdateState.Downloading -> (if (s.percent >= 0) "Baixando… ${s.percent}%" else "Baixando…") to false
            is UpdateState.Ready ->
                (if (AppUpdater.canInstall(context)) "Versão ${s.name} baixada e verificada. Toque em Instalar."
                else "Versão ${s.name} baixada e verificada. O Android vai pedir para permitir \"instalar apps desconhecidos\" " +
                    "para o Dynamic Island; permita, volte e toque em Instalar.") to false
            is UpdateState.Error -> s.message to true
        }
        if (text.isNotEmpty()) Text(text, color = if (warn) Color(0xFFF5B544) else Color.White, fontSize = 13.sp, modifier = Modifier.padding(top = 8.dp))
        val busy = state is UpdateState.Checking || state is UpdateState.Downloading
        if (state is UpdateState.Ready) {
            Button(onClick = { AppUpdater.install(context) }, modifier = Modifier.padding(top = 10.dp), shape = RoundedCornerShape(14.dp)) { Text("Instalar") }
        } else {
            Button(onClick = { AppUpdater.check(context) }, enabled = !busy, modifier = Modifier.padding(top = 10.dp),
                shape = RoundedCornerShape(14.dp)) { Text("Verificar atualização") }
        }
    }
}
