---
status: approved
issue: 26
intent: intent/2026-11-13-26-trackpad-mode.md
---

# Spec: Trackpad mode on the Android remote desktop

The owner approved the intent with its proposed answers:
- close #2 after #26 ships and its device tests pass;
- no saved setting, the mode lasts one visit of the screen;
- hold still, then lift is a left click, as on the touchpad screen;
- a 3-finger move pans a zoomed view;
- the pointer goes to the view's center when trackpad turns on and when
  the monitor switches;
- the move is divided by the zoom, and scroll uses the touchpad's speed
  and direction;
- only pure helpers are shared with the touchpad screen;
- the switch sits next to Keys, and the rail may scroll.

The tablet runs Android 16 (API 36), so the narrow layout is real there.

Paths are under `android/app/src/main/java/org/omarchy/flux/` unless they
start with `android/app/src/test/`, `internal/` or `docs/`.

## Design

### 1. The gesture logic, as a plain class (`desktop/TrackpadGesture.kt`, new)

The trackpad's decisions live in a class with no Compose types, so that
JVM unit tests drive it. The modifier (section 2) only turns pointer
events into calls and carries out the results.

```kotlin
package org.omarchy.flux.desktop

/** A finger on the view: its pointer id and its position in view pixels. */
data class Finger(val id: Long, val x: Float, val y: Float)

/**
 * The gestures of the trackpad mode, for 1 touch from the first finger to
 * the last lift. [slop] is the touch slop in view pixels, [dpPerPx] turns
 * view pixels into dp.
 */
class TrackpadGesture(private val slop: Float, private val dpPerPx: Float) {
    sealed interface Out {
        data class Move(val dx: Float, val dy: Float) : Out      // relative pointer move, computer pixels
        data class Scroll(val dx: Float, val dy: Float) : Out    // finger motion in scroll units, down/right positive
        data class Click(val button: RemoteInput.Click) : Out
        data class Hold(val down: Boolean) : Out
        data class Pan(val dx: Float, val dy: Float) : Out       // view pixels
        data class Zoom(val factor: Float, val x: Float, val y: Float, val dx: Float, val dy: Float) : Out
    }

    /** True while 1 finger rests within the slop and the hold can still start. */
    val waitsForHold: Boolean
    /** True from Hold(true) until Hold(false) is returned. */
    val pressed: Boolean

    /** The fingers that are down now; [zoom] is the view's scale. */
    fun fingers(down: List<Finger>, zoom: Float): List<Out>
    /** The finger held still for HOLD_MS. */
    fun holdTimeout(): List<Out>
    /** All fingers are up. [cancelled] when Android cancelled the touch. */
    fun end(cancelled: Boolean): List<Out>
}
```

`RemoteInput.Click` is in `core`; the class imports it. The bodies are the
plan's work; this section fixes their behaviour.

**What the class keeps.** The state, the first finger's id, the start
position of the first finger, the previous finger set (ids and
positions), the start center and span of the current undecided multi-finger
touch, the most fingers seen (`most`), and `moved` (the pointer moved
before a second finger landed). The center and span are over all fingers
that are down.

**Finger sets.** Each `fingers()` call compares the ids with the previous
call:
- The same ids: a motion. Deltas are from the previous positions.
- Ids added or removed (also a replaced id with the same count): a
  finger change. The previous positions become the new set's positions,
  so a landing or lifting finger never moves anything (the touch mode
  does the same, `ui/DesktopScreen.kt:591-592`). In `Two` and `Three`
  the start center and span are taken again from the new set, so an added
  finger doesn't count as a pan or a pinch.

**States and transitions.** "Lands" and "lifts" mean the finger count goes
up or down while at least 1 finger stays.

