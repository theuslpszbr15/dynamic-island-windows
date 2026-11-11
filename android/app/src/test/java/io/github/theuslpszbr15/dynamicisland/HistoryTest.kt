package io.github.theuslpszbr15.dynamicisland

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

class HistoryTest {
    private fun record(id: String, sent: Boolean = false, status: String = "Recebido", text: String? = null) =
        TransferRecord(id, 1_000L, sent, "$id.jpg", 2048, 1, status,
            if (text == null) listOf("content://media/external/images/media/$id") else emptyList(),
            if (text == null) listOf("image/jpeg") else emptyList(), text)

    @Test
    fun newestFirstAndCappedPerSide() {
        var list = emptyList<TransferRecord>()
        repeat(History.MAX + 5) { list = History.Entries.add(list, record("r$it")) }
        list = History.Entries.add(list, record("s", sent = true, status = "No PC"))
        assertEquals(History.MAX, list.count { !it.sent })
        assertEquals(1, list.count { it.sent })
        assertEquals("s", list[0].id)
        assertEquals("r${History.MAX + 4}", list.first { !it.sent }.id)
    }

    @Test
    fun sameIdReplacesOnlyItsSide() {
        var list = History.Entries.add(emptyList(), record("x", sent = true, status = "Esperando o PC"))
        list = History.Entries.add(list, record("x", sent = false))
        list = History.Entries.setStatus(list, "x", true, "No PC")
        assertEquals(listOf("Recebido", "No PC"), list.map { it.status })
    }

    @Test
    fun cutsLongTextsAndSurvivesAJsonTrip() {
        val long = "a".repeat(History.MAX_TEXT_CHARS + 50)
        val list = History.Entries.add(History.Entries.add(emptyList(), record("t", text = long)), record("f"))
        val back = History.Entries.read(History.Entries.write(list))
        assertEquals(list, back)
        assertEquals(History.MAX_TEXT_CHARS, back[1].text!!.length)
        assertNull(back[0].text)
        assertEquals(listOf("image/jpeg"), back[0].mimes)
    }

    @Test
    fun skipsBrokenEntries() {
        val back = History.Entries.read("""[{"id":""},{"nope":1},{"id":"ok","sent":true,"title":"a","status":"No PC"}]""")
        assertEquals(listOf("ok"), back.map { it.id })
        assertEquals(1, back[0].count)
    }
}
