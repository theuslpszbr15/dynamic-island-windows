package io.github.theuslpszbr15.dynamicisland

import org.junit.Assert.assertEquals
import org.junit.Test

class SessionAlertsTest {
    private fun s(id: String, state: String) = Session(id, "Claude Code", "site", state, "", 0)

    @Test fun finishingAlerts() {
        val alerts = SessionAlerts.changes(listOf(s("a", "working")), listOf(s("a", "done")))
        assertEquals(listOf(SessionAlert.Kind.Done), alerts.map { it.kind })
    }

    @Test fun waitingAlertsOnce() {
        assertEquals(1, SessionAlerts.changes(listOf(s("a", "working")), listOf(s("a", "waiting"))).size)
        assertEquals(0, SessionAlerts.changes(listOf(s("a", "waiting")), listOf(s("a", "waiting"))).size)
    }

    @Test fun firstSnapshotAndPermissionsStayQuiet() {
        // right after connecting nothing is known yet, so an already-done session says nothing
        assertEquals(0, SessionAlerts.changes(emptyList(), listOf(s("a", "done"), s("b", "waiting"))).size)
        // permission requests have their own notification
        assertEquals(0, SessionAlerts.changes(listOf(s("a", "working")), listOf(s("a", "approval"))).size)
        assertEquals(0, SessionAlerts.changes(listOf(s("a", "idle")), listOf(s("a", "done"))).size)
    }

    @Test fun settledWhenBackToWorkOrGone() {
        val before = listOf(s("a", "done"), s("b", "waiting"), s("c", "done"))
        val after = listOf(s("a", "working"), s("c", "done"))
        assertEquals(listOf("a", "b"), SessionAlerts.settled(before, after))
    }
}
