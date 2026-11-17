package io.github.theuslpszbr15.dynamicisland

import android.accessibilityservice.AccessibilityService
import android.app.KeyguardManager
import android.content.Context
import android.content.Intent
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.graphics.PixelFormat
import android.graphics.Region
import android.net.Uri
import android.os.Handler
import android.os.Looper
import android.provider.Settings
import android.view.Gravity
import android.view.MotionEvent
import android.view.View
import android.view.ViewTreeObserver
import android.view.WindowInsets
import android.view.WindowManager
import android.widget.Toast
import androidx.compose.animation.AnimatedContent
import androidx.compose.animation.SizeTransform
import androidx.compose.animation.core.CubicBezierEasing
import androidx.compose.animation.core.FastOutLinearInEasing
import androidx.compose.animation.core.FastOutSlowInEasing
import androidx.compose.animation.core.LinearOutSlowInEasing
import androidx.compose.animation.core.RepeatMode
import androidx.compose.animation.core.animateDpAsState
import androidx.compose.animation.core.animateFloat
import androidx.compose.animation.core.infiniteRepeatable
import androidx.compose.animation.core.keyframes
import androidx.compose.animation.core.rememberInfiniteTransition
import androidx.compose.animation.core.spring
import androidx.compose.animation.core.tween
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.togetherWith
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.Image
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.interaction.MutableInteractionSource
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.wrapContentSize
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableLongStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.clip
import androidx.compose.ui.geometry.CornerRadius
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Rect
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Outline
import androidx.compose.ui.graphics.Path
import androidx.compose.ui.graphics.Shape
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.layout.positionInWindow
import androidx.compose.ui.layout.onGloballyPositioned
import androidx.compose.ui.platform.ComposeView
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Density
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.LayoutDirection
import androidx.compose.ui.unit.IntSize
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleOwner
import androidx.lifecycle.LifecycleRegistry
import androidx.lifecycle.setViewTreeLifecycleOwner
import androidx.savedstate.SavedStateRegistry
import androidx.savedstate.SavedStateRegistryController
import androidx.savedstate.SavedStateRegistryOwner
import androidx.savedstate.setViewTreeSavedStateRegistryOwner
import kotlinx.coroutines.delay

private val Black = Color(0xFF000000)
private val Card = Color(0xFF1C1C1E)
private val Muted = Color(0xFF9A9AA0)
private val Blue = Color(0xFF0A84FF)
private val Orange = Color(0xFFFF9F0A)
private val Green = Color(0xFF30D158)
private val Red = Color(0xFFFF453A)
private val DiscordBlue = Color(0xFF5865F2)
private val ClaudeOrange = Color(0xFFD97757)

/**
 * The island: a black pill at the top of the screen, over any app, sized and placed as the person set it
 * ([IslandLook]). At rest it is just the pill; something new makes it grow into a short notice ("Nova solicitação
 * de envio · Toque para aceitar ou rejeitar"); a tap opens a small card with what to do, or the player for the song
 * playing. An ordinary overlay window (`SYSTEM_ALERT_WINDOW`): it never shows on the lock screen, Android hides it
 * over its own screens, and the status bar icons draw over it. The window is only as big as what it shows, so it
 * never takes touches from the app underneath. Works with or without a PC: the link service keeps the app alive
 * when there is one, [IslandService] otherwise.
 */
object IslandOverlay {
    private const val PREFS = "island"
    private const val KEY_ON = "on"

    private val main by lazy { Handler(Looper.getMainLooper()) }
    private var window: Shown? = null
    @Volatile private var linkRunning = false
    /** The accessibility service, while the person has it on: the island then goes above the status bar. */
    @Volatile private var above: AccessibilityService? = null

