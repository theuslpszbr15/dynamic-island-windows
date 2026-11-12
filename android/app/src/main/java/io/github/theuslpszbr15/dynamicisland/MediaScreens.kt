package io.github.theuslpszbr15.dynamicisland

import android.app.Activity
import android.app.KeyguardManager
import android.content.Context
import android.content.Intent
import android.graphics.ImageDecoder
import android.graphics.drawable.AnimatedImageDrawable
import android.net.Uri
import android.os.Bundle
import android.util.LruCache
import android.util.Size
import android.widget.ImageView
import android.widget.MediaController
import android.widget.Toast
import android.widget.VideoView
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.awaitFirstDown
import androidx.compose.foundation.gestures.calculatePan
import androidx.compose.foundation.gestures.calculateZoom
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.pager.HorizontalPager
import androidx.compose.foundation.pager.rememberPagerState
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Icon
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableFloatStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.produceState
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.input.pointer.positionChanged
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.viewinterop.AndroidView
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.text.SimpleDateFormat
import java.util.Locale

private val MCard = Color(0xFF1C1C1E)
private val MTile = Color(0xFF2C2C30)
private val MMuted = Color(0xFF8E8E93)
private val MBlue = Color(0xFF0A84FF)
private val MOrange = Color(0xFFFF9F0A)
private val MRed = Color(0xFFFF453A)

/** Runs [then] once the phone is unlocked (the app also shows over the lock screen, for the agents' requests). */
fun whenUnlocked(context: Context, then: () -> Unit) {
    val keyguard = context.getSystemService(KeyguardManager::class.java)
    if (keyguard?.isKeyguardLocked != true) return then()
    val activity = context as? Activity ?: return
    keyguard.requestDismissKeyguard(activity, object : KeyguardManager.KeyguardDismissCallback() {
        override fun onDismissSucceeded() = then()
    })
}

/**
 * "Pendentes": what the PC offered and wasn't taken yet (Aceitar / Recusar), and what this phone offered that waits in
 * the PC's Pendentes (Cancelar). Both wait up to a day; the PC offers its own again whenever the link comes back.
 */
@Composable
fun PendingList(state: LinkUi, mine: List<OutgoingOffer>, modifier: Modifier = Modifier) {
    val context = LocalContext.current
    val ready = state.status == LinkStatus.Connected
    androidx.compose.foundation.lazy.LazyColumn(modifier, verticalArrangement = Arrangement.spacedBy(10.dp)) {
        if (state.files.isEmpty() && mine.isEmpty()) item {
            Text("Nada pendente. O que o PC mandar e você não aceitar na hora espera aqui por um dia, e o que você mandar " +
                "e ninguém aceitar no PC também.", color = MMuted, fontSize = 14.sp)
        }
        if (state.files.isNotEmpty()) item { Header("Do PC") }
        items(state.files.size) { i ->
            val offer = state.files[i]
            PendingRow(
                title = if (offer.isText) "Um texto longo" else offer.label,
                line = "${Notifications.sizeText(offer.totalBytes)} · chegou ${whenShort(offer.receivedAt)} · " +
                    leftText(offer.receivedAt),
                icon = if (offer.isText) R.drawable.ic_send_text else if (offer.items.size > 1) R.drawable.ic_tab_tray else R.drawable.ic_send_file,
            ) {
                TextButton(onClick = { Link.declineFile(context, offer.id) }) { Text("Recusar", color = MMuted) }
                TextButton(onClick = { whenUnlocked(context) { Link.acceptFile(context, offer.id) } }, enabled = ready) {
                    Text(if (offer.isText) "Copiar" else "Aceitar", color = if (ready) MBlue else MMuted, fontWeight = FontWeight.SemiBold)
                }
            }
        }
        if (state.files.isNotEmpty() && !ready) item {
            Text("Aceitar funciona quando o celular estiver ligado ao PC.", color = MOrange, fontSize = 13.sp)
        }
        if (mine.isNotEmpty()) item { Header("Seus envios esperando o PC") }
        items(mine.size) { i ->
            val o = mine[i]
            val status = when (o.state) {
                OutgoingOffer.QUEUED -> "vai quando o PC ligar"
                OutgoingOffer.ASKING -> "a ilha está perguntando"
                else -> "nos Pendentes do PC"
            }
            PendingRow(
                title = o.text?.let { t -> t.split(Regex("\\s+")).filter { it.isNotEmpty() }.joinToString(" ").let { if (it.length > 80) it.take(80) + "…" else it } }
                    ?: o.label,
                line = "$status · ${Notifications.sizeText(o.bytes)} · ${leftText(o.at)}",
                icon = if (o.isText) R.drawable.ic_send_text else if (o.items.size > 1) R.drawable.ic_tab_tray else R.drawable.ic_send_file,
            ) {
                TextButton(onClick = { FileSender.cancel(context, o.id) }) { Text("Cancelar", color = MRed) }
            }
        }
        item { Spacer(Modifier.size(12.dp)) }
    }
}

