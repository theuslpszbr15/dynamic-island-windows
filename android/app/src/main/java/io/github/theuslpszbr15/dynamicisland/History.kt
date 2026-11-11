package io.github.theuslpszbr15.dynamicisland

import android.content.Context
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import org.json.JSONArray
import org.json.JSONObject
import java.io.File

/**
 * One thing sent to or received from the PC. [uris]: where received files were saved (MediaStore entries of this app);
 * [mimes] goes with them. [text]: a text that went either way.
 */
data class TransferRecord(
    val id: String,
    val at: Long,
    val sent: Boolean,
    val title: String,
    val bytes: Long,
    val count: Int,
    val status: String,
    val uris: List<String> = emptyList(),
    val mimes: List<String> = emptyList(),
    val text: String? = null,
) {
    val isText get() = text != null

    fun toJson(): JSONObject = JSONObject()
        .put("id", id).put("at", at).put("sent", sent).put("title", title).put("bytes", bytes).put("count", count)
        .put("status", status).put("uris", JSONArray(uris)).put("mimes", JSONArray(mimes))
        .apply { if (text != null) put("text", text) }

    companion object {
        fun parse(o: JSONObject): TransferRecord? {
            val id = o.optString("id").ifEmpty { return null }
            fun strings(name: String) = o.optJSONArray(name)?.let { a -> (0 until a.length()).map { a.optString(it) } } ?: emptyList()
            return TransferRecord(id, o.optLong("at"), o.optBoolean("sent"), o.optString("title"), o.optLong("bytes"),
                o.optInt("count", 1), o.optString("status"), strings("uris"), strings("mimes"),
                if (o.has("text")) o.optString("text") else null)
        }
    }
}

/**
 * The "Histórico" tab: the last [MAX] transfers each way, newest first, in the app's private storage (no other app
 * can read it). Long texts are cut to [MAX_TEXT_CHARS]. The pure list logic lives in [Entries] for the tests.
 */
object History {
    const val MAX = 100
    const val MAX_TEXT_CHARS = 20_000

    private val _records = MutableStateFlow<List<TransferRecord>>(emptyList())
    val records: StateFlow<List<TransferRecord>> = _records
    @Volatile private var file: File? = null

    fun load(context: Context) {
        if (file != null) return
        val f = File(context.applicationContext.filesDir, "transfers.json")
        file = f
        _records.value = try { if (f.exists()) Entries.read(f.readText()) else emptyList() } catch (_: Exception) { emptyList() }
    }

    fun add(context: Context, record: TransferRecord) = change(context) { Entries.add(it, record) }

    fun setStatus(context: Context, id: String, sent: Boolean, status: String) = change(context) { Entries.setStatus(it, id, sent, status) }

    fun remove(context: Context, id: String, sent: Boolean) = change(context) { list -> list.filterNot { it.id == id && it.sent == sent } }

    fun clear(context: Context, sent: Boolean) = change(context) { list -> list.filterNot { it.sent == sent } }

    @Synchronized
    private fun change(context: Context, edit: (List<TransferRecord>) -> List<TransferRecord>) {
        load(context)
        val next = edit(_records.value)
        if (next == _records.value) return
        _records.value = next
        try {
            val f = file!!
            val temp = File(f.parentFile, f.name + ".tmp")
            temp.writeText(Entries.write(next))
            if (!temp.renameTo(f)) { f.delete(); temp.renameTo(f) }
        } catch (_: Exception) {}
    }

    /** The list rules, without Android. */
    object Entries {
        fun add(list: List<TransferRecord>, record: TransferRecord): List<TransferRecord> {
            val r = if ((record.text?.length ?: 0) > MAX_TEXT_CHARS) record.copy(text = record.text!!.take(MAX_TEXT_CHARS)) else record
            val rest = list.filterNot { it.id == r.id && it.sent == r.sent }
            // at most MAX per side
            val same = rest.filter { it.sent == r.sent }.take(MAX - 1).toSet()
            return listOf(r) + rest.filter { it.sent != r.sent || it in same }
        }

        fun setStatus(list: List<TransferRecord>, id: String, sent: Boolean, status: String) =
            list.map { if (it.id == id && it.sent == sent) it.copy(status = status) else it }

        fun write(list: List<TransferRecord>): String = JSONArray().apply { list.forEach { put(it.toJson()) } }.toString()

        fun read(json: String): List<TransferRecord> {
            val a = JSONArray(json)
            return (0 until a.length()).mapNotNull { a.optJSONObject(it)?.let(TransferRecord::parse) }
        }
    }
}
