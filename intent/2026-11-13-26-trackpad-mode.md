---
status: approved
issue: 26
author: olafkfreund
---

# Intent: Trackpad mode on the Android remote desktop

## Problem

On the Android remote desktop, every touch on the video points and clicks
where the finger is.
- The issue (mirrored from upstream `bjarneo/flux#182`) asks for a
  trackpad mode over the video: a drag moves the computer's pointer, and a
  tap clicks where the pointer is.
- Issue #2 (upstream `bjarneo/flux#50`) asks for the same thing, for three
  reasons:
  - **Precision.** A 1920×1080 screen shows at phone size. The finger
    covers small targets, such as the icons in the bar.
  - **Hover.** Every tap moves the pointer and clicks. Tooltips and menus
    that open on hover need a pointer move without a click.
  - **Type and point together.** Put the cursor in a text field without
    closing the keys panel.
- #2 proposes a small touchpad in the free space above the keys panel, in
  landscape. Its last option, and the comments on it, ask for a trackpad
  mode over the video instead. #26 is that option.

### Facts

Paths are under `android/app/src/main/java/org/omarchy/flux/` unless they
start with `android/app/`, `internal/` or `docs/`.

- **The video's gestures today** are `Modifier.desktopGestures`
  (`ui/DesktopScreen.kt:517-640`), also in `docs/remote-desktop.md:188-201`.
  - A tap clicks at the finger with `clickAt` (`DesktopScreen.kt:620-627`).
    A second tap near the first within 400 ms clicks at the first point, for
    a double-click (`DesktopScreen.kt:102-104,623`).
  - A finger held still for 450 ms puts the pointer under it
    (`DesktopScreen.kt:100,554-559`). Lifting it right-clicks
    (`DesktopScreen.kt:628-631`); moving it drags with `holdAt`
    (`DesktopScreen.kt:576-587,632`).
  - 1 finger pans the zoomed view (`DesktopScreen.kt:570-575`).
  - A second finger turns a pending touch or a pan into `Multi`
    (`DesktopScreen.kt:564-568`). From there a change of span beyond the
    touch slop is a pinch, and a move of the center is a scroll, with the
    pointer first put under the fingers (`DesktopScreen.kt:588-601`). The
    choice then stays for the gesture. A pinch also moves the view with the
    fingers' center (`DesktopScreen.kt:604-605`). Scroll is always natural
    and scaled by the video's pixels (`DesktopScreen.kt:607-608`). A
    2-finger tap right-clicks at the center (`DesktopScreen.kt:633-636`).
  - So 2 fingers moved together always scroll; panning needs a pinch first.
    The span is the mean distance from the center (`DesktopScreen.kt:648-650`),
    so a pinch with one finger still moves the span and the center about
    the same, and can be taken as a scroll.
  - A second finger that lands during a pan and lifts again right-clicks
    (`DesktopScreen.kt:564-568,633-636`). This is the case today.
  - A gesture that ends with no finger pressed runs the end branch
    (`DesktopScreen.kt:561-562,619-637`). An Android cancel, for example an
    edge swipe that shows the hidden system bars (`DesktopScreen.kt:318`),
    probably ends the same way, so it can click. Unverified on a device.
  - Clicks on the video record their point as `focus`. When the keys or
    the phone keyboard shrink the view, the view keeps `focus` in sight, or
    else the center (`DesktopScreen.kt:408-417`, `desktop/DesktopViewport.kt:72-89`).
  - Points on the black bars beside the video give no position
    (`DesktopViewport.kt:40-45`), so a tap there does nothing.
  - The gestures run only on a live video (`DesktopScreen.kt:418-423`).
