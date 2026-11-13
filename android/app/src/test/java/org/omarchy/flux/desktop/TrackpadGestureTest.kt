package org.omarchy.flux.desktop

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import org.omarchy.flux.core.RemoteInput
import org.omarchy.flux.desktop.TrackpadGesture.Out

class TrackpadGestureTest {
    // A slop of 10 view pixels, and 1 dp per view pixel.
    private fun pad() = TrackpadGesture(slop = 10f, dpPerPx = 1f)

    private fun f(id: Long, x: Float, y: Float = 0f) = Finger(id, x, y)

    /** Feeds finger lists, 1 event each, at zoom 1, and returns all outputs. */
    private fun TrackpadGesture.feed(vararg events: List<Finger>): List<Out> = events.flatMap { fingers(it, 1f) }

    private val left = Out.Click(RemoteInput.Click.Left)
    private val right = Out.Click(RemoteInput.Click.Right)
    private val middle = Out.Click(RemoteInput.Click.Middle)

    @Test
    fun aTapClicksLeft() {
        val g = pad()
        assertEquals(emptyList<Out>(), g.feed(listOf(f(1, 100f)), listOf(f(1, 104f, 3f))))
        assertTrue("a jitter under the slop still waits for the hold", g.waitsForHold)
        assertEquals(listOf(left), g.end(cancelled = false))
    }

    @Test
    fun aTremblingFingerStillHolds() {
        val g = pad()
        val out = g.feed(listOf(f(1, 100f)), listOf(f(1, 106f)), listOf(f(1, 94f)), listOf(f(1, 104f)))
        assertEquals("the slop is the net distance from the start", emptyList<Out>(), out)
        assertEquals(listOf(Out.Hold(true)), g.holdTimeout())
    }

    @Test
    fun aReplacedFingerInPendingNeverJumpsNorClicks() {
        val g = pad()
        val out = g.feed(listOf(f(1, 0f)), listOf(f(2, 500f)), listOf(f(2, 520f)))
        assertEquals(emptyList<Out>(), out)
        assertEquals(emptyList<Out>(), g.holdTimeout() + g.end(cancelled = false))
    }

    @Test
    fun twoFingersInTheFirstEventTapRight() {
        assertEquals(listOf(right), pad().run { feed(listOf(f(1, 0f), f(2, 50f))) + end(false) })
    }

    @Test
    fun aMotionPastTheSlopMovesWithTheWholeMotion() {
        val g = pad()
        val out = g.feed(listOf(f(1, 0f)), listOf(f(1, 5f)), listOf(f(1, 20f)))
        val (dx, _) = RemoteInput.trackpadMove(20f, 0f, 1f)
        assertEquals(1, out.size)
        assertEquals(dx, (out[0] as Out.Move).dx, 0.01f)
        assertFalse(g.waitsForHold)
        assertEquals("a move ends without a click", emptyList<Out>(), g.end(cancelled = false))
    }

    @Test
    fun holdThenLiftPressesAndReleases() {
        val g = pad()
        g.feed(listOf(f(1, 0f)))
        assertEquals(listOf(Out.Hold(true)), g.holdTimeout())
        assertTrue(g.pressed)
        assertEquals(listOf(Out.Hold(false)), g.end(cancelled = false))
        assertFalse(g.pressed)
    }

    @Test
    fun holdThenMoveDrags() {
        val g = pad()
        g.feed(listOf(f(1, 0f)))
        g.holdTimeout()
        val out = g.feed(listOf(f(1, 3f)))
        assertTrue("a small move drags too", out.single() is Out.Move)
        assertTrue(g.pressed)
        assertEquals(listOf(Out.Hold(false)), g.end(cancelled = false))
    }

    @Test
    fun aSecondFingerDuringAHoldIsIgnored() {
        val g = pad()
        g.feed(listOf(f(1, 0f)))
        g.holdTimeout()
        val out = g.feed(listOf(f(1, 0f), f(2, 50f)), listOf(f(1, 0f), f(2, 90f)), listOf(f(1, 0f)))
        assertEquals("no scroll, no click", emptyList<Out>(), out)
        assertTrue(g.pressed)
        assertEquals(listOf(Out.Hold(false)), g.end(cancelled = false))
    }

