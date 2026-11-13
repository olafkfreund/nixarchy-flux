package org.omarchy.flux.core

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class RemoteClipTest {
    private val long = "x".repeat(399)

    private fun clip(label: String?, text: String, device: String = "pc", remote: String = text, from: String = "pc") =
        isRemoteClip(label, text, null, device, from to remote, null)

    @Test
    fun theSameTextFromTheSameComputerMatches() {
        assertTrue(clip("Flux", "hunter2"))
        assertTrue(clip("Flux", long + "ab"))
    }

    @Test
    fun anythingElseStays() {
        assertFalse("another app", clip("Notes", "hunter2"))
        assertFalse("no label", clip(null, "hunter2"))
        assertFalse("another computer", clip("Flux", "hunter2", device = "desk"))
        assertFalse("differs after 399 characters", clip("Flux", long + "a", remote = long + "b"))
        assertFalse("a prefix", clip("Flux", long, remote = long + "…"))
        assertFalse("a cut text", clip("Flux", long + "…", remote = long + "abc"))
        assertFalse("nothing came", isRemoteClip("Flux", "hunter2", null, "pc", null, null))
    }

    @Test
    fun anImageMatchesByItsAddressAndComputer() {
        val uri = "content://org.omarchy.flux.clipboard/clipboard/clip-1.png"
        assertTrue(isRemoteClip("Flux", null, uri, "pc", null, "pc" to uri))
        assertFalse(isRemoteClip("Flux", null, uri, "desk", null, "pc" to uri))
        assertFalse(isRemoteClip("Flux", null, "$uri.old", "pc", null, "pc" to uri))
        assertFalse("a text doesn't match an image", isRemoteClip("Flux", uri, null, "pc", null, "pc" to uri))
        assertFalse("an image doesn't match a text", isRemoteClip("Flux", null, uri, "pc", "pc" to uri, null))
    }
}
