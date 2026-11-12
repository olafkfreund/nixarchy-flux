---
status: draft
issue: 45
spec: spec/2026-11-12-45-resume-followups.md
coders:
  1: codex   # small, concurrency-tricky Go: worker ordering, error classes, lock discipline
  2: codex   # tricky race tests on the same worker, warm context from step 1
reviewers:
  - codex    # diff review against this plan before the PR
  - agy      # second, independent diff review
---

# Plan: Resumable receives — history noise, false stalls, reconnects and weak tests

Branch `fix/45-resume-followups`, issue #45. Step 3 (verification, reviews,
PR) is the session model's. Line numbers are those of `master` at 70f39aa.

## Approved decisions (from the spec)

- **D1 Order for a packet with no run** (`processResumePacket`,
  `resume.go:212-243`):
  1. **Prune.** For offers only, `pruneIncoming` runs as today (lines 216-222).
  2. **Check.** `checkResumable(dev, l, b) (resumeHead, error)` takes the head
     of `receiveResumable` (lines 423-475) and changes nothing on disk except
     `MkdirAll(d.resumeDir)`. In order, it checks:
     - the metadata;
     - the link: `l.Done()` not closed, and `dev.link == l`;
     - that the device is paired and `d.ctx` is alive;
     - the record: metadata changed, canceled, or finished (`head.done`);
     - "no offer for this transfer";
     - `checkSpace(d.resumeDir, b.Size - dataSize)`, where `dataSize` is 0
       when the file is missing;
     - for a new offer, `admitOffer(dev.ID, false)`.
  3. **On failure,** send the error reply and call `refused`, then return.
     When `head.done`, reply `{done, Offset: record.Size}` and return.
  4. **Slot.** If `startReceive` fails, send the error reply and call
     `refused`, then return.
  5. **Run.** `newTransfer`, then the run with `dev: pkt.dev` and
     `link: pkt.link`, then `receiveResumable(ctx, dev, l, p, b, run, head)`.

  `type resumeHead struct { record incomingRecord; exists, done bool }`.
- **D2 `receiveResumable` with a head.**
  - Its first step, when `!head.exists`: call `admitOffer(dev.ID, true)`,
    then save `state.json` and `touch()`. Both come before `checkPeer`.
  - It keeps `checkPeer`, and it keeps `r.Path != ""` → "done".
  - Everything from line 476 on is unchanged, apart from D6 and D9.
  - `admitOffer(device string, evict bool)`: with `false`, it skips the
    removal loop (lines 392-399).
- **D3 An existing run.**
  - When `pkt.dev != run.dev`, end the old run with `errReceiveUnpaired`,
    then go on as for a packet with no run (D1).
  - Otherwise call `checkResumable`, then `receiveResumable`. D5 decides
    what an error does to the run.
- **D4 Refusal log.**
  - A daemon field `resumeRefusals map[string]*refusalLog` guarded by `d.mu`,
    with `refusalLog{last time.Time; skipped int}`.
  - `func (d *Daemon) refused(dev *Device, err error)` reads `dev.Name` and
    updates the entry under `d.mu`, then logs after it unlocks:
    - the first refusal logs `resumable transfer from %s refused: %v`;
    - a refusal within `receiveIdle` of the last line only increments
      `skipped`;
    - the next refusal after that logs `… refused: %v (and %d more)` and
      resets `skipped`.
- **D5 Errors that keep the run** (lines 247-257).
  - The run is kept only when `run.ctx.Err() == nil`, `d.stillPaired(run.dev)`
    and `!run.published` all hold, and the error is one of:
    - `errors.Is(err, errLinkDown)`;
    - `errors.Is(err, errTransport)`;
    - the packet context ended while `run.ctx` is alive. This replaces the
      `resumeStop` call at line 249 for that case.
  - A kept run still sends the reply. It does not call `endResumeRun`, and it
    changes neither `run.last` nor `run.link`.
  - Any other error ends the run as today.
  - A success sets `run.last = time.Now()` and `run.link = pkt.link`.
  - The rule never looks at `link.Done()`.
