---
status: approved
issue: 48
author: olafkfreund
---

# Intent: Fix the known bugs merged with #35 and #38

## Problem

The owner merged #35 (a userspace tailnet node in Flux for Android) and #38
(every lens of the phone as a webcam) as they were. Their reviews found
confirmed bugs that are now on master.

**#35: the tailnet forwarder leaks** (`android/tsnet/forward.go`).

- `forward` (lines 30–51) copies both ways and can hang either way. When the
  local side closes first, `forward` waits on `<-done` while the inner copy
  still reads the tailnet connection: 2 goroutines and both connections stay
  open. When the tailnet side closes first, the inner copy half-closes the
  local socket and ends, but the outer copy can keep reading a local service
  that keeps its side open. Either way the connection lives as long as the
  other end does.
- `serve` (lines 14–28) starts a goroutine per accepted connection with no
  limit, and `main.go:81` starts one `serve` per port. Its cancellation
  watcher stays when `Accept` fails first, and a cancel closes only the
  listener: open connections and their 5-second dial get no signal (the
  process exits at `main.go:89`, which reclaims them).

**#38: the webcam can show a black image** (`android/app/src/main/java/org/omarchy/flux/webcam/CameraSource.kt`).

- `repeat` (around lines 204–228) builds a capture request for one physical
  lens with `createCaptureRequest(TEMPLATE_RECORD, setOf(physicalId))` and
  sets `SCALER_CROP_REGION` with `setPhysicalCameraKey`, without checking
  that the logical camera lists that key in
  `availablePhysicalCameraRequestKeys` (API 28+, may be null). Any exception
  before the first repeating request is only logged (line 226), so no
  request runs and the stream stays black. Whether this exact setter throws
  on such a camera is not shown by the code; the missing check and the
  swallowed error are.
- `startSession` (lines 286–320) falls back to the logical camera only when
  `isSessionConfigurationSupported` says no (an exception there counts as
  yes, line 309). `onConfigureFailed` (line 303) and a throwing
  `createCaptureSession` (lines 315–318) only report a failure.
- The fallback (line 313) clears only `physicalId` and `activeArray`, so it
  keeps the physical lens's size, orientation and zoom limits, and at line
  161 a size the logical camera does not list can survive.

## Proposed outcome

- A tailnet connection never outlives both ends: whichever side closes
  first, the forwarder finishes the other direction within a bounded time and
  closes both, without cutting a response that the other side is still
  sending.
- At most 64 tailnet connections are forwarded at once across all ports of
  the process; more are closed at once, and capacity returns as connections
  end.
- Cancelling the node closes the listeners and every open connection, and
  `serve` leaves no goroutine behind.
- A webcam stream with a lens that the camera cannot control alone keeps
  running: it either drops the per-lens crop (no zoom on that lens) or falls
  back to the logical camera, instead of going black.
- A lens session that fails to configure, or whose creation throws, is
  retried once with the logical camera, using a configuration that is valid
  for the logical camera (its sizes, orientation and zoom); if that fails
  too, the webcam reports the error.
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

## Decisions (approved 2026-11-11)

The owner approved the recommended answer to each question below (64 connections across the process).


1. **Connection limit.** How many tailnet connections at once, and counted
   how? Recommended: 64 across all ports of the process, closing the ones
   over it at once.
2. **Lens without physical keys.** Keep the chosen lens without zoom, or fall
   back to the logical camera (zoom works, but the lens may not be the one
   picked)? Recommended: keep the lens and drop the per-lens zoom, since the
   user picked the lens.
3. **The other #35 findings** (10-minute link monitor on Android, "Change key"
   ignored, `lastIp` overwritten with `127.0.0.1`, start/stop race, log upload
   to `log.tailscale.com`): leave them to a separate issue? Recommended: yes;
   they need design and phone testing.
