package org.omarchy.flux.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

class DndSyncTest {
    private class Fake(var sync: Boolean = true, var access: Boolean = true, var on: Boolean = false) : DndLogic.Env {
        val log = mutableListOf<String>()
        override fun syncOn() = sync
        override fun access() = access
        override fun isOn() = on
        override fun now() = 0L
        override fun async(block: () -> Unit) = block()
        override fun setFilter(on: Boolean) {
            this.on = on
            log += "set $on"
        }
        override fun forward(on: Boolean, except: String?) {
            log += "forward $on"
        }
        override fun report(by: String, on: Boolean?, to: String?) {
            log += "report $by $on ${to ?: "all"}"
        }
    }

    private fun logic() = DndLogic(DndGuard())

    @Test
    fun setAppliesWithoutForwardAndReportsOnce() {
        val env = Fake()
        logic().onSet(env, true)
        assertEquals(listOf("set true", "report set true all"), env.log)
    }

    @Test
    fun ordinaryDndReportsSync() {
        val env = Fake()
        logic().onPacket(env, "pc1", true)
        assertEquals(listOf("set true", "forward true", "report sync true all"), env.log)
    }

    @Test
    fun linkUpReportsConnectToThatComputer() {
        val env = Fake(on = true)
        logic().onConnected(env, "pc1")
        assertEquals(listOf("report connect true pc1"), env.log)
    }

    @Test
    fun syncOffIgnoresSetAndReportsNull() {
        val env = Fake(sync = false)
        val l = logic()
        l.onSet(env, true)
        assertEquals(listOf("report set null all"), env.log)
        env.log.clear()
        l.onConnected(env, "pc1")
        assertTrue(env.log == listOf("report connect null pc1"))
    }
}