    /** The person turned it on in the app (off until then). */
    fun isOn(context: Context) = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).getBoolean(KEY_ON, false)

    fun setOn(context: Context, on: Boolean) {
        context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit().putBoolean(KEY_ON, on).apply()
        refresh(context)
    }

    fun canDraw(context: Context) = Settings.canDrawOverlays(context)

    /** The island can show: as an ordinary overlay, or above the status bar through the accessibility service. */
    fun canShow(context: Context) = above != null || canDraw(context)

    /** The island is above the status bar right now (the accessibility service is running). */
    fun isAbove() = above != null

    /** The accessibility service connected (the service) or went away (null). */
    fun accessibility(context: Context, service: AccessibilityService?) {
        above = service
        refresh(context)
    }

    /** Android's "Sobrepor a outros apps" screen for this app. */
    fun permissionIntent(context: Context) =
        Intent(Settings.ACTION_MANAGE_OVERLAY_PERMISSION, Uri.parse("package:${context.packageName}"))
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)

    /** The link service started or stopped. */
    fun linkRunning(context: Context, running: Boolean) {
        linkRunning = running
        refresh(context)
    }

    /** Shows or removes the window to match the switch and the permission; without the link service running, a
     * small service of its own keeps the island alive. */
    fun refresh(context: Context) {
        val app = context.applicationContext
        main.post {
            IslandLook.load(app)
            val host = above
            val want = isOn(app) && (host != null || canDraw(app))
            // the accessibility service came or went: the window moves to (or from) above the status bar
            window?.let { if (!want || it.host !== host) { it.remove(); window = null } }
            if (want && window == null) window = try { Shown(host ?: app, host) } catch (_: Exception) { null }
            // the accessibility service keeps the app alive by itself
            if (want && !linkRunning && host == null) IslandService.start(app) else IslandService.stop(app)
        }
    }

    /** The size or position changed: move the window (the pill redraws by itself). */
    fun place(context: Context) = main.post { window?.place() }.let { }

    /**
     * The window, its view and the lifecycle Compose needs outside an activity. With [host] (the accessibility
     * service) it is an accessibility overlay, above the status bar; otherwise an ordinary overlay, kept under it.
     */
    private class Shown(val context: Context, val host: AccessibilityService?) : LifecycleOwner, SavedStateRegistryOwner {
        private val registry = LifecycleRegistry(this)
        private val saved = SavedStateRegistryController.create(this)
        override val lifecycle: Lifecycle get() = registry
        override val savedStateRegistry: SavedStateRegistry get() = saved.savedStateRegistry

        private val manager = context.getSystemService(WindowManager::class.java)
        private val density = context.resources.displayMetrics.density
        private val params = WindowManager.LayoutParams(
            WindowManager.LayoutParams.WRAP_CONTENT,
            WindowManager.LayoutParams.WRAP_CONTENT,
            if (host != null) WindowManager.LayoutParams.TYPE_ACCESSIBILITY_OVERLAY else WindowManager.LayoutParams.TYPE_APPLICATION_OVERLAY,
            WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE or WindowManager.LayoutParams.FLAG_LAYOUT_IN_SCREEN or
                WindowManager.LayoutParams.FLAG_LAYOUT_NO_LIMITS or
                // split: the drawing takes no touch at all (the touch window over the island does)
                (if (host != null) WindowManager.LayoutParams.FLAG_NOT_TOUCHABLE else WindowManager.LayoutParams.FLAG_WATCH_OUTSIDE_TOUCH),
            PixelFormat.TRANSLUCENT,
        ).apply {
            gravity = Gravity.TOP or Gravity.CENTER_HORIZONTAL
            // up into the camera cutout, like the real thing
            layoutInDisplayCutoutMode = WindowManager.LayoutParams.LAYOUT_IN_DISPLAY_CUTOUT_MODE_ALWAYS
            // y always counts from the top of the screen. By default Android keeps a window clear of the status bar
            // in some sizes and not in others, so the island jumped up and down as the window grew and shrank.
            fitInsetsTypes = 0
            fitInsetsSides = 0
            title = "Dynamic Island"
        }
        /** Which card is open (null: just the pill). */
        private val open = mutableStateOf<Pill.Kind?>(null)
        private val view = ComposeView(context)

        /**
         * Above the status bar (accessibility overlays are trusted, so a window may let touches through): the island
         * is drawn in a window that never changes size and takes no touch, and an invisible window just over the
         * island takes the taps and hands them to the drawing. Resizing the drawn window is what made the island
         * flash out of place for a frame; the touch window can move and resize freely, nobody sees it.
         */
        private val touch: View? = if (host != null) View(context) else null
        private val touchParams = WindowManager.LayoutParams(
            1, 1,
            WindowManager.LayoutParams.TYPE_ACCESSIBILITY_OVERLAY,
            WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE or WindowManager.LayoutParams.FLAG_WATCH_OUTSIDE_TOUCH or
                WindowManager.LayoutParams.FLAG_LAYOUT_IN_SCREEN or WindowManager.LayoutParams.FLAG_LAYOUT_NO_LIMITS,
            PixelFormat.TRANSLUCENT,
        ).apply {
            gravity = Gravity.TOP or Gravity.LEFT
            layoutInDisplayCutoutMode = WindowManager.LayoutParams.LAYOUT_IN_DISPLAY_CUTOUT_MODE_ALWAYS
            fitInsetsTypes = 0
            fitInsetsSides = 0
            title = "Dynamic Island (toque)"
        }
        private val moveTouch = Runnable { placeTouch() }

        /** The touch window goes exactly over the island (the drawn window is screen wide, moved by x). */
        private fun placeTouch() {
            val t = touch ?: return
            touchParams.x = (context.resources.displayMetrics.widthPixels - params.width) / 2 + params.x + island.left
            touchParams.y = params.y + island.top
            touchParams.width = island.width().coerceAtLeast(1)
            touchParams.height = island.height().coerceAtLeast(1)
            try { manager.updateViewLayout(t, touchParams) } catch (_: Exception) {}
        }
        /** Where the island is in the window: the only part that takes touches when [fixed]. */
        private val island = android.graphics.Rect()
        /** The window never changes size (see [touchOnlyTheIsland]). */
        private var fixed = false
        /** Big for an animation; [settle] fits it back to the island. */
        private var staged = false
        private val settle = Runnable {
            staged = false
            // Exactly the island's size, never WRAP_CONTENT: Android first measures a WRAP_CONTENT window at a
            // dialog's width, and the open card came out squeezed after it had opened wide.
            resize(island.width().coerceAtLeast(1), island.height().coerceAtLeast(1))
        }

        /** The island moved or changed size. */
        fun bounds(r: android.graphics.Rect) {
            if (r == island) return
            island.set(r)
            // the touchable part is read on the window's next pass
            if (touch != null) { main.removeCallbacks(moveTouch); main.postDelayed(moveTouch, 24) }
            else if (fixed) view.invalidate()
            // the island changed size while the window was fitted to it: give it room again, then fit once more
            else if (!staged && (r.width() != params.width || r.height() != params.height)) stage()
        }

        /**
         * Tells Android which part of the window takes touches (the island) through ViewTreeObserver's internal
         * insets listener, which Android keeps hidden from apps, so it is reached by name. False if this Android
         * does not allow it.
         */
        private fun touchOnlyTheIsland(): Boolean = try {
            val listener = Class.forName("android.view.ViewTreeObserver\$OnComputeInternalInsetsListener")
            val info = Class.forName("android.view.ViewTreeObserver\$InternalInsetsInfo")
            val setTouchable = info.getMethod("setTouchableInsets", Int::class.javaPrimitiveType)
            val region = info.getField("touchableRegion")
            val onlyRegion = info.getField("TOUCHABLE_INSETS_REGION").getInt(null)
            val add = ViewTreeObserver::class.java.getMethod("addOnComputeInternalInsetsListener", listener)
            val proxy = java.lang.reflect.Proxy.newProxyInstance(listener.classLoader, arrayOf(listener)) { self, method, args ->
                when (method.name) {
                    "onComputeInternalInsets" -> {
                        setTouchable.invoke(args!![0], onlyRegion)
                        (region.get(args[0]) as Region).set(island)
                        null
                    }
                    "equals" -> self === args?.get(0)
                    "hashCode" -> System.identityHashCode(self)
                    "toString" -> "IslandTouch"
                    else -> null
                }
            }
            add.invoke(view.viewTreeObserver, proxy)
            true
        } catch (_: Throwable) { false }

        init {
            saved.performRestore(null)
            registry.handleLifecycleEvent(Lifecycle.Event.ON_CREATE)
            view.setViewTreeLifecycleOwner(this)
            view.setViewTreeSavedStateRegistryOwner(this)
            view.setContent {
                // The window can be bigger than the island (staged) or, for a moment, smaller (fitted to its old size):
                // the island always keeps its own size and its place at the top center.
                // Split (above the status bar): the window stays put at the top and the island moves inside it, so a
                // new place or the docked shape animates instead of the window jumping there.
                val look by IslandLook.state.collectAsState()
                val moveSpec = spring<androidx.compose.ui.unit.Dp>(dampingRatio = 0.85f, stiffness = 380f)
                val ox by animateDpAsState(if (touch != null) look.x.dp else 0.dp, moveSpec, label = "x")
                val oy by animateDpAsState(if (touch != null && !look.dock) look.y.dp else 0.dp, moveSpec, label = "y")
                Box(contentAlignment = Alignment.TopCenter) {
                    Box(Modifier.offset(ox, oy).wrapContentSize(Alignment.TopCenter, unbounded = true)) {
                        IslandUi(open.value, { open.value = it }, ::stage, ::bounds, above = host != null)
                    }
                }
            }
            // a tap anywhere else closes the card
            view.setOnTouchListener { _, e ->
                if (e.action == MotionEvent.ACTION_OUTSIDE) open.value = null
                false
            }
            // The window keeps one size for good and only the island's own rectangle takes touches; everything else
            // goes to the app below. Without that (an Android that refuses it), the window follows the island's size.
            fixed = touch != null || touchOnlyTheIsland()
            params.width = context.resources.displayMetrics.widthPixels
            params.height = (STAGE_HEIGHT_DP * density).toInt()
            setPosition()
            // If either add fails, nothing must stay in the WindowManager: undo the first view, then rethrow.
            try { manager.addView(view, params) } catch (e: Throwable) {
                try { manager.removeView(view) } catch (_: Throwable) {}
                throw e
            }
            touch?.let { t ->
                t.setOnTouchListener { _, e ->
                    if (e.action == MotionEvent.ACTION_OUTSIDE) open.value = null
                    else {
                        // the same touch, where the island is in the drawn window
                        val moved = MotionEvent.obtain(e)
                        moved.offsetLocation(island.left.toFloat(), island.top.toFloat())
                        view.dispatchTouchEvent(moved)
                        moved.recycle()
                    }
                    true
                }
                try { manager.addView(t, touchParams) } catch (e: Throwable) {
                    try { manager.removeView(view) } catch (_: Throwable) {}
                    throw e
                }
            }
            if (!fixed) { staged = true; main.postDelayed(settle, STAGE_MS) }
            registry.handleLifecycleEvent(Lifecycle.Event.ON_START)
            registry.handleLifecycleEvent(Lifecycle.Event.ON_RESUME)
        }

        private fun setPosition() {
            val look = IslandLook.state.value
            // split: the island moves inside the window (see setContent)
            params.x = if (touch != null) 0 else (look.x * density).toInt()
            val y = (look.y * density).toInt()
            params.y = when {
                host != null -> 0
                // under the status bar the island could not be tapped: keep it just below the bar
                else -> maxOf(y, statusBar() + (4 * density).toInt())
            }
        }

        private fun statusBar(): Int = try {
            manager.currentWindowMetrics.windowInsets.getInsetsIgnoringVisibility(WindowInsets.Type.statusBars()).top
        } catch (_: Exception) { (24 * density).toInt() }

        fun place() {
            setPosition()
            try { manager.updateViewLayout(view, params) } catch (_: Exception) {}
            placeTouch()
        }

        /**
         * While the island grows or shrinks, the window is made big once, so the animation runs inside it; then it
         * goes back to the island's own size, so it never takes touches from the app below. Resizing the window on
         * every frame (what it did before) is slow on Android and made the animation stutter.
         */
        /** The island is about to change: the window takes its big size until the animation settles. */
        fun stage() {
            if (fixed) return
            main.removeCallbacks(settle)
            if (!staged) {
                staged = true
                resize(context.resources.displayMetrics.widthPixels, (STAGE_HEIGHT_DP * density).toInt())
            }
            main.postDelayed(settle, STAGE_MS)
        }

        private fun resize(w: Int, h: Int) {
            if (params.width == w && params.height == h) return
            params.width = w
            params.height = h
            try { manager.updateViewLayout(view, params) } catch (_: Exception) {}
        }

        fun remove() {
            main.removeCallbacks(settle)
            main.removeCallbacks(moveTouch)
            registry.handleLifecycleEvent(Lifecycle.Event.ON_DESTROY)
            try { manager.removeView(view) } catch (_: Exception) {}
            touch?.let { try { manager.removeView(it) } catch (_: Exception) {} }
        }
    }
}

