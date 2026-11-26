package io.github.theuslpszbr15.dynamicisland

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class TouchpadTest {
    @Test
    fun typingMoreSendsOnlyTheNewLetters() = assertEquals(Touchpad.Edit(0, "a"), Touchpad.diff("ol", "ola"))

    @Test
    fun deletingSendsBackspaces() = assertEquals(Touchpad.Edit(2, ""), Touchpad.diff("olá", "o"))

    /** "coracao" and "coração" share "cora": the rest is erased and typed again. */
    @Test
    fun autocorrectErasesTheWordAndTypesTheNewOne() = assertEquals(Touchpad.Edit(3, "ção"), Touchpad.diff("oi coracao", "oi coração"))

    @Test
    fun padFrameCarriesButtonsSticksAndTriggers() {
        Gamepad.ps4 = true
        Gamepad.press(Gamepad.A or Gamepad.UP, true)
        Gamepad.stick(true, 1f, -0.5f)
        Gamepad.trigger(false, true)
        val f = Gamepad.frame()
        assertEquals("ps4", f.getString("kind"))
        assertEquals(Gamepad.A or Gamepad.UP, f.getInt("b"))
        assertEquals(32767, f.getInt("lx"))
        assertEquals(-16383, f.getInt("ly"))
        assertEquals(255, f.getInt("rt"))
        assertEquals(0, f.getInt("lt"))
        Gamepad.stop()
        assertEquals(0, Gamepad.frame().getInt("b"))
    }

    @Test
    fun layoutSurvivesSavingAndClampsJunk() {
        val edited = PadLayout.Racing.with("face", PadItem(0.6f, 0.4f, 150f, 150f)).copy(scale = 1.2f, alpha = 0.6f)
        val back = PadLayout.fromJson(org.json.JSONObject(edited.toJson().toString()))
        assertEquals(edited, back)
        val junk = PadLayout.fromJson(org.json.JSONObject("""{"scale":9,"alpha":0,"items":{"ls":[5,-1,9999,1,0]}}"""))
        assertEquals(PadLayout.MAX_SCALE, junk.scale)
        assertEquals(PadLayout.MIN_ALPHA, junk.alpha)
        assertEquals(PadItem(1f, 0f, PadLayout.MAX_SIZE, PadLayout.MIN_SIZE), junk.item("ls"))
        assertEquals(PadLayout.Default.item("face"), junk.item("face"))
    }

    @Test
    fun everyPresetPlacesEveryControl() = PadLayout.Presets.forEach { (name, preset) ->
        PadLayout.Ids.forEach { id -> assertTrue("$name sem $id", preset.items.containsKey(id)) }
    }

    @Test
    fun tiltingClockwiseSteersRight() {
        assertEquals(0f, PadLayout.steer(0f, 9.8f), 0.001f)
        // turned clockwise by 20°, gravity leans left on the screen
        val right = PadLayout.steer(-9.8f * kotlin.math.sin(Math.toRadians(20.0)).toFloat(), 9.8f * kotlin.math.cos(Math.toRadians(20.0)).toFloat())
        assertTrue(right > 0.4f && right < 0.6f)
        assertEquals(-1f, PadLayout.steer(9.8f, 0f), 0.001f)
        assertEquals(0f, PadLayout.steer(0.5f, 0.5f), 0.001f) // flat on a table
    }

    @Test
    fun tiltSteersUnlessTheThumbIsOnTheStick() {
        Gamepad.steering(0.5f)
        assertEquals(16384, Gamepad.frame().getInt("lx"))
        Gamepad.stick(true, -1f, 0f)
        assertEquals(-32767, Gamepad.frame().getInt("lx"))
        Gamepad.stick(true, 0f, 0f)
        Gamepad.steering(null)
        assertEquals(0, Gamepad.frame().getInt("lx"))
    }

    @Test
    fun slowMovesArePreciseFastOnesGoFurther() {
        val (slowX, _) = Touchpad.accelerate(2f, 0f)
        val (fastX, _) = Touchpad.accelerate(40f, 0f)
        assertTrue(slowX / 2f < 1.3f)
        assertTrue(fastX / 40f > 2.5f)
        assertEquals(-Touchpad.accelerate(10f, 0f).first, Touchpad.accelerate(-10f, 0f).first, 0.001f)
    }
}
