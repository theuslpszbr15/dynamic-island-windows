package io.github.theuslpszbr15.dynamicisland

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.padding
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.github.takahirom.roborazzi.captureRoboImage
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.annotation.Config
import org.robolectric.annotation.GraphicsMode

/** The island's states on the app's grey, to app/build/screens/island.png (no phone needed). */
@RunWith(AndroidJUnit4::class)
@GraphicsMode(GraphicsMode.Mode.NATIVE)
@Config(sdk = [34], qualifiers = "w411dp-h1250dp-night-xxhdpi")
class IslandShotTest {
    @Test fun island() = captureRoboImage("build/screens/island.png") {
        CompositionLocalProvider(LocalStill provides true) {
            run {
                Column(Modifier.background(Ui.Card).padding(16.dp), verticalArrangement = Arrangement.spacedBy(14.dp),
                    horizontalAlignment = Alignment.CenterHorizontally) {
                    listOf("rest", "media", "approval", "note", "timer", "open-approval", "open-player", "open-notes").forEach { IslandSample(it) }
                }
            }
        }
    }
}