/** The tallest open card fits in this while the window is staged (see `Shown.stage`). */
private const val STAGE_HEIGHT_DP = 440
/** Long enough for the slowest animation below to settle. */
private const val STAGE_MS = 800L
/** With nothing to show, the island waits this long before it tucks away (when the person chose so). */
private const val HIDE_AFTER_MS = 4_000L

/** Overshoots a little and settles: the island's "pop". */
private val Pop = CubicBezierEasing(0.34f, 1.36f, 0.64f, 1f)

@Composable
private fun IslandUi(
    open: Pill.Kind?,
    onOpen: (Pill.Kind?) -> Unit,
    stage: () -> Unit,
    bounds: (android.graphics.Rect) -> Unit,
    above: Boolean,
) {
    val context = LocalContext.current
    val link by Link.state.collectAsState()
    val outgoing by FileSender.waiting.collectAsState()
    val transfers by Island.transfers.collectAsState()
    val discord by Island.discord.collectAsState()
    val call by Island.call.collectAsState()
    val claude by Island.claude.collectAsState()
    val mediaState by Island.media.collectAsState()
    val art by MediaWatcher.art.collectAsState()
    val look by IslandLook.state.collectAsState()
    var now by remember { mutableLongStateOf(System.currentTimeMillis()) }
    // The clock only ticks while something on screen moves on its own: an open card (elapsed times, player position),
    // the Discord call timer, or a pill entry inside its flash window. At rest, it stops, so an idle island costs
    // nothing. The gate reads the wall clock once per recomposition — it must not read `now`, or it would keep
    // the ticker alive through itself.
    val wall = System.currentTimeMillis()
    val needsClock = open != null ||
        call != null ||
        (claude != null && wall - claude.at in 0..Island.CLAUDE_MS) ||
        discord.any { wall - it.at in 0..Island.FLASH_MS } ||
        transfers.any { it.state == LiveTransfer.State.Running || wall - it.at in 0..Island.FLASH_MS }
    LaunchedEffect(needsClock) {
        if (!needsClock) return@LaunchedEffect
        while (true) { delay(500); now = System.currentTimeMillis() }
    }

    val media = if (look.media) mediaState else null
    val pill = Island.pill(link.approvals.size, link.files.size, outgoing.size, transfers, discord, call, media, now, claude)
    val screen = LocalConfiguration.current.screenWidthDp.dp

    // What shows: an open card, a short notice, the song (cover and bars beside the camera) or the resting pill;
    // the resting pill tucks away after a while if the person chose so.
    val shown: Any = open ?: when (pill.kind) {
        Pill.Kind.Idle -> "rest"
        Pill.Kind.Media -> "media"
        else -> "pill"
    }
    var hidden by remember { mutableStateOf(false) }
    LaunchedEffect(shown, look.hide) {
        hidden = false
        if (look.hide && shown == "rest") { delay(HIDE_AFTER_MS); hidden = true }
    }
    val target: Any = if (hidden && shown == "rest") "hidden" else shown
    LaunchedEffect(target, pill.kind) { stage() }

    val rest = (look.height / 2f * look.corner).dp
    val radius by animateDpAsState(
        if (open != null) maxOf(14.dp, 30.dp * look.corner) else maxOf(rest, 6.dp),
        spring(dampingRatio = 0.8f, stiffness = 380f), label = "corner",
    )
    val dot = with(LocalDensity.current) { look.height.dp.roundToPx() }
    // docked: hangs from the top edge (only possible above the status bar)
    val docked = above && look.dock
    val shoulder = if (docked) 10.dp else 0.dp

    // A tap on the pill: open what it is about (the player when only a song is around); again: close.
    val tapKind = when {
        pill.kind != Pill.Kind.Idle -> pill.kind
        media != null -> Pill.Kind.Media
        else -> null
    }
    Box(
        Modifier
            .onGloballyPositioned { c ->
                // its whole size, not boundsInWindow: that is cut to the window, so an island that outgrew a window
                // fitted to its old size (a new shape or preset) never asked for room and stayed cut
                val p = c.positionInWindow()
                bounds(android.graphics.Rect(p.x.toInt(), p.y.toInt(), p.x.toInt() + c.size.width, p.y.toInt() + c.size.height))
            }
            .alpha(look.opacity)
            .clip(if (docked) DockShape(radius, shoulder) else RoundedCornerShape(radius))
            .background(Black)
            .clickable(interactionSource = remember { MutableInteractionSource() }, indication = null) {
                onOpen(if (open != null) null else tapKind)
            }
            .padding(horizontal = shoulder)
    ) {
        AnimatedContent(
            targetState = target,
            transitionSpec = {
                (fadeIn(tween(150, delayMillis = 50)) togetherWith fadeOut(tween(50))) using SizeTransform(clip = true) { from, to ->
                    when {
                        // docked: it goes back up into the edge, and comes down out of it
                        docked && to == IntSize.Zero -> tween(360, easing = FastOutSlowInEasing)
                        docked && from == IntSize.Zero -> tween(520, easing = Pop)
                        // going away: into a dot, then the dot goes
                        to == IntSize.Zero -> keyframes {
                            durationMillis = 420
                            from at 0 using FastOutSlowInEasing
                            IntSize(dot, dot) at 250 using FastOutLinearInEasing
                        }
                        // coming back: a dot first, then it opens into the pill
                        from == IntSize.Zero -> keyframes {
                            durationMillis = 620
                            IntSize.Zero at 0 using LinearOutSlowInEasing
                            IntSize(dot, dot) at 170 using Pop
                        }
                        else -> spring(dampingRatio = 0.78f, stiffness = 420f)
                    }
                }
            },
            label = "island",
        ) { which ->
            when (which) {
                "hidden" -> Box(Modifier.size(0.dp))
                "rest" -> Box(Modifier.size(look.width.dp, look.height.dp))
                "media" -> MediaPill(look, art, media)
                "pill" -> Notice(pill, look, art, discord, screen)
                is Pill.Kind -> Box(Modifier.width(minOf(screen - 16.dp, 380.dp)).padding(14.dp)) {
                    OpenCard(which, context, link, outgoing, transfers, discord, call, claude, media, art, now, onOpen)
                }
                else -> {}
            }
        }
    }
}