- **D6 `errTransport`.**
  - `var errTransport = errors.New("the transfer connection failed")`.
  - Wrap with it only the error of `l.FetchPayload` and of the read side of
    the payload copy, through a reader around `rc` that wraps a non-EOF read
    error.
  - `f.Sync` and the write side are not wrapped.
  - `var chunkTimeout = time.Minute` replaces the constant at line 498.
- **D7 Per-run stall check.**
  - `resumeWorker.queued map[string]int`, guarded by `d.mu`.
    - `handleResumable` increments it when a packet goes into `w.queue`.
    - The worker decrements it after `processResumePacket` returns, and
      deletes the key at 0.
  - `sweepResumeRuns` copies `queued[id]` for each `id` in `w.runs` under
    `d.mu`, unlocks, and then works from the copy.
  - The sweep checks, in order:
    1. `run.ctx.Err()`: `resumeStop`;
    2. `!d.stillPaired(run.dev)`: `errReceiveUnpaired`;
    3. `queued[id] == 0 && time.Since(run.last) >= receiveIdle`:
       `errLinkDown` if `run.link` is closed, else `errStalled`.
  - The closed-link check at lines 283-286 is removed.
- **D8 `endResumeRun`** calls `d.endReceive(run.dev)`. `resumeRun` gets the
  field `dev *Device`.
- **D9 Test hooks** (package variables, nil or the default value in
  production):
  - `var afterSweep func()`, called at the end of `sweepResumeRuns`;
  - `var copyAcross = io.Copy`, used at line 598;
  - `chunkTimeout` (D6).
- **D10** No protocol, storage or `share.go` change.

## Steps

1. **`internal/core/resume.go`, `internal/core/daemon.go`** (the
   `resumeRefusals` field next to `resumeWorkers`, line 56, initialised at
   line 383): D1–D9.
   - `processResumePacket` lines 212-261, `sweepResumeRuns` 280-294,
     `endResumeRun` 296-326, `admitOffer` 365-401, `receiveResumable`
     422-629, `handleResumable` 173-175, `runResumeWorker` 188-191.
   - → verify: `go vet ./internal/core/ && test -z "$(gofmt -l internal)" && go test -race ./internal/core/ -run Resume -count=2`.
     Every existing test passes. `TestResumeLinkDropKeepsPartialAndResumes`
     now fails after `receiveIdle`, with the same message.
   - Traps:
     - `resumeStop` and `endResumeRun` take `d.mu`. Never call them while
       holding it, and never hold it across file I/O.
     - `w.runs`, `run.last`, `run.link` and `run.dev` belong to the worker
       only. `w.queued` and `resumeRefusals` are guarded by `d.mu`.
     - Do not add a goroutine per packet.
     - Keep every #43 limit: `maxReceives`, `maxPartials`, `maxRecords`, both
       space checks, `spaceWriter`, the pairing and current-link checks in
       `publish()`, and the cancel tombstone.
     - `handleResumable` already validates the metadata before the queue
       (line 159). Keep that check as well.

