package io.github.theuslpszbr15.dynamicisland

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class SystemWatchersTest {
    private fun pill(timer: TimerState = TimerState(), battery: BatteryInfo? = null, recording: Recording = Recording(), now: Long = 10_000,
                     off: Set<Pill.Kind> = emptySet(), media: NowPlaying? = null) =
        Island.pill(0, 0, 0, emptyList(), emptyList(), null, media, now, null, off, emptyList(), emptyList(), timer, battery, recording)

    @Test fun aCountdownCountsDownAndRoundsUp() {
        val t = TimerState.countdown(5 * 60_000L, now = 0)
        assertEquals("5:00", t.text(0))
        assertEquals("4:59", t.text(1_000))
        assertEquals("5:00", t.text(500))
        assertEquals("0:01", t.text(5 * 60_000L - 1))
        assertEquals("1:00:00", TimerState.countdown(3_600_000L, 0).text(0))
    }

    @Test fun aStopwatchCountsUpAndPausesWithoutLosingTime() {
        val t = TimerState.stopwatch(1_000)
        assertEquals("0:05", t.text(6_000))
        val paused = t.pause(6_000)
        assertEquals("0:05", paused.text(60_000))
        val back = paused.resume(60_000)
        assertEquals("0:05", back.text(60_000))
        assertEquals("0:08", back.text(63_000))
        assertTrue(back.running)
    }

    @Test fun aCountdownThatReachesZeroIsToldOnce() {
        val t = TimerState.countdown(1_000, 0)
        assertFalse(t.reached(500))
        assertTrue(t.reached(1_000))
        val done = t.finish(1_000)
        assertFalse(done.reached(5_000))
        assertEquals("0:00", done.text(5_000))
    }

    @Test fun theRunningTimerShowsOnThePillBeforeTheSong() {
        val song = NowPlaying("spotify", "T", "A", true, 0, 1000, 0)
        val t = TimerState.countdown(60_000, 0)
        val p = pill(timer = t, now = 0, media = song)
        assertEquals(Pill.Kind.Timer, p.kind)
        assertEquals("1:00", p.text)
        assertEquals("Temporizador", p.hint)
        assertEquals("Acabou!", pill(timer = t.finish(60_000), now = 61_000).text)
        assertEquals(Pill.Kind.Media, pill(timer = t, now = 0, media = song, off = setOf(Pill.Kind.Timer)).kind)
    }

    @Test fun batteryEventsAreTheChangesNotTheFirstReading() {
        val first = BatteryWatcher.next(null, 50, false, 0)
        assertEquals(BatteryInfo.Event.None, first.event)
        assertEquals(BatteryInfo.Event.Plugged, BatteryWatcher.next(first, 50, true, 10).event)
        val charging = BatteryWatcher.next(first, 50, true, 10)
        assertEquals(BatteryInfo.Event.Unplugged, BatteryWatcher.next(charging, 51, false, 20).event)
        assertEquals(BatteryInfo.Event.Full, BatteryWatcher.next(BatteryWatcher.next(null, 99, true, 0), 100, true, 5).event)
        assertEquals(BatteryInfo.Event.Low, BatteryWatcher.next(BatteryWatcher.next(null, 21, false, 0), 20, false, 5).event)
        assertEquals(BatteryInfo.Event.Critical, BatteryWatcher.next(BatteryWatcher.next(null, 11, false, 0), 10, false, 5).event)
    }

    @Test fun aQuietBatteryChangeKeepsTheLastEventAndItsTime() {
        val plugged = BatteryWatcher.next(BatteryWatcher.next(null, 50, false, 0), 50, true, 100)
        val later = BatteryWatcher.next(plugged, 51, true, 9_000)
        assertEquals(BatteryInfo.Event.Plugged, later.event)
        assertEquals(100L, later.at)
        assertEquals(51, later.percent)
    }

    @Test fun batteryNoticesLightThePillOnlyBriefly() {
        val plugged = BatteryInfo(63, true, at = 1_000, event = BatteryInfo.Event.Plugged)
        assertEquals("Carregando · 63%", pill(battery = plugged, now = 2_000).text)
        assertEquals(Pill.Kind.Idle, pill(battery = plugged, now = 1_000 + BatteryWatcher.FLASH_MS + 1).kind)
        assertEquals(Pill.Kind.Idle, pill(battery = plugged, now = 2_000, off = setOf(Pill.Kind.Battery)).kind)
        assertEquals("Bateria baixa · 20%", pill(battery = BatteryInfo(20, false, 1_000, BatteryInfo.Event.Low), now = 2_000).text)
    }

    @Test fun usingTheMicOrCameraIsToldWhenItStarts() {
        val mic = Recording(mic = true, since = 1_000)
        assertEquals("Microfone em uso", pill(recording = mic, now = 2_000).text)
        assertEquals("Câmera e microfone em uso", Recording(true, true, 0).text)
        assertEquals(Pill.Kind.Idle, pill(recording = mic, now = 1_000 + Island.FLASH_MS + 1).kind)
        assertEquals(Pill.Kind.Idle, pill(recording = Recording(since = 1_000), now = 2_000).kind)
    }
}