/**
 * Hanging from the top edge: flat on top, [radius] below, and a small inward curve ([shoulder] wide) on each side
 * where it meets the edge, so it looks like it comes out of the screen. The body sits between the shoulders.
 */
private class DockShape(val radius: Dp, val shoulder: Dp) : Shape {
    override fun createOutline(size: Size, layoutDirection: LayoutDirection, density: Density): Outline {
        val w = size.width
        val h = size.height
        val s = with(density) { shoulder.toPx() }.coerceAtMost(minOf(w / 4, h / 2)).coerceAtLeast(0f)
        val r = with(density) { radius.toPx() }.coerceAtMost(minOf((w - 2 * s) / 2, h - s)).coerceAtLeast(0f)
        val path = Path().apply {
            moveTo(0f, 0f)
            lineTo(w, 0f)
            quadraticTo(w - s, 0f, w - s, s)
            lineTo(w - s, h - r)
            arcTo(Rect(w - s - 2 * r, h - 2 * r, w - s, h), 0f, 90f, false)
            lineTo(s + r, h)
            arcTo(Rect(s, h - 2 * r, s + 2 * r, h), 90f, 90f, false)
            lineTo(s, s)
            quadraticTo(s, 0f, 0f, 0f)
            close()
        }
        return Outline.Generic(path)
    }
}

