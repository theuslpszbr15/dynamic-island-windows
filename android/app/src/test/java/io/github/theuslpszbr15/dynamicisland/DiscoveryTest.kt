package io.github.theuslpszbr15.dynamicisland

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class DiscoveryTest {
    private val pairing = Pairing("192.168.0.12", 47900, "a".repeat(64), "abcDEF0123456789_-xyz", "PC")

    @Test fun takesTheNewAddressOfTheSamePc() {
        val found = Discovery.answer(pairing, "192.168.0.40", """{"type":"here","v":1,"fp":"${"a".repeat(64)}","port":47901}""")
        assertEquals(pairing.copy(host = "192.168.0.40", port = 47901), found)
    }

    @Test fun ignoresOtherPcsAndJunk() {
        assertNull(Discovery.answer(pairing, "192.168.0.40", """{"type":"here","fp":"${"b".repeat(64)}","port":47900}"""))
        assertNull(Discovery.answer(pairing, "192.168.0.40", """{"type":"discover","fp":"${"a".repeat(64)}","port":47900}"""))
        assertNull(Discovery.answer(pairing, "192.168.0.40", """{"type":"here","fp":"${"a".repeat(64)}","port":0}"""))
        assertNull(Discovery.answer(pairing, "192.168.0.40", "not json"))
    }

    @Test fun neverAPublicAddress() {
        assertNull(Discovery.answer(pairing, "8.8.8.8", """{"type":"here","fp":"${"a".repeat(64)}","port":47900}"""))
    }
}
