---
status: draft
issue: 26
spec: spec/2026-11-13-26-trackpad-mode.md
coders:
  1: coder   # two RemoteInput helpers, one constant moved and one test, given in full below
  2: coder   # the TrackpadGesture class and its 19 tests, given in full and already run on the JVM; keeps codex and agy independent reviewers of the state machine
  3: coder   # the touch mode's release flag and cancel skip, a short edit given in full
  4: coder   # the trackpad modifier, the switch, the pointer centering and the scroll setting, given in full
  5: coder   # the rail's scrolling column, one block given in full
  6: coder   # one docs section, given in full
reviewers:
  - codex    # reads the branch diff with `codex review --base master`; strongest on the gesture state machine and the release paths
  - agy      # independent read of the diff against this plan, including the Compose pointer input, the effects and the layout
---

# Plan: Trackpad mode on the Android remote desktop

## The approved decisions

From the intent and the spec, both approved:

- **A trackpad switch** next to Keys on the remote desktop. With it on, the
  video works as a touchpad:
  - 1 finger moves the pointer, relative, divided by the view's zoom;
  - a tap clicks at the pointer, two taps double-click, no pointer jump;
  - hold still, then lift: a left click (`singlehold` then `singlerelease`);
  - hold still, then move: a drag, until the first finger lifts;
  - 2 fingers scroll with the touchpad screen's speed and direction
    setting, or pinch to zoom the view; a 2-finger tap right-clicks;
  - 3 fingers pan a zoomed view; a 3-finger tap middle-clicks;
  - the taps survive fingers lifting one by one (`TapLift`);
  - works over the black bars too (no `toVideo` on a gesture).
- **No saved setting.** The mode lasts one visit of the screen
  (`rememberSaveable`, so a rotation keeps it). It is off without remote
  input (`pad = trackpad && control`), and comes back when input returns
  in the same visit.
- **The pointer goes to the view's center** when trackpad turns on, when
  the video becomes ready (also a stream restart) and when the monitor
  switches: `RemoteInput.at` with the center's video position.
- **Focus.** A trackpad click and turning trackpad on clear the touch
  mode's `focus`, so the view keeps its center when the keys or the phone
  keyboard open.
- **Shared code** with the touchpad screen: only pure helpers in
  `RemoteInput` (`trackpadMove`, `TOUCHPAD_SCROLL_SPEED`). `Modifier.touchpad`
  stays unchanged.
- **The gesture logic is a plain class**, `desktop/TrackpadGesture.kt`,
  with JVM tests. The modifier only turns pointer events into calls and
  carries out the results. Small moves add up: a move goes out only when
  it reaches 0.01 pixel, and the rest waits for the next one.
- **Releases.** Every `Hold(false)` and every coroutine end with the
  button down calls `release`, which sends `hold(false)` without the
  control gate and without a toast. A cancelled touch (all-up event with
  consumed changes) never clicks.
- **Touch mode fixes.** `desktopGestures` gets the same release path: one
  flag, the click skipped on a cancel, and one release in a `finally`.
  Nothing else in the touch gestures changes.
- **Layout.** The landscape rail scrolls when its buttons don't fit and
  keeps them at the bottom when they do. The narrow layout gets the
  switch in the top bar through the same `buttons()`, and a trackpad hint.
- **Docs.** A section "Use the trackpad (Android)" in
  `docs/remote-desktop.md`.
- **#2** is closed after the merge and the device tests, with the spec's
  section 7 comment.
- **Unchanged:** `fluxd`, the protocol, the desktop app, iOS, macOS,
  `Modifier.touchpad` and the touchpad screen's gestures (except the
  scroll constant's home).

Refinements of the spec, each recorded in Corrections when its step is
done:

- **P1 (step 4).** No new `TRACKPAD_HOLD_MS`: the modifier uses the
  existing `DESKTOP_HOLD_MS` (`DesktopScreen.kt:100`, 450 ms), the value
  the spec asks for.
- **P2 (steps 3, 4).** `desktopGestures` gets `control` as a
  `State<Boolean>` (`rememberUpdatedState(control)`), not a `Boolean`.
  `pointerInput("touch", viewport)` doesn't restart when `control`
  changes, so a captured `Boolean` would go stale.
- **P3 (step 4).** The modifier's local function is `act`, not `run`, so
  it doesn't shadow the standard `run`.