/**
 * The song while it plays: the cover on one side of the camera and the bars on the other, the pill only a little
 * wider than at rest. Its size follows the person's choices, never the song's name (that is in the player).
 */
@Composable
private fun MediaPill(look: Look, art: Bitmap?, media: NowPlaying?) {
    val h = look.height.dp
    val icon = (h - 8.dp).coerceAtLeast(14.dp)
    Row(
        Modifier.size(look.width.dp + h * 2, h).padding(horizontal = (h - icon) / 2 + 2.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        if (art != null) Cover(art, icon, icon * 0.28f)
        else Box(Modifier.size(icon).background(Green, CircleShape), contentAlignment = Alignment.Center) {
            Text("♪", color = Color.White, fontSize = 11.sp, fontWeight = FontWeight.Bold)
        }
        Spacer(Modifier.weight(1f))
        Wave(media?.playing == true)
    }
}

/** The grown pill: an icon, one line and what a tap does. Always the same width: a long text is cut, never stretches it. */
@Composable
private fun Notice(pill: Pill, look: Look, art: Bitmap?, discord: List<DiscordEvent>, screen: Dp) {
    val height = maxOf(look.height.dp, if (pill.hint.isNotEmpty()) 44.dp else 0.dp)
    val width = minOf(screen - 16.dp, maxOf(look.width.dp + 120.dp, 250.dp))
    Row(
        Modifier.size(width, height).padding(horizontal = 8.dp, vertical = 5.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Leading(pill, art, discord.firstOrNull(), size = (height - 12.dp).coerceIn(20.dp, 32.dp))
        Column(Modifier.padding(horizontal = 8.dp).weight(1f)) {
            Text(pill.text, color = Color.White, fontSize = 12.5.sp, fontWeight = FontWeight.SemiBold, maxLines = 1,
                overflow = TextOverflow.Ellipsis)
            if (pill.hint.isNotEmpty())
                Text(pill.hint, color = Muted, fontSize = 10.5.sp, maxLines = 1, overflow = TextOverflow.Ellipsis)
        }
        when (pill.kind) {
            Pill.Kind.Offer, Pill.Kind.Approval, Pill.Kind.Outgoing -> if (pill.count > 1) Badge(pill.count, colorOf(pill.kind))
            else -> {}
        }
    }
}

private fun colorOf(kind: Pill.Kind) = when (kind) {
    Pill.Kind.Approval -> Orange
    Pill.Kind.Claude -> ClaudeOrange
    Pill.Kind.Offer -> Green
    Pill.Kind.Transfer, Pill.Kind.Outgoing -> Blue
    Pill.Kind.Discord, Pill.Kind.Call -> DiscordBlue
    Pill.Kind.Media -> Green
    Pill.Kind.Idle -> Muted
}

@Composable
private fun Leading(pill: Pill, art: Bitmap?, event: DiscordEvent?, size: Dp) {
    val avatar = if (pill.kind == Pill.Kind.Discord) event?.avatar else null
    when {
        pill.kind == Pill.Kind.Media && art != null -> Cover(art, size, 7.dp)
        avatar != null -> Avatar(event!!, size)
        else -> {
            val glyph = when (pill.kind) {
                Pill.Kind.Approval -> "!"
                Pill.Kind.Claude -> "✳"
                Pill.Kind.Offer -> "↓"
                Pill.Kind.Transfer -> "⇅"
                Pill.Kind.Outgoing -> "↑"
                Pill.Kind.Discord, Pill.Kind.Call -> event?.name?.take(1)?.uppercase() ?: "☏"
                Pill.Kind.Media -> "♪"
                Pill.Kind.Idle -> ""
            }
            Box(Modifier.size(size).background(colorOf(pill.kind), CircleShape), contentAlignment = Alignment.Center) {
                Text(glyph, color = Color.White, fontSize = 13.sp, fontWeight = FontWeight.Bold)
            }
        }
    }
}

@Composable
private fun Badge(n: Int, color: Color) =
    Box(Modifier.size(20.dp).background(color, CircleShape), contentAlignment = Alignment.Center) {
        Text("$n", color = Color.White, fontSize = 11.sp, fontWeight = FontWeight.Bold)
    }

/** Four bars bouncing while the song plays (still when paused). */
@Composable
private fun Wave(playing: Boolean) {
    val t = rememberInfiniteTransition(label = "wave")
    val phases = (0 until 4).map { i ->
        t.animateFloat(0.25f, 1f, infiniteRepeatable(tween(420 + i * 90), RepeatMode.Reverse), label = "bar$i")
    }
    Canvas(Modifier.size(22.dp, 16.dp)) {
        val w = size.width / 7
        phases.forEachIndexed { i, p ->
            val h = size.height * (if (playing) p.value else 0.25f)
            drawRoundRect(Green, Offset(i * 2 * w, (size.height - h) / 2), Size(w, h), CornerRadius(w / 2))
        }
    }
}

@Composable
private fun Cover(art: Bitmap, size: Dp, corner: Dp) =
    Image(art.asImageBitmap(), null, contentScale = ContentScale.Crop, modifier = Modifier.size(size).clip(RoundedCornerShape(corner)))

@Composable
private fun Avatar(e: DiscordEvent, size: Dp) {
    val bitmap = remember(e) { e.avatar?.let { try { BitmapFactory.decodeByteArray(it, 0, it.size) } catch (_: Exception) { null } } }
    if (bitmap != null) Image(bitmap.asImageBitmap(), null, modifier = Modifier.size(size).clip(CircleShape))
    else Box(Modifier.size(size).background(DiscordBlue, CircleShape), contentAlignment = Alignment.Center) {
        Text(e.name.take(1).uppercase(), color = Color.White, fontSize = 13.sp, fontWeight = FontWeight.Bold)
    }
}

// ───────────────────────── open cards ─────────────────────────

@Composable
private fun OpenCard(
    kind: Pill.Kind,
    context: Context,
    link: LinkUi,
    outgoing: List<OutgoingOffer>,
    transfers: List<LiveTransfer>,
    discord: List<DiscordEvent>,
    call: PhoneCall?,
    claude: ClaudeNote?,
    media: NowPlaying?,
    art: Bitmap?,
    now: Long,
    onOpen: (Pill.Kind?) -> Unit,
) {
    val close = { onOpen(null) }
    when (kind) {
        Pill.Kind.Media -> if (media != null) Player(context, media, art, now) else Gone(close)
        Pill.Kind.Offer -> link.files.firstOrNull()?.let { offer ->
            Ask(
                title = "O PC quer mandar ${if (offer.isText) "um texto" else offer.label}",
                detail = Notifications.sizeText(offer.totalBytes) + if (link.files.size > 1) " · mais ${link.files.size - 1} esperando" else "",
                no = "Rejeitar" to { Link.declineFile(context, offer.id); if (link.files.size <= 1) close() },
                yes = "Aceitar" to { if (accept(context, offer.id) && link.files.size <= 1) close() },
            )
        } ?: Gone(close)
        Pill.Kind.Approval -> link.approvals.firstOrNull()?.let { a ->
            Ask(
                title = "${a.agent}${if (a.project.isBlank()) "" else " · ${a.project}"} quer rodar",
                detail = a.command,
                no = "Negar" to { Link.decide(context, a.fingerprint, allow = false); if (link.approvals.size <= 1) close() },
                // "Permitir" always goes through the app and the screen lock, like the notification
                yes = "Permitir…" to {
                    context.startActivity(Intent(context, MainActivity::class.java)
                        .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_SINGLE_TOP)
                        .putExtra(Notifications.EXTRA_FINGERPRINT, a.fingerprint)
                        .putExtra(Notifications.EXTRA_ALLOW, !a.truncated))
                    close()
                },
            )
        } ?: Gone(close)
        // what to choose stays in the Claude app (it has no way in from outside): the island tells and takes you there
        Pill.Kind.Claude -> claude?.let { c ->
            Column {
                Title(c.title.ifEmpty { "Claude" }, c.text)
                Row(Modifier.fillMaxWidth().padding(top = 12.dp), horizontalArrangement = Arrangement.spacedBy(10.dp)) {
                    Button("Dispensar", Card, Modifier.weight(1f)) { DiscordListener.dismissClaude(); close() }
                    Button("Abrir no Claude", ClaudeOrange, Modifier.weight(1f)) { DiscordListener.openClaude(context); close() }
                }
            }
        } ?: Gone(close)
        Pill.Kind.Transfer -> if (transfers.isEmpty()) Gone(close) else Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
            transfers.take(3).forEach { t ->
                Title(t.title, t.text)
                Bar(if (t.state == LiveTransfer.State.Running) t.percent / 100f else 1f,
                    if (t.state == LiveTransfer.State.Failed) Red else if (t.state == LiveTransfer.State.Done) Green else Blue)
            }
        }
        Pill.Kind.Outgoing -> outgoing.firstOrNull()?.let { o ->
            Ask(
                title = "Esperando o PC aceitar",
                detail = o.label + if (outgoing.size > 1) " · mais ${outgoing.size - 1}" else "",
                no = "Cancelar envio" to { FileSender.cancel(context, o.id); if (outgoing.size <= 1) close() },
                yes = null,
            )
        } ?: Gone(close)
        Pill.Kind.Discord, Pill.Kind.Call -> if (discord.isEmpty() && call == null) Gone(close) else
            Column(verticalArrangement = Arrangement.spacedBy(10.dp)) {
                if (call != null) {
                    Title(call.title.ifEmpty { "Na call do Discord" }, listOf(call.text, elapsed(now - call.since)).filter { it.isNotEmpty() }.joinToString(" · "))
                    if (call.actions.isNotEmpty()) Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                        call.actions.forEachIndexed { i, label -> Button(label, Card) { DiscordListener.press(i) } }
                    }
                }
                discord.take(3).forEach { e ->
                    Row(verticalAlignment = Alignment.CenterVertically) {
                        Avatar(e, 30.dp)
                        Column(Modifier.padding(start = 10.dp)) {
                            Text(if (e.joined) "${e.name} entrou" else "${e.name} saiu", color = Color.White, fontSize = 13.sp,
                                fontWeight = FontWeight.SemiBold, maxLines = 1, overflow = TextOverflow.Ellipsis)
                            Text(listOf(e.channel, if (e.joined && e.count > 0) "${e.count} na call" else "", ago(now - e.at))
                                .filter { it.isNotEmpty() }.joinToString(" · "), color = Muted, fontSize = 11.sp, maxLines = 1)
                        }
                    }
                }
            }
        Pill.Kind.Idle -> Gone(close)
    }
}

