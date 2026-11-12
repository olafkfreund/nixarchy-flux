---
status: approved
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
PR) is the session model's. Line numbers refer to `internal/core/resume.go` on
`master` at 70f39aa unless a file is named. This plan is self-contained; the
coder does not need the spec.

## Decisions

The decisions come from the approved spec. Corrections from the plan review
are marked R1–R9 and listed in the table at the end. **R1 changes spec
section 3**: approve or strike it.

- **D1 A packet with no run** (`processResumePacket`, lines 212-243), in
  this order:
  1. **Prune.** Offers only: `pruneIncoming`, as today (lines 216-222).
  2. **Check.** `checkResumable(dev, l, b) (resumeHead, error)`, where
     `type resumeHead struct { record incomingRecord; exists, done bool }`.
     It changes nothing on disk except the `MkdirAll` below. In order:
     1. `os.MkdirAll(d.resumeDir, 0o700)`, first, so `checkSpace` works on a
        fresh install.
     2. The metadata, as at line 424.
     3. Under `d.mu`, in the same order as `checkPeer` (lines 428-437)
        (R6):
        - `!dev.Paired` → `errReceiveUnpaired`;
        - `l.Done()` is closed, or `dev.link != l` → `errLinkDown`;
        - `d.ctx.Err()` → that error.
     4. The record (lines 448-462): changed metadata → error; canceled →
        error; `Path != ""` → `head.done`.
     5. No record and `b.Action != "offer"` → "no offer for this transfer".
     6. `dataSize` is the size of `data`, 0 when the file is missing.
        `dataSize > b.Size` → "partial file exceeds the transfer size" (R5).
        Then `checkSpace(d.resumeDir, b.Size-dataSize)`.
     7. No record: `admitOffer(dev.ID, false)`.
  3. **Refusal.** On an error: send `{Action: "error", ID: b.ID, Error: err.Error()}`,
     call `refused(dev, err)`, and return. On `head.done`: send
     `{Action: "done", ID: b.ID, Offset: head.record.Size}` (R9) and return,
     with no log line.
  4. **Slot.** `startReceive` fails → send the existing error reply, call
     `refused`, and return.
  5. **Run.** `newTransfer`, then `resumeRun{…, dev: pkt.dev, link: pkt.link, last: now}`,
     then `receiveResumable(ctx, dev, l, p, b, run, head)`.
- **D2 `receiveResumable` with a head.**
  - Its first step: when `!head.exists`, call `admitOffer(dev.ID, true)`,
    then `saveJSON(metaPath, head.record)` and `touch()`. All of these come
    before its `checkPeer`.
  - It keeps `checkPeer`, and it keeps the record check, including
    `Path != ""` → "done".
  - It **removes** the space check at lines 489-493, which `checkResumable`
    now does (R5).
  - Apart from that, line 476 onward is unchanged except for D6 and D9.
  - `admitOffer(device string, evict bool)`: with `false` it skips the
    removal loop at lines 392-399. The count and the `maxPartials` refusal
    run in both cases.
- **D3 A packet of an existing run.**
  - When `pkt.dev != run.dev`, the device was replaced. End the old run with
    `endResumeRun(…, errReceiveUnpaired)`, then handle the packet as in D1.
  - Otherwise call `checkResumable`. On an error, skip `receiveResumable`
    and use that error in D5. On `head.done`, reply "done" and end the run
    with `nil`. Otherwise call `receiveResumable`.
- **D4 Refusal log.**
  - A daemon field, `resumeRefusals map[string]*refusalLog`, keyed by
    `dev.ID` and guarded by `d.mu`. It is initialized in `New` next to
    `resumeWorkers` (`daemon.go:383`), and `refused` initializes it lazily
    when it is nil (R8).
  - `type refusalLog struct { last time.Time; skipped int }`.
  - `func (d *Daemon) refused(dev *Device, err error)`: under `d.mu` it reads
    `dev.Name` and the entry, then decides what to log:
    - no entry, or `now.Sub(last) >= receiveIdle`: log, set `last = now`
      and reset `skipped`;
    - otherwise: `skipped++` and no log line.

    It unlocks before it logs. The line is
    `resumable transfer from %s refused: %v`, with ` (and %d more)` added
    only when `skipped > 0` (R9).
