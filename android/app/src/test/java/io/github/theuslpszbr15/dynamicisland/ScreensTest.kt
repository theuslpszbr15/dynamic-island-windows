package io.github.theuslpszbr15.dynamicisland

import androidx.compose.runtime.CompositionLocalProvider
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.github.takahirom.roborazzi.captureRoboImage
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.annotation.Config
import org.robolectric.annotation.GraphicsMode

/** Renders the screens to PNG files in app/build/screens (Robolectric native graphics, no phone needed). */
@RunWith(AndroidJUnit4::class)
@GraphicsMode(GraphicsMode.Mode.NATIVE)
@Config(sdk = [34], qualifiers = "w411dp-h891dp-night-xxhdpi")
class ScreensTest {
    private val pairing = Pairing("192.168.0.12", 47900, "a".repeat(64), "abcDEF0123456789_-xyz", "PC-DO-MATHEUS")

    private fun shot(name: String, state: LinkUi, pending: Pairing? = null, setup: Boolean = false) {
        val context = androidx.test.core.app.ApplicationProvider.getApplicationContext<android.content.Context>()
        // the permissions screen only shows on the first run: every other shot starts after it
        if (!setup) Setup.finish(context)
        captureRoboImage("build/screens/$name.png") {
            CompositionLocalProvider(LocalStill provides true) {
                AppTheme {
                    AppScreen(state, null, pending, {}, {}, {}, {}, {}, {}, {}, {}, {}, {})
                }
            }
        }
    }

    @Test fun permissions() = shot("00-permissoes", LinkUi(), setup = true)

    @Test fun send() = shot("07-enviar", LinkUi(
        pairing = pairing, status = LinkStatus.Connected, desktop = "PC-DO-MATHEUS", initialTab = 2,
    ))

    @Test fun history() {
        val context = androidx.test.core.app.ApplicationProvider.getApplicationContext<android.content.Context>()
        val now = System.currentTimeMillis()
        History.add(context, TransferRecord("r1", now - 3_600_000, sent = false, title = "relatorio.pdf", bytes = 840_000, count = 1,
            status = "Recebido", uris = listOf("content://media/external/downloads/1"), mimes = listOf("application/pdf")))
        History.add(context, TransferRecord("r2", now - 600_000, sent = false, title = "print da tela.png", bytes = 1_900_000, count = 1,
            status = "Copiado", uris = listOf("content://io.github.theuslpszbr15.dynamicisland.updates/received/r2/print.png"), mimes = listOf("image/png")))
        History.add(context, TransferRecord("r3", now - 60_000, sent = false, title = "Texto", bytes = 60, count = 1,
            status = "Copiado", text = "Senha do Wi-Fi da sala: visitas2026"))
        shot("08-historico", LinkUi(pairing = pairing, status = LinkStatus.Connected, desktop = "PC-DO-MATHEUS", initialTab = 3))
    }

    @Test fun pair() = shot("01-parear", LinkUi())

    @Test fun confirm() = shot("02-confirmar", LinkUi(), pending = pairing)

    @Test fun approval() = shot("03-aprovacao", LinkUi(
        pairing = pairing, status = LinkStatus.Connected, desktop = "PC-DO-MATHEUS",
        approvals = listOf(Approval("f".repeat(64), "Claude Code", "dynamic-island",
            "$ git push origin main --force-with-lease", false, System.currentTimeMillis())),
        sessions = listOf(
            Session("1", "Claude Code", "dynamic-island", "approval", "Pedindo permissão: $ git push origin main", 0),
            Session("2", "Codex", "api", "working", "$ npm test", 0),
        ),
    ))

    @Test fun offline() = shot("04-sem-conexao", LinkUi(
        pairing = pairing, status = LinkStatus.Offline, message = "connect timed out",
    ))

    @Test fun paused() = shot("06-parado", LinkUi(pairing = pairing, status = LinkStatus.Paused))

    @Test fun tray() = shot("05-bandeja", LinkUi(
        pairing = pairing, status = LinkStatus.Connected, desktop = "PC-DO-MATHEUS", initialTab = 1,
        tray = listOf(
            TrayItem("1", "Code.exe", "app", null),
            TrayItem("2", "Relatório de vendas.xlsx", "file", null),
            TrayItem("3", "Projetos", "folder", null),
            TrayItem("4", "notepad.exe", "app", null),
        ),
    ))

    @Test fun jarvis() = shot("09-jarvis", LinkUi(
        pairing = pairing, status = LinkStatus.Connected, desktop = "PC-DO-MATHEUS", initialTab = 1,
        jarvis = listOf(
            JarvisTurn("1", "que horas são?", "São 15:42.", "done"),
            JarvisTurn("2", "abre o Spotify e toca minha playlist", "", "thinking"),
        ),
    ))

    private fun touchpad(name: String, mode: Int) = captureRoboImage("build/screens/$name.png") {
        CompositionLocalProvider(LocalStill provides true) {
            AppTheme { TouchpadScreen(connected = true, viaRelay = false, onClose = {}, initialMode = mode) }
        }
    }

    @Test fun touchpadMouse() = touchpad("10-touchpad", 0)

    @Test fun touchpadMovie() = touchpad("11-touchpad-filme", 1)

    @Test fun touchpadSlides() = touchpad("12-touchpad-slides", 2)

    @Test @Config(sdk = [34], qualifiers = "w891dp-h411dp-night-xxhdpi")
    fun gamepad() = captureRoboImage("build/screens/13-controle-jogo.png") {
        CompositionLocalProvider(LocalStill provides true) {
            AppTheme { TouchpadScreen(connected = true, viaRelay = false, onClose = {}, initialMode = 3) }
        }
    }

    @Test @Config(sdk = [34], qualifiers = "w891dp-h411dp-night-xxhdpi")
    fun gamepadEditor() = captureRoboImage("build/screens/14-controle-editar.png") {
        CompositionLocalProvider(LocalStill provides true) {
            AppTheme { TouchpadScreen(connected = true, viaRelay = false, onClose = {}, initialMode = 3, initialEditing = true) }
        }
    }

    @Test @Config(sdk = [34], qualifiers = "w891dp-h411dp-night-xxhdpi")
    fun gamepadRacing() = captureRoboImage("build/screens/15-controle-corrida.png") {
        CompositionLocalProvider(LocalStill provides true) {
            AppTheme { TouchpadScreen(connected = true, viaRelay = false, onClose = {}, initialMode = 3, initialPad = PadLayout.Racing) }
        }
    }
}
