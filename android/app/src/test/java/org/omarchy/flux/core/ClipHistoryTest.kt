package org.omarchy.flux.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import org.omarchy.flux.protocol.INCOMING
import org.omarchy.flux.protocol.OUTGOING
import org.omarchy.flux.protocol.Packet
import org.omarchy.flux.protocol.Types
import org.omarchy.flux.protocol.bodyOf

class ClipHistoryTest {
    private fun answer(vararg fields: Pair<String, Any?>) = Packet(Types.FLUX_CLIPBOARD_HISTORY, bodyOf(*fields))

    private fun entry(id: String, text: String, vararg more: Pair<String, Any?>) =
        mapOf("id" to id, "text" to text, "sent" to true, "time" to 1_763_123_456L) + more

    @Test
    fun capabilityIsInBothLists() {
        assertTrue(Types.FLUX_CLIPBOARD_HISTORY in INCOMING)
        assertTrue(Types.FLUX_CLIPBOARD_HISTORY in OUTGOING)
    }

    @Test
    fun parseListReadsEntries() {
        val p = answer(
            "request" to 7L, "state" to "ok", "more" to true, "extra" to 1,
            "entries" to listOf(entry("a", "git status"), mapOf("text" to "no id"), entry("b", "x".repeat(1024), "truncated" to true, "size" to 4096)),
        )
        val a = ClipHistory.parseList(p, "git")!!
        assertEquals(7L, a.request)
        assertEquals("git", a.query)
        assertEquals("ok", a.state)
        assertTrue(a.more)
        assertEquals(listOf("a", "b"), a.entries.map { it.id })
        assertEquals(1_763_123_456_000L, a.entries[0].time)
        assertTrue(a.entries[0].sent)
        assertTrue(a.entries[1].truncated)
        assertEquals(4096, a.entries[1].size)
    }

    @Test
    fun parseListStates() {
        assertEquals("disabled", ClipHistory.parseList(answer("request" to 1L, "state" to "disabled"), "")!!.state)
        assertNull(ClipHistory.parseList(answer("request" to 1L, "state" to "ok", "entry" to entry("a", "t")), ""))
        assertNull(ClipHistory.parseList(answer("request" to 1L, "state" to "not_found"), ""))
        assertNull(ClipHistory.parseList(answer("state" to "ok"), ""))
    }

    @Test
    fun linkAndCopyable() {
        fun e(text: String, truncated: Boolean = false, size: Int = 0) = HistoryEntry("i", text, false, 0, size, truncated)
        assertEquals("https://omarchy.org/flux", e("https://omarchy.org/flux").link)
        assertNull(e("https://omarchy.org/flux", truncated = true, size = 2000).link)
        assertNull(e("file:///etc/passwd").link)
        assertNull(e("yay -S flux-git").link)
        assertTrue(e("x", truncated = true, size = 256 * 1024).copyable)
        assertFalse(e("x", truncated = true, size = 256 * 1024 + 1).copyable)
    }

    @Test
    fun staleListAnswersAreIgnored() {
        var sent = 0
        val first = ClipHistory.list("list-pc", "a") { sent++; true }
        val second = ClipHistory.list("list-pc", "ab") { sent++; true }
        assertEquals(2, sent)
        assertEquals(ClipHistory.Kind.Stale, ClipHistory.kind("list-pc", first))
        assertEquals(ClipHistory.Kind.List, ClipHistory.kind("list-pc", second))
        // The same id from another computer is stale.
        assertEquals(ClipHistory.Kind.Stale, ClipHistory.kind("other-pc", second))
        // Only the current request's answer is taken, with its query.
        assertNull(ClipHistory.acceptList("list-pc", answer("request" to first, "state" to "ok", "entries" to emptyList<Any>())))
        assertNull(ClipHistory.acceptList("other-pc", answer("request" to second, "state" to "ok", "entries" to emptyList<Any>())))
        assertNull(ClipHistory.acceptList("list-pc", answer("request" to second, "state" to "ok", "entry" to entry("a", "t"))))
        val a = ClipHistory.acceptList("list-pc", answer("request" to second, "state" to "ok", "entries" to listOf(entry("a", "ab c"))))!!
        assertEquals("ab", a.query)
        assertEquals(listOf("a"), a.entries.map { it.id })
    }

    @Test
    fun copyMessages() {
        assertEquals("The text is too large to copy", ClipHistory.copyMessage("too_large", "desk"))
        assertEquals("Clipboard is off for this phone on desk", ClipHistory.copyMessage("disabled", "desk"))
        assertEquals("desk no longer has this entry", ClipHistory.copyMessage("not_found", "desk"))
        assertEquals("desk no longer has this entry", ClipHistory.copyMessage(null, "desk"))
    }

    @Test
    fun onlyTheCurrentCopyIsTaken() {
        var first = 0L
        assertTrue(ClipHistory.copy("copy-pc", "e1") { first = it.long("request")!!; true })
        var request = 0L
        ClipHistory.copy("copy-pc", "e2") { request = it.long("request")!!; it.string("entry") == "e2" }
        // The newer copy replaced the older one.
        assertFalse(ClipHistory.takeCopy("copy-pc", first))
        // An older copy, another computer, and a taken copy are refused.
        assertFalse(ClipHistory.takeCopy("other-pc", request))
        assertEquals(ClipHistory.Kind.Copy, ClipHistory.kind("copy-pc", request))
        assertTrue(ClipHistory.takeCopy("copy-pc", request))
        assertFalse(ClipHistory.takeCopy("copy-pc", request))
    }

    @Test
    fun forgetAndFailedSendDropTheCopy() {
        var request = 0L
        ClipHistory.copy("forget-pc", "e1") { request = it.long("request")!!; true }
        ClipHistory.forget()
        assertFalse(ClipHistory.takeCopy("forget-pc", request))
        assertFalse(ClipHistory.copy("forget-pc", "e2") { request = it.long("request")!!; false })
        assertFalse(ClipHistory.takeCopy("forget-pc", request))
    }

    @Test
    fun queryLimitCountsUtf8Bytes() {
        assertTrue(ClipHistory.fits("a".repeat(1024)))
        assertFalse(ClipHistory.fits("a".repeat(1025)))
        // 1020 bytes plus a 4-byte character fit; one more byte doesn't.
        assertTrue(ClipHistory.fits("a".repeat(1020) + "😀"))
        assertFalse(ClipHistory.fits("a".repeat(1021) + "😀"))
    }
}
