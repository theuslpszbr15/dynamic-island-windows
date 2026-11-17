package io.github.theuslpszbr15.dynamicisland

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class WakeTest {
    @Test
    fun magicPacketIsSixFfThenTheMacSixteenTimes() {
        val packet = Wake.packet("A1:B2:C3:D4:E5:F6")
        assertEquals(6 + 16 * 6, packet.size)
        assertTrue(packet.take(6).all { it == 0xFF.toByte() })
        val mac = byteArrayOf(0xA1.toByte(), 0xB2.toByte(), 0xC3.toByte(), 0xD4.toByte(), 0xE5.toByte(), 0xF6.toByte()).toList()
        for (i in 0 until 16) assertEquals(mac, packet.slice(6 + i * 6 until 12 + i * 6))
    }
}
