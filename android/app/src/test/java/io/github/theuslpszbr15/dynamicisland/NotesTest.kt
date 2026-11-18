package io.github.theuslpszbr15.dynamicisland

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class NotesTest {
    private fun note(key: String, pkg: String = "com.chat", title: String = "Ana", text: String = "oi", at: Long = 1_000, category: String = "msg") =
        AppNote(key, pkg, "Chat", title, text, at, category)

    private fun pill(notes: List<AppNote> = emptyList(), pending: List<Session> = emptyList(), now: Long = 2_000,
                     off: Set<Pill.Kind> = emptySet(), media: NowPlaying? = null) =
        Island.pill(0, 0, 0, emptyList(), emptyList(), null, media, now, null, off, notes, pending)

    private fun session(id: String, state: String, at: Long = 1) = Session(id, "Claude", "app", state, "esperando", at)

    @Test fun anyNewNotificationLightsThePill() {
        val p = pill(listOf(note("a")))
        assertEquals(Pill.Kind.Notification, p.kind)
        assertEquals("Ana", p.text)
        assertEquals("oi", p.hint)
    }

    @Test fun anOldNotificationOnlyWaitsInTheCard() {
        assertEquals(Pill.Kind.Idle, pill(listOf(note("a")), now = 1_000 + Island.NOTE_MS + 1).kind)
    }

    @Test fun switchedOffNotificationsStayDark() {
        assertEquals(Pill.Kind.Idle, pill(listOf(note("a")), off = setOf(Pill.Kind.Notification)).kind)
    }

    @Test fun aNotificationBeatsTheSongButNotAnApproval() {
        val song = NowPlaying("spotify", "T", "A", true, 0, 1000, 0)
        assertEquals(Pill.Kind.Notification, pill(listOf(note("a")), media = song).kind)
        val approval = Island.pill(1, 0, 0, emptyList(), emptyList(), null, null, 2_000, null, emptySet(), listOf(note("a")), emptyList())
        assertEquals(Pill.Kind.Approval, approval.kind)
    }

    @Test fun theSameNotificationRepostedIsNotNewsButAnUpdateIs() {
        val first = Island.with(emptyList(), note("k"))
        assertEquals(first, Island.with(first, note("k", at = 9_000)))
        val updated = Island.with(first, note("k", text = "tudo bem?", at = 9_000))
        assertEquals(1, updated.size)
        assertEquals("tudo bem?", updated[0].text)
    }

    @Test fun twinsFromOneAppInAFewSecondsAreOne() {
        val first = Island.with(emptyList(), note("k1"))
        assertEquals(1, Island.with(first, note("k2", at = 2_000)).size)
        assertEquals(2, Island.with(first, note("k2", at = 1_000 + Island.DUPLICATE_MS + 1)).size)
        assertEquals(2, Island.with(first, note("k3", pkg = "com.other", at = 2_000)).size)
    }

    @Test fun theListKeepsTheNewestFirstAndIsBounded() {
        var list = emptyList<AppNote>()
        for (i in 0 until Island.MAX_NOTES + 5) list = Island.with(list, note("k$i", title = "t$i", at = i * 10_000L))
        assertEquals(Island.MAX_NOTES, list.size)
        assertEquals("k${Island.MAX_NOTES + 4}", list.first().key)
    }

    @Test fun ongoingGroupsAndPlayersAreNotNotices() {
        assertTrue(DiscordListener.isNote(0, "msg"))
        assertFalse(DiscordListener.isNote(android.app.Notification.FLAG_ONGOING_EVENT, null))
        assertFalse(DiscordListener.isNote(android.app.Notification.FLAG_GROUP_SUMMARY, null))
        assertFalse(DiscordListener.isNote(android.app.Notification.FLAG_FOREGROUND_SERVICE, null))
        assertFalse(DiscordListener.isNote(0, android.app.Notification.CATEGORY_TRANSPORT))
    }

    @Test fun eachKindOfNotificationMovesItsOwnWay() {
        val styles = listOf("msg", "email", "call", "promo", "event", "progress").map { NoteStyle.of(it, "x") }
        assertEquals(NoteStyle.values().toSet(), styles.toSet())
        // an unknown kind follows the app, always the same way
        assertEquals(NoteStyle.of("", "com.some.app"), NoteStyle.of("", "com.some.app"))
    }

    @Test fun everyAnimationStartsAwayFromRestAndEndsAtRest() {
        NoteStyle.values().forEach { style ->
            val start = style.frame(0.1f)
            assertNotEquals("$style starts moving", NoteFrame(), start)
            val end = style.frame(1f)
            assertEquals(1f, end.scale, 0.001f)
            assertEquals(0f, end.dx, 0.001f)
            assertEquals(1f, end.alpha, 0.001f)
            assertEquals(0f, end.rotation % 360f, 0.001f)
        }
    }

    @Test fun agentsWaitingShowUntilTheyAreHandledOnOneSideOrTheOther() {
        val waiting = session("a", "waiting", at = 5)
        assertEquals(listOf(waiting), Island.pending(listOf(waiting, session("b", "working"), session("c", "approval")), emptyMap()))
        // dismissed here: gone (so the shade alert and the island do not both nag)
        assertEquals(emptyList<Session>(), Island.pending(listOf(waiting), mapOf("a" to 5L)))
        // the session moved on and is waiting again: it is new news
        assertEquals(1, Island.pending(listOf(waiting.copy(updatedAt = 9)), mapOf("a" to 5L)).size)
    }

    @Test fun pendingAgentsGetTheirBarAfterTheSong() {
        val song = NowPlaying("spotify", "T", "A", true, 0, 1000, 0)
        val waiting = listOf(session("a", "waiting"))
        assertEquals(Pill.Kind.Media, pill(pending = waiting, media = song).kind)
        val p = pill(pending = waiting)
        assertEquals(Pill.Kind.Pending, p.kind)
        assertEquals("Claude está esperando você", p.text)
        assertEquals(Pill.Kind.Idle, pill(pending = waiting, off = setOf(Pill.Kind.Pending)).kind)
    }

    @Test fun theNewSwitchesTravelWithTheOthers() {
        val off = setOf(Pill.Kind.Notification, Pill.Kind.Pending)
        assertEquals(off, IslandFilter.parse(IslandFilter.names(off)))
    }

    @Test fun swipingMovesBetweenPagesAndWrapsRound() {
        val pages = Island.hubPages(hasMedia = true)
        assertEquals(listOf(Pill.Kind.Notification, Pill.Kind.Pending, Pill.Kind.Timer, Pill.Kind.Today, Pill.Kind.Media), pages)
        assertEquals(listOf(Pill.Kind.Notification, Pill.Kind.Pending, Pill.Kind.Timer, Pill.Kind.Today), Island.hubPages(hasMedia = false))
        assertEquals(Pill.Kind.Pending, Island.hubStep(pages, Pill.Kind.Notification, 1))
        assertEquals(Pill.Kind.Media, Island.hubStep(pages, Pill.Kind.Notification, -1))
        assertEquals(Pill.Kind.Timer, Island.hubStep(pages, Pill.Kind.Pending, 1))
        assertEquals(Pill.Kind.Notification, Island.hubStep(pages, Pill.Kind.Media, 1))
        assertEquals(Pill.Kind.Notification, Island.hubStep(pages, Pill.Kind.Offer, 1))
    }
}
