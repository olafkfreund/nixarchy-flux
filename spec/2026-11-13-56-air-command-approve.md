---
status: approved
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
    "Another window covered Flux, so Flux ignored the tap. " +
        "It can be an app's bubble or floating button, or the S Pen Air Command button. " +
        "Move it off the button or close it, then tap again."
```

Add a composable next to `HideOverlays`:

```kotlin
/** The notice of a refused tap. It stays while [show] is true, and TalkBack reads it when it appears. */
@Composable
fun BlockedNotice(show: Boolean, align: TextAlign = TextAlign.Center) {
    if (!show) return
    Text(
        Overlays.BLOCKED,
        Modifier.fillMaxWidth().semantics { liveRegion = LiveRegionMode.Polite },
        style = MaterialTheme.typography.bodyMedium,
        color = MaterialTheme.colorScheme.error,
        textAlign = align,
    )
}
```

- The text replaces the four toasts.
- It says the window covered Flux, in the past tense, because the notice
  stays after the user moves the window. It says Flux, not the button. Before Android 12, a
  window over any part of Flux blocks the tap (`Overlays.kt:27-34`), so
  "close it" is the fallback when moving doesn't help.
- It names the likely causes and the fix. It names no app, because Flux
  can't know which window it was.

### 2. Each screen keeps a `blocked` state

A refused tap sets `blocked = true`, and nothing else changes:
- the request stays open;
- the deadline is the same;
- nothing retries by itself.

`blocked` is cleared only when a tap passes every check and the action
goes on (the `else` branch of each guard), or when the screen shows another
request. Nothing writes it during composition. In the stream request,
that is the `else -> onStart()` branch, after both the arming check and
the obscured check (`StreamRequestUi.kt:114-120`).

**Placement**, so that the notice and the buttons stay reachable at any
font size:
- **Approve and SSH signing:** the notice goes at the end of the scrolling
  request column (`ApproveActivity.kt:176-180`, `SshSignActivity.kt:148`),
  not in the fixed area. The buttons stay fixed below it. When `blocked`
  turns true, the column scrolls to its end, so the notice is in view:
  `LaunchedEffect(blocked) { if (blocked) scroll.animateScrollTo(scroll.maxValue) }`,
  with the column's `rememberScrollState()` lifted into a `val scroll`.
- **The pair sheet and the stream request:** their sheet columns don't
  scroll today (`TiledScreens.kt:78`, and `FluxSheet` in
  `ui/TiledKit.kt:601-621` passes the content straight to
  `ModalBottomSheet`). Each gets `verticalScroll(rememberScrollState())`
  on its outer column, and the notice goes directly above its buttons.

| Screen | Where the tap is refused | State |
| --- | --- | --- |
| Approve and Enroll | `ui/ApproveActivity.kt:279-282` (`start`) | `private val blockedFor = mutableStateOf<String?>(null)` in the activity, holding `"${r.computerId}/${r.id}"` of the refused request. `start` sets it on a refused tap and sets `null` when it goes on. `AskView` shows the notice when `blockedFor.value` equals the shown request's key, so another request shows none, without a write in `track()`. |
| SSH signing | `ui/SshSignActivity.kt:206-209` (`start`) | The same, keyed by `SshRequest.id` (`core/SshMessage.kt:30`). |
| Pair sheet | `ui/TiledScreens.kt:66-72` (`pair` lambda) | `var blocked by remember(name) { mutableStateOf(false) }` in `TiledPairSheet`. Keyed by the device `name`, not the `key`: the key can arrive after a tap (it shows dots until then), and the same pairing keeps its sheet when `waiting` turns true. The `else` branch, before `onPair()`, sets it false, so the notice is gone while the sheet waits. Notice above the Cancel and Pair row (`:121`). |
| Stream request | `ui/StreamRequestUi.kt:114-120` | `var blocked by remember(r) { mutableStateOf(false) }`. `StreamRequest` is a data class with the time `at` (`core/StreamRequests.kt:77`), so a replacing request is a new key. Set false in `else -> onStart()`. |

In the two sheets, the notice uses `align = TextAlign.Start`, to match the
start-aligned sheet text.

`MainActivity.touchObscured` (`ui/MainActivity.kt:219-220`) stays the
pair sheet's source, unchanged; only `TiledPairSheet` gets the state.

These stay exactly as they are:
- `Overlays.obscured` and its flags;
- the down and up checks in each `dispatchTouchEvent` or `motionEventSpy`;
- `HideOverlays` and `Overlays.hide`;
- the stream request's arming (`down < armedAt`).

A tap that is still covered is refused every time. Only a tap with no
window over it gets through.

The KDoc of `Overlays.obscured` (`Overlays.kt:20-26`) says a window the
system keeps "covers only a part and does not block a tap". Air Command
shows that a privileged window can cover the touch point too, so the
comment is corrected to say so.

### 3. Docs

- `docs/approve.md:128-130`, the design text, becomes: "On Android 12 and
  later, the approval screen hides the overlay windows of other apps.
  Windows that the system or the phone maker keeps, such as the S Pen Air
  Command button, can stay. The phone refuses **Approve** when another
  window covered the touch point, and says so on the screen."

- `docs/troubleshooting.md`: a new section after "Fingerprint approval
  falls back to a password" (`:440-450`).
  - Title: "Approve, Enroll, Pair, or Start does nothing". The body also
    names **Play on phone**.
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
- **A bypass for S Pen (stylus) taps.** A stylus tap can be tapjacked like
  a finger.

Pair and stream requests have no fingerprint step after the tap, so the
overlay guard is their only defense against tapjacking. That is a further
reason not to loosen it.

## Risks

- **A notice on a small screen at a large font size, or in landscape.**
  The placement above keeps the buttons reachable. The sheets now scroll,
  which they didn't before; a sheet that fits is unchanged. Checked on the
  tablet at font scale 2.0 in split screen, portrait and landscape
  (Verification 3).
- **The text is long.** It is three short sentences, the same length as
  the existing warnings.
- **A tap that never reaches Flux.** If the tap lands on Air Command's
  own button, Air Command takes it, and Flux sees nothing. Then no notice
  shows. Verification 2 checks first that a tap next to the button reaches
  Flux and shows the notice. If it doesn't, the fix doesn't help this
  case: stop and report before going on.
- **TalkBack after a covered touch (already true today).** An accessibility
  click has no touch, so the obscured flag from the last real touch stays.
  The Approve or Start button then stays refused until a real touch. This
  change doesn't make it worse, and it is out of scope. The notice at
  least tells the user why.
- **A second refused tap isn't announced again.** The notice's text is the
  same, so TalkBack reads it only when it first appears. The notice stays
  on screen. Accepted: announcing on each tap would need a deprecated
  call or changing text.
- **Touches that Android drops itself.** Android can drop an untrusted
  overlay's touch before it reaches Flux (Android 12+ untrusted touches).
  Then no notice shows. Nothing changes from today there.

## Verification

1. `./gradlew :app:testDebugUnitTest :app:lintDebug :app:assembleDebug`
   in CI (`build.yml`).
   - `OverlaysTest` gets one test: `BLOCKED` names Air Command and says
     to tap again. That keeps the text from drifting into a vague message.
2. **Tablet** (Galaxy Tab S8 Ultra), with the #13 setup (`fluxd` on p620,
   paired and enrolled) and the branch's debug APK. For each flow, with
   the Air Command button over the action:
   - tap three times: nothing happens beyond the notice, which shows and
     stays (no fingerprint prompt, no pairing, no stream start);
   - move the button and tap once in the same request: the action goes on
     and the notice is gone.

   | Flow | How to start it |
   | --- | --- |
   | Approve | the helper with `PAM_TYPE=auth`, as in #13 |
   | Enroll | `sudo flux-cli approve enroll` |
   | Pair | unpair the tablet, then `flux-cli pair` |
   | SSH signing | turn on the SSH key in Flux, then `ssh-keygen -Y sign` through `fluxd`'s agent socket `/run/user/1000/flux/ssh-agent.sock` |
   | Stream request | `flux-cli webcam start` |

   Also check:
   - the request still ends at its normal timeout;
   - a new request starts with no notice;
   - TalkBack reads the notice once when it appears, and Deny, Cancel and
     Not now still work.
3. **Large font:** on the tablet, `adb shell settings put system font_scale 2.0`,
   with Flux in split screen (half the screen), in portrait and in
   landscape.
   - Show the notice with Air Command on Approve and on the pair sheet.
   - The notice and all buttons must be reachable.
   - Afterwards, put back the value read before the change.

   The tablet is used because Air Command is the only window here known to
   set the flag. The Pixel hides app overlays on these screens, and its
   bubbles are system windows, which may not set it. No debug-only code is
   added for this.
4. **Regression:** one approval on the Pixel with nothing over the button.
5. **Paths no device here can show:**
   - a touch obscured only at down or only at up;
   - partial cover before Android 12;
   - a stream touch that starts before arming.

   The code for these doesn't change. `OverlaysTest` already covers the
   flag logic, and the PR diff shows the checks untouched.
6. `nix flake check path:.`.

## Review changes

| Finding | Change |
| --- | --- |
| Codex 1: the notice could push the buttons off screen; the sheets don't scroll | The notice goes in the scrolling area on Approve and SSH signing and scrolls into view. The sheets get `verticalScroll`. Checked on the tablet at font scale 2.0 in split screen. |
| Codex 2: Enroll, SSH signing and stream were not tested; stream cleared too early | All five flows in Verification 2, with TalkBack, the timeout and a new request. The stream clears only in `else -> onStart()`. |
| Codex 3: "covers this button" is wrong before Android 12 | The text says the window covers Flux, with "close it" as the fallback. |
| agy (intent) 1: a tap on Air Command's own button never reaches Flux | A Risks entry, and Verification 2 checks first that the notice shows at all. |
| agy (intent) 3, 6: `MainActivity`, the KDoc | Named. `MainActivity` is unchanged; the KDoc is corrected. |
| agy (intent) 4: pair and stream have no fingerprint step | Added to Alternatives rejected, as a reason to keep the guard. |
| agy (intent) 5: TalkBack after a covered touch | A Risks entry: true today, out of scope. |
| agy (intent) 7: `docs/approve.md:128-130` is inaccurate | Rewritten in Docs. |
| agy (spec) 2: `remember(key)` on the pair sheet; `else` clearing not in the table | Keyed by `name`, and every `else` branch clears it. |
| agy (spec) 3: a state write in `track()` during composition; request identity | `blockedFor` holds the refused request's key, so nothing is written in composition. Keys named for each request type. |
| agy (spec) 6: a second refused tap isn't re-announced | A Risks entry, accepted. The TalkBack sticky flag was already a Risks entry. |
| agy (spec) 7: alignment, tense, doc details | `fillMaxWidth`, start-aligned in sheets, "covered", troubleshooting `:440-450`, Oxford comma, Enroll and Play on phone named. |
| agy (spec) 1, 4, 5 | Covered by the Codex changes above (agy reviewed the first draft). |