- **D5 Ending or keeping the run after a packet** (lines 244-260):
  1. **Normalise first, as today (R3).** If
     `(stopped || run.ctx.Err() != nil) && !run.published && (err == nil || errors.Is(err, context.Canceled))`,
     then `err = d.resumeStop(run)`. A packet context that ended through its
     link, while `run.ctx` is alive, therefore becomes `errLinkDown`, and a
     real error such as a failed `f.Sync` stays what it is.
  2. **Unpaired.** If `!d.stillPaired(run.dev)` and `err` is `nil`,
     `errLinkDown` or `errTransport`, then `err = errReceiveUnpaired`.
  3. **Keep the run** when all of these hold:
     - `err` is `errLinkDown` or `errTransport` (`errors.Is`);
     - `run.ctx.Err() == nil`;
     - `d.stillPaired(run.dev)`;
     - `!run.published`.

     Send the reply, and do not call `endResumeRun`. `run.last` and
     `run.link` stay unchanged.
  4. **Otherwise,** do what the code does today: an error, `done`, `stopped`
     or `run.ctx` ended → `endResumeRun`.
  5. **On success,** set `run.last = time.Now()` and `run.link = pkt.link`.

  The rule never reads `link.Done()`.
- **D6 `errTransport` (R2).**
  - Define `var errTransport = errors.New("the transfer connection failed")`.
  - Wrap these, with `fmt.Errorf("%w: %w", errTransport, err)`:
    - the error of `l.FetchPayload` (line 500);
    - a non-EOF read error of `rc`, through a small reader around `rc`
      (`src` keeps its `countingReader`);
    - `io.EOF` from `io.CopyN` at line 516, which means the payload ended
      short. `os.File.Write` never returns `io.EOF`.
  - `f.Sync` and write errors are not wrapped.
  - `var chunkTimeout = time.Minute` replaces the `time.Minute` at line 498.
- **D7 Per-run stall check, with R1.**
  - `resumePacket` gets `at time.Time`, set in `handleResumable`.
  - `resumeWorker.queued map[string][]time.Time` holds the arrival times of
    each ID's queued packets, oldest first. It is guarded by `d.mu` and
    initialized at line 169 (R8).
    - `handleResumable` appends `at` when the packet goes into `w.queue`
      (line 174).
    - The worker removes the first time for that ID after
      `processResumePacket` returns, and deletes the key when the slice is
      empty.
  - `sweepResumeRuns` copies, under `d.mu`, the oldest queued time for each
    `id` in `w.runs`, then unlocks. It then checks each run in order:
    1. `run.ctx.Err() != nil` → `resumeStop(run)`;
    2. `!d.stillPaired(run.dev)` → `errReceiveUnpaired`;
    3. `time.Since(run.last) >= receiveIdle`, and there is no queued packet
       for `id` with `at` before `run.last + receiveIdle` →
       `errLinkDown` when `run.link` is closed, else `errStalled`.
  - Remove the closed-link check at lines 282-287.
- **D8 `endResumeRun`** calls `d.endReceive(run.dev)`. `resumeRun` gets a new
  field, `dev *Device`.
- **D9 Test hooks.** All are package variables, nil or the default in
  production:
  - `var afterSweep func()`, called at the end of `sweepResumeRuns` when it
    is not nil;
  - `var copyAcross = io.Copy`, which replaces the `io.Copy` at line 598;
  - `chunkTimeout` (D6).
- **D10** No change to the protocol, the on-disk format or `share.go`.

## Test cases (acceptance criteria for step 2)

Common setup:
- `shortIdle(t, d)` and every hook are set, and their restores registered,
  **before** `newRx`. `t.Cleanup` runs in reverse order, so the restores run
  after `waitWorkers`.
- `afterSweep` is assigned before the first packet. It signals with a
  non-blocking send on a buffered channel and never blocks (R7).
- Results are waited for with `waitFor`, `afterSweep` or the stub channels.

### The weak tests, rewritten

- **`TestResumeSlowChunkIsNotIdle`** (`resume_test.go:743`)
  - Keep the transfer ID from `r.find(f)` after the offer.
  - After the slow chunk's reply, wait for 2 `afterSweep` calls, with nothing
    sent.
  - Then the same ID is "active", and the history has exactly 1 entry with
    `f.name`.
  - Then send the next chunk, which answers "offset".
