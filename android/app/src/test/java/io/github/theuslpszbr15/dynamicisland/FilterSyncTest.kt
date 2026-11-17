package io.github.theuslpszbr15.dynamicisland

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class FilterSyncTest {
    @Test fun newestChangeWinsAndTheSameInstantStops() {
        assertTrue(IslandFilter.isNewer(10, 11))
        assertFalse(IslandFilter.isNewer(10, 10))
        assertFalse(IslandFilter.isNewer(10, 9))
        assertTrue(IslandFilter.isNewer(0, 1))
    }

    @Test fun namesRoundTripAndUnknownOnesAreIgnored() {
        val off = setOf(Pill.Kind.Discord, Pill.Kind.Call)
        assertEquals(off, IslandFilter.parse(IslandFilter.names(off)))
        assertEquals(setOf(Pill.Kind.Media), IslandFilter.parse(listOf("Media", "Idle", "FromTheFuture")))
        assertEquals(emptyList<String>(), IslandFilter.names(emptySet()))
    }

    private fun event(name: String, joined: Boolean, at: Long, channel: String = "Geral") =
        DiscordEvent(joined, name, channel, 2, null, at)

    @Test fun theSamePersonTwiceInAFewSecondsIsOneNotice() {
        val first = Island.with(emptyList(), event("Ana", true, 1_000))
        assertEquals(first, Island.with(first, event("Ana", true, 2_500)))
    }

    @Test fun otherPeopleLeavesAndLaterJoinsStillShow() {
        val list = Island.with(emptyList(), event("Ana", true, 1_000))
        assertEquals(2, Island.with(list, event("Bia", true, 1_100)).size)
        assertEquals(2, Island.with(list, event("Ana", false, 1_100)).size)
        assertEquals(2, Island.with(list, event("Ana", true, 1_000 + Island.DUPLICATE_MS + 1)).size)
        assertEquals(2, Island.with(list, event("Ana", true, 1_100, channel = "Outra")).size)
    }
}