- **P4 (step 2).** As the spec says, a `Move` goes out only when the
  kept motion reaches 0.01 on an axis. It then carries the value rounded
  to 2 decimals, as the packet would, and the class keeps exactly what
  the rounding left, so the sum of the packets follows the finger.
- **P5 (step 2).** The remainder test moves 0.02 dp per event, not the
  spec's 0.2 dp: at 6×, 0.2 dp gives 0.044 pixel, which a packet already
  carries, so it would not test the remainder. 0.02 dp gives 0.0043.
- **P6 (step 2).** A replaced finger in `Pending` (a new id, still 1
  finger) ends the touch (`Done`): its start belongs to the old finger,
  so a move would jump and a hold would lose its finger. A replaced
  finger in `Scroll`, `Pinch` or `Pan` ends the gesture, and in
  `TapLift` it ends the tap; the spec leaves these open. In `Two` and
  `Three` it re-bases, as the spec says.
- **P7 (step 4).** The modifier gives the class every finger of the first
  event (`currentEvent`), not only `first`, so two fingers that land in
  the same event are a 2-finger touch.
- **D1 (step 9).** The spec asks the owner to turn remote desktop and
  input on in p620's own config. The device tests use the test `fluxd`,
  whose config the test made; the lead was told they may change it.
  `fluxd` doesn't log button events (`internal/core/input.go` logs only
  ignored, dropped and failed input, `:238,277,350`), so release counts
  come from `wev`, which the spec offers; the owner confirms both before
  step 9.

## Steps

Steps 1-6 run in order on the branch, and the lead commits each one before
the next starts. Steps 3, 4 and 5 edit the same file (`DesktopScreen.kt`):
the line numbers below are from the current code, so steps 4 and 5 find
each place by the function or the code quoted, not by the number. There
is no local Android SDK, so CI in step 8 is the first compile; the class
and test of step 2 were compiled and run with kotlinc 2.4.21 against a
copy of the `RemoteInput` helpers of step 1 (19 of 19 pass).

Paths below are under `android/app/src/main/java/org/omarchy/flux/` unless
they start with `android/app/src/test/` or `docs/`.

1. **The shared helpers: `core/RemoteInput.kt`, `ui/TouchpadScreen.kt`,
   `RemoteInputTest.kt`**
   - `core/RemoteInput.kt`: after `import kotlin.math.abs` (`:6`), add
     `import kotlin.math.hypot`. After `MAX_BOOST` (`:167`), add:
     ```kotlin

         /** The scroll speed of the touchpad screen and the trackpad mode, in scroll units per dp of finger motion. */
         const val TOUCHPAD_SCROLL_SPEED = 1.2f

         /** The pointer motion for a finger motion of [dx], [dy] dp on a view zoomed by [zoom]. */
         fun trackpadMove(dx: Float, dy: Float, zoom: Float): Pair<Float, Float> {
             val s = pointerScale(hypot(dx, dy)) / zoom.coerceAtLeast(1f)
             return dx * s to dy * s
         }
     ```
   - `ui/TouchpadScreen.kt`: delete `SCROLL_SPEED` and its KDoc
     (`:87-88`) and the blank line after them. At `:532`, replace both
     `SCROLL_SPEED` with `RemoteInput.TOUCHPAD_SCROLL_SPEED`
     (`RemoteInput` is imported at `:75`).
   - `android/app/src/test/java/org/omarchy/flux/core/RemoteInputTest.kt`:
     after `fastFingersMoveFurther` (`:161-167`), add:
     ```kotlin
     @Test
     fun trackpadMoveIsTheTouchpadMoveDividedByTheZoom() {
         val s = RemoteInput.pointerScale(5f)
         val (x1, y1) = RemoteInput.trackpadMove(3f, 4f, 1f)
         assertEquals("at zoom 1 it is the touchpad screen's move", 3f * s, x1, 0.0001f)
         assertEquals(4f * s, y1, 0.0001f)
         val (x4, y4) = RemoteInput.trackpadMove(3f, 4f, 4f)
         assertEquals(x1 / 4, x4, 0.0001f)
         assertEquals(y1 / 4, y4, 0.0001f)
         assertEquals("a zoom under 1 counts as 1", x1, RemoteInput.trackpadMove(3f, 4f, 0.5f).first, 0.0001f)
     }
     ```

   Verify: CI in step 8;
   `git diff -- android/app/src/main/java/org/omarchy/flux/ui/TouchpadScreen.kt`
   shows only the constant's removal and `:532`.

   Traps:
   - The touchpad screen's own move (`TouchpadScreen.kt:520-524`) stays as
     it is; don't route it through `trackpadMove` (spec, Verification 5).
   - `SCROLL_SPEED` is used only at `TouchpadScreen.kt:532`
     (`grep -rn SCROLL_SPEED android`).

