package io.github.theuslpszbr15.dynamicisland

import android.app.Notification
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class NotificationActionsTest {
    private val ringing = PhoneRings.RINGING
    private val offhook = PhoneRings.OFFHOOK
    private val idle = PhoneRings.IDLE

    @Test fun aRingingCallWaitsForTheNumberThenKeepsItsTime() {
        val first = PhoneRings.next(null, false, ringing, "", "", 1_000)
        assertEquals(PhoneRing.State.Ringing, first.ring!!.state)
        assertEquals("Número desconhecido", first.ring!!.who)
        val second = PhoneRings.next(first.ring, false, ringing, "+5511999", "Ana", 1_200)
        assertEquals("Ana", second.ring!!.who)
        assertEquals(1_000L, second.ring!!.at)
    }

    @Test fun aCallNotAnsweredBecomesMissedAndOnePickedUpDoesNot() {
        val r = PhoneRings.next(null, false, ringing, "123", "", 0)
        val missed = PhoneRings.next(r.ring, r.answered, idle, "", "", 5_000)
        assertEquals(PhoneRing.State.Missed, missed.ring!!.state)
        assertEquals("123", missed.ring!!.who)
        val picked = PhoneRings.next(r.ring, r.answered, offhook, "", "", 2_000)
        assertNull(picked.ring)
        assertTrue(picked.answered)
        assertNull(PhoneRings.next(picked.ring, picked.answered, idle, "", "", 9_000).ring)
    }

    @Test fun theRingBeatsEverythingButAnApproval() {
        val ring = PhoneRing(PhoneRing.State.Ringing, "Ana", "1", 0)
        val p = Island.pill(0, 0, 0, emptyList(), emptyList(), null, null, 100, null, emptySet(), emptyList(), emptyList(),
            TimerState.countdown(60_000, 0), null, Recording(), ring)
        assertEquals(Pill.Kind.Ring, p.kind)
        assertEquals("Ana", p.text)
        val approval = Island.pill(1, 0, 0, emptyList(), emptyList(), null, null, 100, null, emptySet(), emptyList(), emptyList(), ring = ring)
        assertEquals(Pill.Kind.Approval, approval.kind)
        val missed = ring.copy(state = PhoneRing.State.Missed, at = 50)
        assertEquals("Chamada perdida", Island.pill(0, 0, 0, emptyList(), emptyList(), null, null, 100, null, emptySet(), ring = missed).text)
        assertEquals(Pill.Kind.Idle, Island.pill(0, 0, 0, emptyList(), emptyList(), null, null, 50 + PhoneRing.MISSED_MS + 1, null, emptySet(), ring = missed).kind)
    }

    @Test fun liveNotificationsAreProgressOrOngoingStatus() {
        val ongoing = Notification.FLAG_ONGOING_EVENT
        assertTrue(DiscordListener.isLive(ongoing, null, 100, false))
        assertTrue(DiscordListener.isLive(ongoing, null, 0, true))
        assertTrue(DiscordListener.isLive(ongoing, Notification.CATEGORY_STATUS, 0, false))
        assertTrue(DiscordListener.isLive(ongoing, Notification.CATEGORY_PROGRESS, 0, false))
        assertFalse(DiscordListener.isLive(ongoing, null, 0, false))
        assertFalse(DiscordListener.isLive(ongoing, Notification.CATEGORY_TRANSPORT, 100, false))
        assertFalse(DiscordListener.isLive(ongoing, Notification.CATEGORY_CALL, 0, false))
        assertFalse(DiscordListener.isLive(Notification.FLAG_GROUP_SUMMARY, null, 100, false))
        assertFalse(DiscordListener.isLive(0, Notification.CATEGORY_STATUS, 0, false))
    }

    @Test fun aLiveNoticeLightsThePillWithItsProgressAndUpdatesInPlace() {
        val a = LiveNote("k", "com.food", "Food", "Pedido a caminho", "chega em 5 min", 40, 0)
        val p = Island.pill(0, 0, 0, emptyList(), emptyList(), null, null, 100, null, emptySet(), live = listOf(a))
        assertEquals(Pill.Kind.Live, p.kind)
        assertEquals("Pedido a caminho", p.text)
        assertEquals("40% · chega em 5 min", p.hint)
        Island.clearLive()
        Island.live(a); Island.live(a.copy(percent = 80))
        assertEquals(listOf(80), Island.live.value.map { it.percent })
        Island.liveGone("k")
        assertTrue(Island.live.value.isEmpty())
    }

    @Test fun pinnedNoticesStayFirstAndSurviveTheShadeButNotADismissal() {
        Island.clearNotes()
        val a = AppNote("a", "p", "P", "t", "x", 1_000)
        val b = AppNote("b", "p", "P", "u", "y", 2_000)
        Island.note(a); Island.note(b)
        Island.togglePin("a")
        assertEquals(listOf("a", "b"), Island.ordered(Island.notes.value, Island.pinned.value).map { it.key })
        Island.noteGone("a")
        assertEquals(listOf("b", "a").toSet(), Island.notes.value.map { it.key }.toSet())
        Island.noteGone("b")
        assertEquals(listOf("a"), Island.notes.value.map { it.key })
        Island.dismissNote("a")
        assertTrue(Island.notes.value.isEmpty())
        assertTrue(Island.pinned.value.isEmpty())
    }
}
