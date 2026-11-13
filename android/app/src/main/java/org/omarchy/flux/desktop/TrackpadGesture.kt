package org.omarchy.flux.desktop

import org.omarchy.flux.core.RemoteInput
import kotlin.math.abs
import kotlin.math.hypot
import kotlin.math.max
import kotlin.math.roundToInt

/** A finger on the view: its pointer id and its position in view pixels. */
data class Finger(val id: Long, val x: Float, val y: Float)

/**
 * The gestures of the trackpad mode, for 1 touch from the first finger to
 * the last lift. 1 finger moves the pointer, and a tap clicks. A finger
 * that holds still presses the left button until it lifts. 2 fingers
 * scroll or pinch, and a tap with 2 fingers clicks the right button. 3
 * fingers move the zoomed view, and a tap with 3 fingers clicks the middle
 * button. [slop] is the touch slop in view pixels, [dpPerPx] turns view
 * pixels into dp.
 */
class TrackpadGesture(private val slop: Float, private val dpPerPx: Float) {
    sealed interface Out {
        /** A relative pointer move, in computer pixels, with at most 2 decimals. */
        data class Move(val dx: Float, val dy: Float) : Out

        /** The fingers' motion in scroll units. A positive value is a motion down or to the right. */
        data class Scroll(val dx: Float, val dy: Float) : Out
        data class Click(val button: RemoteInput.Click) : Out
        data class Hold(val down: Boolean) : Out

        /** View pixels. */
        data class Pan(val dx: Float, val dy: Float) : Out

        /** View pixels, as DesktopViewport.zoom takes them. */
        data class Zoom(val factor: Float, val x: Float, val y: Float, val dx: Float, val dy: Float) : Out
    }

    private enum class State { Pending, Move, Held, Two, Three, TapLift, Scroll, Pinch, Pan, Done }

    private var state = State.Pending
    private var firstId = 0L
    private var startX = 0f
    private var startY = 0f
    private var prev: List<Finger> = emptyList()
    private var startCx = 0f
    private var startCy = 0f
    private var startSpan = 0f
    // The fingers when the first finger of an undecided tap lifted.
    private var tapFrom: List<Finger> = emptyList()
    private var most = 0
    private var moved = false
    // The pointer motion that a packet can't carry yet: it has 2 decimals, and the computer drops a move of 0.
    private var restX = 0f
    private var restY = 0f

    /** True while 1 finger rests within the slop and the hold can still start. */
    val waitsForHold: Boolean get() = state == State.Pending && prev.size == 1

    /** True from Hold(true) until Hold(false) is returned. */
    var pressed = false
        private set

    /** The fingers that are down now; [zoom] is the view's scale. */
    fun fingers(down: List<Finger>, zoom: Float): List<Out> {
        if (down.isEmpty()) return emptyList()
        val before = prev
        prev = down
        if (before.isEmpty()) {
            firstId = down[0].id
            startX = down[0].x
            startY = down[0].y
            most = down.size
            if (down.size >= 2) multi(down)
            return emptyList()
        }
        most = max(most, down.size)
        return if (down.map { it.id } == before.map { it.id }) motion(before, down, zoom) else change(before, down)
    }

    /** The finger held still for the hold time. */
    fun holdTimeout(): List<Out> {
        if (!waitsForHold) return emptyList()
        state = State.Held
        pressed = true
        return listOf(Out.Hold(true))
    }

    /** All fingers are up. [cancelled] when Android cancelled the touch. */
    fun end(cancelled: Boolean): List<Out> {
        val out = when {
            pressed -> Out.Hold(false)
            cancelled || moved -> null
            state == State.Pending -> Out.Click(RemoteInput.Click.Left)
            state == State.Two -> Out.Click(RemoteInput.Click.Right)
            state == State.Three -> Out.Click(RemoteInput.Click.Middle)
            state == State.TapLift -> Out.Click(if (most >= 3) RemoteInput.Click.Middle else RemoteInput.Click.Right)
            else -> null
        }
        pressed = false
        state = State.Done
        return listOfNotNull(out)
    }