@Composable
private fun Header(text: String) =
    Text(text, color = Color.White, fontWeight = FontWeight.SemiBold, fontSize = 15.sp, modifier = Modifier.padding(top = 4.dp))

@Composable
private fun PendingRow(title: String, line: String, icon: Int, actions: @Composable () -> Unit) {
    Column(Modifier.fillMaxWidth().background(MCard, RoundedCornerShape(16.dp)).padding(14.dp)) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Icon(painterResource(icon), contentDescription = null, tint = MMuted, modifier = Modifier.padding(end = 12.dp).size(22.dp))
            Column(Modifier.weight(1f)) {
                Text(title, color = Color.White, fontWeight = FontWeight.SemiBold, fontSize = 15.sp, maxLines = 2,
                    overflow = TextOverflow.Ellipsis)
                Text(line, color = MMuted, fontSize = 12.5.sp, maxLines = 1, overflow = TextOverflow.Ellipsis)
            }
        }
        Row(Modifier.fillMaxWidth().padding(top = 4.dp), horizontalArrangement = Arrangement.End) { actions() }
    }
}

private fun whenShort(at: Long) = SimpleDateFormat("HH:mm", Locale.getDefault()).format(at)

private fun leftText(at: Long): String {
    val hours = (FileOffer.PENDING_MS - (System.currentTimeMillis() - at)) / (60 * 60_000L)
    return if (hours < 1) "vale menos de 1 h" else "vale mais $hours h"
}

// ───────────────────────── Imagens / Vídeos ─────────────────────────

/** Thumbnails already loaded (by uri), so scrolling back doesn't load them again. */
private val thumbs = object : LruCache<String, ImageBitmap>(8 * 1024 * 1024) {
    override fun sizeOf(key: String, value: ImageBitmap) = value.width * value.height * 4
}

/** A grid of the pictures (or videos, gifs included) received from the PC; a tap opens the viewer. */
@Composable
fun MediaGrid(entries: List<MediaEntry>, videos: Boolean, modifier: Modifier = Modifier) {
    val context = LocalContext.current
    if (entries.isEmpty()) {
        Text(if (videos) "Nenhum vídeo recebido ainda. Os vídeos (mp4, mkv, webm, mov até 60 MB) e gifs que o PC mandar aparecem aqui."
            else "Nenhuma imagem recebida ainda. As fotos (png, jpg, webp…) que o PC mandar aparecem aqui.",
            color = MMuted, fontSize = 14.sp, modifier = modifier)
        return
    }
    LazyVerticalGrid(GridCells.Adaptive(104.dp), modifier, horizontalArrangement = Arrangement.spacedBy(6.dp),
        verticalArrangement = Arrangement.spacedBy(6.dp)) {
        items(entries.size, key = { entries[it].uri }) { i ->
            MediaTile(entries[i]) { MediaViewerActivity.open(context, entries, i) }
        }
    }
}

@Composable
private fun MediaTile(entry: MediaEntry, onClick: () -> Unit) {
    val context = LocalContext.current
    val thumb by produceState<ImageBitmap?>(thumbs.get(entry.uri), entry.uri) {
        if (value == null) value = withContext(Dispatchers.IO) {
            try {
                context.contentResolver.loadThumbnail(Uri.parse(entry.uri), Size(320, 320), null).asImageBitmap()
                    .also { thumbs.put(entry.uri, it) }
            } catch (_: Exception) { null }
        }
    }
    Box(Modifier.aspectRatio(1f).clip(RoundedCornerShape(10.dp)).background(MTile).clickable(onClick = onClick),
        contentAlignment = Alignment.Center) {
        val t = thumb
        if (t != null) Image(t, contentDescription = entry.title, contentScale = ContentScale.Crop, modifier = Modifier.fillMaxSize())
        else Icon(painterResource(R.drawable.ic_send_media), contentDescription = entry.title, tint = MMuted, modifier = Modifier.size(28.dp))
        if (Media.isVideo(entry.mime)) {
            Text(if (entry.mime == "image/gif") "GIF" else "▶", color = Color.White, fontSize = 12.sp, fontWeight = FontWeight.Bold,
                modifier = Modifier.align(Alignment.BottomStart).padding(6.dp)
                    .background(Color.Black.copy(alpha = 0.55f), RoundedCornerShape(6.dp)).padding(horizontal = 6.dp, vertical = 2.dp))
        }
    }
}

