package io.github.theuslpszbr15.dynamicisland

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.padding
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.unit.dp
import androidx.test.ext.junit.runners.AndroidJUnit4
import com.github.takahirom.roborazzi.captureRoboImage
import org.junit.Test
import org.junit.runner.RunWith
import org.robolectric.annotation.Config
import org.robolectric.annotation.GraphicsMode

/** The five frames of the pat (hand, squash, heart) next to the resting Bit: app/build/screens/07-carinho.png. */
@RunWith(AndroidJUnit4::class)
@GraphicsMode(GraphicsMode.Mode.NATIVE)
@Config(sdk = [34], qualifiers = "w411dp-h891dp-night-xxhdpi")
class PatPatTest {
    @Test fun frames() = captureRoboImage("build/screens/07-carinho.png") {
        CompositionLocalProvider(LocalStill provides true) {
            AppTheme {
                Row(Modifier.background(Color(0xFF111418)).padding(start = 8.dp, end = 8.dp, top = 56.dp, bottom = 8.dp)) {
                    Bit(BitMood.Idle, size = 56.dp)
                    for (frame in 0..4) Bit(BitMood.Idle, size = 56.dp, previewPatFrame = frame)
                }
            }
        }
    }
}
