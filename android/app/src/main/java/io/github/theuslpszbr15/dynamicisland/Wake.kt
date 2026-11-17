package io.github.theuslpszbr15.dynamicisland

import android.content.Context
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress

/**
 * "Ligar o PC" (Wake-on-LAN): the "magic packet" (6 × 0xFF, then the PC's MAC 16 times) over UDP. The PC tells its
 * wired card's MAC and the LAN's broadcast address while connected; with the PC off, the phone on the home Wi-Fi sends
 * it there. Away from home it can also go to an address the person set up on the router (port forwarding).
 */
object Wake {
    private val MAC = Regex("^([0-9A-F]{2}:){5}[0-9A-F]{2}$")
    private val IPV4 = Regex("^(\\d{1,3}\\.){3}\\d{1,3}$")

    private fun prefs(context: Context) = context.getSharedPreferences("wake", Context.MODE_PRIVATE)

    fun save(context: Context, mac: String, broadcast: String) {
        val m = mac.uppercase()
        if (!MAC.matches(m)) return
        prefs(context).edit().putString("mac", m).putString("broadcast", broadcast.takeIf { IPV4.matches(it) } ?: "255.255.255.255").apply()
    }

    fun mac(context: Context): String? = prefs(context).getString("mac", null)

    /** "host:port" (or just host, port 9) the router forwards to the PC, for waking it from outside. */
    fun external(context: Context): String = prefs(context).getString("external", "") ?: ""

    fun setExternal(context: Context, value: String) = prefs(context).edit().putString("external", value.trim().take(253)).apply()

    fun packet(mac: String): ByteArray {
        val bytes = mac.split(':').map { it.toInt(16).toByte() }
        return ByteArray(6) { 0xFF.toByte() } + List(16) { bytes }.flatten()
    }

    /** Sends the packet everywhere it might help; [done] gets how many sends went out (on a background thread). */
    fun send(context: Context, done: (Int) -> Unit) {
        val mac = mac(context) ?: return done(0)
        val broadcast = prefs(context).getString("broadcast", "255.255.255.255")!!
        val external = external(context)
        Thread {
            val data = packet(mac)
            var sent = 0
            DatagramSocket().use { socket ->
                socket.broadcast = true
                val targets = mutableListOf(broadcast to 9, "255.255.255.255" to 9, broadcast to 7)
                if (external.isNotEmpty()) {
                    val host = external.substringBeforeLast(':').ifEmpty { external }
                    val port = external.substringAfterLast(':', "9").toIntOrNull()?.takeIf { it in 1..65535 } ?: 9
                    targets += host to port
                }
                // a few times: UDP may drop one, and the card only needs one
                repeat(3) {
                    for ((host, port) in targets) try {
                        socket.send(DatagramPacket(data, data.size, InetAddress.getByName(host), port))
                        sent++
                    } catch (_: Exception) {}
                }
            }
            done(sent)
        }.start()
    }
}
