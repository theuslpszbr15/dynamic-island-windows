package io.github.theuslpszbr15.dynamicisland

/** A session that needs a heads-up on the phone: it finished, or it is waiting for the user (not a permission). */
data class SessionAlert(val session: Session, val kind: Kind) {
    enum class Kind { Done, Waiting }
}

/** Turns two session lists from the PC into alerts (plain JVM code, tested off the phone). */
object SessionAlerts {
    /**
     * Only real transitions count: a session first seen already done (e.g. right after connecting) says nothing.
     * Permission requests ("approval") have their own notification, so they never alert here.
     */
    fun changes(before: List<Session>, after: List<Session>): List<SessionAlert> {
        val old = before.associateBy { it.id }
        return after.mapNotNull { s ->
            val was = old[s.id]?.state ?: return@mapNotNull null
            when {
                s.state == "done" && was in BUSY -> SessionAlert(s, SessionAlert.Kind.Done)
                s.state == "waiting" && was in BUSY && was != "waiting" -> SessionAlert(s, SessionAlert.Kind.Waiting)
                else -> null
            }
        }
    }

    /** Sessions whose alert is stale: back to work, asking for permission, or gone from the PC. */
    fun settled(before: List<Session>, after: List<Session>): List<String> {
        val now = after.associateBy { it.id }
        return before.filter { s -> now[s.id]?.state.let { it == null || it == "working" || it == "approval" } }.map { it.id }
    }

    private val BUSY = setOf("working", "approval", "waiting")
}
