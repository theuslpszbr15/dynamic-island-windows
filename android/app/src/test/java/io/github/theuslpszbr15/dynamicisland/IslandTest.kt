package io.github.theuslpszbr15.dynamicisland

import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.util.Base64

class IslandTest {
    private val png = byteArrayOf(0x89.toByte(), 'P'.code.toByte(), 'N'.code.toByte(), 'G'.code.toByte(), 13, 10, 26, 10, 0, 0)

    private fun discord(vararg pairs: Pair<String, Any>) = JSONObject().put("type", "discord").apply { pairs.forEach { put(it.first, it.second) } }

    @Test fun parsesJoinAndLeave() {
        val e = DiscordEvent.parse(discord("event" to "join", "name" to "Ana", "channel" to "Geral", "count" to 3,
            "avatar" to Base64.getEncoder().encodeToString(png)), now = 5)!!
        assertTrue(e.joined)
        assertEquals("Ana", e.name)
        assertEquals("Geral", e.channel)
        assertEquals(3, e.count)
        assertNotNull(e.avatar)
        assertFalse(DiscordEvent.parse(discord("event" to "leave", "name" to "Ana"))!!.joined)
    }

    @Test fun dropsWhatIsOff() {
        assertNull(DiscordEvent.parse(discord("event" to "kick", "name" to "Ana")))
        assertNull(DiscordEvent.parse(discord("event" to "join", "name" to "  ")))
        // not a PNG, or too big: the event shows without a picture
        assertNull(DiscordEvent.parse(discord("event" to "join", "name" to "Ana", "avatar" to Base64.getEncoder().encodeToString(ByteArray(40))))!!.avatar)
        val big = png + ByteArray(DiscordEvent.MAX_AVATAR_BYTES)
        assertNull(DiscordEvent.parse(discord("event" to "join", "name" to "Ana", "avatar" to Base64.getEncoder().encodeToString(big)))!!.avatar)
        assertNull(DiscordEvent.parse(discord("event" to "join", "name" to "Ana", "avatar" to "não é base64"))!!.avatar)
        assertEquals(999, DiscordEvent.parse(discord("event" to "join", "name" to "Ana", "count" to 100000))!!.count)
    }

    @Test fun namesAreOneSafeLine() {
        assertEquals("Anagpj.exe", DiscordEvent.clean("Ana‮gpj.exe\n", 64))
        assertEquals(64, DiscordEvent.clean("x".repeat(500), 64).length)
    }

    @Test fun pillPutsTheUrgentFirst() {
        val now = 100_000L
        val running = LiveTransfer("a", "Mandando pro PC", "foto.jpg", 40, LiveTransfer.State.Running, now)
        val joined = DiscordEvent(true, "Ana", "Geral", 2, null, now - 1_000)
        val call = PhoneCall("Geral", "Servidor", now - 60_000, emptyList())
        assertEquals(Pill.Kind.Approval, Island.pill(1, 2, 0, listOf(running), listOf(joined), call, now).kind)
        assertEquals("Mandando pro PC · 40%", Island.pill(0, 2, 0, listOf(running), listOf(joined), call, now).text)
        assertEquals("Ana entrou", Island.pill(0, 2, 0, emptyList(), listOf(joined), call, now).text)
        // an old event no longer lights the pill
        assertEquals(Pill(Pill.Kind.Pending, "3 pendentes", 3), Island.pill(0, 2, 1, emptyList(), listOf(joined), call, now + Island.FLASH_MS + 1))
        assertEquals(Pill.Kind.Call, Island.pill(0, 0, 0, emptyList(), emptyList(), call, now).kind)
        assertEquals(Pill.Kind.Idle, Island.pill(0, 0, 0, emptyList(), emptyList(), null, now).kind)
    }

    @Test fun finishedTransfersLeaveAfterAMinute() {
        val done = LiveTransfer("a", "Recebido do PC", "", 100, LiveTransfer.State.Done, 0)
        val list = Island.Transfers.put(listOf(done), LiveTransfer("b", "Recebendo", "", 5, LiveTransfer.State.Running, Island.KEEP_DONE_MS + 1), Island.KEEP_DONE_MS + 1)
        assertEquals(listOf("b"), list.map { it.id })
        // the same id is replaced, not repeated
        val again = Island.Transfers.put(list, LiveTransfer("b", "Recebendo", "", 50, LiveTransfer.State.Running, 1), 1)
        assertEquals(listOf(50), again.map { it.percent })
    }

    @Test fun onlyTheCallNotificationCounts() {
        val ongoing = android.app.Notification.FLAG_ONGOING_EVENT
        assertTrue(DiscordListener.isCall(true, "call", "", ongoing))
        assertTrue(DiscordListener.isCall(true, null, "voice_connection", ongoing))
        assertFalse(DiscordListener.isCall(false, "call", "voice", 0))
        assertFalse(DiscordListener.isCall(true, null, "uploads", ongoing))
        assertFalse(DiscordListener.isCall(true, null, "voice", ongoing or android.app.Notification.FLAG_GROUP_SUMMARY))
    }
}