| State | Enter | Motion | A finger lands | A finger lifts | All up |
| --- | --- | --- | --- | --- | --- |
| `Pending` | first finger | net distance from the start past `slop` → `Move`, and sends the whole motion | → `Two` with 2 fingers, `Three` with 3 or more | (not possible) | `Click(Left)` |
| `Move` | slop passed | `Out.Move` each event | → `Two` or `Three`, with `moved = true` | (not possible) | nothing |
| `Held` | `holdTimeout()` in `Pending`; returns `Hold(true)` | stays `Held`; the first finger's motion gives `Out.Move` (a drag) | stays `Held`; the other fingers are ignored | the first finger lifts → `Hold(false)`, → `Done`; another finger lifts → nothing | `Hold(false)` |
| `Two` | 2 fingers | arbitration, below | → `Three` | → `TapLift` | `Click(Right)` unless `moved` |
| `Three` | 3 or more fingers | center past `slop` → `Pan` | stays `Three` | → `TapLift` | `Click(Middle)` unless `moved` |
| `TapLift` | a finger lifts in undecided `Two` or `Three` | any finger past `slop` → `Done` | → `Done` | stays | `Click(Right)` if `most == 2`, `Click(Middle)` if `most >= 3`, none if `moved` |
| `Scroll` | `Two` decided | `Scroll` from the center's motion | ignored, in the set from then on | → `Done` | nothing |
| `Pinch` | `Two` decided | `Zoom` from the span and the center | ignored, in the set from then on | → `Done` | nothing |
| `Pan` | `Three` decided | `Pan` from the center's motion | ignored, in the set from then on | → `Done` | nothing |
| `Done` | see above | nothing | nothing | nothing | nothing |

- Fingers lift one at a time on Android (`ACTION_POINTER_UP` before
  `ACTION_UP`), so `TapLift` keeps a 2- or 3-finger tap while its fingers
  lift. The touch mode (`DesktopScreen.kt:588`) and the touchpad screen
  (`TouchpadScreen.kt:515`, `fingers = max(…)`) keep it the same way.
- `holdTimeout()` acts only in `Pending` with 1 finger, as on the touchpad
  screen (`TouchpadScreen.kt:500-512`). Hold and lift without a move gives
  `Hold(true)` then `Hold(false)`: a left click (approved Q3). The slop in
  `Pending` is the net distance from the start, as in the touch mode
  (`DesktopScreen.kt:572`), so a trembling finger can still hold.
- `Done` ignores every event until all fingers lift, so a finger change
  never clicks.
- `end(cancelled = true)` returns only `Hold(false)` when `pressed`, and
  no click in any state.
- Every `Hold(false)` clears `pressed`.

**Two-finger arbitration.** In `Two`, with `dc` the distance of the center
from its start and `ds = abs(span - startSpan)`:
- when `dc > slop` or `ds > slop`: `Pinch` if `ds >= dc * PINCH_RATIO`,
  else `Scroll`, with `PINCH_RATIO = 0.5f`.
- The span is the mean distance from the center (`DesktopScreen.kt:648-650`).
  A pinch with one finger still moves the span and the center about the
  same, so it is a pinch. Two fingers moved together barely change the
  span, so they scroll. `abs` makes a pinch in (zoom out) a pinch too.
- `Zoom` uses the factor `span / lastSpan` only when `lastSpan > 0`, else
  1, as the touch mode does (`DesktopScreen.kt:605`).
- The touch mode keeps its own rule (`DesktopScreen.kt:593-600`).

**Speed.**
- `Move`: the finger's motion in dp times `RemoteInput.pointerScale(hypot)`,
  divided by `zoom` (approved Q6). The pure helper goes in
  `core/RemoteInput.kt` (with `import kotlin.math.hypot`):

  ```kotlin
  /** The pointer motion for a finger motion of [dx], [dy] dp on a view zoomed by [zoom]. */
  fun trackpadMove(dx: Float, dy: Float, zoom: Float): Pair<Float, Float> {
      val s = pointerScale(hypot(dx, dy)) / zoom.coerceAtLeast(1f)
      return dx * s to dy * s
  }
  ```

  At zoom 1 it is the touchpad screen's move (`TouchpadScreen.kt:520-524`),
  which stays as it is.
