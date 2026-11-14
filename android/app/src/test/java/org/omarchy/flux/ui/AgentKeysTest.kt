package org.omarchy.flux.ui

import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class AgentKeysTest {
    private val gap = 6.dp

    @Test
    fun codexGetsTheShiftedArrowsBeforeEnter() {
        assertEquals(listOf("esc", "tab", "up", "down", "shift+left", "shift+right", "enter"), dockKeys("codex").map { it.second })
        for (a in listOf("claude", "opencode")) assertEquals(listOf("esc", "tab", "up", "down", "enter"), dockKeys(a).map { it.second })
    }

    @Test
    fun theShiftedKeysHaveNamesAndMoveTheFocus() {
        assertEquals("Shift Left", keyName("shift+left"))
        assertEquals("Shift Right", keyName("shift+right"))
        assertTrue(movesFocus("shift+left"))
        assertTrue(movesFocus("shift+right"))
        for (k in listOf("left", "right", "tab")) assertFalse(k, movesFocus(k))
    }

    private fun check(name: String, shares: List<Float>, widths: List<Dp>, counts: List<Int>) {
        for ((w, n) in widths.zip(counts)) {
            val rows = keyRows(shares, w, gap)
            assertEquals("$name at $w", n, rows.size)
            assertEquals("$name at $w", shares.indices.toList(), rows.flatten())
            for (r in rows) assertTrue("$name at $w fits", keysFitOneRow(r.map { shares[it] }, w, gap))
            if (n > 1) {
                val fewer = shares.indices.chunked((shares.size + n - 2) / (n - 1))
                assertFalse("$name at $w: one row fewer fits", fewer.all { r -> keysFitOneRow(r.map { shares[it] }, w, gap) })
            }
        }
    }

    @Test
    fun theKeyRowsKeepTheMinimumWidth() {
        val dock = listOf(296.dp, 336.dp, 387.dp)
        val live = listOf(300.dp, 340.dp, 391.dp)
        fun ones(n: Int) = List(n) { 1f }
        check("dock other", listOf(1f, 1f, 1f, 1f, 1.5f), dock, listOf(1, 1, 1))
        check("dock codex", ones(6) + 1.5f, dock, listOf(2, 2, 2))
        check("live other", ones(4) + 1.5f + 1f, live, listOf(2, 2, 1))
        check("live codex", ones(6) + 1.5f + 1f, live, listOf(2, 2, 2))
        check("live 3 other", ones(7) + 1.5f + 1f, live, listOf(2, 2, 2))
        check("live 3 codex", ones(9) + 1.5f + 1f, live, listOf(3, 2, 2))
        check("live 9 codex", ones(15) + 1.5f + 1f, live, listOf(4, 3, 3))
    }

    @Test
    fun noKeysAreNoRowsAndOneKeyIsOneRow() {
        assertTrue(keyRows(emptyList(), 300.dp, gap).isEmpty())
        assertEquals(listOf(listOf(0)), keyRows(listOf(1.5f), 40.dp, gap))
    }
}
