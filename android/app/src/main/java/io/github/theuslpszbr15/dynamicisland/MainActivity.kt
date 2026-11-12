package io.github.theuslpszbr15.dynamicisland

import android.Manifest
import android.content.Intent
import android.os.Build
import android.os.Bundle
import android.provider.Settings
import android.widget.Toast
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import androidx.biometric.BiometricManager
import androidx.biometric.BiometricManager.Authenticators.BIOMETRIC_STRONG
import androidx.biometric.BiometricManager.Authenticators.DEVICE_CREDENTIAL
import androidx.biometric.BiometricPrompt
import androidx.core.app.NotificationManagerCompat
import androidx.core.content.ContextCompat
import androidx.fragment.app.FragmentActivity
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import com.journeyapps.barcodescanner.ScanContract
import com.journeyapps.barcodescanner.ScanOptions

class MainActivity : FragmentActivity() {
    /** A link waiting for the user's "yes, pair with this PC". */
    private val pendingPairing = mutableStateOf<Pairing?>(null)
    private val focused = mutableStateOf<String?>(null)
    /** "Permitir" tapped on a notification: ask for the fingerprint as soon as the screen is up. */
    private var allowOnResume: String? = null

    private val scanner = registerForActivityResult(ScanContract()) { result ->
        result.contents?.let(::offerPairing)
    }
    private val notificationPermission = registerForActivityResult(ActivityResultContracts.RequestPermission()) {}

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        Notifications.createChannels(this)
        History.load(this)
        FileSender.load(this)
        PairingStore.load(this)?.let { pairing ->
            val paused = PairingStore.isPaused(this)
            Link.update {
                if (it.pairing == null) it.copy(pairing = pairing, status = if (paused) LinkStatus.Paused else LinkStatus.Connecting) else it
            }
            if (!paused) LinkService.start(this)
        }
        if (Build.VERSION.SDK_INT >= 33 &&
            ContextCompat.checkSelfPermission(this, Manifest.permission.POST_NOTIFICATIONS) != android.content.pm.PackageManager.PERMISSION_GRANTED)
            notificationPermission.launch(Manifest.permission.POST_NOTIFICATIONS)
        handle(intent)

        setContent {
            val state by Link.state.collectAsStateWithLifecycle()
            AppTheme {
                AppScreen(
                    state = state,
                    focused = focused.value,
                    pending = pendingPairing.value,
                    onScan = {
                        scanner.launch(ScanOptions()
                            .setDesiredBarcodeFormats(ScanOptions.QR_CODE)
                            .setPrompt("Aponte para o código na tela do PC")
                            .setBeepEnabled(false)
                            .setOrientationLocked(false))
                    },
                    onPaste = ::offerPairing,
                    onConfirmPairing = ::pair,
                    onCancelPairing = { pendingPairing.value = null },
                    onAllow = ::allow,
                    onDeny = { Link.decide(this, it.fingerprint, allow = false) },
                    onOpen = { item ->
                        confirmed("Abrir no PC?", item.name) {
                            Link.open(item)
                            Toast.makeText(this, "Abrindo ${item.name} no PC", Toast.LENGTH_SHORT).show()
                        }
                    },
                    onUnpair = ::unpair,
                    onRetry = { Link.resume(this) },
                    onPause = { Link.pause(this) },
                    onFixNotifications = ::openNotificationSettings,
                )
            }
        }
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        handle(intent)
    }

    override fun onResume() {
        super.onResume()
        // back from Android's settings: the island may be allowed now
        IslandOverlay.refresh(this)
        val off = Notifications.approvalsBlocked(this)
        Link.update { if (it.notificationsOff == off) it else it.copy(notificationsOff = off) }
        val fingerprint = allowOnResume ?: return
        allowOnResume = null
        val approval = Link.state.value.approvals.firstOrNull { it.fingerprint == fingerprint }
        if (approval == null) Toast.makeText(this, "Esse pedido já expirou ou foi respondido no PC.", Toast.LENGTH_LONG).show()
        else if (!approval.truncated) allow(approval)
    }

    private fun handle(intent: Intent?) {
        if (intent == null) return
        intent.data?.toString()?.let(::offerPairing)
        val fingerprint = intent.getStringExtra(Notifications.EXTRA_FINGERPRINT) ?: return
        focused.value = fingerprint
        if (intent.getBooleanExtra(Notifications.EXTRA_ALLOW, false)) allowOnResume = fingerprint
        // consumed: a later recreation (rotation) must not ask again
        intent.removeExtra(Notifications.EXTRA_ALLOW)
    }

    private fun openNotificationSettings() {
        val app = Intent(Settings.ACTION_APP_NOTIFICATION_SETTINGS).putExtra(Settings.EXTRA_APP_PACKAGE, packageName)
        // the app itself is allowed but the requests' channel was silenced: open that channel instead
        val target = if (NotificationManagerCompat.from(this).areNotificationsEnabled())
            Intent(Settings.ACTION_CHANNEL_NOTIFICATION_SETTINGS)
                .putExtra(Settings.EXTRA_APP_PACKAGE, packageName)
                .putExtra(Settings.EXTRA_CHANNEL_ID, Notifications.APPROVAL_CHANNEL)
        else app
        try { startActivity(target) } catch (_: Exception) { startActivity(app) }
    }

    private fun offerPairing(link: String) {
        val pairing = Pairing.parse(link)
        if (pairing == null) {
            Toast.makeText(this, "Esse código não é de pareamento da Dynamic Island.", Toast.LENGTH_LONG).show()
            return
        }
        pendingPairing.value = pairing
    }

    private fun pair(pairing: Pairing) {
        pendingPairing.value = null
        LinkService.stop(this)
        PairingStore.save(this, pairing)
        PairingStore.setPaused(this, false)
        Link.update { LinkUi(pairing = pairing, status = LinkStatus.Connecting) }
        LinkService.start(this)
    }

    private fun unpair() {
        LinkService.stop(this)
        PairingStore.clear(this)
        Link.update { LinkUi() }
    }

    /** Nothing runs on the PC without the owner's fingerprint, face or screen lock. */
    private fun allow(approval: Approval) =
        confirmed("Permitir no PC?", "${approval.agent}: ${approval.command.take(80)}") {
            Link.decide(this, approval.fingerprint, allow = true)
        }

    private fun confirmed(title: String, subtitle: String, action: () -> Unit) {
        val authenticators = BIOMETRIC_STRONG or DEVICE_CREDENTIAL
        if (BiometricManager.from(this).canAuthenticate(authenticators) != BiometricManager.BIOMETRIC_SUCCESS) {
            Toast.makeText(this, "Configure um bloqueio de tela (digital ou PIN) para usar isso pelo celular.", Toast.LENGTH_LONG).show()
            return
        }
        val prompt = BiometricPrompt(this, ContextCompat.getMainExecutor(this), object : BiometricPrompt.AuthenticationCallback() {
            override fun onAuthenticationSucceeded(result: BiometricPrompt.AuthenticationResult) = action()
        })
        prompt.authenticate(BiometricPrompt.PromptInfo.Builder()
            .setTitle(title)
            .setSubtitle(subtitle)
            .setAllowedAuthenticators(authenticators)
            .build())
    }
}
