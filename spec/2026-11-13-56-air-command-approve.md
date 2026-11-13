---
status: draft
issue: 56
intent: intent/2026-11-13-56-air-command-approve.md
---

# Spec: Explain a tap that an overlay blocked

The owner approved the intent with its proposed answers:
- explain only, with no allow-list;
- all screens with the overlay guard;
- the tablet test, including taps that must stay blocked.

## Design

### 1. One shared message (`android/app/src/main/java/org/omarchy/flux/ui/Overlays.kt`)

Add to `object Overlays`:

```kotlin
/** Shown in place of the action when [obscured] refused a tap. */
const val BLOCKED =
    "Another window covers this button, so Flux ignored the tap. " +
        "It can be an app's bubble or floating button, or the S Pen Air Command button. " +
        "Move it away or close it, then tap again."
```

Add a composable next to `HideOverlays`:

```kotlin
/** The notice of a refused tap. It stays while [show] is true, and TalkBack reads it when it appears. */
@Composable
fun BlockedNotice(show: Boolean) {
    if (!show) return
    Text(
        Overlays.BLOCKED,
        Modifier.semantics { liveRegion = LiveRegionMode.Polite },
        style = MaterialTheme.typography.bodyMedium,
        color = MaterialTheme.colorScheme.error,
        textAlign = TextAlign.Center,
    )
}
```

- The text replaces the four toasts.
- It names the likely causes and the fix. It names no app, because Flux
  can't know which window it was.

### 2. Each screen keeps a `blocked` state

A refused tap sets `blocked = true`, and nothing else changes:
- the request stays open;
- the deadline is the same;
- nothing retries by itself.

`blocked` is cleared when a tap passes the guard, or when the screen shows
another request. Each screen puts `BlockedNotice(blocked)` directly above
its buttons, so the buttons stay where they are and reachable.

| Screen | Where the tap is refused | State |
| --- | --- | --- |
| Approve and Enroll | `ui/ApproveActivity.kt:279-282` (`start`) | `private val blocked = mutableStateOf(false)` in the activity. `start` sets it on a refused tap and clears it when it goes on. `track()` clears it when the request ID changes. `AskView` shows the notice above the Deny and Approve/Enroll row (`:216`). |
| SSH signing | `ui/SshSignActivity.kt:206-209` (`start`) | The same pattern in that activity, above its button row (`:175`). |
| Pair sheet | `ui/TiledScreens.kt:66-72` (`pair` lambda) | `var blocked by remember(key) { mutableStateOf(false) }` in `TiledPairSheet`, shown above the Pair button. |
| Stream request | `ui/StreamRequestUi.kt:114-120` (`obscured ->` branch) | `var blocked by remember(r) { mutableStateOf(false) }`, shown above the Not now / Start row. |

These stay exactly as they are:
- `Overlays.obscured` and its flags;
- the down and up checks in each `dispatchTouchEvent` or `motionEventSpy`;
- `HideOverlays` and `Overlays.hide`;
- the stream request's arming (`down < armedAt`).

A tap that is still covered is refused every time. Only a tap with no
window over it gets through.

### 3. Docs

- `docs/troubleshooting.md`: a new section after "Fingerprint approval
  falls back to a password" (`:440-449`).
  - Title: "Approve, Pair or Start does nothing".
  - It names the symptom: the screen says another window covers the
    button.
  - It names the likely causes: bubbles, floating buttons, and on Samsung
    devices the S Pen Air Command button.
  - The fix: move or close it, then tap again before the request times
    out.
  - It says that Flux keeps this guard against apps that draw a false
    request over it.
- `docs/android.md:211`: "The prompt refuses a tap while another window
  covers the button, and says so on the screen."

## Alternatives rejected

- **Allow-list Air Command.**
  - A `MotionEvent` doesn't say which window covers the touch, so this
    would trust every overlay while Air Command is installed or running.
  - That defeats the tapjacking guard.
- **`filterTouchesWhenObscured`, `onFilterTouchEventForSecurity`.** They
  read the same flag, so Air Command is still refused. They also drop the
  pre-Android 12 partial check unless that is kept by hand.
- **Accessibility-service window inspection.** It needs a service that the
  user enables, it is a new privilege, and it still can't tie a window to
  this touch.
- **A second tap that overrides the guard.** A hostile overlay can get two
  taps as easily as one.
- **A longer toast.** It still disappears, and TalkBack users can miss it.

## Risks

- **A notice on a small screen at a large font size.**
  - On Approve, the notice sits in the fixed area below the scrolling
    request, so it can push the buttons up but not off screen. That area
    already holds the warning lines.
  - On the pair sheet and the stream request, the sheet grows.

  Checked on the tablet at font scale 2.0 (Verification 3).
- **The text is long.** It is three short sentences, the same length as
  the existing warnings.
- **Touches that Android drops itself.** Android can drop an untrusted
  overlay's touch before it reaches Flux (Android 12+ untrusted touches).
  Then no notice shows. Nothing changes from today there.

## Verification

1. `./gradlew :app:testDebugUnitTest :app:lintDebug :app:assembleDebug`
   in CI (`build.yml`).
   - `OverlaysTest` gets one test: `BLOCKED` names Air Command and says
     to tap again. That keeps the text from drifting into a vague message.
2. Tablet (Galaxy Tab S8 Ultra), with the #13 setup (`fluxd` on p620,
   paired, enrolled) and the branch's debug APK:
   1. Put the Air Command button over Approve and send an approval with
      the helper.
   2. Tap Approve three times. Each time, no fingerprint prompt opens, and
      the notice shows and stays.
   3. Move the button and tap Approve. The fingerprint prompt opens, the
      approval succeeds, and the notice is gone.
   4. Repeat steps 2-3 for the pair sheet (unpair, then pair again).
3. Large font: repeat step 2.2 on the tablet with
   `adb shell settings put system font_scale 2.0`. The notice and both
   buttons must stay on screen. Afterwards, restore the tablet's value,
   which is read before the change.
4. Pixel: one approval with nothing over the button, as a regression
   check.
5. `nix flake check path:.`.

## Review changes

Filled in after the Codex and agy reviews.