2. **`internal/core/resume_test.go`**: the weak tests (spec section 5) and the
   new tests (spec section 6), including their sync points.
   - Rewrite `TestResumeSlowChunkIsNotIdle` (line 743) and
     `TestResumeCancelWithFullEvents` (line 655). Keep
     `TestResumeCancelRacesCompletion` (line 684).
   - Add these tests:
     - `TestResumeCancelBeforePublishWins`, `TestResumeCancelAfterPublishKeepsFile`;
     - `TestResumeRefusalsAddNoHistory`, with 6 sub-tests;
     - `TestResumeFifthOfferKeepsRecords`, `TestResumeExpiredPartialsArePruned`;
     - `TestResumeReofferDoneWhileBusy`, `TestResumeRefusalLogIsLimited`;
     - `TestResumeQueuedPacketIsNotStalled`, `TestResumeBusyWorkerStillStallsSilentRun`;
     - `TestResumeReconnectKeepsRun`, `TestResumeStaleLinkPacketKeepsRun`;
     - `TestResumeLinkDropMidChunkKeepsRun`, `TestResumeFailingPacketsDoNotExtendRun`;
     - `TestResumeUnpairEndsIdleRun`, `TestResumePayloadStall`;
     - `TestResumeCrossFsCopyFails`, `TestResumeCrossFsCopyLinkDrop`.
   - Setups and expected results: the spec's table in section 6, copied here
     for the coder:
     - **Refusals:** an error reply. The history length is unchanged, and the
       peer's `d.content.receives` count is unchanged.
     - **Fifth offer:** refused, and the tombstone is kept.
     - **Expired:** 16 unfinished records with `Chtimes` older than 7 days.
       The new offer is accepted.
     - **Done while busy:** reply `done`, and no entry.
     - **Log:** capture `d.logger` with a writer guarded by a mutex. Wait
       more than 1.25 × `receiveIdle` for the worker to retire. The 4th
       refusal logs "(and 2 more)".
     - **Queued:** A is stalled in `stubSpace` for longer than `receiveIdle`
       while B's chunk is queued. After the release, B answers "offset" with
       the same ID.
     - **Busy-silent:** a 2nd packet of A is queued before the release, and B
       sends nothing. B ends `errStalled`.
     - **Reconnect:** close, wait for the old `onPhone.Done()` and one
       `afterSweep`, attach, then the next chunk. The same ID ends "done",
       with no "failed" entry.
     - **Stale:** a 2nd link while the old one stays open, and a chunk on the
       old one. An error reply; the run continues on the new link.
     - **Mid-chunk:** cut after 500 bytes by a close, then wait, attach and
       offer. The reply is offset 500, with the same entry.
     - **Failing:** stale packets every `receiveIdle/4`. The run still ends
       within about `receiveIdle` of its last success.
     - **Unpair:** `shortIdle(400ms)`. "failed" `errReceiveUnpaired` within
       about 100 ms.
     - **Payload stall:** `chunkTimeout = 200ms`, `shortIdle(400ms)`. `data`
       has 500 bytes, and the next offer answers 500.
     - **Copy fails:** `renameFile` returns `EXDEV`, and `copyAcross` writes
       1000 bytes then errors. "failed"; no file or `.flux-*.part`; `data`
       is whole. With the hook restored, a re-offer answers "done".
     - **Copy link drop:** the hook closes the link after 1000 bytes and
       waits for `onPhone.Done()`, then copies on. The run is kept, with no
       file or `.part`; a reattach and re-offer publishes.
   - → verify: `go test -race ./internal/core/ -run Resume -count=5`, all pass.
   - Traps:
     - Register the restore of `receiveIdle` or `chunkTimeout`, and the
       `afterSweep`, `copyAcross` and `renameFile` hooks, **before** `newRx`.
       Restoring them in `t.Cleanup` must run after `waitWorkers`.
     - Never wait for a result with a fixed sleep; use `waitFor`,
       `afterSweep` or the stub channels. Use a sleep only to let more than
       `receiveIdle` pass.
     - `stubSpace` stalls every `checkSpace` while armed. Arm it right before
       the call you want, and remember that the space checks now sit in
       `checkResumable`.
     - Do not change any test outside the resume tests.

3. **Session model:**
   - rerun the step checks;
   - root `go vet ./... && test -z "$(gofmt -l .)"` and `make vet test`;
   - `nix flake check path:.`;
   - the spec's six mutation checks (Verification 5), each reverted after it
     shows the named test failing;
   - Codex and agy diff reviews;
   - the PR, linking intent, spec and plan, and naming who did each step.

## Tests

| Command | Expected |
| --- | --- |
| `go test -race ./internal/core/ -run Resume -count=5` | all pass, 5 times |
| `make vet test` | passes, as CI runs it |
| `nix flake check path:.` | passes |
| Mutations (spec, Verification 5) | each makes the named test fail |

## Rollback

Revert the commits. Nothing on disk or in the protocol changes, so an older
fluxd reads the same `incoming/` folders.
