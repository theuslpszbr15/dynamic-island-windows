package io.github.theuslpszbr15.dynamicisland

import org.json.JSONObject
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.SocketTimeoutException

/**
 * Finds the paired PC again when its address changed (DHCP): broadcasts {"type":"discover","fp":…} on the
 * link's UDP port and takes the answer of the PC with that certificate. Nothing secret goes out, and the TLS
 * pinning still decides whether the answer is really the paired PC.
 */
object Discovery {
    const val PORT = 47900
    private const val WAIT_MS = 2_000

    /** The pairing with the address the PC answered from, or null if no PC answered in time. */
    fun find(pairing: Pairing, broadcast: InetAddress = InetAddress.getByName("255.255.255.255")): Pairing? {
        val request = JSONObject().put("type", "discover").put("v", 1).put("fp", pairing.fingerprint)
            .toString().toByteArray(Charsets.UTF_8)
        DatagramSocket().use { socket ->
            socket.broadcast = true
            socket.soTimeout = WAIT_MS
            socket.send(DatagramPacket(request, request.size, broadcast, PORT))
            val deadline = System.currentTimeMillis() + WAIT_MS
            val buffer = ByteArray(512)
            while (System.currentTimeMillis() < deadline) {
                val packet = DatagramPacket(buffer, buffer.size)
                try {
                    socket.receive(packet)
                } catch (_: SocketTimeoutException) {
                    return null
                }
                answer(pairing, packet.address.hostAddress ?: continue, String(packet.data, 0, packet.length, Charsets.UTF_8))
                    ?.let { return it }
            }
        }
        return null
    }

    /** Checks one reply: right type, same certificate, a local sender and a sane port. */
    fun answer(pairing: Pairing, from: String, text: String): Pairing? = try {
        val o = JSONObject(text)
        val port = o.optInt("port")
        if (o.optString("type") == "here" && o.optString("fp") == pairing.fingerprint &&
            Pairing.isLocalAddress(from) && port in 1..65535
        ) pairing.copy(host = from, port = port) else null
    } catch (_: Exception) {
        null
    }
}