- **The touchpad screen's gestures** are a private `Modifier.touchpad`
  (`ui/TouchpadScreen.kt:476-546`), used only by the touchpad screen
  (`TouchpadScreen.kt:179`).
  - 1 finger moves the pointer with `RemoteInput.move` and
    `RemoteInput.pointerScale` (`TouchpadScreen.kt:516-526`,
    `core/RemoteInput.kt:37,163-167`). A tap clicks where the pointer is.
    A 2-finger tap right-clicks, a 3-finger tap middle-clicks
    (`TouchpadScreen.kt:536-544`).
  - A finger held still for 450 ms presses the left button at once; it is
    released on lift (`TouchpadScreen.kt:85,508-512,537`). Hold and lift
    without a move is a left click.
  - Its 2-finger branch scrolls once the fingers pass the slop, and
    consumes every change (`TouchpadScreen.kt:527-534`). It has no pinch.
    A second finger during a hold scrolls while the button stays pressed.
  - Its scroll direction is the touchpad setting `touchpadScroll`
    (`core/Settings.kt:55-58`), resolved with the computer's own setting by
    `RemoteInput.naturalScroll` (`RemoteInput.kt:48-52`,
    `TouchpadScreen.kt:158,181`). `AppShell` passes it to the touchpad
    screen only, not to the desktop screen (`ui/AppShell.kt:544-546`).
- **fluxd already takes relative input.** A body with `dx`/`dy` and no
  `x`/`y` is a relative move; a click or hold with no position acts where
  the pointer is (`internal/core/input.go:111-165`). No protocol or daemon
  change is needed.
- **fluxd releases a held button** when remote input turns off, the device
  loses permission, or its link drops; it checks every 500 ms
  (`input.go:46-48,320-344,398-416`). A release has reserved room in a full
  queue (`input.go:252-269`). It does not release while the link stays up,
  for example when only the stream stops.
- **Positions go to the streamed monitor; relative moves don't.** A
  position is applied on the monitor the phone shows, and dropped when the
  device has no stream (`input.go:246-260`). A relative move can take the
  pointer onto another monitor, out of the video. This is the case for
  the extended screen and for a computer with several monitors
  (`DesktopScreen.kt:176-183`).
- **The pointer in the stream.** gpu-screen-recorder runs with
  `-cursor yes` (`internal/core/remotedesktop.go:164`). The wf-recorder
  fallback passes no cursor flag (`remotedesktop.go:180-186`); whether it
  shows the pointer is unverified. The phone never learns where the
  pointer is, so with relative moves it can't keep the pointer in a zoomed
  view, and a click has no position for `focus`.
- **Orientation.** When the screen is ready it asks for landscape
  (`DesktopScreen.kt:144-145,306-313`), and the layout follows the actual
  orientation (`DesktopScreen.kt:144,147`).
  - On a phone this gives the wide layout: a rail of buttons at the side,
    and the keys panel 320 dp wide at the bottom right
    (`DesktopScreen.kt:232-259`). That leaves the empty area #2 shows.
  - The app targets SDK 36 (`android/app/build.gradle.kts:40`). Android 16
    ignores orientation requests on large screens for such apps, and split
    screen ignores them too. So the Galaxy Tab S8 Ultra can show the
    narrow layout: buttons in the top bar, panels under the video, and the
    gesture hint, which states the touch gestures
    (`DesktopScreen.kt:198-199,261-266,450-457`). Which Android version
    the tablet runs is unverified.
- **The rail doesn't scroll** (`DesktopScreen.kt:233-242`). It holds Back,
  Audio, the monitor key, Omarchy, Keys and Dictate (`DesktopScreen.kt:168-194`),
  each at least 48 dp high (`DesktopScreen.kt:280-281`). The whole screen
  takes `imePadding` (`DesktopScreen.kt:197`), so the phone keyboard
  shrinks the rail too. A 7th button may not fit, and with the keyboard
  open the rail may already overflow today. Unverified on a device.
- **Panel buttons** are `PanelButton` switches with a role and a state
  for TalkBack, shown only with remote input on (`DesktopScreen.kt:184-194,277-287`).
  `Panel` is one of Omarchy or Keys (`DesktopScreen.kt:107,141,148`), so a
  mode that stays on with either panel needs its own state. There is an
  `Ic.touchpad` icon (`ui/Icons.kt:118`). The touchpad screen's slides
  switch shows a toast on each change (`TouchpadScreen.kt:129-133`).
- **A held button is released only when the finger lifts.** Both gesture
  loops send the release after the loop ends (`DesktopScreen.kt:632`,
  `TouchpadScreen.kt:537`). If the gesture's coroutine is cancelled, for
  example the stream stops and the gestures modifier goes away
  (`DesktopScreen.kt:419-423`), the left button stays pressed on the
  computer until fluxd's own release, which may never come. The video's
  `send` drops packets without remote input (`DesktopScreen.kt:396-403`).
