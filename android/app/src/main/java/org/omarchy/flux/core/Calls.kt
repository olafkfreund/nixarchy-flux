package org.omarchy.flux.core

import kotlinx.serialization.json.JsonObject
import org.omarchy.flux.protocol.Packet
import org.omarchy.flux.protocol.Types
import org.omarchy.flux.protocol.bodyOf
import java.security.SecureRandom

/** The state of the phone line, as TelephonyManager reports it. */
enum class LineState { Idle, Ringing, OffHook }

/**
 * 1 flux.telephony event. [cancel] is true when the event ends. [number] is
 * the number of the call, or null when the phone does not know it.
 */
data class CallEvent(val event: String, val cancel: Boolean = false, val number: String? = null, val id: String? = null, val waiting: Boolean = false)

data class CallSnapshot(val state: LineState, val id: String?, val waiting: Boolean)

/**
 * Turns the line states of the phone into telephony events in this order:
 *
 * - Idle to Ringing: ringing.
 * - Ringing or Idle to OffHook: talking. The user answered, or started a call.
 * - Ringing to Idle: missedCall, then the end of ringing.
 * - OffHook to Idle: the end of talking.
 *
 * The tracker also keeps the number of the current call. Each event has
 * that number.
 */
class CallTracker {
    private val random = SecureRandom()
    private fun newId() = random.nextLong().toULong().toString(16).padStart(16, '0')

    var state = LineState.Idle
        private set
    var callId: String? = null
        private set
    var waiting = false
        private set
    /** The call on the line while a second one rings: it comes back when that ringing ends. */
    private var heldId: String? = null

    /** The number of the current call. It is null between calls. */
    var number: String? = null
        private set

    /**
     * Applies the next line state. Before Android 12, [phoneNumber] comes
     * with the state. The number stays until the line is idle again.
     */
    @Synchronized fun snapshot() = CallSnapshot(state, callId, waiting)

    @Synchronized fun reset() {
        state = LineState.Idle
        callId = null
        heldId = null
        waiting = false
        number = null
    }

    /** The action guard uses this monitor across its state check and Telecom call. */
    fun <T> withLock(action: (CallSnapshot) -> T): T = synchronized(this) { action(snapshot()) }

    @Synchronized fun onState(next: LineState, phoneNumber: String? = null): List<CallEvent> {
        phoneNumber?.trim()?.takeIf { it.isNotEmpty() }?.let { number = it }
        val prev = state
        if (next == LineState.Ringing && prev != LineState.Ringing) {
            waiting = prev == LineState.OffHook
            heldId = if (waiting) callId else null
            callId = newId()
        } else if (next == LineState.OffHook && prev == LineState.Idle) {
            callId = newId()
        } else if (next == LineState.OffHook && prev == LineState.Ringing && waiting) {
            callId = heldId ?: newId()   // the second call ended or was taken: the line is one call again
            heldId = null
            waiting = false
        }
        state = next
        val n = number
        val id = callId
        val isWaiting = waiting
        val events = when {
            prev == next -> emptyList()
            next == LineState.Ringing -> listOf(CallEvent("ringing", number = n, id = id, waiting = isWaiting))
            next == LineState.OffHook -> listOf(CallEvent("talking", number = n, id = id, waiting = isWaiting))
            prev == LineState.Ringing -> listOf(CallEvent("missedCall", number = n, id = id, waiting = isWaiting), CallEvent("ringing", cancel = true, number = n, id = id, waiting = isWaiting))
            else -> listOf(CallEvent("talking", cancel = true, number = n, id = id, waiting = isWaiting))
        }
        if (next == LineState.Idle) {
            number = null
            callId = null
            heldId = null
            waiting = false
        }
        return events
    }

    /**
     * Applies the number from 1 PHONE_STATE broadcast. [line] is the line
     * state of that broadcast. On Android 12 and later, the broadcast can
     * come before or after the state from TelephonyCallback.
     *
     * The broadcast for the idle line has the number of the call that
     * ended. The tracker does not keep that number, because the next call
     * can have a different number. When the number of a ringing call comes
     * after the ringing event, the tracker gives ringing again with the
     * number.
     */
    @Synchronized fun onNumber(phoneNumber: String?, line: LineState): List<CallEvent> {
        if (line == LineState.Idle) {
            if (state == LineState.Idle) number = null
            return emptyList()
        }
        val n = phoneNumber?.trim().orEmpty()
        if (n.isEmpty() || n == number) return emptyList()
        number = n
        return if (state == LineState.Ringing && line == LineState.Ringing) listOf(CallEvent("ringing", number = n, id = callId, waiting = waiting)) else emptyList()
    }
}

object CallPackets {
    /** The contact name when the phone knows neither the number nor the name. */
    const val UNKNOWN = "Unknown caller"

    /**
     * The body of a flux.telephony packet. Without the contacts
     * permission, [name] is null, and the packet has only the number. With
     * no number either, the name is [UNKNOWN].
     */
    fun body(e: CallEvent, number: String?, name: String?, handsFree: List<String> = emptyList()): JsonObject {
        val n = number?.trim().orEmpty()
        val c = name?.trim().orEmpty().ifEmpty { if (n.isEmpty()) UNKNOWN else "" }
        val fields = buildList {
            add("event" to e.event)
            add("callId" to e.id)
            add("waiting" to e.waiting)
            add("handsFree" to handsFree)
            if (n.isNotEmpty()) add("phoneNumber" to n)
            if (c.isNotEmpty()) add("contactName" to c)
            if (e.cancel) add("isCancel" to true)
        }
        return bodyOf(*fields.toTypedArray())
    }

    fun packet(e: CallEvent, number: String?, name: String?, handsFree: List<String> = emptyList()) = Packet(Types.TELEPHONY, body(e, number, name, handsFree))
}