    // A finger landed, lifted, or was replaced. The new positions are the start of the next motion, so nothing moves.
    private fun change(before: List<Finger>, down: List<Finger>): List<Out> {
        val lifted = down.size < before.size
        val landed = down.size > before.size
        when (state) {
            // A replaced finger in Pending has another start, so the touch ends.
            State.Pending -> if (down.size >= 2) multi(down) else state = State.Done
            State.Move -> if (down.size >= 2) {
                moved = true
                multi(down)
            }
            State.Held -> if (down.none { it.id == firstId }) {
                pressed = false
                state = State.Done
                return listOf(Out.Hold(false))
            }
            State.Two, State.Three -> if (lifted) {
                state = State.TapLift
                tapFrom = before
            } else {
                multi(down)
            }
            State.TapLift -> if (!lifted) state = State.Done
            State.Scroll, State.Pinch, State.Pan -> if (!landed) state = State.Done
            State.Done -> Unit
        }
        return emptyList()
    }

    private fun multi(down: List<Finger>) {
        state = if (down.size >= 3) State.Three else State.Two
        startCx = cx(down)
        startCy = cy(down)
        startSpan = span(down)
    }

    private fun motion(before: List<Finger>, down: List<Finger>, zoom: Float): List<Out> {
        when (state) {
            State.Pending -> {
                val f = down[0]
                if (hypot(f.x - startX, f.y - startY) <= slop) return emptyList()
                state = State.Move
                return move(f.x - startX, f.y - startY, zoom)
            }
            State.Move -> return move(down[0].x - before[0].x, down[0].y - before[0].y, zoom)
            State.Held -> {
                val a = before.firstOrNull { it.id == firstId } ?: return emptyList()
                val b = down.firstOrNull { it.id == firstId } ?: return emptyList()
                return move(b.x - a.x, b.y - a.y, zoom)
            }
            State.Two -> {
                val dc = hypot(cx(down) - startCx, cy(down) - startCy)
                val ds = abs(span(down) - startSpan)
                if (dc <= slop && ds <= slop) return emptyList()
                state = if (ds >= dc * PINCH_RATIO) State.Pinch else State.Scroll
            }
            State.Three -> {
                if (hypot(cx(down) - startCx, cy(down) - startCy) <= slop) return emptyList()
                state = State.Pan
            }
            State.TapLift -> {
                val far = down.any { f -> tapFrom.firstOrNull { it.id == f.id }?.let { hypot(f.x - it.x, f.y - it.y) > slop } ?: true }
                if (far) state = State.Done
                return emptyList()
            }
            else -> Unit
        }
        val dx = cx(down) - cx(before)
        val dy = cy(down) - cy(before)
        return when (state) {
            State.Scroll -> listOf(Out.Scroll(dx * dpPerPx * RemoteInput.TOUCHPAD_SCROLL_SPEED, dy * dpPerPx * RemoteInput.TOUCHPAD_SCROLL_SPEED))
            State.Pinch -> {
                val last = span(before)
                listOf(Out.Zoom(if (last > 0f) span(down) / last else 1f, cx(before), cy(before), dx, dy))
            }
            State.Pan -> listOf(Out.Pan(dx, dy))
            else -> emptyList()
        }
    }

    // The motion of [dx], [dy] view pixels as a pointer move. The part that a packet can't carry waits for the next move.
    private fun move(dx: Float, dy: Float, zoom: Float): List<Out> {
        val (mx, my) = RemoteInput.trackpadMove(dx * dpPerPx, dy * dpPerPx, zoom)
        restX += mx
        restY += my
        if (abs(restX) < 0.01f && abs(restY) < 0.01f) return emptyList()
        val sx = (restX * 100).roundToInt() / 100f
        val sy = (restY * 100).roundToInt() / 100f
        restX -= sx
        restY -= sy
        return listOf(Out.Move(sx, sy))
    }

    private fun cx(f: List<Finger>) = f.sumOf { it.x.toDouble() }.toFloat() / f.size
    private fun cy(f: List<Finger>) = f.sumOf { it.y.toDouble() }.toFloat() / f.size

    // The mean distance of the fingers from their center, as the touch mode's span.
    private fun span(f: List<Finger>): Float {
        val x = cx(f)
        val y = cy(f)
        return f.sumOf { hypot(it.x - x, it.y - y).toDouble() }.toFloat() / f.size
    }

    private companion object {
        // In Two, a span change of at least half the center's motion is a pinch.
        const val PINCH_RATIO = 0.5f
    }
}
