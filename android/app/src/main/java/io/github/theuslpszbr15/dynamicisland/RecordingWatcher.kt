package io.github.theuslpszbr15.dynamicisland

import android.content.Context
import android.hardware.camera2.CameraManager
import android.media.AudioManager
import android.media.AudioRecordingConfiguration
import android.os.Handler
import android.os.Looper
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow

/** Whether some app is using the microphone or a camera right now, and since when. */
data class Recording(val mic: Boolean = false, val camera: Boolean = false, val since: Long = 0L) {
    val any get() = mic || camera
    val text get() = when {
        mic && camera -> "Câmera e microfone em uso"
        camera -> "Câmera em uso"
        mic -> "Microfone em uso"
        else -> ""
    }
}

/**
 * The red dot: Android tells any app when the microphone starts being recorded (AudioRecordingCallback) and when a camera
 * is taken (CameraManager), with no permission and without saying which app. Screen recording has no such signal in
 * Android, so it cannot be shown. Nothing leaves the phone.
 */
object RecordingWatcher {
    private val _state = MutableStateFlow(Recording())
    val state: StateFlow<Recording> = _state
    private var started = false
    private val busyCameras = HashSet<String>()

    private fun update(mic: Boolean? = null, camera: Boolean? = null) {
        val s = _state.value
        val next = s.copy(mic = mic ?: s.mic, camera = camera ?: s.camera)
        if (next == s) return
        // "since" restarts only when something new starts, so a second app joining announces itself again
        _state.value = if ((next.mic && !s.mic) || (next.camera && !s.camera)) next.copy(since = System.currentTimeMillis()) else next
    }

    fun start(context: Context) {
        if (started) return
        started = true
        val app = context.applicationContext
        val main = Handler(Looper.getMainLooper())
        try {
            app.getSystemService(AudioManager::class.java)?.registerAudioRecordingCallback(object : AudioManager.AudioRecordingCallback() {
                override fun onRecordingConfigChanged(configs: MutableList<AudioRecordingConfiguration>?) = update(mic = !configs.isNullOrEmpty())
            }, main)
        } catch (_: Exception) {}
        try {
            app.getSystemService(CameraManager::class.java)?.registerAvailabilityCallback(object : CameraManager.AvailabilityCallback() {
                override fun onCameraUnavailable(cameraId: String) { busyCameras.add(cameraId); update(camera = true) }
                override fun onCameraAvailable(cameraId: String) { busyCameras.remove(cameraId); update(camera = busyCameras.isNotEmpty()) }
            }, main)
        } catch (_: Exception) {}
    }
}
