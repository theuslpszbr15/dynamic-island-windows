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
    fun slowMovesArePreciseFastOnesGoFurther() {
        val (slowX, _) = Touchpad.accelerate(2f, 0f)
        val (fastX, _) = Touchpad.accelerate(40f, 0f)
        assertTrue(slowX / 2f < 1.3f)
        assertTrue(fastX / 40f > 2.5f)
        assertEquals(-Touchpad.accelerate(10f, 0f).first, Touchpad.accelerate(-10f, 0f).first, 0.001f)
    }
}