/** What the card was about is gone (answered elsewhere): back to the pill. */
@Composable
private fun Gone(close: () -> Unit) {
    LaunchedEffect(Unit) { close() }
    Spacer(Modifier.size(1.dp))
}

@Composable
private fun Title(title: String, detail: String) = Column {
    Text(title, color = Color.White, fontSize = 15.sp, fontWeight = FontWeight.SemiBold, maxLines = 2, overflow = TextOverflow.Ellipsis)
    if (detail.isNotEmpty()) Text(detail, color = Muted, fontSize = 12.5.sp, maxLines = 4, overflow = TextOverflow.Ellipsis)
}

@Composable
private fun Ask(title: String, detail: String, no: Pair<String, () -> Unit>, yes: Pair<String, () -> Unit>?) = Column {
    Title(title, detail)
    Row(Modifier.fillMaxWidth().padding(top = 12.dp), horizontalArrangement = Arrangement.spacedBy(10.dp)) {
        Button(no.first, Card, Modifier.weight(1f), color = Red, onClick = no.second)
        if (yes != null) Button(yes.first, Green, Modifier.weight(1f), color = Black, onClick = yes.second)
    }
}

@Composable
private fun Button(label: String, background: Color, modifier: Modifier = Modifier, color: Color = Color.White, onClick: () -> Unit) =
    Box(modifier.clip(RoundedCornerShape(50)).background(background).clickable(onClick = onClick).padding(horizontal = 14.dp, vertical = 9.dp),
        contentAlignment = Alignment.Center) {
        Text(label, color = color, fontSize = 13.5.sp, fontWeight = FontWeight.SemiBold, maxLines = 1)
    }