/**
 * The viewer: pictures (pinch or double tap to zoom), gifs and animated webp (they play), and the videos Android can
 * play itself (mp4, webm, mkv, 3gp; a mov or odd codec gets "Abrir com outro app"). Swipe for the next one. Only this
 * app's own received files are listed here.
 */
class MediaViewerActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        val uris = intent.getStringArrayListExtra(EXTRA_URIS) ?: arrayListOf()
        val mimes = intent.getStringArrayListExtra(EXTRA_MIMES) ?: arrayListOf()
        if (uris.isEmpty()) { finish(); return }
        val start = intent.getIntExtra(EXTRA_INDEX, 0).coerceIn(0, uris.lastIndex)
        setContent { Viewer(uris, mimes, start) }
    }

    @Composable
    private fun Viewer(uris: List<String>, mimes: List<String>, start: Int) {
        val pager = rememberPagerState(initialPage = start) { uris.size }
        var zoomed by remember { mutableStateOf(false) }
        LaunchedEffect(pager.currentPage) { zoomed = false }
        Box(Modifier.fillMaxSize().background(Color.Black)) {
            HorizontalPager(pager, Modifier.fillMaxSize(), userScrollEnabled = !zoomed, key = { uris[it] }) { page ->
                val uri = Uri.parse(uris[page])
                val mime = mimes.getOrElse(page) { "" }
                when {
                    mime.startsWith("video/") -> VideoPage(uri, mime, active = pager.currentPage == page)
                    else -> PicturePage(uri, mime, page == pager.currentPage) { zoomed = it }
                }
            }
            Row(Modifier.fillMaxWidth().statusBarsPadding().padding(horizontal = 8.dp, vertical = 4.dp),
                verticalAlignment = Alignment.CenterVertically) {
                TextButton(onClick = { finish() }) { Text("Fechar", color = Color.White) }
                Spacer(Modifier.weight(1f))
                Text("${pager.currentPage + 1} de ${uris.size}", color = Color.White, fontSize = 14.sp)
                Spacer(Modifier.weight(1f))
                TextButton(onClick = { openWith(Uri.parse(uris[pager.currentPage]), mimes.getOrElse(pager.currentPage) { "*/*" }) }) {
                    Text("Abrir com…", color = Color.White)
                }
            }
        }
    }

    /** Still pictures as a bitmap (big ones scaled to the screen), animated ones as a playing drawable. */
    @Composable
    private fun PicturePage(uri: Uri, mime: String, current: Boolean, onZoom: (Boolean) -> Unit) {
        val loaded by produceState<Any?>(null, uri) {
            value = withContext(Dispatchers.IO) {
                try {
                    val source = ImageDecoder.createSource(contentResolver, uri)
                    if (mime == "image/gif" || mime == "image/webp") {
                        val drawable = ImageDecoder.decodeDrawable(source) { decoder, info, _ -> limit(decoder, info) }
                        if (drawable is AnimatedImageDrawable) drawable
                        else ImageDecoder.decodeBitmap(ImageDecoder.createSource(contentResolver, uri)) { d, info, _ -> limit(d, info) }.asImageBitmap()
                    } else ImageDecoder.decodeBitmap(source) { decoder, info, _ -> limit(decoder, info) }.asImageBitmap()
                } catch (_: Exception) { false }
            }
        }
        var scale by remember { mutableFloatStateOf(1f) }
        var offset by remember { mutableStateOf(Offset.Zero) }
        LaunchedEffect(current) { if (!current) { scale = 1f; offset = Offset.Zero } }
        Box(Modifier.fillMaxSize(), contentAlignment = Alignment.Center) {
            when (val l = loaded) {
                null -> Text("Abrindo…", color = MMuted)
                false -> Failed(uri, mime, "Não deu para mostrar essa imagem aqui.")
                is AnimatedImageDrawable -> AndroidView(
                    factory = { ImageView(it).apply { scaleType = ImageView.ScaleType.FIT_CENTER } },
                    update = { view -> if (view.drawable !== l) { view.setImageDrawable(l); l.start() } },
                    modifier = Modifier.fillMaxSize(),
                )
                is ImageBitmap -> Image(l, contentDescription = null, contentScale = ContentScale.Fit,
                    modifier = Modifier.fillMaxSize()
                        .pointerInput(Unit) {
                            detectTapGestures(onDoubleTap = {
                                if (scale > 1f) { scale = 1f; offset = Offset.Zero } else scale = 2.5f
                                onZoom(scale > 1f)
                            })
                        }
                        .pointerInput(Unit) {
                            // two fingers zoom; one finger pans only when zoomed (otherwise it swipes to the next one)
                            awaitEachGesture {
                                awaitFirstDown(requireUnconsumed = false)
                                do {
                                    val event = awaitPointerEvent()
                                    if (event.changes.count { it.pressed } >= 2 || scale > 1f) {
                                        scale = (scale * event.calculateZoom()).coerceIn(1f, 6f)
                                        offset = if (scale == 1f) Offset.Zero else offset + event.calculatePan()
                                        onZoom(scale > 1f)
                                        event.changes.forEach { if (it.positionChanged()) it.consume() }
                                    }
                                } while (event.changes.any { it.pressed })
                            }
                        }
                        .graphicsLayer(scaleX = scale, scaleY = scale, translationX = offset.x, translationY = offset.y))
            }
        }
    }

    /** At most about 4096 px on a side: enough for any screen, small enough not to run out of memory. */
    private fun limit(decoder: ImageDecoder, info: ImageDecoder.ImageInfo) {
        val longest = maxOf(info.size.width, info.size.height)
        if (longest > MAX_SIDE) {
            val f = MAX_SIDE.toFloat() / longest
            decoder.setTargetSize((info.size.width * f).toInt().coerceAtLeast(1), (info.size.height * f).toInt().coerceAtLeast(1))
        }
        decoder.allocator = ImageDecoder.ALLOCATOR_SOFTWARE
    }

    /** VideoView with the system controls; plays only while it is the page on screen. */
    @Composable
    private fun VideoPage(uri: Uri, mime: String, active: Boolean) {
        var failed by remember { mutableStateOf(false) }
        Box(Modifier.fillMaxSize().navigationBarsPadding(), contentAlignment = Alignment.Center) {
            if (failed) Failed(uri, mime, "O Android não consegue tocar esse vídeo aqui.")
            else AndroidView(
                factory = { ctx ->
                    VideoView(ctx).apply {
                        val controls = MediaController(ctx)
                        controls.setAnchorView(this)
                        setMediaController(controls)
                        setOnErrorListener { _, _, _ -> failed = true; true }
                        setOnPreparedListener { player -> player.isLooping = false }
                        setVideoURI(uri)
                    }
                },
                update = { view -> if (active) view.start() else if (view.isPlaying) view.pause() },
                onRelease = { it.stopPlayback() },
                modifier = Modifier.fillMaxWidth(),
            )
        }
    }

    @Composable
    private fun Failed(uri: Uri, mime: String, message: String) {
        Column(horizontalAlignment = Alignment.CenterHorizontally, modifier = Modifier.padding(24.dp)) {
            Text(message, color = Color.White, fontSize = 15.sp)
            TextButton(onClick = { openWith(uri, mime) }) { Text("Abrir com outro app", color = MBlue) }
        }
    }

    private fun openWith(uri: Uri, mime: String) {
        val view = Intent(Intent.ACTION_VIEW).setDataAndType(uri, mime.ifEmpty { "*/*" }).addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
        try { startActivity(Intent.createChooser(view, "Abrir com")) }
        catch (_: Exception) { Toast.makeText(this, "Nenhum app abre esse arquivo.", Toast.LENGTH_LONG).show() }
    }

    companion object {
        private const val EXTRA_URIS = "uris"
        private const val EXTRA_MIMES = "mimes"
        private const val EXTRA_INDEX = "index"
        private const val MAX_SIDE = 4096

        fun open(context: Context, entries: List<MediaEntry>, index: Int) {
            context.startActivity(Intent(context, MediaViewerActivity::class.java)
                .putStringArrayListExtra(EXTRA_URIS, ArrayList(entries.map { it.uri }))
                .putStringArrayListExtra(EXTRA_MIMES, ArrayList(entries.map { it.mime }))
                .putExtra(EXTRA_INDEX, index))
        }
    }
}
