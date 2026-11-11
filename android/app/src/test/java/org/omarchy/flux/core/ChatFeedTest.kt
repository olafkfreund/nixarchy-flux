package org.omarchy.flux.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class ChatFeedTest {
    private val blocked = HerdrAgent("w2:p1", "codex", AgentStatus.Blocked, "Migrate")
    private val done = HerdrAgent("w3:p1", "claude", AgentStatus.Done, "Fix test")

    private fun send(id: Long, at: Long) = SendEntry(id, "pc", "omarchy-xps", SendKind.Link, "https://omarchy.org", at = at)

    @Test
    fun theEntriesAreInTimeOrder() {
        val ask = AgentItem("pc", "omarchy-xps", blocked, control = true)
        val fin = AgentItem("pc", "omarchy-xps", done, control = true)
        val pair = PairItem("new", "framework-13")
        val since = mapOf("w2:p1" to 300L, "w3:p1" to 100L)
        val chat = chatItems(
            log = listOf(send(1, 50), send(2, 200)),
            items = listOf(ask, fin, pair),
            since = { _, pane -> since[pane] },
            seen = mapOf(pair.key to 250L),
        )
        assertEquals(listOf("log|1", fin.key, "log|2", pair.key, ask.key), chat.map { it.key })
        assertEquals(300L, chat.last().at)
    }

    @Test
    fun anAgentWithNoStartTakesTheTimeThatTheChatSawIt() {
        val ask = AgentItem("pc", "omarchy-xps", blocked, control = true)
        val chat = chatItems(listOf(send(1, 500)), listOf(ask), since = { _, _ -> null }, seen = mapOf(ask.key to 400L))
        assertEquals(listOf(ask.key, "log|1"), chat.map { it.key })
    }

    @Test
    fun theClockKeepsTheFirstTimeAndForgetsWhatIsGoneForLong() {
        val clock = SeenClock(graceMs = 100)
        assertEquals(mapOf("a" to 10L), clock.update(listOf("a"), 10))
        assertEquals(mapOf("a" to 10L, "b" to 20L), clock.update(listOf("a", "b"), 20))
        assertEquals(mapOf("b" to 20L), clock.update(listOf("b"), 30))
        assertEquals("an item that comes back soon keeps its time", mapOf("a" to 10L, "b" to 20L), clock.update(listOf("a", "b"), 40))
        clock.update(listOf("b"), 100)
        assertEquals("an item that is gone for long is new", mapOf("a" to 201L, "b" to 20L), clock.update(listOf("a", "b"), 201))
        assertEquals("an item that stays keeps its time without updates", mapOf("a" to 201L, "b" to 20L), clock.update(listOf("a", "b"), 9_999))
    }

    @Test
    fun aTellChoiceOpensAReply() {
        assertTrue(isTellChoice(AgentChoice("3", "No, and tell Codex what to do differently (esc)")))
        assertTrue(isTellChoice(AgentChoice("3", "No, and tell Claude what to do differently")))
        assertFalse(isTellChoice(AgentChoice("1", "Yes")))
        assertFalse(isTellChoice(AgentChoice("2", "Yes, and do not ask again for bin/migrate commands")))
    }
}