- **Accessibility.** The video has no semantics; its gestures are raw
  pointer input (`DesktopScreen.kt:424,530`). TalkBack's explore by touch
  takes 1-finger moves.
- **Other apps.** iOS (`ios/App/Features/Desktop/DesktopViews.swift`) and
  macOS (`macos/Sources/FluxKit/Plugins/Desktop/DesktopTouches.swift`) have
  their own remote desktop gestures. This issue doesn't change them.

## Proposed outcome

- A **Trackpad** switch with the touchpad icon sits with the panel
  buttons. It shows only with remote input on, and TalkBack reads it as a
  switch. It stays reachable with the phone keyboard open (open
  question 8).
- **Touch mode** (off, the default) works as today.
- **Trackpad mode** (on), anywhere over the video, black bars included:
  - 1-finger move moves the pointer with the touchpad's acceleration. No
    click, so hover works. A tap's small jitter doesn't move it.
  - Tap clicks where the pointer is. Two quick taps double-click.
  - Hold still, then move: drag with the left button, released on lift.
    Hold and lift is open question 3.
  - 2-finger tap right-clicks, 3-finger tap middle-clicks, where the
    pointer is.
  - 2-finger move scrolls where the pointer is, in the touchpad's scroll
    direction. It doesn't move the pointer.
  - Pinch zooms the view. The way to pan a zoomed view is open question 4.
  - A click or drag ends the keys panel's text, as a tap does today, and
    clears `focus`, so the view keeps its center when the keys or the phone
    keyboard open. The remote text cursor isn't kept in view.
- **Finger changes are explicit.** For each touch state (pending, move,
  hold, drag, scroll, pinch, pan) the spec says what a finger that lands
  or lifts does. No finger change and no cancel makes a click.
- **No button stays pressed.** A drag is released when the finger lifts,
  when Android cancels the gesture, when the gesture's coroutine ends,
  when the mode changes, and when the stream, the screen or the app stops.
  The release goes out even without a stream. This also fixes the touch
  mode's drag. If the app dies with the link up, fluxd's own release is
  the limit; this issue doesn't add a hold timeout to fluxd.
- **The pointer starts in view.** Turning trackpad on and switching the
  monitor put the pointer in the middle of the shown view (open
  question 5).
- When remote input turns off, trackpad mode is off with it, and the video
  goes back to view-only panning.
- The mode works with the keys panel and the phone keyboard open, so #2's
  "type and point" case works.
- It works in both the wide and the narrow layout. The narrow layout's
  hint shows the trackpad gestures while trackpad is on. No new layout.
- A toast says the mode on each change, e.g. "Trackpad: 1 finger moves ·
  tap clicks" and "Touch: tap clicks at the finger".
- `docs/remote-desktop.md` gets a trackpad gesture table.
- #2 is closed when #26 ships and its device tests pass, with a comment
  that the trackpad mode covers it and that the separate touchpad and the
  portrait split layout are declined.

## Affected users and systems

- Android users of the remote desktop and the extended screen, on phones
  and tablets.
- `ui/DesktopScreen.kt` (the switch, the trackpad gestures, button
  release, focus), `ui/AppShell.kt` (pass the scroll setting), possibly
  `ui/TouchpadScreen.kt` and `core/RemoteInput.kt` if pure helpers are
  shared, `docs/remote-desktop.md`, and unit tests for any pure gesture
  logic that is split out.
- Not affected: `fluxd`, the protocol, the desktop GUI, iOS, macOS,
  Windows, and the touchpad screen's behaviour.

## Constraints

- Android only. No new layout and no portrait redesign; the mode works in
  the layouts that exist.
- Touch mode behaviour doesn't change, except the button release and the
  cancel fixes.
- No new packet types or fluxd changes; use `RemoteInput.move`, `click`,
  `hold`, `scroll` and `at`.
- Never leave a mouse button pressed on the computer when the app can
  still release it.
- A tap over a waiting or failed stream never clicks, as today
  (`DesktopScreen.kt:418`).