- **`TestResumeCancelWithFullEvents`** (line 655)
  - `shortIdle(t, 200*time.Millisecond)` before `newRx`.
  - Keep today's flow: offer A, chunk, stall the space check, offer B, wait
    for `s.entered`, fill `w.events`, then `CancelTransfer(A)`.
  - Then **empty `w.events`** from the test, and only after that call
    `release()`.
  - Expect A's record to have `Canceled`, and A's `data` to be gone.
- **`TestResumeCancelRacesCompletion`** (line 684) is unchanged.
- **`TestResumeCancelBeforePublishWins`**
  - Offer, then chunk 1. Arm `stubSpace.stall()`, send the last chunk, and
    wait for `s.entered`: the space check in `checkResumable`.
  - `CancelTransfer`, then `release()`.
  - Expect state "canceled", no file in downloads, `record.Canceled`, and
    `data` deleted.
- **`TestResumeCancelAfterPublishKeepsFile`**
  - `renameFile` signals, then blocks on a channel, then calls `os.Rename`.
  - Send the last chunk and wait for the signal: by then `publish()` has set
    "done".
  - `CancelTransfer`, then unblock.
  - Expect state "done", the file in downloads, `record.Path != ""` and
    `!record.Canceled`.

### New tests

| Test | Setup | Expected |
| --- | --- | --- |
| `TestResumeRefusalsAddNoHistory` | 6 sub-tests, each sending one packet: (a) a chunk with no offer; (b) a retry of a canceled ID; (c) a 17th unfinished offer (16 records with no path, not canceled); (d) a new offer with `stubSpace` low; (e) a re-offer of an existing partial with `stubSpace` low; (f) a 5th offer while 4 runs are active | An error reply for the ID. The history length is unchanged, and `d.content.receives[peer]` is unchanged (0, or 4 in (f)). |
| `TestResumeFifthOfferKeepsRecords` | `maxRecords` records of the peer, one of them a cancel tombstone, 4 of them active runs; then a 5th new offer | Refused. Every record folder, including the tombstone, is still there. |
| `TestResumeExpiredPartialsArePruned` | 16 unfinished records, with folder times set by `os.Chtimes` to 8 days ago; then a new offer | `offset` 0, and the old folders are gone. |
| `TestResumeReofferDoneWhileBusy` | A finished transfer; then 4 active runs; then a re-offer of the finished ID | `done` with `Offset == size`, and no new history entry. |
| `TestResumeRefusalLogIsLimited` | Capture `r.b.logger` with a writer guarded by a mutex. `shortIdle(200ms)`. 3 chunks with no offer; wait 300 ms, past the worker's retirement at 1.25 × `receiveIdle`; 1 more | 1 line after the first 3. The 4th logs a second line ending in "(and 2 more)". |
| `TestResumeQueuedPacketIsNotStalled` | `shortIdle(200ms)`; runs A and B with one chunk each; stall the space check; send A's chunk and wait for `entered`; send B's chunk; sleep 300 ms; `release()` | B's chunk answers "offset", B's history ID is unchanged, and B is not "failed". |
| `TestResumeBusyWorkerStillStallsSilentRun` | As above, but B sends nothing, and a 2nd packet of A is queued before `release()` | B ends "failed" with `errStalled`. A is still in `w.runs`, or still running. |
| `TestResumeReconnectKeepsRun` | `shortIdle(400ms)`; offer, chunk; `r.onDesk.Close()`; wait for the old `onPhone.Done()` and 1 `afterSweep`; `r.attach()`; the next chunks on the new link | The same history ID ends "done", and there is no "failed" entry for `f.name`. |
| `TestResumeStaleLinkPacketKeepsRun` | Offer and chunk on link 1; attach link 2 and keep link 1 open; a chunk on link 1 | An error reply on link 1. The run keeps its ID, and the next chunk on link 2 answers "offset". |
| `TestResumeLinkDropMidChunkKeepsRun` | A chunk of 1000 bytes whose payload reader sends 500 and then closes `r.onDesk`; wait for `onPhone.Done()`; attach; offer | `offset` 500, and the same ID is still active. |
| `TestResumeFailingPacketsDoNotExtendRun` | `shortIdle(400ms)`; a run with one success; then chunks on a stale, open link every 100 ms | The run ends "failed", `errStalled` or `errLinkDown`, within 400 ms + 300 ms of its last success. |
| `TestResumeUnpairEndsIdleRun` | `shortIdle(400ms)`; offer; set `peer.Paired = false` under `r.b.mu` | "failed" with `errReceiveUnpaired` within 250 ms, well before `receiveIdle`. |
| `TestResumePayloadStall` | `chunkTimeout = 200ms` and `shortIdle(400ms)`; a 1000-byte chunk whose payload sends 500 bytes and then blocks, on a link that stays open | An error reply. "failed" once idle. `data` has 500 bytes, and the next offer answers 500. |
| `TestResumeCrossFsCopyFails` | `renameFile` returns `syscall.EXDEV`; `copyAcross` writes 1000 bytes, then returns `errors.New("disk failed")` | "failed". No file and no `.flux-*.part` in downloads. `record.Path == ""` and `data` is whole. Then, with both hooks restored, a re-offer answers "done" and publishes. |
| `TestResumeCrossFsCopyLinkDrop` | `renameFile` returns `EXDEV`; `copyAcross` writes 1000 bytes, closes `r.onDesk`, waits for `onPhone.Done()`, then `io.Copy`s the rest | The run is kept: not "failed" before `receiveIdle`. No file and no `.part`. After `r.attach()` and the hooks restored, a re-offer publishes. |

