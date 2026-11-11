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
- the remaining copy gets an idle deadline: each `Read` on its source first
  calls `SetReadDeadline(now + drainIdle)`, with `drainIdle = 30 * time.Second`.
  A reply that keeps arriving keeps going; a silent end ends after 30 s;
- when both copies have ended, `forward` closes both connections (the
  existing `defer`s).

While both directions are open there is no idle limit, because a Flux link
can be quiet for a long time. `drainIdle` is a package var so tests can
shorten it.

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
first. `forward` takes `ctx`: the dial uses `d.DialContext(ctx, …)`, and
`context.AfterFunc(ctx, …)` closes both connections when the node stops, so
open connections end with it.

### Part B: the webcam (`CameraSource.kt`)

**B1. Use physical keys only when the camera lists them.** A pure helper in
the companion object, next to `cropFor` (`CameraSource.kt:366`):

```kotlin
/** True when a request may set the crop of one physical lens. */
fun physicalCropSupported(sdk: Int, keyNames: Collection<String>?): Boolean =
    sdk >= Build.VERSION_CODES.P && keyNames?.contains("android.scaler.cropRegion") == true
```

`choiceFor` (line 148) computes it once from the **logical** camera's
`availablePhysicalCameraRequestKeys?.map { it.name }` (`null` on API < 28 or
when the camera lists none) and stores it in `Choice` as
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
so the size, orientation, fps and zoom come from the logical camera, not the
lens. `startSession` retries once with `logicalChoice(choice)` when the
choice has a `physicalId` and any of these happens:

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
  `android/tsnet`):
  - local side closes first: the tailnet side gets EOF, and `forward`
    returns within `drainIdle` (set to 100 ms) even when the tailnet peer
    never closes;
  - tailnet side closes first: a reply still being written by the local side
    after the EOF arrives in full, then `forward` returns;
  - 65 connections at once: the 65th is closed at once; after one ends, a new
    one is accepted;
  - cancelling the context closes the listener and the open connections, and
    no `forward`/`serve` goroutine remains (`runtime.NumGoroutine` back to its
    start, polled);
  - the existing round-trip test still passes.
- `android/app/src/test/java/org/omarchy/flux/webcam/CameraLensesTest.kt`:
  `physicalCropSupported` is false below API 28, for `null`, for a list
  without `android.scaler.cropRegion`, and true when the list has it.
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