@Composable
private fun Bar(fraction: Float, color: Color) = Canvas(Modifier.fillMaxWidth().height(4.dp)) {
    drawRoundRect(Card, cornerRadius = CornerRadius(size.height / 2))
    if (fraction > 0f) drawRoundRect(color, size = Size(size.width * fraction.coerceIn(0f, 1f), size.height), cornerRadius = CornerRadius(size.height / 2))
}

@Composable
private fun Player(context: Context, m: NowPlaying, art: Bitmap?, now: Long) = Column {
    Row(verticalAlignment = Alignment.CenterVertically) {
        Box(Modifier.clickable { MediaWatcher.openApp(context) }) {
            if (art != null) Cover(art, 56.dp, 12.dp)
            else Box(Modifier.size(56.dp).background(Card, RoundedCornerShape(12.dp)), contentAlignment = Alignment.Center) {
                Text("♪", color = Muted, fontSize = 22.sp)
            }
        }
        Column(Modifier.padding(start = 12.dp).weight(1f)) {
            Text(m.title, color = Color.White, fontSize = 15.sp, fontWeight = FontWeight.SemiBold, maxLines = 1, overflow = TextOverflow.Ellipsis)
            Text(m.artist, color = Muted, fontSize = 12.5.sp, maxLines = 1, overflow = TextOverflow.Ellipsis)
        }
        Wave(m.playing)
    }
    if (m.durationMs > 0) {
        val pos = m.positionAt(now)
        Spacer(Modifier.height(12.dp))
        Bar(pos.toFloat() / m.durationMs, Color.White)
        Row(Modifier.fillMaxWidth().padding(top = 3.dp)) {
            Text(clock(pos), color = Muted, fontSize = 10.5.sp)
            Spacer(Modifier.weight(1f))
            Text("-" + clock(m.durationMs - pos), color = Muted, fontSize = 10.5.sp)
        }
    }
    Row(Modifier.fillMaxWidth().padding(top = 8.dp), horizontalArrangement = Arrangement.SpaceEvenly, verticalAlignment = Alignment.CenterVertically) {
        Control({ MediaWatcher.previous() }) { Skip(forward = false) }
        Control({ MediaWatcher.playPause() }, big = true) { if (m.playing) Pause() else Play() }
        Control({ MediaWatcher.next() }) { Skip(forward = true) }
    }
}