The existing `TestResumeLinkDropKeepsPartialAndResumes` (line 601) keeps its
assertions. It now ends after `receiveIdle` (400 ms), with the same message.

### Mutation checks (step 3)

| Mutation | Test that must fail |
| --- | --- |
| Remove `run.last = time.Now()` on success | `TestResumeSlowChunkIsNotIdle` |
| Remove the `tick.C` branch | `TestResumeCancelWithFullEvents` |
| Replace the queued check with `len(w.queue) > 0` | `TestResumeBusyWorkerStillStallsSilentRun` |
| Drop the queued check | `TestResumeQueuedPacketIsNotStalled` |
| Call `startReceive`/`newTransfer` before `checkResumable` | `TestResumeRefusalsAddNoHistory` |
| Set `run.last` also on a kept (failed) packet | `TestResumeFailingPacketsDoNotExtendRun` |

## Steps

1. **`internal/core/resume.go`, and `internal/core/daemon.go`** for the
   `resumeRefusals` field next to `resumeWorkers` (line 56) and its init at
   line 383: D1–D9.
   - The code to change:
     - `handleResumable` lines 163-175, including the worker literal at 169;
     - `runResumeWorker` 188-191;
     - `processResumePacket` 212-261;
     - `sweepResumeRuns` 280-294;
     - `endResumeRun` 296-326;
     - `admitOffer` 365-401;
     - `receiveResumable` 422-629.
   - → verify: `go vet ./internal/core/ && test -z "$(gofmt -l internal)" && go test -race ./internal/core/ -run Resume -count=2`.
     Every existing test passes.
   - Traps:
     - `resumeStop`, `endResumeRun`, `stillPaired`, `startReceive` and
       `refused` take `d.mu`. Never call them while holding it, and never hold
       it across file I/O.
     - Read `dev.Paired`, `dev.link` and `dev.Name` only under `d.mu`.
     - `w.runs`, `run.last`, `run.link` and `run.dev` belong to the worker.
       `w.queued` and `resumeRefusals` are guarded by `d.mu`.
     - Do not add a goroutine per packet.
     - Keep every #43 limit: `maxReceives`, `maxPartials`, `maxRecords`, the
       space checks in `checkResumable` and at line 584, `spaceWriter`, the
       pairing and current-link checks in `publish()`, and the cancel
       tombstone.
     - Keep the metadata check in `handleResumable` (line 159).

2. **`internal/core/resume_test.go`**: the tests above.
   - → verify: `go test -race ./internal/core/ -run Resume -count=5`, all pass.
   - Traps:
     - `stubSpace` stalls every `checkSpace` while it is armed. Arm it just
       before the packet you mean, and remember that the space checks are now
       in `checkResumable`.
     - Use a fixed sleep only to let more than `receiveIdle` pass.
     - Change no test outside the resume tests, apart from what `outbox_test.go`
       needs, if anything.

