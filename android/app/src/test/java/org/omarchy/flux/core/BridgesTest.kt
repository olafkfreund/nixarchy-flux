package org.omarchy.flux.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import org.omarchy.flux.protocol.Packet
import org.omarchy.flux.protocol.Types
import org.omarchy.flux.protocol.bodyOf

class BridgesTest {
    private fun List<CallEvent>.withoutIds() = map { it.copy(id = null) }

    private fun events(vararg states: LineState): List<CallEvent> {
        val t = CallTracker()
        return states.flatMap { t.onState(it) }.withoutIds()
    }

    @Test
    fun answeredCall() {
        assertEquals(
            listOf(CallEvent("ringing"), CallEvent("talking"), CallEvent("talking", cancel = true)),
            events(LineState.Ringing, LineState.OffHook, LineState.Idle),
        )
    }

    @Test
    fun missedCall() {
        assertEquals(
            listOf(CallEvent("ringing"), CallEvent("missedCall"), CallEvent("ringing", cancel = true)),
            events(LineState.Ringing, LineState.Idle),
        )
    }

    @Test
    fun outgoingCallAndRepeatedStates() {
        assertEquals(
            listOf(CallEvent("talking"), CallEvent("talking", cancel = true)),
            events(LineState.Idle, LineState.OffHook, LineState.OffHook, LineState.Idle, LineState.Idle),
        )
    }

    @Test
    fun numberFromLateBroadcast() {
        val t = CallTracker()
        assertEquals(listOf(CallEvent("ringing")), t.onState(LineState.Ringing).withoutIds())
        // The broadcast without READ_CALL_LOG has no number.
        assertEquals(emptyList<CallEvent>(), t.onNumber(null, LineState.Ringing))
        assertEquals(listOf(CallEvent("ringing", number = "+47 111")), t.onNumber(" +47 111 ", LineState.Ringing).withoutIds())
        assertEquals(emptyList<CallEvent>(), t.onNumber("+47 111", LineState.Ringing))
        assertEquals(
            listOf(CallEvent("missedCall", number = "+47 111"), CallEvent("ringing", cancel = true, number = "+47 111")),
            t.onState(LineState.Idle).withoutIds(),
        )
        assertNull(t.number)
    }

    @Test
    fun numberFromEarlyBroadcast() {
        val t = CallTracker()
        assertEquals(emptyList<CallEvent>(), t.onNumber("+47 111", LineState.Ringing))
        assertEquals(listOf(CallEvent("ringing", number = "+47 111")), t.onState(LineState.Ringing).withoutIds())
        assertEquals(emptyList<CallEvent>(), t.onNumber("+47 111", LineState.OffHook))
        assertEquals(listOf(CallEvent("talking", number = "+47 111")), t.onState(LineState.OffHook).withoutIds())
    }

    @Test
    fun nextCallDoesNotShowTheNumberOfTheCallBefore() {
        val t = CallTracker()
        t.onState(LineState.Ringing)
        t.onNumber("+47 111", LineState.Ringing)
        t.onState(LineState.Idle)
        // The idle broadcast of the first call comes after the idle state.
        assertEquals(emptyList<CallEvent>(), t.onNumber("+47 111", LineState.Idle))
        assertNull(t.number)

        // The second call rings before its broadcast comes.
        assertEquals(listOf(CallEvent("ringing")), t.onState(LineState.Ringing).withoutIds())
        assertEquals(listOf(CallEvent("ringing", number = "+47 222")), t.onNumber("+47 222", LineState.Ringing).withoutIds())
    }

    @Test
    fun lateRingingBroadcastOfAShortCall() {
        val t = CallTracker()
        t.onState(LineState.Ringing)
        t.onState(LineState.Idle)
        // The ringing broadcast comes after the call ended. The idle
        // broadcast that follows removes its number.
        t.onNumber("+47 111", LineState.Ringing)
        t.onNumber("+47 111", LineState.Idle)
        assertNull(t.number)
        assertEquals(listOf(CallEvent("ringing")), t.onState(LineState.Ringing).withoutIds())
    }

    @Test
    fun numberBeforeAndroid12() {
        val t = CallTracker()
        assertEquals(listOf(CallEvent("ringing", number = "+47 111")), t.onState(LineState.Ringing, "+47 111").withoutIds())
        assertEquals(listOf(CallEvent("talking", number = "+47 111")), t.onState(LineState.OffHook, "").withoutIds())
        assertEquals(listOf(CallEvent("talking", cancel = true, number = "+47 111")), t.onState(LineState.Idle, "+47 111").withoutIds())
        assertEquals(listOf(CallEvent("ringing", number = "+47 222")), t.onState(LineState.Ringing, "+47 222").withoutIds())
    }