2. **The gesture class: `desktop/TrackpadGesture.kt` (new),
   `TrackpadGestureTest.kt` (new)**
   - New `desktop/TrackpadGesture.kt`:
     ```kotlin
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
     ```
   - New `android/app/src/test/java/org/omarchy/flux/desktop/TrackpadGestureTest.kt`:
     ```kotlin
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
     ```

   Verify: CI in step 8 (`TrackpadGestureTest`, 19 tests). Already run on
   the JVM with kotlinc 2.4.21 and a copy of step 1's helpers: 19 pass.
   With the remainder dropped, or with a threshold of 0.005 instead of
   0.01, `slowMovesAtZoom6AddUp` fails.

   Traps:
   - The class has no Android or Compose import; keep it that way, so the
     JVM tests run it.
   - Finger sets are compared by their id lists in order, as the touch
     mode does (`DesktopScreen.kt:563,592`); Compose keeps the order of
     the changes.
   - `end()` sets the state to `Done` and clears `pressed`; the modifier
     calls it once per touch.
   - The table in the spec (`spec/2026-11-13-26-trackpad-mode.md:92-103`)
     is the reference for each transition; P6 adds the replaced fingers.

3. **Touch mode release: `ui/DesktopScreen.kt`**
   - In `RemoteDesktop`, after `send` (`:396-403`), add:
     ```kotlin
         // The release skips the control gate of send, so a button pressed with control is released after control ends.
         val release by rememberUpdatedState { RemoteInput.send(FluxCore, d.id, RemoteInput.hold(false)); Unit }
         // The gestures start once per key, so they read control through this state.
         val controlState = rememberUpdatedState(control)
     ```
   - The `desktopGestures` call (`:420`) becomes:
     ```kotlin
     Modifier.desktopGestures(viewport, controlState, { viewport.value = it }, { send(it) }, { release() }, { clicked(it) }) { haptic.performHapticFeedback(HapticFeedbackType.LongPress) }
     ```
   - `desktopGestures` (`:524-640`):
     - its parameters become `viewport: State<DesktopViewport>,
       control: State<Boolean>, onViewport: …, send: …,
       release: () -> Unit, onClick: …, onHold: …`; add to the KDoc
       (`:517-523`): "[release] lets go of the left button without the
       control gate; it runs once when a drag ends without its lift."
     - `pointerInput(viewport)` (`:530`) becomes
       `pointerInput("touch", viewport)`.
     - the body of `awaitEachGesture` (`:535-638`) is wrapped:
       ```kotlin
       awaitEachGesture {
           // True while the computer holds the left button for a drag.
           var buttonDown = false
           try {
               // … the current body, with the three changes below …
           } finally {
               // The gesture's coroutine ends without a lift: a mode change, the stream or the screen stops.
               if (buttonDown) release()
           }
       }
       ```
     - before `while (true)` (`:547`), add `var cancelled = false`; and
       `if (down.isEmpty()) break` (`:562`) becomes:
       ```kotlin
       if (down.isEmpty()) {
           // Compose marks the changes of a cancelled touch as consumed when they arrive.
           cancelled = event.changes.any { it.isConsumed }
           break
       }
       ```
     - the drag start (`:580-583`) becomes:
       ```kotlin
       video(start, clamp = true)?.let { (x, y) ->
           onClick(x to y)
           send(RemoteInput.holdAt(true, x, y))
           // send drops the packet without control.
           if (control.value) buttonDown = true
       }
       ```
     - `when (mode)` (`:619`) becomes `if (!cancelled) when (mode)`, and
       its `Touch.Drag` branch (`:632`) becomes:
       ```kotlin
       Touch.Drag -> video(last, clamp = true)?.let { (x, y) ->
           send(RemoteInput.holdAt(false, x, y))
           // Without control send drops it, and the finally releases.
           if (control.value) buttonDown = false
       }
       ```

   Verify: CI in step 8; device check 9 (step 9).

   Traps:
   - Name the flag `buttonDown`, not `pressed`: `it.pressed` on the
     changes stays readable next to it.
   - `release` must not suspend: it runs in a cancelled coroutine's
     `finally`. `RemoteInput.send` and `Device.send` don't suspend
     (`core/Device.kt:133-139`).
   - Nothing else in the touch gestures changes: the Pending tap, the
     double tap, the Held right-click, Multi, Pinch and Scroll stay.

