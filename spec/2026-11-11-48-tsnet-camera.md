---
status: draft
issue: 48
intent: intent/2026-11-11-48-tsnet-camera.md
---

# Spec: Fix the known bugs merged with #35 and #38

Approved decisions from the intent: at most 64 forwarded connections across
the process, closing the rest at once; a lens without physical request keys
keeps the lens and drops its per-lens zoom; the other #35 findings go to a
separate issue.

## Design

### Part A: the tailnet forwarder (`android/tsnet/forward.go`)

**A1. Both directions end, without cutting a reply.** `forward` keeps its two
copies, local→tailnet and tailnet→local. When either copy ends:

- it half-closes the write side of the other connection (`CloseWrite` when
  the connection has it, as the inner copy does today, `forward.go:41-44`),
  so the other end sees EOF and can finish its reply;
- it starts a single **drain watchdog** goroutine. The remaining copy counts
  its bytes in an `atomic.Int64`. Every `drainIdle / 2` the watchdog sets
  `SetDeadline(now + drainIdle)` on **both** connections when the count moved
  since its last look, and stops extending when it did not. `SetDeadline`
  applies to reads and writes that are already blocked, so a copy stuck in a
  read or in a write to a peer that stopped reading fails once the deadline
  passes. Only the watchdog sets deadlines, so there is no race between
  setting and refreshing them;
- when both copies have ended, `forward` stops the watchdog and closes both
  connections (the existing `defer`s).

This is an **idle timeout** after a half-close, `drainIdle = 30 * time.Second`:
a reply that pauses longer than 30 s after the other side closed is cut, and
a reply that keeps sending data keeps the connection for as long as it does.
That is the meaning of the intent's "bounded time": bounded by inactivity,
not by a total length. While both directions are open there is no limit,
because a Flux link can be quiet for a long time. `drainIdle` is a package
var so tests can shorten it.

**A2. A process-wide limit.** A package-level semaphore
`slots = make(chan struct{}, maxForwards)` with `maxForwards = 64`, shared by
every `serve` (one per port, `main.go:81`). `serve` takes a slot without
blocking before it starts `forward`; when none is free it closes the accepted
connection at once and logs "too many connections". `forward` gives the slot
back when it returns. Only accepted connections count; the dial to the local
side happens inside `forward`, under the slot.

**A3. Cancellation and cleanup.** `serve(ctx, …)` uses
`stop := context.AfterFunc(ctx, func() { ln.Close() })` with `defer stop()`,
so the watcher goroutine ends when `serve` returns, also when `Accept` fails
first. `forward` takes `ctx`. Before it dials it registers
`stopConn := context.AfterFunc(ctx, func() { conn.Close() })` for the
accepted connection, and after a successful `d.DialContext(ctx, …)` a second
`stopUp := context.AfterFunc(ctx, func() { up.Close() })`; both are deferred,
so a finished `forward` leaves no callback behind. A callback that already
started may still be closing when `forward` returns; closing twice is safe.
Every acquired slot (A2) has exactly one deferred release, also on dial
failure and on cancel.

### Part B: the webcam (`CameraSource.kt`)

**B1. Use physical keys only when the camera lists them.** A pure helper in
the companion object, next to `cropFor` (`CameraSource.kt:366`):

```kotlin
/** True when a request may set the crop of one physical lens. */
fun physicalCropSupported(keyNames: Collection<String>?): Boolean =
    keyNames?.contains("android.scaler.cropRegion") == true
```

