package io.github.theuslpszbr15.dynamicisland

import org.json.JSONObject
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class PendingTest {
    private val id = "0123456789abcdef0123456789abcdef"
    private val sha = "a".repeat(64)

    @Test
    fun outgoingOfferSurvivesAWriteAndRead() {
        val offer = OutgoingOffer(id, "2 arquivos", listOf(OfferItem("a.jpg", 10, sha), OfferItem("b.mp4", 20, sha)), null, 5_000L,
            OutgoingOffer.PENDING)
        assertEquals(offer, OutgoingOffer.parse(JSONObject(offer.toJson().toString())))
        assertEquals(30L, offer.bytes)
        val text = OutgoingOffer(id, "Texto", emptyList(), "olá", 5_000L)
        assertEquals(text, OutgoingOffer.parse(JSONObject(text.toJson().toString())))
        assertEquals(4L, text.bytes) // UTF-8
        assertTrue(text.isText)
    }

    @Test
    fun badMetaIsDropped() {
        assertNull(OutgoingOffer.parse(JSONObject().put("id", "../x").put("text", "oi")))
        assertNull(OutgoingOffer.parse(JSONObject().put("id", id))) // nothing to send
        val bad = OutgoingOffer(id, "a", listOf(OfferItem("a", 1, sha)), null, 0).toJson()
        bad.getJSONArray("items").getJSONObject(0).put("sha256", "zz")
        assertNull(OutgoingOffer.parse(bad))
        // an unknown state reads as not yet heard by the PC (it is offered again)
        val odd = OutgoingOffer(id, "Texto", emptyList(), "oi", 0).toJson().put("state", "???")
        assertEquals(OutgoingOffer.QUEUED, OutgoingOffer.parse(odd)!!.state)
        // names are made safe again
        val name = OutgoingOffer(id, "a", listOf(OfferItem("..\\..\\x.exe", 1, sha)), null, 0).toJson()
        assertEquals("x.exe", OutgoingOffer.parse(name)!!.items[0].name)
    }

    @Test
    fun aDayLaterItExpires() {
        val now = 10L * FileOffer.PENDING_MS
        val offer = OutgoingOffer(id, "Texto", emptyList(), "oi", now - FileOffer.PENDING_MS + 1000)
        assertFalse(offer.expired(now))
        assertTrue(offer.copy(at = now - FileOffer.PENDING_MS - 1).expired(now))
        // a clock far in the future (changed by hand) doesn't keep it forever
        assertTrue(offer.copy(at = now + FileOffer.PENDING_MS).expired(now))
        assertTrue(FileOffer.WINDOW_MS < FileOffer.PENDING_MS)
    }

    @Test
    fun mediaSplitsPicturesFromVideosAndGifs() {
        val records = listOf(
            TransferRecord("1", 3, false, "3 arquivos", 1, 3, "Recebido",
                listOf("content://m/1", "content://m/2", "content://m/3"), listOf("image/jpeg", "image/gif", "video/mp4")),
            TransferRecord("2", 2, false, "doc.pdf", 1, 1, "Recebido", listOf("content://m/4"), listOf("application/pdf")),
            TransferRecord("3", 1, true, "x.png", 1, 1, "No PC", listOf("content://m/5"), listOf("image/png")),
            TransferRecord("4", 0, false, "y.webp", 1, 1, "Recebido", listOf("content://m/1"), listOf("image/webp")),
        )
        assertEquals(listOf("content://m/1"), Media.entries(records, videos = false).map { it.uri })
        assertEquals(listOf("content://m/2", "content://m/3"), Media.entries(records, videos = true).map { it.uri })
    }
}