- **Small moves add up.** A packet carries 2 decimals (`RemoteInput.kt:150`),
  and fluxd drops a move of 0 (`internal/core/input.go:163`). At 6× a slow
  finger gives less than 0.01 pixel per event. The class keeps the part
  of the motion that a packet can't carry and adds it to the next `Move`:
  it returns `Move` only when `abs(dx)` or `abs(dy)` reaches 0.01, and
  keeps the rest. The computer's side keeps fractions (`fixed`,
  `internal/desktop/pointer.go:62-65`).
- `Scroll`: the center's motion in dp times `SCROLL_SPEED`. That constant
  moves from `TouchpadScreen.kt:88` to `RemoteInput` as
  `TOUCHPAD_SCROLL_SPEED`, and both screens use it. The modifier applies
  the direction with `RemoteInput.touchpadScroll(dx, dy, natural)`
  (`RemoteInput.kt:59`).
- `Pan` and `Zoom` are in view pixels, as the touch mode's
  (`DesktopScreen.kt:573,604-605`).

### 2. The modifier (`ui/DesktopScreen.kt`, next to `desktopGestures`)

```kotlin
/**
 * The gestures of the trackpad mode, see TrackpadGesture. The video box
 * has no child that takes touches, so a change that arrives consumed is a
 * cancelled touch. A child that takes touches must change that test.
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
    val dpPerPx = 1f / 1.dp.toPx()
    awaitEachGesture {
        val g = TrackpadGesture(viewConfiguration.touchSlop, dpPerPx)
        fun run(outs: List<TrackpadGesture.Out>) = outs.forEach { o -> /* the table below */ }
        try {
            val first = awaitFirstDown(requireUnconsumed = false)
            val holdAt = first.uptimeMillis + TRACKPAD_HOLD_MS
            run(g.fingers(listOf(first.finger()), viewport.value.scale))
            while (true) {
                val event = if (g.waitsForHold) {
                    val left = holdAt - SystemClock.uptimeMillis()
                    if (left > 0) withTimeoutOrNull(left) { awaitPointerEvent() } else null
                } else awaitPointerEvent()
                if (event == null) { run(g.holdTimeout()); continue }
                val down = event.changes.filter { it.pressed }
                if (down.isEmpty()) {
                    // Compose marks the changes of a cancelled touch as consumed when they arrive.
                    run(g.end(cancelled = event.changes.any { it.isConsumed }))
                    break
                }
                run(g.fingers(down.map { it.finger() }, viewport.value.scale))
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

`run` carries out each `Out`:

| Out | Action |
| --- | --- |
| `Move` | `send(RemoteInput.move(dx, dy))` |
| `Scroll` | `send(RemoteInput.touchpadScroll(dx, dy, natural.value))` |
| `Click` | `onClick()`, then `send(RemoteInput.click(button))` |
| `Hold(true)` | `onHold()` (haptic), `onClick()`, `send(RemoteInput.hold(true))` |
| `Hold(false)` | `release()` |
| `Pan` | `onViewport(viewport.value.pan(dx, dy))` |
| `Zoom` | `onViewport(viewport.value.zoom(factor, x, y, dx, dy))` |

- Every release, on lift, cancel or coroutine end, goes through
  `release`. `g.pressed` is false after `Hold(false)`, so the `finally`
  never releases twice.
- The `"trackpad"` key, and `"touch"` on `desktopGestures`, make the two
  modifiers different elements, so a mode change cancels the old
  gesture's coroutine and runs its `finally`, whatever the Compose
  version's rule for comparing handlers.
- No `toVideo` call: the trackpad works over the black bars too
  (`desktop/DesktopViewport.kt:40-45`).
- `TRACKPAD_HOLD_MS = 450L`, the same value as the touch mode
  (`DesktopScreen.kt:100`) and the touchpad screen (`TouchpadScreen.kt:85`).
- `release` skips the control gate of `send` (`DesktopScreen.kt:396-403`),
  shows no toast, and is read through `rememberUpdatedState` like `send`:

  ```kotlin
  val release by rememberUpdatedState { RemoteInput.send(FluxCore, d.id, RemoteInput.hold(false)); Unit }
  ```

  It is positionless, so fluxd runs it even with no stream
  (`internal/core/input.go:246-260`), with the release's reserved queue
  room (`input.go:252-269`). It is non-suspending, so it runs in a
  cancelled coroutine's `finally`. `Device.send` hands the packet to the
  link (`core/Device.kt:133-139`); delivery is the link's, as for every
  packet. A release with no button down is harmless: fluxd's backend gets
  a button-up (`input.go:387-393`).

### 3. Touch mode fixes (`ui/DesktopScreen.kt:524-640`)

`desktopGestures` gets the `release` parameter and the `"touch"` key.
Inside, one flag and one release site:
- `var pressed = false`, set to true where `holdAt(true)` is sent
  (`:582`) and only when `control` (the call goes through `send`, which
  drops it without control), and set to false where `holdAt(false)` is
  sent (`:632`).
- When the loop ends (`:561-562`) with `event.changes.any { it.isConsumed }`,
  the whole `when (mode)` at `:619-637` is skipped.
- The body of `awaitEachGesture` is wrapped in
  `try { … } finally { if (pressed) release() }`.

So a cancel and a coroutine end both release once, in the `finally`, and
nothing clicks. `desktopGestures` needs `control` for the flag; it gets it
as a parameter. Nothing else in the touch gestures changes.

### 4. The mode, the switch and the pointer (`ui/DesktopScreen.kt`)

**State.** In `DesktopScreen`, next to `panel` (`:141`):

```kotlin
// The trackpad mode lasts for this visit. It is off without remote input.
var trackpad by rememberSaveable(d.id) { mutableStateOf(false) }
val pad = trackpad && control
```

It is its own state, not a `Panel` (`:107`), so it stays on with either
panel. `pad` is what every use below reads, and what `RemoteDesktop`
gets (`:245`, as its parameter `trackpad = pad`). When remote input turns
off, `pad` is false, the switch is gone (`:184`), and the video uses the
touch gestures, view-only without control (`:396-403`). `trackpad` keeps
its value, so the mode comes back if remote input turns on again in the
same visit.

**Switch.** In `buttons()` (`:184-194`), after Keys:

```kotlin
PanelButton(Ic.touchpad, "Trackpad", pad) {
    trackpad = !trackpad
    FluxCore.toast(if (trackpad) "Trackpad: 1 finger moves the pointer, a tap clicks" else "Touch: a tap clicks at the finger")
}
```

`PanelButton` is a switch for TalkBack (`:277-287`). The toast follows
the slides switch (`TouchpadScreen.kt:129-133`).

**Gestures.** `RemoteDesktop` (`:337`) gets `trackpad: Boolean` and
`scroll: ScrollMode`. `gestures` (`:419-423`) becomes:

```kotlin
val gestures = when {
    !(live && status.width > 0) -> Modifier
    trackpad -> Modifier.trackpadGestures(viewport, natural, { viewport.value = it }, { send(it) }, { release() }, { padClicked() }) { haptic.performHapticFeedback(HapticFeedbackType.LongPress) }
    else -> Modifier.desktopGestures(viewport, control, { viewport.value = it }, { send(it) }, { release() }, { clicked(it) }) { haptic.performHapticFeedback(HapticFeedbackType.LongPress) }
}
```

**Focus.** `padClicked` clears `focus` and ends the typing, and turning
trackpad on clears it too, so a touch-mode tap doesn't steer the view
later:

```kotlin
val padClicked by rememberUpdatedState {
    focus = null
    if (control) onClick()
}
LaunchedEffect(trackpad) { if (trackpad) focus = null }
```

With no `focus`, `DesktopViewport.resized` keeps the view's center when
the keys or the phone keyboard open (`DesktopViewport.kt:81-83`).

**Pointer to the center.** In `RemoteDesktop`, after `gestures`:

```kotlin
// The pointer starts where the user looks. A relative move can't find it on another monitor.
val videoReady = live && status.width > 0 && box != IntSize.Zero
LaunchedEffect(trackpad, videoReady, status.monitor) {
    if (trackpad && videoReady) vp.toVideo(box.width / 2f, box.height / 2f, clamp = true)?.let { (x, y) -> send(RemoteInput.at(x, y)) }
}
```

- The key is readiness, not the sizes, so a live stream whose size comes
  later (`DesktopScreen.kt:464-465`) still centers once, and the phone
  keyboard changing the box doesn't center again.
- `RemoteInput.at` (`RemoteInput.kt:134`) carries a position, so fluxd puts
  the pointer on the streamed monitor (`input.go:246-260`).
- This also runs when the stream starts again, for example after the app
  comes back; the pointer then goes to the center again.

**Scroll setting.** `AppShell.kt:545-546` pass `state.touchpadScroll` to
`DesktopScreen(d, scroll, onBack, extend)`, as `:544` does for the
touchpad screen. `AppShell` is the only caller. `RemoteDesktop` resolves
it as the touchpad does (`TouchpadScreen.kt:158`):

```kotlin
val natural = rememberUpdatedState(RemoteInput.naturalScroll(scroll, d.naturalScroll))
```

### 5. Layout (`ui/DesktopScreen.kt`)

- **The rail scrolls when it doesn't fit.** The rail (`:234-242`) keeps
  Back at the top. The spacer and `buttons()` become a scrolling column
  that keeps the buttons at the bottom when they fit (it needs
  `import androidx.compose.foundation.layout.BoxWithConstraints`):

  ```kotlin
  BoxWithConstraints(Modifier.weight(1f)) {
      Column(
          Modifier.verticalScroll(rememberScrollState()).heightIn(min = maxHeight),
          horizontalAlignment = Alignment.CenterHorizontally,
          verticalArrangement = Arrangement.spacedBy(8.dp, Alignment.Bottom),
      ) { buttons() }
  }
  ```

  A rail that fits looks as it does today. The touchpad screen's
  landscape column scrolls the same way when the keyboard leaves too
  little room (`TouchpadScreen.kt:219-229`).
- **The narrow layout** (`:198-199`) gets the switch through the same
  `buttons()` in the top bar. The hint (`:450-457`) shows the trackpad's
  gestures while `trackpad` is on:
  "1 finger moves the pointer · tap clicks · hold, then move to drag\n2 fingers
  scroll · tap with 2 for the right button · pinch zooms · 3 fingers move
  the zoomed view".

### 6. Docs (`docs/remote-desktop.md`)

A section "Use the trackpad (Android)" after the touch section's last line
(`:206`), before "Move around Omarchy" (`:208`), so the text about the
last tap (`:203-206`) stays with the touches:
- "On the Android phone or tablet, select the trackpad button next to the
  keys button. The video then works as a touchpad, and the pointer goes to
  the middle of the view."
- A table: move 1 finger, tap, tap 2 times fast, hold still then lift
  (left click), hold still then move (drag), move 2 fingers (scroll, in the
  direction of the touchpad setting), tap with 2 fingers, tap with 3
  fingers, pinch, move 3 fingers.
- "When the keys or the phone keyboard open, the view keeps its middle.
  The trackpad stays on until you leave the screen."

### 7. Closing #2

After the PR merges and Verification 3 has passed, #2 gets a comment and
is closed: the trackpad mode covers precision, hover and typing while
pointing; the separate touchpad above the keys and the portrait split
layout are declined, because a second touchpad duplicates the mode and the
screen asks for landscape.

## Alternatives rejected

- **Reuse `Modifier.touchpad` from the touchpad screen.** It has no pinch
  and no pan, and its second finger scrolls during a drag
  (`TouchpadScreen.kt:515-533`). Changing it changes the touchpad screen.
- **A trackpad branch inside `desktopGestures`.** One loop with both
  modes' states is harder to read and to test, and risks changing the
  touch mode. The two modifiers share only `RemoteInput` helpers.
- **A class in view pixels only, with the speed in the modifier.** The
  tests would then miss the zoom scale and the small-move remainder,
  which are the parts most likely to go wrong.
- **A small touchpad above the keys panel (#2's proposal).** It only works
  with the keys panel open, and duplicates the mode. Declined in the
  intent.
- **Pan with 2 fingers and scroll with 3.** Two fingers scroll on every
  touchpad and on the touchpad screen; panning is the rarer action.
- **Let the view follow the pointer.** The phone doesn't know where the
  pointer is. Estimating it from sent moves drifts with the computer's own
  acceleration and other input. A later issue.
- **A saved setting.** Not asked for; approved Q2.
- **A hold timeout in fluxd.** It changes the daemon and the protocol
  contract; out of scope in the intent. The app releases on every path it
  can see.

## Risks

- **The cancel test depends on Compose.** Compose sends a cancelled touch
  as an event with all fingers up and the changes already consumed
  (`onCancelPointerInput` in Compose's suspending pointer input; read
  from memory of its source, not from this repository). If a Compose
  version doesn't mark them, a cancel looks like a lift and can click, as
  today. Verification 3.5 checks it with an edge swipe in each state.
- **A consumed change from elsewhere.** The video box has no child that
  takes pointer input while the stream is live (`DesktopScreen.kt:424-449`;
  `StreamState` shows nothing then, `:462-490`), so a consumed change on
  the all-up event means a cancel. The KDoc of `trackpadGestures` says so
  for later changes.
- **Home during a drag.** Android cancels the touch when the window loses
  focus, which releases through `end(cancelled)`. If recomposition stops
  before the stream's end removes the modifier, the coroutine `finally`
  may come late; the cancel path covers it. Verification 3.5 presses Home
  mid-drag.
- **Dividing by the zoom makes a zoomed pointer slow on the computer.**
  At 6× a finger moves the pointer a sixth as far on the computer, about
  the same distance on the phone. That is approved Q6. If it feels wrong
  on the device, the plan doesn't change it alone: a different divisor
  goes back to the owner.
- **The rail as a scrolling column.** The buttons stay in the same order
  and place when they fit. Checked with the phone keyboard open on the
  Pixel (Verification 3.4).
- **The narrow top bar.** It gets one more button. On the tablet in
  portrait it has room; a phone shows the narrow layout only in split
  screen, where the bar may crowd. Verification 3.8 checks the phone in
  split screen too.
- **The pointer jumps to the center** each time the stream starts again in
  trackpad mode. Accepted: the user can't see the pointer otherwise.
- **The extended screen and the wf-recorder pointer.** The extended
  screen always uses wf-recorder (`docs/remote-desktop.md:40`), which runs
  with no cursor flag (`internal/core/remotedesktop.go:180-186`). If it
  doesn't draw the pointer, the trackpad is blind on the extended screen.
  wf-recorder is not installed on p620, so this can't be checked here
  (Verification 4).
- **Touch mode.** Only the cancel and the coroutine-end paths change.
  Verification 3.9 runs the touch table on both devices.

## Verification

1. `./gradlew :app:testDebugUnitTest :app:lintDebug :app:assembleDebug`
   in CI (`build.yml`).
   - New `android/app/src/test/java/org/omarchy/flux/desktop/TrackpadGestureTest.kt`,
     driving `TrackpadGesture` with finger lists, one finger change per
     call as Android sends them:
     - a tap gives `Click(Left)`; a jitter under the slop gives no `Move`,
       and a hold still starts after it;
     - a motion past the slop gives `Move` with the whole motion;
     - `holdTimeout()` then lift gives `Hold(true)`, `Hold(false)`; a
       motion between them gives `Move` and stays pressed;
     - in `Held`, a second finger lands and lifts: no `Scroll`, no click,
       still pressed; the first finger's lift releases;
     - 2 → 1 → 0 fingers with no motion: `Click(Right)`; 3 → 2 → 1 → 0:
       `Click(Middle)`; 1 → 2 → 3 → 2 → 1 → 0: `Click(Middle)`;
     - in `TapLift`, a motion past the slop or a new finger: no click;
     - two fingers moved together: `Scroll`, not `Zoom`; a spread with one
       finger still: `Zoom`; a pinch in: `Zoom` with a factor below 1;
     - a third finger landing in `Two` doesn't pan by itself; a 3-finger
       move gives `Pan` and no click;
     - a move, then a second finger, then all up: no click;
     - a pan, a scroll or a pinch, then one finger lifts and the rest
       move: nothing;
     - `end(cancelled = true)` in every state: no click, and `Hold(false)`
       only when pressed, once;
     - at zoom 6, many motions of 0.2 dp add up to `Move` packets whose sum
       equals the sum of `trackpadMove` (the remainder is kept).
   - `RemoteInputTest` gets `trackpadMove`: at zoom 1 it equals the
     touchpad's move; at zoom 4 it is a quarter.
2. `nix flake check path:.`.
3. **Device checks** on the Pixel and the Galaxy Tab S8 Ultra, with the
   #13 setup (`fluxd` on p620, paired) and the branch's debug APK.
   **This needs the owner's OK first:** on p620 `remote_desktop` and
   `remote_input` are `false` in `~/.config/flux/config.toml` (lines
   13-14). The owner turns them on (`flux-cli desktop on`,
   `flux-cli input on`) and off again afterwards. p620 has
   gpu-screen-recorder and two connected monitors (DP-1, DP-3), so the
   monitor switch can be checked. To count releases, fluxd's log or
   `wev` on p620 shows each button event; the owner chooses which.
   1. Trackpad on: the toast shows, TalkBack reads "Trackpad, switch, on",
      the pointer comes to the view's center; move it onto a Waybar icon,
      see its tooltip, tap to click. Taps on the black bars work too.
   2. Two taps double-click a file in a file manager without a pointer
      jump; hold and move drags a window; 2 fingers scroll a browser page
      without moving the pointer, in the touchpad setting's direction;
      2-finger tap right-clicks; 3-finger tap middle-clicks (paste in a
      terminal), with the fingers lifting one by one.
   3. At about 4× zoom: pinch out and in zoom, a one-finger-still pinch
      zooms, 3 fingers pan, 2 fingers scroll and don't zoom; a finger
      moves the pointer about as far on the phone as at 1×, and a very
      slow move still moves it.
   4. Open Keys and the phone keyboard: the rail's buttons stay
      reachable (scroll the rail if needed); point into a text field and
      type; the view keeps its center, also after a touch-mode tap
      elsewhere before turning trackpad on.
   5. Edge swipe for the system bars while: one finger rests (pending),
      after the hold, mid-drag, and with 2 fingers down. Nothing clicks,
      and a held button is released once. Mid-drag also: lift and add a
      finger, press Home, switch to touch mode with the other hand, stop
      the stream from the computer (`flux-cli desktop off`). Each time the
      window is dropped once and nothing else clicks.
   6. Switch the monitor with the monitor key: the pointer comes to the
      center of the new view.
   7. Turn remote input off on the computer during a hold: the button is
      released (by fluxd), the switch goes, the video pans view-only.
   8. Rotate the tablet: trackpad stays on. Leave the screen and open it
      again: touch mode. Tablet in portrait and the Pixel in split screen:
      the narrow layout shows the switch in the top bar and the trackpad
      hint.
   9. Trackpad off: the touch table (`docs/remote-desktop.md:190-199`)
      works as before, including a drag, an edge swipe during a drag, and
      a mode switch during a touch-mode drag.
4. **Not shown here:** the extended screen needs wf-recorder
   (`docs/remote-desktop.md:40`), which isn't installed on p620.
   Installing it is a system change on p620 and needs the owner's OK.
   Without it, the extended screen and the wf-recorder pointer stay
   unchecked, and the PR says so. The cancel path's reliance on Compose
   is shown only by the device checks in 3.5, not by a unit test.
5. **Review against the diff:** `Modifier.touchpad` and the touchpad
   screen's gestures are unchanged except `SCROLL_SPEED` moving to
   `RemoteInput`.
6. **After merge:** close #2 with the comment in section 7.

## Review changes

Codex and agy reviewed the first draft, read only. Each finding was checked
against the code.

| Finding | Source | Verdict | Change |
| --- | --- | --- | --- |
| 2- and 3-finger taps never click: fingers lift one at a time, and `Two`/`Three` went to `Done` | Codex (high), agy (high) | Confirmed: the table did that; the touch mode keeps the tap, `DesktopScreen.kt:588` | `TapLift` state; tests for 2→1→0 and 3→2→1→0 |
| Pointer-to-center misses a stream whose size comes later | Codex (high), agy (high) | Confirmed, `DesktopScreen.kt:464-465` | Keyed on `videoReady` |
| `Hold(false)` went through the control-gated `send` | Codex (high) | Confirmed, `DesktopScreen.kt:396-403` | Every `Hold(false)` calls `release` |
| Both modifiers share `pointerInput(viewport)`, so a mode change may not cancel | agy (high) | Plausible: depends on the Compose version's element equality | `"trackpad"` and `"touch"` keys |
| `Held` "→ `Move`" was ambiguous and could lose the release | Codex | Confirmed | `Held` stays `Held` and emits `Out.Move`; finger changes in `Held` defined |
| Pinch in fails without `abs`; start center not re-based for a new finger; `Move` with 3 fingers | Codex, agy | Confirmed | `abs`, re-base rule, `Move` → `Two` or `Three`; tests |
| Touch-mode cancel released twice; view-only `singlerelease` | Codex, agy | Confirmed in the draft | One `pressed` flag, set only with control; one release in `finally` |
| Slow moves at 6× round to 0 | agy | Partly confirmed: the phone rounds to 0.01 (`RemoteInput.kt:150`) and fluxd drops 0 (`input.go:163`); the computer keeps fractions (`pointer.go:62-65`) | Remainder kept in the class; test and device check |
| Stale `focus` from a touch-mode tap before the first trackpad click | agy | Confirmed, `DesktopViewport.kt:81-83` | Cleared when trackpad turns on; device check 3.4 |
| `pad` vs `trackpad` naming at the call site | agy | Agreed | `RemoteDesktop` gets `pad`, stated |
| `isConsumed` as cancel and Compose claims need care; `Device.send` "only queues" | Codex | Agreed; Compose behaviour is from memory of its source, confirmed by agy, not in this repository | Wording narrowed; Risks and Verification 4 say the device test is the proof |
| Home: recomposition may pause, release comes from Android's cancel | agy | Plausible, unverified | Risks entry; device check 3.5 |
| Verification misses cancel per state, release counts, black bars, rotation, revisit, TalkBack, speed on the phone | Codex, agy | Agreed | Verification 3.1-3.9 extended |
| Docs placement splits the touch section; Android-only wording | agy | Confirmed, `docs/remote-desktop.md:7,203-208` | Section moved after `:206`, titled Android |
| Narrow top bar may crowd on a phone in split screen | agy | Plausible, unverified | Risks; device check 3.8 |
| `docs/remote-desktop.md:42` should be `:40` | Codex, agy | Confirmed | Corrected |
| `finger()` undefined, `PointerId.value`, imports, `apply` shadowing, `release` capture | agy | Confirmed | `finger()` defined; `run`; imports named; `rememberUpdatedState` |
| Net vs path slop for the hold; zero-span guard | agy | Agreed | Net distance; `lastSpan > 0` guard |
| `sqrt(zoom)` would change an approved answer | Codex | Agreed | Goes back to the owner, not to the plan |
| Closing #2 missing | Codex, agy | Confirmed | Section 7 and Verification 6 |
| Simpler: class in view pixels only | agy | Not adopted | Alternatives rejected |
| Simpler: one release site in `desktopGestures` | agy | Adopted | Section 3 |
