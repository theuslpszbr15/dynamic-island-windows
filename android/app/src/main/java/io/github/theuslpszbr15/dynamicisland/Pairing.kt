package io.github.theuslpszbr15.dynamicisland

import org.json.JSONObject
import java.net.URI
import java.net.URLDecoder

/** What the PC's QR code holds: where to reach it, its certificate's SHA-256 and the shared secret. */
data class Pairing(
    val host: String,
    val port: Int,
    val fingerprint: String,
    val token: String,
    val name: String,
) {
    fun toJson(): String = JSONObject()
        .put("host", host).put("port", port).put("fp", fingerprint).put("token", token).put("name", name)
        .toString()

    companion object {
        private val TOKEN = Regex("[A-Za-z0-9_-]{16,128}")
        private val FINGERPRINT = Regex("[0-9a-f]{64}")
        private val IPV4 = Regex("(\\d{1,3})\\.(\\d{1,3})\\.(\\d{1,3})\\.(\\d{1,3})")

        /** `dynamicisland://pair?v=1&host=…&port=…&fp=…&token=…&name=…`, or null if anything is off. */
        fun parse(link: String): Pairing? {
            val uri = try { URI(link.trim()) } catch (e: Exception) { return null }
            if (uri.scheme != "dynamicisland" || uri.host != "pair") return null
            val query = uri.rawQuery ?: return null
            val q = query.split('&').associate {
                it.substringBefore('=') to URLDecoder.decode(it.substringAfter('=', ""), "UTF-8")
            }
            if (q["v"] != "1") return null
            return build(q["host"], q["port"]?.toIntOrNull(), q["fp"], q["token"], q["name"])
        }

        fun fromJson(json: String): Pairing? = try {
            val o = JSONObject(json)
            build(o.optString("host"), o.optInt("port"), o.optString("fp"), o.optString("token"), o.optString("name"))
        } catch (e: Exception) {
            null
        }

        private fun build(host: String?, port: Int?, fp: String?, token: String?, name: String?): Pairing? {
            if (host == null || !isLocalAddress(host)) return null
            if (port == null || port !in 1..65535) return null
            if (fp == null || !FINGERPRINT.matches(fp)) return null
            if (token == null || !TOKEN.matches(token)) return null
            val label = name?.trim()?.take(40)?.ifEmpty { null } ?: host
            return Pairing(host, port, fp, token, label)
        }

        /** The link only goes to a PC on the local network (private, link-local or loopback IPv4). */
        fun isLocalAddress(host: String): Boolean {
            val m = IPV4.matchEntire(host) ?: return false
            val b = m.groupValues.drop(1).map { it.toInt() }
            if (b.any { it > 255 }) return false
            return b[0] == 10 || b[0] == 127 ||
                (b[0] == 172 && b[1] in 16..31) ||
                (b[0] == 192 && b[1] == 168) ||
                (b[0] == 169 && b[1] == 254)
        }
    }
}
