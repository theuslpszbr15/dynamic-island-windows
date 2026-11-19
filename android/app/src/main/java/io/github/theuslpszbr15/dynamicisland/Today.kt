package io.github.theuslpszbr15.dynamicisland

import android.Manifest
import android.content.Context
import android.content.ContentUris
import android.content.pm.PackageManager
import android.location.LocationManager
import android.provider.CalendarContract
import androidx.core.content.ContextCompat
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import org.json.JSONObject
import java.net.HttpURLConnection
import java.net.URL
import java.util.Calendar
import java.util.TimeZone
import kotlin.math.roundToInt

/** The weather now and today's range, from Open-Meteo (no account, no key). */
data class Weather(val temp: Int, val code: Int, val min: Int, val max: Int, val at: Long) {
    val label get() = describe(code).first
    val glyph get() = describe(code).second

    companion object {
        /** WMO weather code to a short Portuguese label and a symbol. */
        fun describe(code: Int): Pair<String, String> = when (code) {
            0 -> "Céu limpo" to "☀"
            1, 2 -> "Poucas nuvens" to "⛅"
            3 -> "Nublado" to "☁"
            45, 48 -> "Neblina" to "🌫"
            in 51..57 -> "Garoa" to "🌦"
            in 61..67, in 80..82 -> "Chuva" to "🌧"
            in 71..77, 85, 86 -> "Neve" to "❄"
            95, 96, 99 -> "Tempestade" to "⛈"
            else -> "Tempo" to "•"
        }

        /** The answer of `forecast?current=temperature_2m,weather_code&daily=temperature_2m_max,temperature_2m_min`; null if it is not that. */
        fun parse(json: String, now: Long): Weather? = try {
            val o = JSONObject(json)
            val cur = o.getJSONObject("current")
            val daily = o.getJSONObject("daily")
            Weather(cur.getDouble("temperature_2m").roundToInt(), cur.getInt("weather_code"),
                daily.getJSONArray("temperature_2m_min").getDouble(0).roundToInt(),
                daily.getJSONArray("temperature_2m_max").getDouble(0).roundToInt(), now)
        } catch (_: Exception) { null }
    }
}

/** An event of the phone's calendar. */
data class AgendaEvent(val title: String, val begin: Long, val end: Long, val allDay: Boolean, val place: String) {
    /** "Agora", "Hoje 14:30", "Amanhã 09:00" or "Dia todo". [zone] is the phone's. */
    fun whenText(now: Long, zone: TimeZone = TimeZone.getDefault()): String {
        if (allDay) return if (sameDay(begin, now, zone, 1)) "Amanhã · dia todo" else "Dia todo"
        if (now in begin until end) return "Agora"
        val c = Calendar.getInstance(zone).apply { timeInMillis = begin }
        val hour = "%02d:%02d".format(c.get(Calendar.HOUR_OF_DAY), c.get(Calendar.MINUTE))
        return (if (sameDay(begin, now, zone, 1)) "Amanhã " else if (sameDay(begin, now, zone, 0)) "Hoje " else "") + hour
    }

    private fun sameDay(t: Long, now: Long, zone: TimeZone, plusDays: Int): Boolean {
        val a = Calendar.getInstance(zone).apply { timeInMillis = now; add(Calendar.DAY_OF_YEAR, plusDays) }
        val b = Calendar.getInstance(zone).apply { timeInMillis = t }
        return a.get(Calendar.YEAR) == b.get(Calendar.YEAR) && a.get(Calendar.DAY_OF_YEAR) == b.get(Calendar.DAY_OF_YEAR)
    }
}

/**
 * The "Hoje" page: the weather and the next events. The calendar needs "Agenda" and the weather needs the approximate
 * location (rounded to about 10 km before it goes to Open-Meteo, which is told nothing else); both are asked for from
 * a button on the page, the first time, and the page works with either one alone.
 */
object Today {
    const val STALE_MS = 20 * 60_000L

    private val _weather = MutableStateFlow<Weather?>(null)
    val weather: StateFlow<Weather?> = _weather
    private val _events = MutableStateFlow<List<AgendaEvent>>(emptyList())
    val events: StateFlow<List<AgendaEvent>> = _events
    @Volatile private var loading = false
    @Volatile private var lastAt = 0L

    private fun granted(context: Context, p: String) = ContextCompat.checkSelfPermission(context, p) == PackageManager.PERMISSION_GRANTED
    fun canReadCalendar(context: Context) = granted(context, Manifest.permission.READ_CALENDAR)
    fun canLocate(context: Context) = granted(context, Manifest.permission.ACCESS_COARSE_LOCATION)

    /** Looks again unless it looked a moment ago. Runs on its own thread. */
    fun refresh(context: Context, force: Boolean = false) {
        val now = System.currentTimeMillis()
        if (loading || (!force && now - lastAt < STALE_MS / 4)) return
        loading = true
        lastAt = now
        val app = context.applicationContext
        Thread {
            try {
                if (canReadCalendar(app)) _events.value = readEvents(app, now)
                if (canLocate(app)) fetchWeather(app, now)?.let { _weather.value = it }
            } catch (_: Exception) {} finally { loading = false }
        }.start()
    }

    private fun readEvents(context: Context, now: Long): List<AgendaEvent> {
        val end = Calendar.getInstance().apply { timeInMillis = now; add(Calendar.DAY_OF_YEAR, 2); set(Calendar.HOUR_OF_DAY, 0); set(Calendar.MINUTE, 0) }.timeInMillis
        val uri = CalendarContract.Instances.CONTENT_URI.buildUpon().also { ContentUris.appendId(it, now); ContentUris.appendId(it, end) }.build()
        val out = ArrayList<AgendaEvent>()
        context.contentResolver.query(uri, arrayOf(CalendarContract.Instances.TITLE, CalendarContract.Instances.BEGIN, CalendarContract.Instances.END,
            CalendarContract.Instances.ALL_DAY, CalendarContract.Instances.EVENT_LOCATION), null, null, CalendarContract.Instances.BEGIN + " ASC")?.use { c ->
            while (c.moveToNext() && out.size < 6) {
                val e = AgendaEvent(DiscordEvent.clean(c.getString(0).orEmpty().ifEmpty { "(sem título)" }, 60), c.getLong(1), c.getLong(2), c.getInt(3) == 1,
                    DiscordEvent.clean(c.getString(4).orEmpty(), 50))
                if (e.end > now) out.add(e)
            }
        }
        return out
    }

    private fun fetchWeather(context: Context, now: Long): Weather? {
        val lm = context.getSystemService(LocationManager::class.java) ?: return null
        val place = lm.getProviders(true).mapNotNull { try { lm.getLastKnownLocation(it) } catch (_: SecurityException) { null } }
            .maxByOrNull { it.time } ?: return null
        // about 10 km: enough for the weather, not for finding the person
        val lat = Math.round(place.latitude * 10) / 10.0
        val lon = Math.round(place.longitude * 10) / 10.0
        val url = URL("https://api.open-meteo.com/v1/forecast?latitude=$lat&longitude=$lon&current=temperature_2m,weather_code" +
            "&daily=temperature_2m_max,temperature_2m_min&timezone=auto&forecast_days=1")
        val http = (url.openConnection() as HttpURLConnection).apply { connectTimeout = 8_000; readTimeout = 8_000 }
        return try { if (http.responseCode == 200) Weather.parse(http.inputStream.bufferedReader().use { it.readText() }, now) else null } finally { http.disconnect() }
    }
}
