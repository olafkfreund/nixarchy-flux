package org.omarchy.flux.ui

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import org.omarchy.flux.core.ApproveRequest

class ApproveFastPathTest {
    private fun req(kind: ApproveRequest.Kind = ApproveRequest.Kind.Approve, service: String = "sudo") = ApproveRequest(
        computerId = "pc", computerName = "omarchy-xps", id = "r1", kind = kind,
        host = "omarchy-xps", user = "dev", service = if (kind == ApproveRequest.Kind.Enroll) "" else service,
        tty = "pts/3", rhost = "", time = 1, nonce = "", timeoutSeconds = 20,
    )

    private fun fast(
        r: ApproveRequest = req(), tapFor: String? = "pc/r1", on: Boolean = true,
        locked: Boolean = false, done: String? = null, asking: Boolean = true,
    ) = fastPath(r, tapFor, on, locked, done, asking)

    @Test
    fun aTapOnAnApprovalTakesTheFastPath() {
        assertTrue(fast())
        assertTrue(fast(req(service = "polkit-1")))
        assertTrue(fast(req(service = "hyprlock")))
    }

    @Test
    fun anyMissingConditionKeepsTheApproveScreen() {
        assertFalse("an enrollment", fast(req(ApproveRequest.Kind.Enroll)))
        assertFalse("another service", fast(req(service = "login")))
        assertFalse("no tap", fast(tapFor = null))
        assertFalse("a tap on another request", fast(tapFor = "pc/r0"))
        assertFalse("the switch is off", fast(on = false))
        assertFalse("the phone is locked", fast(locked = true))
        assertFalse("the request took it already", fast(done = "pc/r1"))
        assertFalse("the screen shows a result", fast(asking = false))
    }
}
