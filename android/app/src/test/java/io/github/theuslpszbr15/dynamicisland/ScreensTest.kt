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

    private fun shot(name: String, state: LinkUi, pending: Pairing? = null) =
        captureRoboImage("build/screens/$name.png") {
            CompositionLocalProvider(LocalStill provides true) {
                AppTheme {
                    AppScreen(state, null, pending, {}, {}, {}, {}, {}, {}, {}, {}, {})
                }
            }
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

    @Test fun tray() = shot("05-bandeja", LinkUi(
        pairing = pairing, status = LinkStatus.Connected, desktop = "PC-DO-MATHEUS", initialTab = 1,
        tray = listOf(
            TrayItem("1", "Code.exe", "app", null),
            TrayItem("2", "Relatório de vendas.xlsx", "file", null),
            TrayItem("3", "Projetos", "folder", null),
            TrayItem("4", "notepad.exe", "app", null),
        ),
    ))
}