3. **Session model:**
   - rerun the step checks;
   - root `go vet ./... && test -z "$(gofmt -l .)"` and `make vet test`;
   - `nix flake check path:.`;
   - the mutation checks, each reverted after it shows its failure;
   - Codex and agy diff reviews;
   - the PR, linking intent, spec and plan, and naming who did each step.

## Tests

| Command | Expected |
| --- | --- |
| `go test -race ./internal/core/ -run Resume -count=5` | all pass, 5 times |
| `make vet test` | passes (the CI target) |
| `nix flake check path:.` | passes |
| Mutation checks (table above) | each makes its test fail |

## Rollback

Revert the commits. Nothing on disk or in the protocol changes.

## Review corrections (Codex and agy plan review — approve or strike)

| # | Draft said | Plan does | Why |
| --- | --- | --- | --- |
| R1 | (Codex 3, 5) Spec §3: a run with any queued packet is never idle | a queued packet protects a run only if it **arrived** before `run.last + receiveIdle` (D7) | a backlog of failing packets would otherwise keep a run alive forever. A packet that arrives after the deadline would not have saved the run anyway, which also settles the race between the sweep's copy and a new arrival. **This changes spec §3.** |
| R2 | (Codex 1, agy 1) wrap only non-EOF read errors | also `io.EOF` from `io.CopyN`, a short payload (D6) | otherwise a payload that ends before the link close ends the run |
| R3 | (Codex 2, agy 4) a packet context that ended keeps the run | keep today's normalisation through `resumeStop` first, then the keep rule on `errLinkDown`/`errTransport` (D5) | a write error or a daemon stop must not be kept, or relabelled as a cancel |
| R4 | (Codex 4, agy 2) test names only | every test's setup, sync point and assertions, plus the mutation table, are in this plan | the coder reads only the plan |
| R5 | (agy 3) "unchanged from line 476" | remove the space check at 489-493; check `dataSize > b.Size` in `checkResumable` | no double space check, and no run for an oversized partial |
| R6 | (agy 5) link before pairing | pairing first, then the link (`errLinkDown`), then `d.ctx`, as `checkPeer` does; all under `d.mu` | an unpaired peer gets `errReceiveUnpaired`, and a closed link gets the class that keeps the run |
| R7 | (agy 6) only restore hooks before `newRx` | also assign `afterSweep` before the first packet; it never blocks | `-race` reports a write after the worker reads it, and the final sweep at shutdown must not hang |
| R8 | (agy 6) nil maps | `queued` in the worker literal (line 169); `resumeRefusals` in `New` and lazily in `refused` | tests that build `&Daemon{}` without `New` |
| R9 | (agy 7) reply and log details | the "done" reply carries `ID`; "(and N more)" only when N > 0; keyed by `dev.ID` | the sender drops a reply without an ID |
| — | (Codex 6) `MkdirAll` position | first, in D1 step 2 | the spec says so |
| R12 | (agy diff review) D5.2 turns `nil`/link errors into `errReceiveUnpaired` when unpaired; `publish()` checks `dev.link == l`; D6 wraps EOF after `io.CopyN` | D5.2 only while `!run.published`; `publish()` also refuses a closed `l.Done()`; only `err == io.EOF` is wrapped after `io.CopyN` | an unpair just after publishing relabelled a saved file "failed" (new `TestResumeUnpairAfterPublishKeepsDone`, which fails without the fix); a link can close before its reader clears `dev.link`; `io.ErrUnexpectedEOF` was wrapped twice |
| R11 | (implementation, step 2) the stale-link tests send through the link | they call `handleResumable` directly with the old link | `handlePacket` already drops packets from a link that is no longer the device's, so only a packet that was queued before the switch reaches the worker; the direct call simulates that |
| R10 | (implementation, step 1) D2: `receiveResumable` keeps the record check | the record is checked once, in `checkResumable`; `receiveResumable` starts from `head.record` and keeps only `checkPeer` | a second read caught the save failure that `TestResumeStateSaveFailureKeepsFile` injects after publishing, and failed it. One worker per device means the record cannot change between the two calls |
