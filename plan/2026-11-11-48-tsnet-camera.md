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

## Review corrections (Codex plan review — approve or strike)

| # | Spec/plan said | Plan does | Why |
| --- | --- | --- | --- |
| R1 | the watchdog extends deadlines when the count moved | it sets `SetDeadline(now + drainIdle)` on both connections **at once** when the drain starts, then extends only on progress | a remaining copy that never moves a byte would never get a deadline, and 64 silent drains would use up every slot |
| R2 | a failed first request goes to `fail` and so to the retry | a per-attempt failure route `attemptFailed(attempt, reason)` on the handler: with a `physicalId` and the current attempt it starts the one logical retry, else it calls the caller's `fail`. Session callbacks and the first `setRepeatingRequest` use it; once a `setRepeatingRequest` has succeeded for an attempt, later request errors (a controls update) keep today's logging only | `fail` reports to the caller and does not retry; a controls update must not trigger a retry |
| R3 | CI runs after push | `build.yml` runs on pushes to `master` only, so step 3 runs `gh workflow run build.yml --ref fix/48-tsnet-camera` and checks that the run's commit is the branch HEAD | otherwise the Kotlin is not compiled before merge |
| R4 | `drainIdle` restored in `t.Cleanup`; a hook "when `forward` returns" | every forwarding test, including the existing round trip, waits for its listener, copies and watchdog to finish before it restores globals; the hook fires after the slot is released and the watchdog has exited | goroutines of one test could overlap the next test's changes to package vars |
| R6 | (agy spec review) `isSessionConfigurationSupported` says no **or throws** → retry | it says no, or throws anything **other than** `UnsupportedOperationException` → retry; `UnsupportedOperationException` means the HAL cannot answer the query, so the lens is tried as today | many HALs throw it for the query itself, which would push them off a lens that works |
| R7 | (agy spec review) the request keeps `createCaptureRequest(TEMPLATE_RECORD, setOf(physicalId))` | that 2-argument form only when `choice.physicalCrop`; otherwise the plain `createCaptureRequest(TEMPLATE_RECORD)`, which works with a session whose output is bound to a physical lens | the physical form is only needed to set physical keys, and it may be what fails on such a HAL |
| R5 | "tests per the spec's list" | the cases below are the acceptance criteria | a coder must not have to open the spec |

Step 1 cases (TCP loopback, short `drainIdle`): local side closes first while the tailnet peer stays silent, and the inner copy was already blocked in a read; tailnet side closes first and a multi-chunk reply with pauses shorter than `drainIdle` arrives in full; a peer that stops reading while the other side writes; a silent drain with no byte ever moving ends (R1); 2 listeners share 64 slots, the 65th on either is closed, and a slot is reused after one ends; `Accept` failing before cancel ends `serve` with no watcher left; cancel closes open connections; the existing round trip still passes.

Step 2 cases (`CameraLensesTest.kt`): `physicalCropSupported(null)` is false; a list without `android.scaler.cropRegion` is false; a list with it is true.

## Steps

1. **`android/tsnet/forward.go`, `android/tsnet/forward_test.go`** — A1, A2,
   A3. `serve` is at `forward.go:14-28`, `forward` at 30–51; `main.go:81`
   calls `serve(ctx, ln, target, logf)` per port and must keep compiling
   (only `forward`'s signature gains `ctx`). Tests per the spec's list, over
   TCP loopback, `drainIdle` set short with `t.Cleanup` restore, completion
   checked through channels (a small test hook, for example a package var
   `forwardDone func()` called when `forward` returns), never
   `runtime.NumGoroutine`. Apply R1 and R4.
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
   Apply R2, R6, R7.
   → verify: this host has no Android SDK; check the Kotlin by reading, then
   R3: `gh workflow run build.yml --ref fix/48-tsnet-camera` and its
   `android` job (`:app:testDebugUnitTest :app:lintDebug :app:assembleDebug`).
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
