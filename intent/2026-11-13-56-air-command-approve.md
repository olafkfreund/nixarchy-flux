---
status: draft
issue: 56
author: olafkfreund
---

# Intent: Approve does nothing when the S Pen Air Command button covers it

## Problem

On the owner's Galaxy Tab S8 Ultra (SM-X900, Android 16), tapping
**Approve** on a `sudo` request did nothing while the floating S Pen Air
Command button sat over it.
- No fingerprint prompt opened, and the request timed out after 20 s.
- After the owner moved the button, the same kind of request was approved
  with the fingerprint.
- The tablet is one of the devices the owner wants to use Flux on.

### Facts

- **The guard.** `ApproveActivity`, `SshSignActivity`, the pair sheet and
  the stream request refuse a tap when `Overlays.obscured` is true:
  - `ui/ApproveActivity.kt:131-136,279-282`;
  - `ui/SshSignActivity.kt:111-116,207`;
  - `ui/TiledScreens.kt:68`;
  - `ui/StreamRequestUi.kt:93-95,119`.

  On Android 12+, that is `FLAG_WINDOW_IS_OBSCURED`: another app's window
  lies over the touch point (`ui/Overlays.kt:27-34`). It guards against
  tapjacking: an overlay that shows a false request so the user taps
  Approve or Pair.
- **Hiding overlays.** `Overlays.hide` calls `setHideOverlayWindows(true)`
  (`Overlays.kt:37-39`). That hides `TYPE_APPLICATION_OVERLAY` windows
  only.
- **Air Command** is a window of the ordinary app
  `com.samsung.android.service.aircommand` (uid 10032), with Samsung's own
  window type 2401, from `dumpsys window`.
  - It is not hidden, and a tap under it is flagged as obscured.
  - The `MotionEvent` doesn't say which window covers the touch, so Flux
    can't tell Air Command from a hostile overlay.
- **The only feedback** is a toast, "Another app draws over Flux. Close
  that app, then approve."
  - It names no app, and Air Command isn't something a user thinks of as
    an app that is open.
  - The toast is gone in a few seconds, and the request then times out
    silently.
- **Approval also needs the fingerprint.** After Approve, the system's
  `BiometricPrompt` shows the real service, user and host
  (`ApproveActivity.kt:328-337`). The overlay guard is a second layer on
  top of that, which `docs/approve.md` describes.

## Proposed outcome

- When a tap on Approve, Pair or a stream request is refused for an
  overlay, the screen itself says so, in a message that stays until the
  next tap. It names the likely causes: an app's bubble or floating
  button, or the S Pen Air Command button. It says to move it away or
  close it, then tap again.
- The request stays open, as today, so a second tap after moving the
  button works within the timeout.
- `docs/troubleshooting.md` (or `docs/approve.md`) has a short entry for
  this on Samsung devices.
- The tapjacking guard itself stays as it is.

## Affected users and systems

- Android users with a floating window from another app, notably Samsung
  devices with an S Pen.
- `ui/ApproveActivity.kt`, `ui/SshSignActivity.kt`, `ui/TiledScreens.kt`,
  `ui/StreamRequestUi.kt`, possibly a shared composable in `ui/Overlays.kt`,
  and their UI tests.
- Docs: `docs/troubleshooting.md` or `docs/approve.md`.
- Not affected: `fluxd`, the protocol, iOS, macOS, Windows.

## Constraints

- No weaker tapjacking guard: no allow-list of packages, and no way to
  approve while the flag is set.
- No new permission. Reading other apps' windows needs permissions an app
  can't have, so the message can only name likely causes.
- Text follows the house style of the app's other strings.

## Open questions

1. **Allow-list Air Command, or only explain?** Proposed: only explain.
   - The flag can't say which window covers the touch, so an allow-list
     would have to trust every overlay while Air Command is installed.
     That defeats the guard.
   - The cost is one more tap after moving the button.
2. **All four screens, or Approve only?** Proposed: all four, through one
   shared message, because the same button blocks Pair, SSH signing and
   stream requests too.
3. **Test:** reproduce on the tablet with the button over Approve, check
   that the message shows and stays, then move the button and approve in
   the same request. Uses the #13 test setup that is still running.
