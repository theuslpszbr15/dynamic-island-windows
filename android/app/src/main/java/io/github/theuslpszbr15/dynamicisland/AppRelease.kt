package io.github.theuslpszbr15.dynamicisland

import org.json.JSONObject
import java.security.MessageDigest

/** What the app needs from a GitHub release to update itself. Plain data and parsing, tested off the phone. */
data class AppRelease(
    val tag: String,
    val notes: String,
    val apkUrl: String,
    val size: Long,
    /** Lowercase hex sha256 of the APK, from GitHub's asset digest. */
    val sha256: String,
    /** The new app's versionCode when the release notes say it ("versionCode: 12"); null when they don't. */
    val code: Int?,
)

object AppReleases {
    /** Where updates come from. Change these lines if the repository moves, goes private or changes host. */
    const val REPOSITORY = "theuslpszbr15/dynamic-island-windows"
    const val LATEST_URL = "https://api.github.com/repos/$REPOSITORY/releases/latest"
    const val APK_NAME = "DynamicIsland-Android.apk"

    private val codeMark = Regex("""(?i)version\s*code\s*[:=]\s*(\d+)""")

    sealed class Parsed {
        data class Ok(val release: AppRelease) : Parsed()
        data class Bad(val why: String) : Parsed()
    }

    /** Reads the `releases/latest` answer. No APK asset or no sha256 digest: nothing unchecked is ever installed. */
    fun parse(json: String): Parsed {
        val root = try { JSONObject(json) } catch (_: Exception) { return Parsed.Bad("Resposta inesperada do GitHub.") }
        val assets = root.optJSONArray("assets") ?: return Parsed.Bad("A última versão não tem arquivos.")
        val asset = (0 until assets.length()).map { assets.getJSONObject(it) }.firstOrNull { it.optString("name") == APK_NAME }
            ?: return Parsed.Bad("A última versão não tem o $APK_NAME.")
        val digest = asset.optString("digest")
        if (!digest.startsWith("sha256:") || digest.length != 7 + 64)
            return Parsed.Bad("A última versão não traz a verificação sha256; por segurança, não instalo.")
        val url = asset.optString("browser_download_url")
        if (!url.startsWith("https://")) return Parsed.Bad("Endereço do download inválido.")
        val notes = root.optString("body")
        return Parsed.Ok(AppRelease(root.optString("tag_name"), notes, url, asset.optLong("size"), digest.substring(7).lowercase(),
            codeMark.find(notes)?.groupValues?.get(1)?.toIntOrNull()))
    }

    /** The release is certainly not newer: its notes say its versionCode and it is not above the installed one. */
    fun knownNotNewer(release: AppRelease, installed: Int) = release.code != null && release.code <= installed

    fun hex(bytes: ByteArray) = bytes.joinToString("") { "%02x".format(it) }

    fun sha256(): MessageDigest = MessageDigest.getInstance("SHA-256")
}
