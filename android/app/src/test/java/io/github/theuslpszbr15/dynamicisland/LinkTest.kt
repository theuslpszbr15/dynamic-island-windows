package io.github.theuslpszbr15.dynamicisland

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Assume.assumeTrue
import org.junit.Test
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import kotlin.concurrent.thread

class PairingTest {
    private val fp = "a".repeat(64)
    private val token = "abcDEF0123456789_-xyz"

    @Test fun parsesThePcLink() {
        val p = Pairing.parse("dynamicisland://pair?v=1&host=192.168.0.12&port=47900&fp=$fp&token=$token&name=MEU%20PC")
        assertNotNull(p)
        assertEquals("192.168.0.12", p!!.host)
        assertEquals(47900, p.port)
        assertEquals("MEU PC", p.name)
        assertEquals(p, Pairing.fromJson(p.toJson()))
    }

    @Test fun refusesWhatIsNotAPairing() {
        assertNull(Pairing.parse("https://example.com"))
        assertNull(Pairing.parse("dynamicisland://pair?v=2&host=192.168.0.12&port=47900&fp=$fp&token=$token"))
        assertNull(Pairing.parse("dynamicisland://pair?v=1&host=192.168.0.12&port=47900&fp=XYZ&token=$token"))
        assertNull(Pairing.parse("dynamicisland://pair?v=1&host=192.168.0.12&port=47900&fp=$fp&token=short"))
        // never a public address
        assertNull(Pairing.parse("dynamicisland://pair?v=1&host=8.8.8.8&port=47900&fp=$fp&token=$token"))
        assertNull(Pairing.parse("dynamicisland://pair?v=1&host=evil.example.com&port=47900&fp=$fp&token=$token"))
    }

    @Test fun localAddressesOnly() {
        assertTrue(Pairing.isLocalAddress("10.0.0.5"))
        assertTrue(Pairing.isLocalAddress("172.20.1.1"))
        assertTrue(!Pairing.isLocalAddress("172.32.1.1"))
        assertTrue(!Pairing.isLocalAddress("300.1.1.1"))
    }
}

/**
 * Against a real Dynamic Island on this PC: PHONE_LINK=<pairing link> (host may be 127.0.0.1).
 * With PHONE_LINK_APPROVE=1 it waits for a permission request and allows it.
 */
class LinkInteropTest {
    @Test fun talksToTheIsland() {
        val link = System.getenv("PHONE_LINK")
        assumeTrue("PHONE_LINK not set", !link.isNullOrBlank())
        val pairing = Pairing.parse(link!!)!!
        val client = LinkClient(pairing, "teste JVM")
        val welcomed = CountDownLatch(1)
        val listed = CountDownLatch(1)
        val asked = CountDownLatch(1)
        val resolved = CountDownLatch(1)
        var approval: Approval? = null
        var failure: Throwable? = null
        val worker = thread {
            try {
                client.run(object : LinkClient.Listener {
                    override fun onWelcome(desktop: String) { println("welcome: $desktop"); welcomed.countDown() }
                    override fun onSessions(sessions: List<Session>) { println("sessions: $sessions"); listed.countDown() }
                    override fun onApproval(approval: Approval) {
                        println("approval: $approval")
                        if (approval.command.contains("interop")) setApproval(approval)
                    }
                    override fun onResolved(fingerprint: String) { println("resolved: $fingerprint"); resolved.countDown() }
                    private fun setApproval(a: Approval) { approval = a; asked.countDown() }
                })
            } catch (t: Throwable) { failure = t }
        }
        assertTrue("no welcome (${failure})", welcomed.await(10, TimeUnit.SECONDS))
        assertTrue("no sessions", listed.await(5, TimeUnit.SECONDS))
        client.ping()

        if (System.getenv("PHONE_LINK_APPROVE") == "1") {
            assertTrue("no approval", asked.await(40, TimeUnit.SECONDS))
            client.decide(approval!!.fingerprint, allow = true)
            assertTrue("not resolved", resolved.await(10, TimeUnit.SECONDS))
        }
        client.close()
        worker.join(3000)
    }

    @Test fun refusesAnotherCertificate() {
        val link = System.getenv("PHONE_LINK")
        assumeTrue("PHONE_LINK not set", !link.isNullOrBlank())
        val pairing = Pairing.parse(link!!)!!.copy(fingerprint = "0".repeat(64))
        val error = runCatching { LinkClient(pairing, "teste").run(object : LinkClient.Listener {
            override fun onWelcome(desktop: String) {}
            override fun onSessions(sessions: List<Session>) {}
            override fun onApproval(approval: Approval) {}
            override fun onResolved(fingerprint: String) {}
        }) }.exceptionOrNull()
        assertNotNull("a different certificate must be refused", error)
        println("refused: $error")
    }
}
