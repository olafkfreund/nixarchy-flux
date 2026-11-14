package org.omarchy.flux.core

import android.os.Handler
import android.os.Looper
import kotlinx.serialization.json.JsonObject
import org.omarchy.flux.protocol.Packet
import org.omarchy.flux.protocol.Types
import org.omarchy.flux.protocol.bodyOf
import org.omarchy.flux.protocol.bool
import org.omarchy.flux.protocol.long
import org.omarchy.flux.protocol.str
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.atomic.AtomicLong
import java.util.concurrent.atomic.AtomicReference

/**
 * 1 entry of a computer's clipboard history. [sent] is true when this
 * phone sent it. [time] is in milliseconds. [size] is the full length in
 * bytes when [truncated].
 */
data class HistoryEntry(val id: String, val text: String, val sent: Boolean, val time: Long, val size: Int, val truncated: Boolean) {
    /** The link to open: only a whole http or https URL, see WebUrl. */
    val link: String? get() = if (truncated) null else WebUrl.of(text)

    /** False when the whole text is more than the phone's clipboard takes. */
    val copyable: Boolean get() = !truncated || size <= Plugins.MAX_CLIPBOARD_TEXT
}

/** The answer to the list request [request] for [query]. [state] is "ok", "disabled" or "bad_request". */
data class ClipHistoryAnswer(val request: Long, val query: String, val state: String, val entries: List<HistoryEntry>, val more: Boolean)

/**
 * flux.clipboard.history: this phone's entries of a computer's clipboard
 * history. The phone asks, the computer answers. Each answer is checked
 * against the request it names and the computer that sent it, so a late
 * or foreign answer changes nothing.
 */
object ClipHistory {
    /** The longest query in UTF-8 bytes, as fluxd. */
    const val MAX_QUERY = 1024

    enum class Kind { List, Copy, Stale }

    private val ids = AtomicLong()

    /** Per device: the list request in flight or last answered, and its query. */
    private val lists = ConcurrentHashMap<String, Pair<Long, String>>()

    /** The one copy in flight: device id and request id. A new copy replaces it. */
    private val copy = AtomicReference<Pair<String, Long>?>(null)

    // Lazy, so the unit tests never touch Looper.
    private val main by lazy { Handler(Looper.getMainLooper()) }

    /** True when [query] fits the limit. */
    fun fits(query: String): Boolean = query.toByteArray(Charsets.UTF_8).size <= MAX_QUERY

    fun listPacket(request: Long, query: String) = Packet(Types.FLUX_CLIPBOARD_HISTORY, bodyOf("request" to request, "query" to query))

    fun entryPacket(request: Long, entryId: String) = Packet(Types.FLUX_CLIPBOARD_HISTORY, bodyOf("request" to request, "entry" to entryId))

    /** Asks [deviceId] for its entries that hold [query]. It returns the request id, which is current even when [send] fails. */
    fun list(deviceId: String, query: String, send: (Packet) -> Boolean): Long {
        val id = ids.incrementAndGet()
        lists[deviceId] = id to query
        send(listPacket(id, query))
        return id
    }

    /** Asks [deviceId] for the full text of [entryId] and copies it on the answer. False when [send] fails. */
    fun copy(deviceId: String, entryId: String, send: (Packet) -> Boolean): Boolean {
        val pending = deviceId to ids.incrementAndGet()
        copy.set(pending)
        if (send(entryPacket(pending.second, entryId))) return true
        copy.compareAndSet(pending, null)
        return false
    }

    /** Drops the copy in flight, so its answer copies nothing. */
    fun forget() {
        copy.set(null)
    }

    /** What the answer [request] from [deviceId] is for. */
    fun kind(deviceId: String, request: Long): Kind {
        val c = copy.get()
        return when {
            c != null && c.first == deviceId && c.second == request -> Kind.Copy
            lists[deviceId]?.first == request -> Kind.List
            else -> Kind.Stale
        }
    }

    /** Takes the copy in flight when it is still [deviceId]'s [request]. */
    fun takeCopy(deviceId: String, request: Long): Boolean {
        val c = copy.get() ?: return false
        // compareAndSet compares references, so it must get the same Pair.
        return c.first == deviceId && c.second == request && copy.compareAndSet(c, null)
    }

    /** Reads a list answer. Null for a full-text answer or one without a request or state. */
    fun parseList(p: Packet, query: String): ClipHistoryAnswer? {
        if (p.has("entry")) return null
        val request = p.long("request") ?: return null
        val state = p.string("state") ?: return null
        if (state == "not_found" || state == "too_large") return null
        val entries = p.array("entries")?.mapNotNull { (it as? JsonObject)?.let(::entryOf) }.orEmpty()
        return ClipHistoryAnswer(request, query, state, entries, p.bool("more") == true)
    }

    private fun entryOf(o: JsonObject): HistoryEntry? {
        val id = o.str("id")?.takeIf { it.isNotEmpty() } ?: return null
        val text = o.str("text") ?: return null
        return HistoryEntry(
            id, text, o.bool("sent") == true, (o.long("time") ?: 0L) * 1000L,
            (o.long("size") ?: 0L).toInt(), o.bool("truncated") == true,
        )
    }

    /** The list answer [p] from [deviceId] when it answers the current list request, else null. */
    fun acceptList(deviceId: String, p: Packet): ClipHistoryAnswer? {
        val request = p.long("request") ?: return null
        val (id, query) = lists[deviceId] ?: return null
        if (id != request) return null
        return parseList(p, query)
    }

    /** The toast for a copy answer that put nothing on the clipboard. */
    fun copyMessage(state: String?, from: String): String = when (state) {
        "too_large" -> "The text is too large to copy"
        "disabled" -> "Clipboard is off for this phone on $from"
        else -> "$from no longer has this entry"
    }

    /** Handles an answer. The core lock is held. */
    fun onPacket(core: FluxCore, d: Device, p: Packet) {
        val from = d.identity.deviceName
        handle(d.id, from, p, { main.post { it() } }, { Plugins.putRemoteText(core, d.id, from, it) }, core::toast) { d.clipHistory = it }
    }

    /** [onPacket] with its effects as arguments, so a test can run the posted work. */
    internal fun handle(
        deviceId: String, from: String, p: Packet,
        post: (() -> Unit) -> Unit, put: (String) -> Boolean, toast: (String) -> Unit, store: (ClipHistoryAnswer) -> Unit,
    ) {
        val request = p.long("request") ?: return
        when (kind(deviceId, request)) {
            Kind.Copy -> {
                val state = p.string("state")
                val text = p.obj("entry")?.str("text")
                // On the main thread, where forget runs, so a forget that came first wins.
                post {
                    if (takeCopy(deviceId, request)) {
                        if (state == "ok" && text != null) {
                            if (put(text)) toast("Copied to this phone")
                        } else {
                            toast(copyMessage(state, from))
                        }
                    }
                }
            }
            Kind.List -> acceptList(deviceId, p)?.let(store)
            Kind.Stale -> Unit
        }
    }
}