`choiceFor` (line 148) computes it once from the **logical** camera's
`availablePhysicalCameraRequestKeys?.map { it.name }` (`null` when the camera
lists none; the app's `minSdk` is 29, so the method always exists) and stores it in `Choice` as
`physicalCrop: Boolean`. In `repeat` (lines 204–228), the per-lens crop
(`setPhysicalCameraKey(SCALER_CROP_REGION, …)`) is set only when
`choice.physicalCrop`; otherwise no crop is set for that lens (the lens
streams at 1×, the decided "keep the lens, drop its zoom"), and `zoomMax`
for that choice is 1 so the UI does not offer zoom it cannot do.

The request is still built with `createCaptureRequest(TEMPLATE_RECORD,
setOf(physicalId))`, because the session's output is bound to that lens
(`setPhysicalCameraId`, line 293). If building or sending the request throws
before the first repeating request runs, `repeat` reports it through the
same `fail` path as a session error (B2), instead of only logging it.

**B2. One logical retry, with a logical configuration.** A helper
`logicalChoice(choice)` rebuilds the choice for the logical camera through
`choiceFor(NamedLens(choice.name, Lens(choice.id, null, choice.facing, 0f)))`,
so the fps, zoom and crop come from the logical camera, not the lens. The
target `Surface` was made by the renderer for `choice.size` and orientation,
and the retry reuses it, so the retry is allowed only when the logical camera
lists `choice.size` among its `SurfaceTexture` sizes and its
`sensorOrientation` equals the lens's; the logical choice then keeps
`choice.size`. Otherwise the webcam reports the error instead of streaming a
wrong size.

Each session attempt gets a number from an `attempt` counter. Before a retry,
`startSession` closes the failed session, clears `session` and `active`, and
bumps the counter; `onConfigured`, `onConfigureFailed` and `repeat` ignore
callbacks of an older attempt, so a queued `setControls` cannot rerun
`repeat` on the failed physical session. Building the output configuration,
`logicalChoice`, and `createCaptureSession` all run inside the protected
block. `startSession` retries once with the logical choice when the choice
has a `physicalId` and any of these happens:

- `isSessionConfigurationSupported` says no, or throws (today a throw counts
  as yes, line 309);
- `onConfigureFailed` (line 303);
- `createCaptureSession` throws (lines 315–318);
- B1's first request fails.

A retry that fails again, or a failure with no `physicalId`, reports the
error through `fail` as today. The retry is logged with the lens and camera
IDs.

### Tests

- `android/tsnet/forward_test.go` (runs here with `go test` in
  `android/tsnet`), over real TCP loopback so half-close behaves as on the
  phone, with `drainIdle` set to 100 ms:
  - local side closes first while the tailnet peer stays silent: `forward`
    returns within a few `drainIdle`, also when the inner copy was already
    blocked in a read when the drain started;
  - tailnet side closes first: a multi-chunk reply written by the local side
    after the EOF, with pauses shorter than `drainIdle`, arrives in full;
  - a peer that stops reading while the other side writes: the blocked write
    fails after the drain and `forward` returns;
  - 2 listeners share the 64 slots: 64 connections across both, the 65th on
    either is closed at once, and after one ends a new one is accepted;
  - `Accept` failing before cancellation (closing the listener) ends `serve`
    with no watcher left, and cancelling closes the open connections; both
    checked with completion channels returned by test hooks, not
    `runtime.NumGoroutine`;
  - the existing round-trip test still passes.
- `android/app/src/test/java/org/omarchy/flux/webcam/CameraLensesTest.kt`:
  `physicalCropSupported` is false for `null` and for a list without
  `android.scaler.cropRegion`, and true when the list has it.
- CI (`build.yml`, job `android`) compiles the app and runs
  `testDebugUnitTest`.

## Alternatives rejected

- **Close both connections as soon as one direction ends.** Would cut a reply
  that the other side is still sending after a half-close.
- **A fixed overall lifetime per connection.** A Flux link is long-lived and
  may be idle; only the drain after a half-close is bounded.
- **A limit per port.** `main.go` starts one listener per port, so per-port
  limits would multiply; the decided limit is process-wide.
- **Fall back to the logical camera when physical keys are missing.** The user
  picked the lens; keeping it without zoom was the approved choice.
- **Reuse the lens's choice with `physicalId = null`** (today's fallback).
  Keeps sizes and zoom of the lens that the logical camera may not support.

## Risks

- **Deadlines on tsnet connections:** `SetReadDeadline` must be supported by
  the userspace `net.Conn` that tsnet returns; tsnet's gVisor connections
  support deadlines. If one did not, the drain would fall back to no limit;
  a test with a `net.Pipe` (which supports deadlines) covers the logic only.
- **Kotlin is not compiled here.** CI compiles it; a phone test by the owner
  confirms that a lens without physical keys streams and that the logical
  retry works.
- **`zoomMax = 1` for such a lens** hides zoom that the logical camera could
  do; that follows the approved decision.

## Verification

1. `cd android/tsnet && go vet ./... && go test -race ./... -count=1` passes.
2. Root `go vet`, `gofmt` and the suite are unchanged (the tsnet module is
   separate).
3. CI's `android` job passes (compile, lint, `testDebugUnitTest`).
4. `nix flake check` passes (unaffected, but run).
5. Owner, on a phone: the built-in tailnet node keeps working; a webcam with
   a non-default lens streams.