    @Test
    fun theFirstFingerLiftingEndsTheHold() {
        val g = pad()
        g.feed(listOf(f(1, 0f)))
        g.holdTimeout()
        assertEquals(listOf(Out.Hold(false)), g.feed(listOf(f(1, 0f), f(2, 50f)), listOf(f(2, 50f))))
        assertFalse(g.pressed)
        assertEquals("the release goes once", emptyList<Out>(), g.feed(listOf(f(2, 90f))) + g.end(cancelled = false))
    }

    @Test
    fun twoAndThreeFingerTapsClickWhileTheFingersLiftOneByOne() {
        assertEquals(listOf(right), pad().run { feed(listOf(f(1, 0f)), listOf(f(1, 0f), f(2, 50f)), listOf(f(2, 50f))) + end(false) })
        assertEquals(
            listOf(middle),
            pad().run { feed(listOf(f(1, 0f)), listOf(f(1, 0f), f(2, 50f), f(3, 90f)), listOf(f(1, 0f), f(2, 50f)), listOf(f(1, 0f))) + end(false) },
        )
        assertEquals(
            listOf(middle),
            pad().run {
                feed(
                    listOf(f(1, 0f)), listOf(f(1, 0f), f(2, 50f)), listOf(f(1, 0f), f(2, 50f), f(3, 90f)),
                    listOf(f(1, 0f), f(2, 50f)), listOf(f(1, 0f)),
                ) + end(false)
            },
        )
    }

    @Test
    fun aMotionOrANewFingerAfterALiftCancelsTheTap() {
        val moved = pad().run { feed(listOf(f(1, 0f)), listOf(f(1, 0f), f(2, 50f)), listOf(f(2, 50f)), listOf(f(2, 80f))) + end(false) }
        assertEquals(emptyList<Out>(), moved)
        val landed = pad().run { feed(listOf(f(1, 0f)), listOf(f(1, 0f), f(2, 50f)), listOf(f(2, 50f)), listOf(f(2, 50f), f(3, 90f))) + end(false) }
        assertEquals(emptyList<Out>(), landed)
    }

    @Test
    fun twoFingersTogetherScroll() {
        val out = pad().feed(listOf(f(1, 0f)), listOf(f(1, 0f), f(2, 50f)), listOf(f(1, 0f, 20f), f(2, 50f, 20f)), listOf(f(1, 0f, 30f), f(2, 50f, 30f)))
        assertTrue(out.isNotEmpty())
        assertTrue("no zoom", out.all { it is Out.Scroll })
        assertEquals(10f * RemoteInput.TOUCHPAD_SCROLL_SPEED, (out.last() as Out.Scroll).dy, 0.001f)
    }

    @Test
    fun aSpreadWithOneFingerStillZooms() {
        val out = pad().feed(listOf(f(1, 0f)), listOf(f(1, 0f), f(2, 50f)), listOf(f(1, 0f), f(2, 80f)), listOf(f(1, 0f), f(2, 100f)))
        assertTrue(out.isNotEmpty())
        assertTrue(out.all { it is Out.Zoom })
        assertTrue((out.last() as Out.Zoom).factor > 1f)
    }

    @Test
    fun aPinchInZoomsOut() {
        val out = pad().feed(listOf(f(1, 0f)), listOf(f(1, 0f), f(2, 100f)), listOf(f(1, 15f), f(2, 85f)), listOf(f(1, 25f), f(2, 75f)))
        assertTrue(out.isNotEmpty())
        assertTrue(out.all { it is Out.Zoom && it.factor < 1f })
    }

    @Test
    fun threeFingersPanAndAThirdFingerLandingDoesNot() {
        val g = pad()
        val landing = g.feed(listOf(f(1, 0f)), listOf(f(1, 0f), f(2, 50f)), listOf(f(1, 0f), f(2, 50f), f(3, 200f)))
        assertEquals("the center jumps, but nothing pans", emptyList<Out>(), landing)
        val out = g.feed(listOf(f(1, 0f, 20f), f(2, 50f, 20f), f(3, 200f, 20f)), listOf(f(1, 0f, 30f), f(2, 50f, 30f), f(3, 200f, 30f)))
        assertTrue(out.isNotEmpty())
        assertTrue(out.all { it is Out.Pan })
        assertEquals(10f, (out.last() as Out.Pan).dy, 0.001f)
        assertEquals("a pan ends without a click", emptyList<Out>(), g.end(false))
    }