4. **The mode: `ui/DesktopScreen.kt`, `ui/AppShell.kt`**
   - Imports in `DesktopScreen.kt`: add
     `org.omarchy.flux.core.ScrollMode`, `org.omarchy.flux.desktop.Finger`
     and `org.omarchy.flux.desktop.TrackpadGesture`, in order.
   - `DesktopScreen` (`:124`) becomes
     `fun DesktopScreen(d: DeviceUi, scroll: ScrollMode, onBack: () -> Unit, extend: Boolean = false)`.
     `ui/AppShell.kt:545-546` become
     `DesktopScreen(device, state.touchpadScroll, pop)` and
     `DesktopScreen(device, state.touchpadScroll, pop, extend = true)`.
     `AppShell` is the only caller (`grep -rn "DesktopScreen(" android`).
   - After `panel` (`:141`), add:
     ```kotlin
         // The trackpad mode lasts for this visit. It is off without remote input.
         var trackpad by rememberSaveable(d.id) { mutableStateOf(false) }
         val pad = trackpad && control
     ```
   - In `buttons()`, after the Keys `PanelButton` (`:186`), add:
     ```kotlin
                 PanelButton(Ic.touchpad, "Trackpad", pad) {
                     trackpad = !trackpad
                     FluxCore.toast(if (trackpad) "Trackpad: 1 finger moves the pointer, a tap clicks" else "Touch: a tap clicks at the finger")
                 }
     ```
   - The `RemoteDesktop` call (`:245`) gets `trackpad = pad, scroll = scroll`
     before `onClick`. `RemoteDesktop` (`:337-345`) gets the parameters
     `trackpad: Boolean, scroll: ScrollMode,` before `onClick`, and its
     KDoc (`:329-335`) adds: "With [trackpad], the video works as a
     touchpad, see trackpadGestures. [scroll] is the touchpad's scroll
     setting."
   - In `RemoteDesktop`, after `controlState` (step 3), add:
     ```kotlin
         // The gestures start once per key, so they read the direction through this state.
         val natural = rememberUpdatedState(RemoteInput.naturalScroll(scroll, d.naturalScroll))
     ```
   - After `clicked` (`:414-417`), add:
     ```kotlin
         // A trackpad click has no position on the view, so the view keeps its center when the keyboard opens.
         val padClicked by rememberUpdatedState {
             focus = null
             if (control) onClick()
         }
         LaunchedEffect(trackpad) { if (trackpad) focus = null }
     ```
   - `gestures` (`:419-423`) becomes:
     ```kotlin
         val gestures = when {
             !(live && status.width > 0) -> Modifier
             trackpad -> Modifier.trackpadGestures(viewport, natural, { viewport.value = it }, { send(it) }, { release() }, { padClicked() }) {
                 haptic.performHapticFeedback(HapticFeedbackType.LongPress)
             }
             else -> Modifier.desktopGestures(viewport, controlState, { viewport.value = it }, { send(it) }, { release() }, { clicked(it) }) {
                 haptic.performHapticFeedback(HapticFeedbackType.LongPress)
             }
         }
         // The pointer starts where the user looks. A relative move can't find it on another monitor.
         val videoReady = live && status.width > 0 && box != IntSize.Zero
         LaunchedEffect(trackpad, videoReady, status.monitor) {
             if (trackpad && videoReady) vp.toVideo(box.width / 2f, box.height / 2f, clamp = true)?.let { (x, y) -> send(RemoteInput.at(x, y)) }
         }
     ```
     Keep the comment at `:418` above `gestures`.
   - The hint text (`:452-453`) becomes:
     ```kotlin
                 when {
                     trackpad -> "1 finger moves the pointer · tap clicks · hold, then move to drag\n2 fingers scroll · tap with 2 for the right button · pinch zooms · 3 fingers move the zoomed view"
                     control -> "Tap clicks · hold for the right button · hold, then move to drag\n2 fingers scroll · pinch zooms · 1 finger moves the zoomed view"
                     else -> "View only · pinch zooms · 1 finger moves the zoomed view"
                 },
     ```
   - After `desktopGestures` (ends at `:640`), before `centroid`, add:
     ```kotlin

     /**
      * The gestures of the trackpad mode, see TrackpadGesture. The video box
      * has no child that takes touches, so a change that arrives consumed is a
      * cancelled touch. A child that takes touches must change that test.
      * [onClick] runs for each click and each hold, [onHold] when a finger holds.
      */
     private fun Modifier.trackpadGestures(
         viewport: State<DesktopViewport>,
         natural: State<Boolean>,
         onViewport: (DesktopViewport) -> Unit,
         send: (Packet) -> Unit,
         release: () -> Unit,
         onClick: () -> Unit,
         onHold: () -> Unit,
     ): Modifier = pointerInput("trackpad", viewport) {
         val slop = viewConfiguration.touchSlop
         val dpPerPx = 1f / 1.dp.toPx()
         awaitEachGesture {
             val g = TrackpadGesture(slop, dpPerPx)
             fun act(outs: List<TrackpadGesture.Out>) = outs.forEach { o ->
                 when (o) {
                     is TrackpadGesture.Out.Move -> send(RemoteInput.move(o.dx, o.dy))
                     is TrackpadGesture.Out.Scroll -> send(RemoteInput.touchpadScroll(o.dx, o.dy, natural.value))
                     is TrackpadGesture.Out.Click -> {
                         onClick()
                         send(RemoteInput.click(o.button))
                     }
                     is TrackpadGesture.Out.Hold -> if (o.down) {
                         onHold()
                         onClick()
                         send(RemoteInput.hold(true))
                     } else {
                         release()
                     }
                     is TrackpadGesture.Out.Pan -> onViewport(viewport.value.pan(o.dx, o.dy))
                     is TrackpadGesture.Out.Zoom -> onViewport(viewport.value.zoom(o.factor, o.x, o.y, o.dx, o.dy))
                 }
             }
             try {
                 val first = awaitFirstDown(requireUnconsumed = false)
                 val holdAt = first.uptimeMillis + DESKTOP_HOLD_MS
                 // Two fingers can land in the first event.
                 val firstDown = currentEvent.changes.filter { it.pressed }.ifEmpty { listOf(first) }
                 act(g.fingers(firstDown.map { it.finger() }, viewport.value.scale))
                 while (true) {
                     val event = if (g.waitsForHold) {
                         val left = holdAt - SystemClock.uptimeMillis()
                         if (left > 0) withTimeoutOrNull(left) { awaitPointerEvent() } else null
                     } else {
                         awaitPointerEvent()
                     }
                     if (event == null) {
                         act(g.holdTimeout())
                         continue
                     }
                     val down = event.changes.filter { it.pressed }
                     if (down.isEmpty()) {
                         // Compose marks the changes of a cancelled touch as consumed when they arrive.
                         act(g.end(cancelled = event.changes.any { it.isConsumed }))
                         break
                     }
                     act(g.fingers(down.map { it.finger() }, viewport.value.scale))
                     event.changes.forEach { it.consume() }
                 }
             } finally {
                 // The gesture's coroutine ends without a lift: a mode change, the stream or the screen stops.
                 if (g.pressed) release()
             }
         }
     }

     private fun PointerInputChange.finger() = Finger(id.value, position.x, position.y)
     ```

   Verify: CI in step 8; device checks 1-8 (step 9).

   Traps:
   - Every `Hold(false)` goes through `release`, never `send`: `send` is
     gated on control (`DesktopScreen.kt:396-403`).
   - The keys `"trackpad"` and `"touch"` make the two modifiers different
     elements, so a mode switch cancels the old gesture and runs its
     `finally`. Don't drop them.
   - `release`, `send`, `padClicked` and `clicked` are passed as lambdas
     that read the `rememberUpdatedState` delegates, as `send` is today;
     don't pass the delegates' current values.
   - The pointer-to-center effect is keyed on `videoReady`, not on the
     sizes, so the phone keyboard changing `box` doesn't center again; a
     live stream whose size comes late (`:464-465`) centers once it comes.
   - No `toVideo` in the trackpad gestures: they work over the black bars.
   - `withTimeoutOrNull` and `currentEvent` are `AwaitPointerEventScope`
     members (`withTimeoutOrNull` as in the touch mode, `:550`); add no
     import for them.