- The touchpad screen keeps its gestures.
- Accessibility: the switch has a role and a state for TalkBack. The
  video's gestures stay without TalkBack actions, as today.
- Tested on the Pixel and the Galaxy Tab S8 Ultra.

## Open questions

1. **Close #2 with this issue?** Proposed: yes, after #26 ships and its
   device tests pass. The trackpad mode covers precision, hover and typing
   while pointing. The separate touchpad above the keys and the portrait
   split layout (`MarcoRLK`'s comment) are declined: a second touchpad
   duplicates the mode, and the screen asks for landscape
   (`DesktopScreen.kt:145`).
2. **Remember the mode?** Proposed: no saved setting. It lasts for one
   visit of the screen, through rotation (`rememberSaveable`, like the
   panel at `DesktopScreen.kt:141`), and starts in touch mode on the next
   visit. A saved setting can come later if asked for.
3. **Hold still, then lift?** Touch mode right-clicks; the touchpad
   screen presses the left button at once, so a lift left-clicks.
   Proposed: as the touchpad screen, since the mode uses its gestures.
   Right-click is the 2-finger tap.
4. **How to pan a zoomed view,** where 1 finger moves the pointer and 2
   fingers scroll? Proposed: a 3-finger move pans the view, so 2 fingers
   always scroll and a pinch only zooms (and moves with its center, as
   today). A 3-finger tap stays the middle click. The view doesn't follow
   the pointer: the phone doesn't know where it is. An estimated pointer
   position is a later issue.
5. **Bring the pointer into view?** Proposed: turning trackpad on and
   switching the monitor send `at` for the center of the shown view
   (`RemoteInput.kt:134`). fluxd applies it on the streamed monitor
   (`input.go:246-260`). The pointer then starts where the user looks.
6. **Speed when zoomed?** At 6× zoom the touchpad's speed moves the
   pointer 6 times faster across the phone. Proposed: divide the move by
   the zoom, so a finger moves the pointer about the same distance on the
   phone at any zoom. Scroll uses the touchpad's speed and direction
   (`TouchpadScreen.kt:88,532`), not the touch mode's.
7. **Share code with `Modifier.touchpad`?** Proposed: share only pure
   helpers (`pointerScale`, the scroll direction and speed); the trackpad
   gestures get their own branch or modifier next to `desktopGestures`.
   The touchpad screen's state machine stays as it is.
8. **Where does the switch go?** Proposed: next to Keys, in the rail and
   in the top bar. The spec checks the rail's height with the phone
   keyboard open on the Pixel and, if it overflows, lets the rail scroll.
9. **Test:** unit tests for the pure gesture decisions split out (finger
   changes, cancel, zoom scale). Then on the Pixel and the Galaxy Tab S8
   Ultra with the #13 test setup:
   1. Trackpad on: the pointer comes to the view's center; move it onto a
      bar icon, see its tooltip, tap to click.
   2. Two taps double-click without a pointer jump; hold and move drags a
      window; 2 fingers scroll without moving the pointer; 2-finger tap
      right-clicks; 3-finger tap middle-clicks.
   3. At 4× zoom: pinch zooms, 3 fingers pan, 2 fingers don't zoom, the
      pointer speed feels the same as at 1×.
   4. With the keys panel and the phone keyboard open, point into a text
      field and type; the rail's buttons stay reachable.
   5. Mid-drag: lift a finger, add a finger, edge swipe for the system
      bars, stop the stream, press Home, switch the mode. The button is
      released each time and nothing clicks.
   6. Switch the monitor: the pointer comes into view. Extend screen: the
      same.
   7. Turn remote input off on the computer: the switch goes, the video
      pans again.
   8. Tablet in split screen: the narrow layout shows the switch and the
      trackpad hint.
   9. Trackpad off: touch mode works as before.

## Review changes

Codex and agy reviewed the first draft, read only. Each finding was checked
against the code.

| Finding | Source | Verdict | Change |
| --- | --- | --- | --- |
| Panning by pinch conflicts with 2-finger scroll; 2 fingers moved together always scroll | Codex (high), agy (high) | Confirmed, `DesktopScreen.kt:588-608` | Fact added; Q4 now proposes a 3-finger pan |
| A pinch with one finger still can be taken as a scroll | agy | Partly confirmed: span and center move about the same, `DesktopScreen.kt:594-596,648-650`; the "2× slop" claim is overstated | Fact added |
| No release on cancel; fluxd releases only on link, permission or input changes | Codex (high), agy | Confirmed, `input.go:320-344,398-416` | Facts and outcome list every release path; fluxd limit stated |
| `holdAt(false)` adds a `moveTo` that loses the release's queue reserve | Codex | Refuted: `moveTo` doesn't clear `releases`, `input.go:252-258` | None; release reserve stated as a fact |
| An Android cancel can end the loop like a lift and click | agy (high) | Plausible from `DesktopScreen.kt:561-562,619-637`; unverified on a device | Fact (marked unverified); outcome: no cancel makes a click; test 5 |
| Relative pointer can leave the streamed monitor, also with several monitors | agy (high), Codex | Confirmed, `input.go:246-260` | Fact widened; outcome and Q5: pointer to the view's center on mode and monitor change |
| Rail doesn't scroll; a 7th button and the phone keyboard may overflow it | agy (high), Codex | Confirmed in code, `DesktopScreen.kt:197,233-242,280`; height unverified on a device | Fact; Q8; test 4 |
| Stale `focus` after switching modes | Codex (high), agy | Confirmed, `DesktopScreen.kt:408-417`, `DesktopViewport.kt:81-83` | Outcome: trackpad clicks clear `focus` |
| `typing.end()` doesn't commit composing text | Codex | Confirmed, `TouchpadScreen.kt:347-350`; same as a tap today | Test 4 covers typing after pointing; no new scope |
| Finger changes and hold semantics underspecified; 3-finger tap missing | Codex, agy | Confirmed, `DesktopScreen.kt:564-577`, `TouchpadScreen.kt:508-544` | Outcome item on finger changes; Q3; 3-finger tap added |
| Pan, then a second finger, then lift right-clicks | agy | Confirmed, `DesktopScreen.kt:564-568,633-636` | Fact added |
| `AppShell.kt` missing; scroll resolver and speed unspecified | Codex, agy | Confirmed, `AppShell.kt:544-546`, `RemoteInput.kt:48-52` | Affected files; Q6 |
| Pointer speed should account for zoom | agy | Agreed | Q6 |
| Landscape not guaranteed; split screen and the narrow layout's hint | Codex, agy | Confirmed in code, `DesktopScreen.kt:144-147,450-457`; targetSdk 36 (`build.gradle.kts:40`) lets Android 16 ignore the request on large screens | "Landscape only" replaced by "no new layout"; narrow-layout hint; test 8 |
| Remote input turning off traps the mode | agy | Confirmed, `DesktopScreen.kt:148,184` | Outcome: mode off with remote input; test 7 |
| Release must not go through `send`, which drops packets without control | agy | Confirmed, `DesktopScreen.kt:396-403` | Fact added; detail for the spec |
| Trackpad must be its own state, not a `Panel` | agy | Confirmed, `DesktopScreen.kt:107,141` | Fact added |
| Black bars give no position | agy | Confirmed, `DesktopViewport.kt:40-45` | Outcome: trackpad works over the bars |
| "Per app session" is really per visit | Codex, agy | Confirmed | Q2 reworded |
| Toast on both changes, like the slides switch | agy | Confirmed, `TouchpadScreen.kt:129-133` | Outcome reworded |
| wf-recorder passes no cursor flag | Codex, agy | Confirmed, `remotedesktop.go:180-186`; runtime behaviour unverified | Fact marked unverified |
| Video gestures have no TalkBack interaction | Codex, agy | Confirmed, `DesktopScreen.kt:424` | Constraint states the limit |
| Two relative clicks need no movement between them | Codex | Agreed | Outcome: tap jitter doesn't move; test 2 |
| "Scrolls on any motion"; lines 157/180; `Multi` set at 564; rail has non-switch buttons | Codex, agy | Confirmed | Corrected |
| "A finger is the only way to point" (mouse or stylus not filtered) | Codex | Agreed | Problem reworded |
| #2 needs a two-handed left button | agy | Not adopted: hold-then-move drags; can come later | None |