    @Test
    fun aMoveThenASecondFingerNeverClicks() {
        val out = pad().run { feed(listOf(f(1, 0f)), listOf(f(1, 30f)), listOf(f(1, 30f), f(2, 80f)), listOf(f(2, 80f))) + end(false) }
        assertTrue(out.single() is Out.Move)
    }

    @Test
    fun afterAGestureALiftAndMoreMotionDoNothing() {
        val scroll = listOf(listOf(f(1, 0f)), listOf(f(1, 0f), f(2, 50f)), listOf(f(1, 0f, 20f), f(2, 50f, 20f)))
        val pinch = listOf(listOf(f(1, 0f)), listOf(f(1, 0f), f(2, 50f)), listOf(f(1, 0f), f(2, 90f)))
        val pan = listOf(listOf(f(1, 0f)), listOf(f(1, 0f), f(2, 50f), f(3, 90f)), listOf(f(1, 0f, 20f), f(2, 50f, 20f), f(3, 90f, 20f)))
        for (start in listOf(scroll, pinch, pan)) {
            val g = pad()
            g.feed(*start.toTypedArray())
            val rest = start.last().drop(1)
            val after = g.feed(rest, rest.map { it.copy(y = it.y + 40f) }) + g.end(false)
            assertEquals(emptyList<Out>(), after)
        }
    }

    @Test
    fun aCancelNeverClicksAndReleasesOnce() {
        val states = listOf(
            listOf(listOf(f(1, 0f))),
            listOf(listOf(f(1, 0f)), listOf(f(1, 30f))),
            listOf(listOf(f(1, 0f)), listOf(f(1, 0f), f(2, 50f))),
            listOf(listOf(f(1, 0f)), listOf(f(1, 0f), f(2, 50f), f(3, 90f))),
            listOf(listOf(f(1, 0f)), listOf(f(1, 0f), f(2, 50f)), listOf(f(2, 50f))),
            listOf(listOf(f(1, 0f)), listOf(f(1, 0f), f(2, 50f)), listOf(f(1, 0f, 20f), f(2, 50f, 20f))),
            listOf(listOf(f(1, 0f)), listOf(f(1, 0f), f(2, 50f)), listOf(f(1, 0f), f(2, 90f))),
            listOf(listOf(f(1, 0f)), listOf(f(1, 0f), f(2, 50f), f(3, 90f)), listOf(f(1, 0f, 20f), f(2, 50f, 20f), f(3, 90f, 20f))),
            // Done: a scroll, then a finger lifts.
            listOf(listOf(f(1, 0f)), listOf(f(1, 0f), f(2, 50f)), listOf(f(1, 0f, 20f), f(2, 50f, 20f)), listOf(f(2, 50f, 20f))),
        )
        for (s in states) {
            val g = pad()
            g.feed(*s.toTypedArray())
            assertEquals(emptyList<Out>(), g.end(cancelled = true))
        }
        val held = pad()
        held.feed(listOf(f(1, 0f)))
        held.holdTimeout()
        held.feed(listOf(f(1, 30f)))
        assertEquals(listOf(Out.Hold(false)), held.end(cancelled = true))
        assertFalse(held.pressed)
        assertEquals("once", emptyList<Out>(), held.end(cancelled = true))
    }

    @Test
    fun slowMovesAtZoom6AddUp() {
        val g = pad()
        g.feed(listOf(f(1, 0f)), listOf(f(1, 20f)))
        var x = 20f
        var sent = 0f
        var expected = 0f
        // 0.02 dp at 6× is under 0.01 pixel, less than a packet carries.
        assertTrue(RemoteInput.trackpadMove(0.02f, 0f, 6f).first < 0.01f)
        assertEquals("2 steps stay under 0.01 and send nothing", emptyList<Out>(), g.fingers(listOf(f(1, 20.02f)), 6f) + g.fingers(listOf(f(1, 20.04f)), 6f))
        x = 20.04f
        expected = 2 * RemoteInput.trackpadMove(0.02f, 0f, 6f).first
        repeat(2000) {
            x += 0.02f
            expected += RemoteInput.trackpadMove(0.02f, 0f, 6f).first
            g.fingers(listOf(f(1, x)), 6f).forEach { sent += (it as Out.Move).dx }
        }
        assertTrue("small moves become packets", sent > 0f)
        assertEquals("the remainder is kept", expected, sent, 0.011f)
    }
}
