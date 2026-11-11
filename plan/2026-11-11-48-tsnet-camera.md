---
status: draft
issue: 48
spec: spec/2026-11-11-48-tsnet-camera.md
coders:
  1: codex   # small, concurrency-tricky Go: drain watchdog, slots, cancellation, TCP tests
  2: codex   # small, tricky Kotlin: physical-key gate, attempt numbers, one guarded retry
reviewers:
  - codex    # diff review against this plan before the PR
  - agy      # second, independent diff review
---

# Plan: Fix the known bugs merged with #35 and #38

Branch `fix/48-tsnet-camera`, issue #48. Step 3 (verification, reviews, PR)
is the session model's. Steps 1 and 2 touch different files and can run one
after the other on the same Codex member.

## Approved decisions (from the spec)

- A1 `forward`: when either copy ends, half-close the other connection's
  write side (`CloseWrite` when available) and start one drain watchdog. The
  remaining copy counts bytes in an `atomic.Int64`; every `drainIdle/2` the
  watchdog sets `SetDeadline(now + drainIdle)` on both connections when the
  count moved, else stops extending. Only the watchdog sets deadlines.
  `drainIdle = 30 * time.Second`, a package var. No limit while both
  directions are open.
- A2 a package semaphore `slots = make(chan struct{}, maxForwards)`,
  `maxForwards = 64`, shared by every `serve`; take without blocking before
  starting `forward`, else close the accepted connection at once and log
  "too many connections"; exactly one release per slot.
- A3 `serve`: `stop := context.AfterFunc(ctx, func() { ln.Close() })`,
  `defer stop()`. `forward(ctx, …)`: `AfterFunc` for the accepted connection
  before the dial, `d.DialContext(ctx, …)`, a second `AfterFunc` for `up`
  after it; both deferred.
- B1 `physicalCropSupported(keyNames: Collection<String>?)` in the companion
  object (true only when it contains `android.scaler.cropRegion`); `choiceFor`
  stores `physicalCrop` from the logical camera's
  `availablePhysicalCameraRequestKeys?.map { it.name }`; `repeat` sets the
  per-lens crop only when `choice.physicalCrop`; such a choice without it has
  `zoomMax = 1`; a failure before the first repeating request goes to `fail`
  (and so to B2's retry).
- B2 `logicalChoice(choice)` via `choiceFor(NamedLens(choice.name,
  Lens(choice.id, null, choice.facing, 0f)))`, allowed only when the logical
  camera lists `choice.size` and has the lens's `sensorOrientation`, keeping
  `choice.size`; else report the error. An `attempt` counter: before a retry
  close the failed session, clear `session`/`active`, bump it; `onConfigured`,
  `onConfigureFailed` and `repeat` ignore older attempts. Output config,
  `logicalChoice` and `createCaptureSession` inside the protected block.
  Retry once when the choice has a `physicalId` and
  `isSessionConfigurationSupported` says no or throws, `onConfigureFailed`,
  `createCaptureSession` throws, or the first request fails.

## Steps

1. **`android/tsnet/forward.go`, `android/tsnet/forward_test.go`** — A1, A2,
   A3. `serve` is at `forward.go:14-28`, `forward` at 30–51; `main.go:81`
   calls `serve(ctx, ln, target, logf)` per port and must keep compiling
   (only `forward`'s signature gains `ctx`). Tests per the spec's list, over
   TCP loopback, `drainIdle` set short with `t.Cleanup` restore, completion
   checked through channels (a small test hook, for example a package var
   `forwardDone func()` called when `forward` returns), never
   `runtime.NumGoroutine`.
   → verify `cd android/tsnet && go vet ./... && test -z "$(gofmt -l .)" && go test -race ./... -count=3`.
   Traps: `slots` is shared by tests too, so each test that fills it must
   release everything before it ends; a `net.Conn` from tsnet may not have
   `CloseWrite`, keep the type assertion.

2. **`android/app/src/main/java/org/omarchy/flux/webcam/CameraSource.kt`,
   `android/app/src/test/java/org/omarchy/flux/webcam/CameraLensesTest.kt`** —
   B1, B2. `Choice` at line 61 gains `physicalCrop: Boolean`; `choiceFor` at
   148; `repeat` at 199–228; `startSession` at 286–320; the companion helpers
   near `cropFor` at 366. Unit test of `physicalCropSupported` in
   `CameraLensesTest.kt` (plain JUnit, as its existing tests).
   → verify: this host has no Android SDK; check the Kotlin by reading, then
   CI (`.github/workflows/build.yml`, job `android`, runs
   `:app:testDebugUnitTest :app:lintDebug :app:assembleDebug`) after push.
   Traps: `CameraSource` runs on its camera `handler` thread; keep every
   session field change on it; `zoomMax = 1` must not hide zoom for a lens
   that does support the crop.

3. **Session model** — rerun step 1's check; root `go vet`/`gofmt`/suite
   unchanged; `nix flake check path:.`; push and wait for CI's `android` job;
   Codex and agy diff reviews; PR linking intent, spec and plan, naming who
   did each step; note the owner's phone checks.

## Tests

| Command | Expected |
| --- | --- |
| `cd android/tsnet && go test -race ./... -count=3` | all pass, 3 times |
| CI `android` job | compile, lint and unit tests pass |
| `nix flake check path:.` | passes (unaffected) |
| Owner, on a phone | the built-in tailnet node keeps working; a webcam with a non-default lens streams; a lens that cannot stream alone falls back |

## Rollback

Revert the commits. No protocol, setting or storage change.
