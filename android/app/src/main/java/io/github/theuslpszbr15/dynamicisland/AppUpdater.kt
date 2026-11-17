package io.github.theuslpszbr15.dynamicisland

import android.content.Context
import android.content.Intent
import android.content.pm.PackageManager
import android.net.Uri
import android.provider.Settings
import androidx.core.content.FileProvider
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.launch
import okhttp3.OkHttpClient
import okhttp3.Request
import java.io.File
import java.io.IOException
import java.util.concurrent.TimeUnit

/** The "Verificar atualização" button: what is happening. */
sealed class UpdateState {
    object Idle : UpdateState()
    object Checking : UpdateState()
    object UpToDate : UpdateState()
    /** [percent] -1: size unknown. */
    data class Downloading(val percent: Int) : UpdateState()
    data class Ready(val name: String) : UpdateState()
    data class Error(val message: String) : UpdateState()
}

/** Looks at the latest release, downloads and checks the APK (sha256, package, version, signing key), opens Android's installer. */
object AppUpdater {
    private val _state = MutableStateFlow<UpdateState>(UpdateState.Idle)
    val state: StateFlow<UpdateState> = _state
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private val http by lazy {
        OkHttpClient.Builder().connectTimeout(15, TimeUnit.SECONDS).readTimeout(30, TimeUnit.SECONDS).build()
    }
    @Volatile private var running = false

    private fun apk(context: Context) = File(File(context.cacheDir, "update"), AppReleases.APK_NAME)

    fun check(context: Context) {
        if (running) return
        running = true
        val app = context.applicationContext
        _state.value = UpdateState.Checking
        scope.launch {
            try { _state.value = run(app) }
            catch (_: IOException) { _state.value = UpdateState.Error("Sem internet ou o GitHub não respondeu. Confira a conexão e tente de novo.") }
            catch (e: Exception) { _state.value = UpdateState.Error("Não consegui atualizar: ${e.message ?: e.javaClass.simpleName}") }
            finally { running = false }
        }
    }

    private fun run(context: Context): UpdateState {
        val installed = AppUpdate.installed(context)
        val body = http.newCall(Request.Builder().url(AppReleases.LATEST_URL)
            .header("Accept", "application/vnd.github+json").header("User-Agent", "DynamicIsland-Android").build()).execute().use { r ->
            if (r.code == 404 || r.code == 403 || r.code == 401)
                return UpdateState.Error("Não consegui ver as versões (HTTP ${r.code}). O repositório pode estar privado ou o GitHub limitou o acesso; tente mais tarde.")
            if (!r.isSuccessful) return UpdateState.Error("O GitHub respondeu HTTP ${r.code}.")
            r.body?.string().orEmpty()
        }
        val release = when (val p = AppReleases.parse(body)) {
            is AppReleases.Parsed.Bad -> return UpdateState.Error(p.why)
            is AppReleases.Parsed.Ok -> p.release
        }
        if (AppReleases.knownNotNewer(release, installed)) return UpdateState.UpToDate

        // the notes do not say the versionCode: the file itself will
        val file = apk(context)
        file.parentFile?.mkdirs()
        file.delete()
        val sum = AppReleases.sha256()
        _state.value = UpdateState.Downloading(-1)
        http.newCall(Request.Builder().url(release.apkUrl).header("User-Agent", "DynamicIsland-Android").build()).execute().use { r ->
            if (!r.isSuccessful) return UpdateState.Error("O download falhou (HTTP ${r.code}).")
            val total = r.body?.contentLength()?.takeIf { it > 0 } ?: release.size
            var done = 0L
            r.body!!.byteStream().use { input ->
                file.outputStream().use { out ->
                    val buf = ByteArray(64 * 1024)
                    while (true) {
                        val n = input.read(buf)
                        if (n < 0) break
                        out.write(buf, 0, n); sum.update(buf, 0, n); done += n
                        if (total > 0) _state.value = UpdateState.Downloading((done * 100 / total).toInt().coerceIn(0, 100))
                    }
                }
            }
        }
        if (AppReleases.hex(sum.digest()) != release.sha256) {
            file.delete()
            return UpdateState.Error("O arquivo baixado não bate com a verificação sha256 da versão. Não instalei; tente de novo.")
        }

        val pm = context.packageManager
        val flags = PackageManager.GET_SIGNING_CERTIFICATES
        val info = pm.getPackageArchiveInfo(file.path, flags)
        if (info == null || info.packageName != context.packageName) {
            file.delete()
            return UpdateState.Error("O arquivo baixado não é o app Dynamic Island.")
        }
        if (info.longVersionCode.toInt() <= installed) { file.delete(); return UpdateState.UpToDate }
        val mine = pm.getPackageInfo(context.packageName, flags).signingInfo?.apkContentsSigners.orEmpty().map { it.toCharsString() }.toSet()
        val theirs = info.signingInfo?.apkContentsSigners.orEmpty().map { it.toCharsString() }.toSet()
        if (mine != theirs) {
            file.delete()
            return UpdateState.Error("A versão nova foi assinada com outra chave, então o Android não deixa instalar por cima. " +
                "Desinstale o app e instale o novo (os ajustes do app serão perdidos).")
        }
        return UpdateState.Ready(release.tag.ifEmpty { "nova" })
    }

    /** True when Android lets this app open the installer. */
    fun canInstall(context: Context) = context.packageManager.canRequestPackageInstalls()

    /** Opens Android's installer for the checked APK; without the "install unknown apps" switch, opens that switch first. */
    fun install(context: Context) {
        val file = apk(context)
        if (!file.exists()) { _state.value = UpdateState.Idle; return }
        if (!canInstall(context)) {
            context.startActivity(Intent(Settings.ACTION_MANAGE_UNKNOWN_APP_SOURCES, Uri.parse("package:${context.packageName}"))
                .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK))
            return
        }
        val uri = FileProvider.getUriForFile(context, "${context.packageName}.updates", file)
        context.startActivity(Intent(Intent.ACTION_VIEW).setDataAndType(uri, "application/vnd.android.package-archive")
            .addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_ACTIVITY_NEW_TASK))
    }
}