    @Test
    fun callIdsFollowCallsAndClearAtIdle() {
        val t = CallTracker()
        assertNull(t.snapshot().id)
        val first = t.onState(LineState.Ringing).single().id
        assertEquals(16, first?.length)
        assertEquals(first, t.onState(LineState.OffHook).single().id)
        val ended = t.onState(LineState.Idle).single()
        assertEquals(first, ended.id)
        assertEquals(first, CallPackets.packet(ended, null, null).string("callId"))
        assertNull(t.snapshot().id)
        val missedId = t.onState(LineState.Ringing).single().id
        val missed = t.onState(LineState.Idle)
        assertTrue(missed.all { CallPackets.packet(it, null, null).string("callId") == missedId })
        val second = t.onState(LineState.OffHook).single().id
        assertTrue(first != second && missedId != second)
        assertEquals(second, t.onState(LineState.Idle).single().id)
    }

    @Test
    fun waitingCallHasItsOwnIdAndKeepsItOnEnd() {
        val t = CallTracker()
        val first = t.onState(LineState.OffHook).single().id
        val waiting = t.onState(LineState.Ringing).single()
        assertTrue(waiting.waiting)
        assertTrue(first != waiting.id)
        assertEquals(CallSnapshot(LineState.Ringing, waiting.id, true), t.snapshot())
        val ended = t.onState(LineState.Idle)
        assertEquals(listOf("missedCall", "ringing"), ended.map { it.event })
        assertTrue(ended.all { it.id == waiting.id && it.waiting })
        assertTrue(ended.all { CallPackets.packet(it, null, null).string("callId") == waiting.id })
        assertEquals(CallSnapshot(LineState.Idle, null, false), t.snapshot())
    }

    @Test
    fun lineIsOneCallAgainAfterTheWaitingCall() {
        val t = CallTracker()
        val first = t.onState(LineState.OffHook).single().id
        t.onState(LineState.Ringing)
        val back = t.onState(LineState.OffHook).single()
        assertEquals("talking", back.event)
        assertEquals(first, back.id)
        assertTrue(!back.waiting)
        assertEquals(CallSnapshot(LineState.OffHook, first, false), t.snapshot())
    }

    @Test
    fun telephonyBody() {
        val p = Packet.parse(CallPackets.packet(CallEvent("ringing"), " +47 123 ", "Mom").serialize())!!
        assertEquals(Types.TELEPHONY, p.type)
        assertEquals("ringing", p.string("event"))
        assertEquals("+47 123", p.string("phoneNumber"))
        assertEquals("Mom", p.string("contactName"))
        assertFalse(p.has("isCancel"))

        // Without the contacts permission, the packet has only the number.
        val numberOnly = CallPackets.body(CallEvent("talking", cancel = true), "+47 123", null)
        assertEquals(setOf("event", "phoneNumber", "isCancel", "callId", "waiting", "handsFree"), numberOnly.keys)

        // With no number either, the name is "Unknown caller".
        val unknown = Packet(Types.TELEPHONY, CallPackets.body(CallEvent("ringing"), null, null))
        assertEquals(CallPackets.UNKNOWN, unknown.string("contactName"))
        assertFalse(unknown.has("phoneNumber"))

        val call = CallTracker().onState(LineState.Ringing).single()
        val withHandsFree = Packet(Types.TELEPHONY, CallPackets.body(call, null, null, listOf("p620", "razer")))
        assertEquals(call.id, withHandsFree.string("callId"))
        assertEquals(false, withHandsFree.bool("waiting"))
        assertEquals(listOf("p620", "razer"), withHandsFree.strings("handsFree"))
    }

    private fun notification(vararg fields: Pair<String, Any?>) = Packet(Types.NOTIFICATION, bodyOf(*fields))

    @Test
    fun notificationFromFluxd() {
        val n = ComputerNotification.from(
            notification("id" to "flux-1", "appName" to "omarchy-xps", "title" to "Build done", "text" to "make · 42s", "time" to "1790000000000", "isClearable" to true),
            deviceId = "pc1", computer = "omarchy-xps", now = 5,
        )!!
        assertEquals("omarchy-xps", n.subText)
        assertEquals("Build done", n.title)
        assertEquals("make · 42s", n.text)
        assertEquals(1790000000000L, n.time)
        assertTrue(n.clearable)
        assertEquals("pc1:flux-1".hashCode(), n.notificationId)
    }

    @Test
    fun notificationFromAnotherApp() {
        // Another app name shows next to the computer name.
        val n = ComputerNotification.from(notification("id" to "7", "appName" to "Firefox", "title" to "Download done"), "pc1", "omarchy-xps", now = 5)!!
        assertEquals("Firefox · omarchy-xps", n.subText)
        assertEquals("Download done", n.title)
        assertEquals("", n.text)
        assertEquals(5L, n.time)
    }

    @Test
    fun notificationWithoutIdOrTitle() {
        assertNull(ComputerNotification.from(notification("id" to "x"), "pc1", "omarchy-xps", 0))
        assertNull(ComputerNotification.from(notification("id" to "x", "title" to "  ", "text" to "body only"), "pc1", "omarchy-xps", 0))
        assertNull(ComputerNotification.from(notification("id" to "x", "ticker" to "ticker only"), "pc1", "omarchy-xps", 0))
        assertNull(ComputerNotification.from(notification("title" to "no id"), "pc1", "omarchy-xps", 0))
    }
}