5. **The rail scrolls: `ui/DesktopScreen.kt`**
   - Import `androidx.compose.foundation.layout.BoxWithConstraints`, and
     remove the `Spacer` import (`:21`), whose only use is `:240`.
   - In the rail (`:234-242`), replace `Spacer(Modifier.weight(1f))` and
     `buttons()` with:
     ```kotlin
                             // The buttons keep the bottom of the rail, and scroll when they don't fit, for example with the phone keyboard open.
                             BoxWithConstraints(Modifier.weight(1f)) {
                                 Column(
                                     Modifier.verticalScroll(rememberScrollState()).heightIn(min = maxHeight),
                                     horizontalAlignment = Alignment.CenterHorizontally,
                                     verticalArrangement = Arrangement.spacedBy(8.dp, Alignment.Bottom),
                                 ) { buttons() }
                             }
     ```
     `SquareButton(Ic.back, …)` stays above it, in the rail's `Column`.

   Verify: CI in step 8; device check 4 (step 9).

   Traps:
   - `verticalScroll` before `heightIn(min = maxHeight)`: the other order
     gives no minimum, and the buttons move to the top.
   - `rememberScrollState`, `verticalScroll` and `heightIn` are already
     imported (`:26,32,35`).

6. **Docs: `docs/remote-desktop.md`**
   - After the touch section's last line (`:207`, "A rotation of the phone
     shows the full screen again."), before `## Move around Omarchy`
     (`:209`), add:
     ```markdown
     ## Use the trackpad (Android)

     On the Android phone or tablet, select the trackpad button next to the keys button.
     The video then works as a touchpad, and the pointer goes to the middle of the view.
     Select the button again to use the touches.

     | Gesture | Result |
     | --- | --- |
     | Move 1 finger | Move the pointer. On a zoomed view, the pointer moves less, so it stays with the finger. |
     | Tap | Click at the pointer |
     | Tap 2 times fast | Double-click |
     | Hold 1 finger still, then lift it | Click |
     | Hold 1 finger still, then move it | Drag. The drag ends when the finger lifts. |
     | Move 2 fingers | Scroll, in the direction of the touchpad setting |
     | Tap with 2 fingers | Right-click |
     | Tap with 3 fingers | Middle-click |
     | Pinch | Zoom the view on the phone, up to 6 times |
     | Move 3 fingers | Move the zoomed view |

     When the keys or the phone keyboard open, the view keeps its middle.
     The trackpad stays on until you leave the screen.
     ```
     Keep one blank line before the new heading and before
     `## Move around Omarchy`.

   Verify: `git diff docs/` shows only the new section.

   Traps: the spec's `:206`/`:208` are now `:207`/`:209`; the touch text
   about the last tap (`:204-207`) stays with the touches.

