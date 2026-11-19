package io.github.theuslpszbr15.dynamicisland

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.util.Calendar
import java.util.TimeZone

class FocusTodayTest {
    private fun at(h: Int, m: Int = 0) = h * 60 + m

    @Test fun quietHoursCanCrossMidnight() {
        val c = FocusConfig(quiet = true, fromMin = at(22), toMin = at(7))
        assertTrue(c.silent("p", at(23)))
        assertTrue(c.silent("p", at(3)))
        assertFalse(c.silent("p", at(7)))
        assertFalse(c.silent("p", at(12)))
        assertTrue(c.silent("p", at(22)))
    }

    @Test fun quietHoursInTheSameDay() {
        val c = FocusConfig(quiet = true, fromMin = at(9), toMin = at(12))
        assertTrue(c.silent("p", at(10)))
        assertFalse(c.silent("p", at(12)))
        assertFalse(c.silent("p", at(8)))
    }

    @Test fun offMeansNothingIsSilencedExceptTheMutedApps() {
        val c = FocusConfig(quiet = false, fromMin = at(0), toMin = at(23), muted = setOf("com.noisy"))
        assertFalse(c.silent("com.other", at(12)))
        assertTrue(c.silent("com.noisy", at(12)))
        assertTrue(FocusConfig(manual = true).silent("anything", at(12)))
    }

    @Test fun theReasonSaysWhyItIsQuiet() {
        assertEquals("Foco ligado", FocusConfig(manual = true).reason(at(12)))
        assertEquals("Horário silencioso", FocusConfig(quiet = true).reason(at(23)))
        assertEquals("", FocusConfig(quiet = true).reason(at(12)))
        assertEquals("07:05", Focus.clock(at(7, 5)))
    }

    @Test fun weatherParsesOpenMeteoAndRejectsGarbage() {
        val json = """{"current":{"temperature_2m":23.6,"weather_code":61},"daily":{"temperature_2m_min":[18.2],"temperature_2m_max":[27.4]}}"""
        val w = Weather.parse(json, 5)!!
        assertEquals(24, w.temp)
        assertEquals(18, w.min)
        assertEquals(27, w.max)
        assertEquals("Chuva", w.label)
        assertNull(Weather.parse("""{"error":true}""", 5))
        assertNull(Weather.parse("not json", 5))
    }

    @Test fun weatherCodesHaveWords() {
        assertEquals("Céu limpo", Weather.describe(0).first)
        assertEquals("Tempestade", Weather.describe(95).first)
        assertEquals("Neve", Weather.describe(73).first)
        assertEquals("Tempo", Weather.describe(1234).first)
    }

    @Test fun eventTimesSayNowTodayTomorrow() {
        val zone = TimeZone.getTimeZone("UTC")
        fun t(day: Int, h: Int, m: Int = 0) = Calendar.getInstance(zone).apply { clear(); set(2026, Calendar.NOVEMBER, day, h, m) }.timeInMillis
        val now = t(18, 10)
        fun e(begin: Long, end: Long, allDay: Boolean = false) = AgendaEvent("x", begin, end, allDay, "")
        assertEquals("Agora", e(t(18, 9), t(18, 11)).whenText(now, zone))
        assertEquals("Hoje 14:30", e(t(18, 14, 30), t(18, 15)).whenText(now, zone))
        assertEquals("Amanhã 09:00", e(t(19, 9), t(19, 10)).whenText(now, zone))
        assertEquals("Dia todo", e(t(18, 0), t(19, 0), allDay = true).whenText(now, zone))
        assertEquals("Amanhã · dia todo", e(t(19, 0), t(20, 0), allDay = true).whenText(now, zone))
    }
}
