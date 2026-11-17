package io.github.theuslpszbr15.dynamicisland

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class AppReleaseTest {
    private val sha = "a".repeat(64)

    private fun json(name: String = AppReleases.APK_NAME, digest: String = "sha256:$sha", body: String = "notas") = """
        {"tag_name":"v1.8.3","body":${org.json.JSONObject.quote(body)},"assets":[
          {"name":"outro.zip","digest":"sha256:${"b".repeat(64)}","browser_download_url":"https://x/o.zip","size":1},
          {"name":"$name","digest":"$digest","browser_download_url":"https://x/a.apk","size":1234}]}"""

    @Test fun readsTheApkAssetAndItsDigest() {
        val r = (AppReleases.parse(json()) as AppReleases.Parsed.Ok).release
        assertEquals("v1.8.3", r.tag)
        assertEquals("https://x/a.apk", r.apkUrl)
        assertEquals(sha, r.sha256)
        assertEquals(1234L, r.size)
        assertNull(r.code)
    }

    @Test fun noApkOrNoDigestIsRefused() {
        assertTrue(AppReleases.parse(json(name = "outro.apk")) is AppReleases.Parsed.Bad)
        assertTrue(AppReleases.parse(json(digest = "")) is AppReleases.Parsed.Bad)
        assertTrue(AppReleases.parse(json(digest = "sha1:abc")) is AppReleases.Parsed.Bad)
        assertTrue(AppReleases.parse("not json") is AppReleases.Parsed.Bad)
    }

    @Test fun versionCodeFromTheNotes() {
        val r = (AppReleases.parse(json(body = "Android\nversionCode: 12\n")) as AppReleases.Parsed.Ok).release
        assertEquals(12, r.code)
        assertTrue(AppReleases.knownNotNewer(r, 12))
        assertTrue(AppReleases.knownNotNewer(r, 13))
        assertFalse(AppReleases.knownNotNewer(r, 11))
        // unknown: the downloaded file has to say
        assertFalse(AppReleases.knownNotNewer(r.copy(code = null), 99))
    }

    @Test fun digestHexIsLowercase() {
        val d = AppReleases.sha256().digest("abc".toByteArray())
        assertEquals("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", AppReleases.hex(d))
    }
}
