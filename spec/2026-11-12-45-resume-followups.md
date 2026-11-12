---
status: approved
issue: 45
intent: intent/2026-11-12-45-resume-followups.md
---

# Spec: Resumable receives — history noise, false stalls, reconnects and weak tests

Approved in the intent, with the proposed answers:

- A refused offer adds no history entry, and is logged in one line,
  rate-limited per device.
- A run is carried over a reconnect.
- All five items go in one PR.

Revised after the Codex and agy reviews of the first draft (see "Review
changes" at the end). All code is in `internal/core/resume.go`, and the tests
are in `internal/core/resume_test.go`. `share.go` does not change.

## Design

### 1. Check first, then start a run

`processResumePacket`, for a packet whose ID has **no run**, in this order:

1. **Prune** (offers only): `pruneIncoming`, unchanged and in the same place
   (lines 216-222). It removes expired folders before anything reads a record
   or counts the limits, as it does today.
2. **Check**: the new `checkResumable(dev, l, b) (resumeHead, error)`. It runs
   the checks that are at the top of `receiveResumable` today (lines 423-475)
   and changes nothing on disk:
   - `os.MkdirAll(d.resumeDir)` first, as today at line 441, so the space
     check works on a fresh install;
   - the metadata;
   - the link: the packet's link is still open (`l.Done()`) and still
     `dev.link`;
   - pairing and `d.ctx`;
   - the existing record: metadata changed, canceled, or finished
     (`head.done`);
   - "no offer for this transfer";
   - the space left: `checkSpace(d.resumeDir, b.Size - <size of data, 0 when
     missing>)`. This now also covers the re-offer of an existing partial,
     which today is checked only after the run starts (line 490);
   - for a new offer, the unfinished limit, through
     `admitOffer(dev.ID, false)`. With `evict = false` it only counts and
     refuses at `maxPartials`; it removes nothing.
3. **Refusals and finished transfers, still with no run:**
   - The check fails: send the error reply, then `refused` (section 2). Done.
   - `head.done`: reply `{Action: "done", Offset: record.Size}`. There is no
     slot, no entry and no log line. Done.
4. **Slot**: `startReceive`. When it fails, send the error reply and call
   `refused`, as today but now with the log line.
5. **Run**: `newTransfer`, `resumeRun{…, dev: pkt.dev, link: pkt.link}`, then
   `receiveResumable(ctx, …, head)`.
   - When `!head.exists`, its first step, before `checkPeer`, is to call
     `admitOffer(dev.ID, true)` to evict old records, then save `state.json`.
     So every run has a record on disk, and a cancel can always write its
     tombstone.
   - A refused 5th concurrent offer never reaches the eviction.
   - The rest is unchanged from line 476 on. `receiveResumable` keeps its own
     checks (`checkPeer`, the record, and `r.Path != ""` returns "done").

For a packet of an **existing run**:

- When `pkt.dev != run.dev`, the device was forgotten and found again. The old
  run ends with `errReceiveUnpaired` first, and the packet is then handled as
  a packet with no run.
- Otherwise: `checkResumable`, then `receiveResumable`, and section 4 decides
  whether an error ends the run.

`admitOffer(device string, evict bool)`: with `evict = false` it skips the
removal loop (lines 392-399); with `true` it is today's function.

### 2. Rate-limited refusal log

The rate limit outlives the worker, because a worker with no runs retires
after `receiveIdle`. Its state is a daemon field:

```go
// resumeRefusals is the refusal log state per device ID, guarded by d.mu.
resumeRefusals map[string]*refusalLog // refusalLog{last time.Time; skipped int}
```

```go
// refused logs a refused packet at most once per receiveIdle per device.
func (d *Daemon) refused(dev *Device, err error)
```

- `refused` reads `dev.Name` and updates the entry under `d.mu`, then logs
  after unlocking.
- The first refusal logs `resumable transfer from <name> refused: <err>`.
- Within `receiveIdle` of the last log line, a refusal only increments
  `skipped`.
- The first refusal after that logs `… refused: <err> (and <skipped> more)`.
- The map holds one entry per device that ever had a refusal. Devices are
  bounded by pairing, so the map is too.

### 3. Per-run stall check

- `resumeWorker` gets `queued map[string]int`, guarded by `d.mu`.
  - `handleResumable` increments it when the packet goes into `w.queue`
    (line 174).
  - The worker decrements it after `processResumePacket` returns, and deletes
    the key when it reaches 0, so refused random IDs leave nothing behind.
- `sweepResumeRuns` takes `d.mu` once, copies `w.queued[id]` for each
  `id` in `w.runs` into a local map, unlocks, and then works from that copy.
  `resumeStop` and `endResumeRun` take `d.mu` themselves.
- The sweep before a packet (line 189) runs while that packet is still
  counted, so the run being handled is not ended under it.
- The idle rule (line 290) becomes
  `queued[id] == 0 && time.Since(run.last) >= receiveIdle`.
  - The rule is per ID: a packet of A, or the worker being busy with A, never
    keeps B alive.
  - `run.last` changes only when a packet of that run **succeeds**
    (line 259), so no stream of failing packets keeps a run alive.
- The payload deadline of each chunk stays, now as
  `var chunkTimeout = time.Minute`, which tests can shorten (line 498).

### 4. Reconnect

A run no longer ends on a link problem, only on the idle rule. Everything
else that ends a run today still does.

- **Sweep** (lines 280-294), checked in this order for each run:
  1. `run.ctx.Err() != nil`: end with `resumeStop(run)`, as today.
  2. `!d.stillPaired(run.dev)`: end with `errReceiveUnpaired`, at once.
  3. The idle rule from section 3: end with `errLinkDown` when `run.link` is
     closed, otherwise with `errStalled`.

  The closed-link check at lines 283-286 is removed.
- **A packet that fails** (lines 247-257) for a reason in the table below
  keeps the run. In that case:
  - the reply is still sent; on a closed link that send just fails;
  - there is no `endResumeRun`;
  - `run.last` and `run.link` stay as they are.

  | Error | Comes from |
  | --- | --- |
  | `errLinkDown` | `checkPeer` or `publish()`: the packet's link is not, or is no longer, `dev.link`. This covers a stale packet of an old link that is still open. |
  | a transport error | `FetchPayload` or the payload copy (`io.CopyN`), including the `chunkTimeout` deadline. `receiveResumable` wraps these errors as `errTransport` (`fmt.Errorf("%w: %w", errTransport, err)`). |
  | the packet's own context ended | `cancelOnLinkDown` canceled the packet context because its link closed, while `run.ctx` is still alive. Today `resumeStop` turns this into `errLinkDown` (line 249); now it keeps the run. |

  The rule applies only while `run.ctx.Err() == nil`,
  `d.stillPaired(run.dev)` holds, and `!run.published`.
  - Every other error still ends the run as today: invalid chunk, changed
    metadata, checksum, space, unpair, user cancel and a write error.
  - The rule does not look at `link.Done()`. A payload EOF that arrives just
    before the link's close is still a transport error, and the decision does
    not depend on that race.
- **A packet that succeeds** sets `run.last = time.Now()` and
  `run.link = pkt.link`, so the run follows the link that is actually
  delivering.
- **Limits.** A run that fails on its link lives at most `receiveIdle` after
  its last successful packet, and it holds its receive slot during that time.
  If the device does not come back, the run ends "failed" with "the device
  disconnected", at most one `receiveIdle` later than today.
- **Who can publish.** Unchanged: `publish()` checks `dev.link == l` under
  `d.mu`, so a packet on an old link cannot publish.
- `endResumeRun` calls `d.endReceive(run.dev)`, the same `*Device` that
  `startReceive` used, and no longer the worker's own `dev`.

### 5. Weak tests

- **`TestResumeSlowChunkIsNotIdle`:**
  - It keeps the entry's transfer ID after the offer.
  - After the slow chunk's reply, it waits for 2 sweeps through a test hook,
    and only then sends anything else.
    - The hook is `var afterSweep func()`, called at the end of
      `sweepResumeRuns` when it is not nil.
    - While it waits, nothing is queued, so only `run.last` can keep the run.
  - It checks that the same ID is "active" and that the file has exactly one
    entry.
- **`TestResumeCancelWithFullEvents`:**
  - It calls `shortIdle(t, 200*time.Millisecond)` before `newRx`, so the
    worker ticks every 50 ms.
  - After `CancelTransfer` and before `release()`, the test empties
    `w.events` itself, so only the ticker can end the canceled run.
  - It checks the tombstone (`record.Canceled`) and that `data` is deleted.
- **`TestResumeCancelRacesCompletion`** stays as the race test. Two
  deterministic tests are added:
  - `TestResumeCancelBeforePublishWins`: the space check of the last chunk
    (`stubSpace`, now in `checkResumable`) is stalled, and the test cancels
    during the stall. After `release()`:
    - the state is "canceled";
    - there is no file in downloads;
    - the tombstone is written;
    - `data` is deleted.
  - `TestResumeCancelAfterPublishKeepsFile`: `renameFile` blocks after
    `publish()` has set "done", and the test cancels while it blocks. After
    the rename:
    - the state is "done";
    - the file is in downloads;
    - the record has the path and is not canceled.

### 6. New tests

Each test that shortens `receiveIdle` or `chunkTimeout` registers the restore
before `newRx`, as `shortIdle` does (lines 87-92), so the workers stop before
the value is restored.

| Test | Setup | Expected |
| --- | --- | --- |
| `TestResumeRefusalsAddNoHistory` | Sub-tests, one packet each: a chunk with no offer; a retry of a canceled ID; a 17th unfinished offer; a new offer refused for space; a re-offer of an existing partial refused for space; a 5th concurrent offer | An error reply each time. The number of history entries is unchanged, and the peer's receive count is back to 0 (or to the 4 that are running). |
| `TestResumeFifthOfferKeepsRecords` | `maxRecords` records, one of them a cancel tombstone, and 4 runs; then a 5th new offer | Refused. Every record, including the tombstone, is still there. |
| `TestResumeExpiredPartialsArePruned` | 16 unfinished records with folder times older than 7 days, then a new offer | Accepted, because the expired ones are pruned first. |
| `TestResumeReofferDoneWhileBusy` | 4 runs, then a re-offer of an ID that finished earlier | `done` with the size, and no new entry. |
| `TestResumeRefusalLogIsLimited` | `shortIdle`; 3 refusals; wait more than 1.25 × `receiveIdle`, so the worker retires; 1 more refusal | 1 line for the first three. The 4th logs "(and 2 more)". |
| `TestResumeQueuedPacketIsNotStalled` | `shortIdle`; A is stalled in `stubSpace` for longer than `receiveIdle` while B's next chunk is queued; then release | B's chunk answers "offset". B has the same ID and is not "failed". |
| `TestResumeBusyWorkerStillStallsSilentRun` | `shortIdle`; A is stalled for longer than `receiveIdle`, B sends nothing, and a 2nd packet of A is queued before `release()` | After the release, B ends "failed" with `errStalled` while A is still a run. A global `len(w.queue)` check would keep B, so this test catches it. |
| `TestResumeReconnectKeepsRun` | `shortIdle(400ms)`; an offer and one chunk; `r.onDesk.Close()`, then wait for the old `onPhone.Done()` and a sweep (`afterSweep`); `r.attach()`; the next chunk on the new link | Ends "done" with the same history ID, and there is no "failed" entry. |
| `TestResumeStaleLinkPacketKeepsRun` | Attach a 2nd link without closing the 1st, then send a chunk on the old, still open link | An error reply on the old link. The run and its ID stay, and a chunk on the new link continues it. |
| `TestResumeLinkDropMidChunkKeepsRun` | The payload is cut after 500 of 1000 bytes by a link close; wait for `onPhone.Done()`; reattach; offer | The offer answers offset 500, and the same entry is still active. |
| `TestResumeFailingPacketsDoNotExtendRun` | `shortIdle`; a run; then stale-link packets every `receiveIdle/4` with no success | The run ends `errStalled`, or `errLinkDown`, within about `receiveIdle` of its last success. |
| `TestResumeUnpairEndsIdleRun` | `shortIdle(400ms)`; a run with no packet in flight; unpair the peer | "failed" with `errReceiveUnpaired` at the next sweep, about 100 ms later, well before `receiveIdle`. |
| `TestResumePayloadStall` | `chunkTimeout = 200ms`, `shortIdle(400ms)`; a chunk whose payload stops after 500 of 1000 bytes on an open link | An error reply. The run ends "failed" when it goes idle. `data` keeps the 500 bytes, and the next offer answers offset 500. |
| `TestResumeCrossFsCopyFails` | `renameFile` returns `EXDEV`; the hook `copyAcross` (`var copyAcross = io.Copy`, used at line 598) writes 1000 bytes and then returns an error | "failed". No file and no `.flux-*.part` in downloads. The record has no path, and `data` is whole. After the hook is restored, a re-offer hashes, publishes, and answers "done". |
| `TestResumeCrossFsCopyLinkDrop` | Same, but the hook closes the link after 1000 bytes, waits for `onPhone.Done()`, and then lets the copy go on through `ctxReader` | The copy stops with the packet's context, or the second `publish()` refuses with `errLinkDown`. Either way the run is kept, with no file and no `.part`. After a reattach, a re-offer publishes. |

`TestResumeLinkDropKeepsPartialAndResumes` (line 601) keeps its assertions.
The run now ends after `receiveIdle` (400 ms in that test), with the same
`errLinkDown` message.

## Alternatives rejected

- **A history entry for every refused offer, deduplicated by ID.** A device
  can still flush the history with new IDs, and the sender already shows the
  error.
- **Measuring stalls by when a packet arrived.** A packet can wait in the
  queue for longer than `receiveIdle`.
- **Pausing every run's clock while the worker is busy.** Traffic for A would
  keep an abandoned B alive.
- **Keeping a run whenever its packet's link is closed (`link.Done()`).** The
  first draft did this. It races payload EOF, it hides real errors (checksum,
  unpair), and failing packets could keep a run alive forever.
- **A goroutine per run.** The intent requires one worker per device.
- **Moving the run to the new link when the device reconnects.** The next
  successful packet already carries the new link.

## Risks

- **A run whose link dropped stays "active" for up to `receiveIdle`.** It
  holds its slot during that time. This is bounded by `maxReceives` and the
  idle rule, and only a successful packet extends it.
- **Classifying errors.** A transport error that is really a local write
  error would keep a run for up to `receiveIdle` instead of ending it at
  once. To avoid that, only `FetchPayload` and the read side of the payload
  copy are wrapped; `f.Sync` and the write side are not. The plan puts the
  wrap in a reader around `rc`.
- **The space check moves before the run.** A disk that fills between
  `checkResumable` and the chunk's write is still caught by the write itself
  (`io.CopyN` fails) and by `spaceWriter` in the copy.
- **Flaky timing.** The tests synchronize with `afterSweep`, `stubSpace`,
  `renameFile` and `copyAcross`, and with `waitFor` for the result. Fixed
  sleeps are used only to let time pass beyond `receiveIdle`.
  `-race -count=5` is part of verification.
- **Senders.** No protocol change. A sender can now get `done` where it used
  to get "too many files at the same time", and it already handles that
  (`sendResumable`, line 107).

## Verification

1. `go vet ./... && test -z "$(gofmt -l .)"`.
2. `go test -race ./internal/core/ -run Resume -count=5`: all pass.
3. `make vet test`, the CI target.
4. `nix flake check path:.`.
5. Mutation checks. Each one must make the named test fail:

   | Mutation | Test that must fail |
   | --- | --- |
   | Remove the `run.last` refresh | `TestResumeSlowChunkIsNotIdle` |
   | Remove the ticker branch | `TestResumeCancelWithFullEvents` |
   | Replace `queued[id]` with `len(w.queue)` | `TestResumeBusyWorkerStillStallsSilentRun` |
   | Drop the `queued` check | `TestResumeQueuedPacketIsNotStalled` |
   | Start the run before `checkResumable` | `TestResumeRefusalsAddNoHistory` |
   | Refresh `run.last` on a failed packet | `TestResumeFailingPacketsDoNotExtendRun` |

## Review changes

| Finding | Change |
| --- | --- |
| Codex 1, agy 1: the check before pruning blocks resuming expired partials, and a cached record can be pruned under the check | Pruning comes first (step 1). Added `TestResumeExpiredPartialsArePruned`. |
| Codex 2, agy 1.3: admission evicted records before the slot check | `admitOffer(…, false)` in the check; eviction only after `startReceive`. Added `TestResumeFifthOfferKeepsRecords`. |
| Codex 3: a re-offer of a partial on low space still made entries | The remaining-space check is in `checkResumable`. Added a sub-test. |
| Codex 4, agy 3, agy 7: a stale packet on an old, open link ended the run, or moved it back to the old link | `errLinkDown` keeps the run, and only a successful packet sets `run.link`. Added `TestResumeStaleLinkPacketKeepsRun`. |
| agy 2: keeping the run on a closed link hid real errors and allowed endless runs | The rule is by error class, never `link.Done()`, and does not refresh `last`. Added `TestResumeFailingPacketsDoNotExtendRun`. |
| agy 3.2: payload EOF raced the link close | Covered by the error-class rule. |
| agy 4: the refusal counters were lost when the worker retired | Moved to a daemon map. The test waits for the worker to retire. |
| agy 5: a run could exist with no `state.json` | The record is saved before `checkPeer`, and the check rejects closed links. |
| agy 6, Codex 5, agy 9: tests could deadlock, pass a broken check, or race | Each test is rewritten with its sync point: a 2nd packet queued for A, `afterSweep`, waiting for `onPhone.Done()`, `shortIdle` for unpair, and the restore order. |
| Codex 6, agy 9.1: the cross-filesystem test did not interrupt a copy | The new `copyAcross` hook fails the copy part way. Two tests: one for a write error, one for a link drop. |
| agy 7: a stale `dev` was passed to `endReceive`, and `run.dev` was overwritten | Uses `run.dev`; a new `*Device` ends the old run. |
| agy 8: a deadlock and an unbounded map in the queued count; `head.done` on a run | The counts are copied under the lock, keys are deleted at 0, and `receiveResumable` keeps `r.Path != ""`. |
| Codex 7, agy 10: the 5th-offer refusal was not logged; `dev.Name` was read without the lock | The log goes through `refused`, which reads under `d.mu`. |
