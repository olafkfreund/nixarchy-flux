package org.omarchy.flux.core

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import org.omarchy.flux.protocol.INCOMING
import org.omarchy.flux.protocol.Types

class CallControlTest {
    private val ringing = CallSnapshot(LineState.Ringing, "call-1", false)

    @Test
    fun allowsAnswerAndRejectForTheCurrentRingingCall() {
        assertTrue(CallControl.allowed(ringing, "call-1", "answer", true))
        assertTrue(CallControl.allowed(ringing, "call-1", "reject", true))
        assertTrue(Types.FLUX_TELEPHONY_ACTION in INCOMING)
    }

    @Test
    fun refusesEveryOtherCallOrAction() {
        assertFalse(CallControl.allowed(ringing, "call-2", "answer", true))
        assertFalse(CallControl.allowed(ringing.copy(waiting = true), "call-1", "answer", true))
        assertFalse(CallControl.allowed(ringing.copy(state = LineState.OffHook), "call-1", "answer", true))
        assertFalse(CallControl.allowed(ringing.copy(state = LineState.Idle), "call-1", "answer", true))
        assertFalse(CallControl.allowed(ringing, "call-1", "answer", false))
        assertFalse(CallControl.allowed(ringing, "call-1", "hangup", true))
        assertFalse(CallControl.allowed(ringing, "call-1", null, true))
        assertFalse(CallControl.allowed(ringing, null, "answer", true))
        assertFalse(CallControl.allowed(ringing.copy(id = null), null, "answer", true))
    }
}