@Composable
private fun Control(onClick: () -> Unit, big: Boolean = false, icon: @Composable () -> Unit) =
    Box(Modifier.size(if (big) 52.dp else 44.dp).clip(CircleShape).clickable(onClick = onClick), contentAlignment = Alignment.Center) {
        Box(Modifier.size(if (big) 26.dp else 20.dp)) { icon() }
    }

@Composable
private fun Play() = Canvas(Modifier.size(26.dp)) {
    drawPath(Path().apply { moveTo(size.width * 0.18f, 0f); lineTo(size.width, size.height / 2); lineTo(size.width * 0.18f, size.height); close() }, Color.White)
}

@Composable
private fun Pause() = Canvas(Modifier.size(26.dp)) {
    val w = size.width * 0.3f
    drawRoundRect(Color.White, Offset(size.width * 0.1f, 0f), Size(w, size.height), CornerRadius(w / 4))
    drawRoundRect(Color.White, Offset(size.width * 0.6f, 0f), Size(w, size.height), CornerRadius(w / 4))
}

@Composable
private fun Skip(forward: Boolean) = Canvas(Modifier.size(20.dp)) {
    val w = size.width
    val h = size.height
    fun x(v: Float) = if (forward) v else w - v
    drawPath(Path().apply { moveTo(x(0f), 0f); lineTo(x(w * 0.45f), h / 2); lineTo(x(0f), h); close() }, Color.White)
    drawPath(Path().apply { moveTo(x(w * 0.4f), 0f); lineTo(x(w * 0.85f), h / 2); lineTo(x(w * 0.4f), h); close() }, Color.White)
    drawRect(Color.White, Offset(if (forward) w * 0.85f else 0f, 0f), Size(w * 0.15f, h))
}

/** Receiving needs the phone unlocked, as from the notification. */
private fun accept(context: Context, id: String): Boolean {
    if (context.getSystemService(KeyguardManager::class.java)?.isKeyguardLocked == true) {
        Toast.makeText(context, "Desbloqueie o celular para aceitar", Toast.LENGTH_SHORT).show()
        return false
    }
    Link.acceptFile(context, id)
    return true
}

private fun clock(ms: Long): String {
    val s = (ms / 1000).coerceAtLeast(0)
    return "%d:%02d".format(s / 60, s % 60)
}

private fun elapsed(ms: Long): String {
    val s = (ms / 1000).coerceAtLeast(0)
    return if (s >= 3600) "%d:%02d:%02d".format(s / 3600, s / 60 % 60, s % 60) else "%d:%02d".format(s / 60, s % 60)
}

private fun ago(ms: Long): String {
    val s = ms / 1000
    return when {
        s < 60 -> "agora"
        s < 3600 -> "há ${s / 60} min"
        else -> "há ${s / 3600} h"
    }
}
