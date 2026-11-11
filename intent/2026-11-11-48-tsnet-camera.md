---
status: draft
issue: 48
author: olafkfreund
---

# Intent: Fix the known bugs merged with #35 and #38

## Problem

The owner merged #35 (a userspace tailnet node in Flux for Android) and #38
(every lens of the phone as a webcam) as they were. Their reviews found
confirmed bugs that are now on master.

**#35: the tailnet forwarder leaks** (`android/tsnet/forward.go`).

- `forward` (lines 30–51) copies both ways. When the local side closes
  first, the main copy ends, but `forward` then waits on `<-done` for the
  other copy, which still reads the tailnet connection. A peer that keeps its
  side open leaves one goroutine and 2 file descriptors behind for each
  connection, for as long as that peer lives.
- `serve` (lines 14–28) starts a goroutine per accepted connection with no
  limit, so a peer can open connections without end.

**#38: the webcam can show a black image** (`android/app/src/main/java/org/omarchy/flux/webcam/CameraSource.kt`).

- `repeat` (around lines 204–228) builds a capture request for one physical
  lens with `createCaptureRequest(TEMPLATE_RECORD, setOf(physicalId))` and
  sets `SCALER_CROP_REGION` with `setPhysicalCameraKey`, without checking
  that the camera lists that key in `availablePhysicalCameraRequestKeys`.
  On a camera that does not, the call throws, the exception is only logged
  (line 226), no repeating request runs, and the stream stays black.
- `startSession` (lines 286–320) falls back to the logical camera when
  `isSessionConfigurationSupported` says no, but `onConfigureFailed`
  (line 303) only reports a failure, so a lens that the phone cannot stream
  alone still ends the webcam.

## Proposed outcome

- A tailnet connection whose one side closes ends both directions, so no
  goroutine or file descriptor is left behind, whichever side closes first.
- The forwarder holds a bounded number of connections at once and refuses or
  drops the rest.
- A webcam stream with a lens that the camera cannot control alone keeps
  running: it either drops the per-lens crop (no zoom on that lens) or falls
  back to the logical camera, instead of going black.
- A lens session that fails to configure falls back to the logical camera,
  as the `isSessionConfigurationSupported` path already does.
- Both fixes have tests where this host can run them: Go tests for the
  forwarder; for the camera, the logic that decides "use the physical keys or
  not" is a pure function with a JVM unit test, and CI compiles the app.

## Affected users and systems

- Android users who use the built-in tailnet node (#35) or the webcam with a
  non-default lens (#38).
- Code: `android/tsnet/forward.go` and its tests;
  `android/app/src/main/java/org/omarchy/flux/webcam/CameraSource.kt`, and a
  small helper plus its test under `android/app/src/test`.
- No desktop or protocol change.

## Constraints

- No protocol or setting change; the fixes stay inside the two files' logic.
- This host has no Android SDK: the Kotlin change is compiled and its unit
  test run by CI (`build.yml`, job `android`), and checked on a phone by the
  owner.
- Keep the change small, so it can be offered upstream with the owner's OK.

## Open questions

1. **Connection limit.** How many tailnet connections at once? Recommended:
   64, refusing (closing at once) the ones over it, since fluxd needs only a
   few per device.
2. **Lens without physical keys.** Keep the chosen lens without zoom, or fall
   back to the logical camera (zoom works, but the lens may not be the one
   picked)? Recommended: keep the lens and drop the per-lens zoom, since the
   user picked the lens.
3. **The other #35 findings** (10-minute link monitor on Android, "Change key"
   ignored, `lastIp` overwritten with `127.0.0.1`, start/stop race, log upload
   to `log.tailscale.com`): leave them to a separate issue? Recommended: yes;
   they need design and phone testing.