7. **Local checks (the lead).**
   - `nix flake check path:.` must pass.
   - No Go code changes; `git diff --stat master -- internal cmd` is empty.
   - The touchpad screen is unchanged except the constant (spec,
     Verification 5):
     `git diff master -- android/app/src/main/java/org/omarchy/flux/ui/TouchpadScreen.kt`
     shows only the removed `SCROLL_SPEED` and its use at `:532`.

8. **CI (the lead).**
   - Push, after an agent-bus announcement.
   - Dispatch `build.yml` on the branch:
     ```sh
     gh workflow run build.yml -R olafkfreund/nixarchy-flux --ref feat/26-trackpad-mode
     ```
   - Poll every 5 minutes. The android job must pass: unit tests (with
     `TrackpadGestureTest` and the new `RemoteInputTest` test),
     `lintDebug`, both assembles, and the ABI check.

9. **Device tests (the lead)**, on the Pixel 11 Pro XL and the Galaxy Tab
   S8 Ultra, with the #13 setup and the branch's `android-debug` APK.

   `$b` is `<scratchpad>/r13/flux-core`, the build that runs `fluxd` now
   (`<scratchpad>` is the lead's scratchpad). Use `$b/bin/flux-cli`,
   never a `flux-cli` from `PATH`.

   **Setup:**
   1. Read the serials with `adb devices`, because wireless ports change.
   2. Compare the signing certificate of the installed app with the APK:
      `adb -s <serial> shell pm dump org.omarchy.flux | grep -A1 signatures`
      against `keytool -printcert -jarfile app-debug.apk`.
      - The same: `adb -s <serial> install -r app-debug.apk`.
      - Different (a CI debug key): ask the owner, then uninstall, install,
        and pair again with `$b/bin/flux-cli pair` and `accept`.
   3. With the owner's OK (D1), and after an agent-bus note that p620's
      desktop streams and takes input: `$b/bin/flux-cli desktop on` and
      `$b/bin/flux-cli input on` on the test `fluxd`, whose config the
      test made. Turn both off at the end.
   4. Release counts (D1): on p620, `nix shell nixpkgs#wev -c wev` opens a
      window that prints each button event. For checks 5, 7 and 9, start
      the drag inside the `wev` window and count the `button: 272`
      (left) `released` lines: exactly 1 per drag. A dropped window alone
      can't show a second release. `<scratchpad>/r13/fluxd.log` shows
      only refused, dropped and failed input.

   **Then, on both devices unless the check says otherwise:**

   | # | Check | How |
   | --- | --- | --- |
   | 1 | Switch, center, hover | Turn trackpad on: the toast shows; TalkBack reads "Trackpad, switch, on"; the pointer comes to the view's center. Move it onto a Waybar icon: its tooltip shows; tap: it clicks. Tap over the black bars: it clicks at the pointer |
   | 2 | Clicks and drag | Two taps double-click a file in a file manager with no pointer jump; hold and move drags a window; hold and lift clicks; 2 fingers scroll a browser page without moving the pointer, in the touchpad setting's direction; a 2-finger tap right-clicks; a 3-finger tap middle-clicks (paste in a terminal), the fingers lifting one by one |
   | 3 | Zoom | At about 4×: pinch out and in, and a pinch with one finger still, zoom; 3 fingers pan; 2 fingers scroll and don't zoom; a finger moves the pointer about as far on the phone as at 1×, and a very slow move still moves it |
   | 4 | Keys and keyboard | In landscape: open Keys and the phone keyboard; the rail's buttons stay reachable (scroll the rail); point into a text field and type; the view keeps its center, also after a touch-mode tap elsewhere before trackpad went on. With Keys closed, the rail looks as before |
   | 5 | Cancels | Edge swipe for the system bars while one finger rests (pending), after the hold, mid-drag, and with 2 fingers down: nothing clicks, a held button is released once. Mid-drag also: lift and add a finger; press Home; switch to touch mode with the other hand; stop the stream with `$b/bin/flux-cli desktop off` (then `on` again). Each time the window drops once and nothing else clicks |
   | 6 | Monitor | Switch the monitor with the monitor key (DP-1, DP-3): the pointer comes to the center of the new view |
   | 7 | Input off | During a hold, `$b/bin/flux-cli input off`: the button is released (by `fluxd`), the switch goes, the video pans view-only. `input on`: trackpad is back |
   | 8 | Rotation, revisit, narrow | Rotate the tablet: trackpad stays on. Leave the screen and open it again: touch mode. Tablet in portrait and the Pixel in split screen: the top bar shows the switch, and the hint shows the trackpad gestures |
   | 9 | Touch mode | Trackpad off: the touch table (`docs/remote-desktop.md:190-199`) works as before, with a drag, an edge swipe during a drag, and a mode switch during a touch-mode drag (the window drops once) |

   Not checked: the extended screen, which needs wf-recorder, not
   installed on p620. The PR says so.

   Afterwards: `$b/bin/flux-cli desktop off` and `$b/bin/flux-cli input off`.
   Record the results in the PR.

10. **PR (the lead).** Open the PR linking the intent, spec and plan, with
    the CI and device results, which steps each implementer did, and the
    unchecked extended screen.

11. **Close #2 (the lead), after the merge and step 9.** Comment with the
    spec's section 7 text, then close it as completed:
    the trackpad mode covers precision, hover and typing while pointing;
    the separate touchpad above the keys and the portrait split layout are
    declined, because a second touchpad duplicates the mode and the screen
    asks for landscape.

## Tests

- CI `build.yml` android job, with:
  - `TrackpadGestureTest` (19 tests): taps, the trembling finger's hold,
    a replaced finger, two fingers in the first event, the hold and drag,
    the second finger in a hold, the 2- and 3-finger taps with fingers
    lifting one by one, `TapLift` cancels, scroll versus pinch, pinch in,
    the third finger and the pan, a move then a finger, a lift after a
    gesture, a cancel in every state (released once), and the remainder
    at 6× with 0.02 dp steps (P5);
  - `RemoteInputTest.trackpadMoveIsTheTouchpadMoveDividedByTheZoom`.
- `nix flake check path:.`.
- The device tests of step 9: the Compose cancel path (`isConsumed`), the
  release on a coroutine end, the effects, TalkBack and the layout need
  real devices.

## Rollback

- Revert the PR's squash commit, then build and install the reverted APK.
  The changed signatures are `DesktopScreen` (its only caller,
  `AppShell`, is reverted with it), `RemoteDesktop` and `desktopGestures`.
  No data or setting is stored, and the protocol and `fluxd` don't change.
- A button left down on the computer by a missed release is let go by any
  click, or by `$b/bin/flux-cli input off`.

## Review changes

Codex and agy reviewed the first draft of this plan, read only. Each
finding was checked against the code.

| Finding | Source | Verdict | Change |
| --- | --- | --- | --- |
| Step 9 drops the spec's owner-OK gate for turning desktop and input on | Codex (high), agy (high) | Partly confirmed: the spec's gate covered p620's own config; the lead's brief says the test `fluxd`'s config was made by the test and may be changed. The plan didn't say it differs from the spec | D1 names the difference; step 9.3 asks the owner's OK and posts an agent-bus note first |
| "Released once" can't be seen from a dropped window, and `fluxd` doesn't log buttons | Codex (medium), agy (high) | Confirmed: `input.go` logs only at `:238,277,350`; a second button-up is invisible | Release counts from `wev` (`nix shell nixpkgs#wev`), the spec's other option; D1 |
| A replaced finger in `Pending` keeps the old start: a jump, or a hold without its finger | agy (medium) | Confirmed in the draft code; Android delivers a lone finger's up and down as separate events, so it's rare | P6: `Done`; test `aReplacedFingerInPendingNeverJumpsNorClicks` |
| A replaced finger in `Two`/`Three` should end the touch | agy (medium) | Refuted: the spec (`:81-87`) re-bases on a replaced id in `Two` and `Three` | None |
| Two fingers in the first event are passed as 1 | agy (low) | Confirmed: `awaitFirstDown` returns one change | P7: the first event's pressed changes; test `twoFingersInTheFirstEventTapRight` |
| The rounding emits from 0.005, not at 0.01 as the spec says | Codex (low) | Confirmed | P4 follows the spec; the test asserts 2 sub-0.01 steps send nothing, and fails with the old rule |
| Check 4 ran only on the Pixel | Codex (medium), agy (low) | Confirmed | Both devices |
| 0.02 dp instead of 0.2 dp in the remainder test is undeclared | Codex (low), agy (low) | Confirmed; 0.2 dp at 6× is 0.044 pixel and doesn't test the remainder | P5 |
| Final check that the touchpad screen is unchanged; step 1's `git diff` path is relative | Codex (low) | Confirmed | Full path; a branch diff in step 7 |
| The cancel test misses `Done` and a second `end` | Codex, agy (low) | Confirmed | `Done` fixture and a second `end(true)` |
| A jitter then a hold is not tested | agy (low) | Confirmed | `aTremblingFingerStillHolds` |
| The gesture body is `:535-639`, not `:535-638` | Codex (low) | Refuted: `:639` closes `awaitEachGesture`'s lambda, `:638` the `when` | None |
| `buttonDown` instead of the spec's `pressed` | agy (low) | Agreed, a naming choice | Step 3's trap says why |
| Centering on a monitor switch uses a `vp` from before the new size | agy (unverified) | Refuted: `DesktopSession.start` sets a new `Status` with width 0 (`desktop/DesktopSession.kt:88`), so `videoReady` goes false, then true with the new size and `vp` | None; device check 6 |
| Code compiles, references match, routing is sensible | both | Agreed, statically; CI is the first compile | None |

## Corrections

| # | Step | What changed and why |
| --- | --- | --- |
