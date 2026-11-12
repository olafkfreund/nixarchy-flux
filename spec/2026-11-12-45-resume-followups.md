---
status: draft
issue: 45
intent: intent/2026-11-12-45-resume-followups.md
---

# Spec: Resumable receives — history noise, false stalls, reconnects and weak tests

The intent was approved with the proposed answers:
- A refused offer adds no history entry. It logs one line, rate-limited per
  device.
- A run is carried over a reconnect.
- All five items go in one PR.

All code is in `internal/core/resume.go`, and all tests are in
`internal/core/resume_test.go`. `share.go` does not change.

## Design

### 1. Check first, then start a run

The checks at the top of `receiveResumable` (lines 423-475) move into a new
function. They do not change:
- metadata;
- `checkPeer`;
- the existing record: changed metadata, canceled or finished;
- "no offer for this transfer";
- for a new offer, `checkSpace` and `admitOffer`.

```go
// resumeHead is the result of the checks of a packet before any run,
// slot or history entry exists for it.
type resumeHead struct {
	record incomingRecord
	exists bool // state.json exists; else the record is saved when the run starts
	done   bool // the transfer finished earlier: reply "done"
}

func (d *Daemon) checkResumable(dev *Device, l *lan.Link, b resumeMessage) (resumeHead, error)
```

- `checkResumable` only reads. The one exception is `admitOffer`, which
  evicts the oldest finished records of this device, as it does today. It
  creates no folder and no `state.json`.
- `receiveResumable` takes the `resumeHead` and starts from it.
  - Its first step is the `MkdirAll` of `d.resumeDir`.
  - It then writes `state.json` and calls `touch()` when `!head.exists`.
  - Everything after line 476 is unchanged.
  - `checkPeer` runs again, because the run's context can end in between.
- `processResumePacket` calls `checkResumable` before it starts a run.
  - **No run for the ID, and the check fails.** It sends the error reply and
    calls `refused` (section 2). There is no `startReceive`, no
    `newTransfer`, and no run.
  - **No run, and `head.done`.** It replies
    `{Action: "done", Offset: record.Size}`, with no slot and no entry. This
    is the re-offer of a finished ID.
  - **No run, and the check passes.** Today's path: `pruneIncoming` (offers
    only), `startReceive`, `newTransfer`, the run, then `receiveResumable`. A
    5th concurrent offer is still refused by `startReceive` with no entry, as
    today.
  - **A run already exists.** Today's path, and any error ends the run, so a
    record changed under a run still fails that run.
  - `pruneIncoming` stays before `startReceive`, so the order of the 7-day
    pruning and the limits is unchanged.
- An error that `receiveResumable` returns after a new run started still ends
  that run as "failed". That error comes from a real attempt: low space for
  the rest of the file, a bad chunk, a checksum mismatch, or a link drop.
  None of these can be repeated without sending data.

### 2. Rate-limited refusal log

`resumeWorker` gets `lastRefusal time.Time` and `refusals int`. Only the
worker touches them, so they need no lock.

```go
// refused logs a refused packet at most once per receiveIdle per device.
func (d *Daemon) refused(dev *Device, w *resumeWorker, b resumeMessage, err error)
```

- The first refusal is logged at once:
  `resumable transfer from <device name> refused: <err>`.
- After that, the refusals of the next `receiveIdle` are only counted.
- The next refusal after that time logs
  `… refused: <err> (and <n> more)`.

### 3. Per-run stall check

- `resumeWorker` gets `queued map[string]int`. It counts the packets waiting
  in `w.queue` for each transfer ID, and it is guarded by `d.mu`.
  - `handleResumable` adds 1 when it puts a packet in the queue (line 174).
  - The worker removes 1 when it has **finished** that packet, after
    `processResumePacket` returns.
  - The sweep at line 189 runs before the packet is handled, so the packet
    still counts as queued there.
- `sweepResumeRuns` reads `w.queued` under `d.mu` once per sweep. Its idle
  rule (line 290) becomes:
  `w.queued[id] == 0 && time.Since(run.last) >= receiveIdle`.
- The rule is per ID. A packet of transfer A, or the worker being busy with
  A, never keeps transfer B alive.
- The payload deadline of a single chunk stays as it is (section 6).

### 4. Reconnect

- `resumeRun` keeps `link`, now the link of the run's **last** packet.
  - `processResumePacket` sets `run.link = pkt.link` and `run.dev = pkt.dev`
    for every packet of an existing run.
  - `dev` is a new field, set when the run starts.
- In `sweepResumeRuns`, a closed `run.link` (lines 283-286) no longer ends
  the run at once. The checks, in order:
  1. `run.ctx.Err() != nil`: end with `resumeStop(run)`, as today.
  2. `!d.stillPaired(run.dev)`: end with `errReceiveUnpaired`. Before, an
     unpair ended the run through the link drop.
  3. Idle (section 3): end with `errLinkDown` when `run.link` is closed,
     otherwise `errStalled`.
- In `processResumePacket` (lines 247-257), a packet whose own link closed
  while the run's context is still alive keeps the run. In code:
  `select { case <-pkt.link.Done(): … }` and `run.ctx.Err() == nil` and
  `!run.published`. In that case:
  - no reply, because the link is gone;
  - no `endResumeRun`;
  - `run.last = time.Now()`.

  The bytes the chunk already wrote stay in `data`, and the next offer's
  offset is the file size, as today.
- The packet's own link is still checked where it is today:
  - `checkPeer` at the start;
  - `publish()` (`dev.link != l`).

  A packet on the old link cannot publish after a reconnect.
- If the device does not come back, the run ends "failed" with "the device
  disconnected" one `receiveIdle` after its last packet. Today it ends at
  the next sweep.

### 5. Weak tests

- **`TestResumeSlowChunkIsNotIdle`:**
  - It keeps the history entry's transfer ID after the offer (`r.find(f)`).
  - After the second chunk, that same ID is still "active".
  - The history has exactly one entry for the file name.
- **`TestResumeCancelWithFullEvents`:**
  - It gets `shortIdle(t, 200*time.Millisecond)` **before** `newRx`, so the
    worker's ticker runs every 50 ms.
  - After `CancelTransfer`, and before `release()`, the test empties
    `w.events` itself.
  - The event branch then has nothing to receive, and only the ticker can
    end the canceled run.
  - It checks the tombstone (`record.Canceled`) and that `data` is deleted.
- **`TestResumeCancelRacesCompletion`** stays unchanged as the race test. Two
  new deterministic tests:
  - `TestResumeCancelBeforePublishWins`: `stubSpace` stalls the space check
    of the last chunk (line 490), and the test cancels while it is stalled.
    After `release()`: state "canceled", no file in downloads, the tombstone
    is written, and `data` is deleted.
  - `TestResumeCancelAfterPublishKeepsFile`: `renameFile` blocks after
    `publish()` has set "done", and the test cancels while it is blocked.
    After the rename: state "done", the file is in downloads, and the record
    has the path and is not canceled.

### 6. New tests

| Test | Setup | Expected |
| --- | --- | --- |
| `TestResumeRefusalsAddNoHistory` | Each of these packets, one per sub-test: a chunk with no offer, a retry of a canceled ID, a 17th unfinished offer, an offer refused for space | An error reply. The number of history entries is unchanged. `d.content.receives` for the peer is 0 afterwards. |
| `TestResumeReofferDoneWhileBusy` | 4 runs active, then a re-offer of an ID that finished earlier | The reply is `done` with the size. The history has no new entry. |
| `TestResumeRefusalLogIsLimited` | 3 refusals in a row | 1 log line. After `receiveIdle`, one more refusal logs "(and 2 more)". |
| `TestResumeQueuedPacketIsNotStalled` | `shortIdle`; run A blocked in `stubSpace` for longer than `receiveIdle`, while run B's next chunk waits in the queue | B's chunk is answered "offset". B's entry is the same ID and not "failed". |
| `TestResumeBusyWorkerStillStallsSilentRun` | Same, but B sends nothing | B ends "failed" with `errStalled`, while A keeps running. |
| `TestResumeReconnectKeepsRun` | Offer and one chunk, then `r.onDesk.Close()` and `r.attach()` within `receiveIdle`, then the next chunk on the new link | The same history ID ends "done". There is no "failed" entry. |
| `TestResumeLinkDropMidChunkKeepsRun` | The payload is cut in the middle by a link close, then a reattach and an offer | The offer answers the bytes written so far. The same entry is still active. |
| `TestResumeUnpairEndsIdleRun` | A run with no packet in flight, then the peer is unpaired | The run ends "failed" with `errReceiveUnpaired` at the next sweep, without waiting for `receiveIdle`. |
| `TestResumePayloadStall` | `chunkTimeout` set short; a chunk whose payload stops after 500 of 1000 bytes | An error reply and "failed". `data` keeps the 500 bytes, and the next offer answers offset 500. |
| `TestResumeCrossFsCopyInterrupted` | `renameFile` returns `EXDEV`; `stubSpace` stalls the destination's space check (line 584); `r.onDesk.Close()`; release | No file and no `.flux-*.part` in downloads. The record has no path. `data` is complete. After a reattach, a re-offer hashes the file, publishes it and answers "done". |

The test `TestResumePayloadStall` needs a hook: the 1-minute timeout at
line 498 becomes the package variable
`var chunkTimeout = time.Minute // tests shorten it`.

`TestResumeLinkDropKeepsPartialAndResumes` (line 601) keeps its assertions.
The run now ends after `receiveIdle` (400 ms in that test), with the same
`errLinkDown` message.

## Alternatives rejected

- **A history entry for every refused offer, deduplicated by ID.** A device
  can still flush the history with new IDs. The sender already shows the
  error.
- **Measuring stalls by the time a packet arrived.** A packet can wait in
  the queue for longer than `receiveIdle`, so its arrival time would already
  count as stale when the worker gets to it. The queued count does not have
  that problem.
- **Pausing every run's idle clock while the worker is busy.** A device that
  keeps sending packets for A would keep an abandoned B, and its receive
  slot, alive forever.
- **A goroutine per run.** The intent requires one worker per device, and
  #43 removed the per-packet goroutines for that reason.
- **Moving the run to the device's new link when it appears.** Waiting for
  the next packet is simpler, and that packet already carries the new link.
  The sweep then only needs the idle rule.

## Risks

- **A run whose link dropped stays "active" for up to `receiveIdle`.** It
  holds a receive slot meanwhile, as an active transfer does today. This is
  bounded by `maxReceives` and by the idle rule.
- **Ordering between `checkResumable` and `receiveResumable`.** The record
  can change between the two calls only through this device's own worker,
  which is single-threaded, or through `pruneIncoming`, which skips folders
  of active runs. A cancel goes through the run's context, and the
  `checkPeer` in `receiveResumable` catches it.
- **Flaky timing in the new tests.** They use the existing
  `shortIdle`/`stubSpace`/`renameFile` hooks and `waitFor`, never a fixed
  sleep for the result. `go test -race -count=5` on the package is part of
  verification.
- **Senders.** The protocol does not change. The only difference a sender
  can see is a `done` reply where it used to get "too many files at the same
  time". The sender already handles `done` for an offer (`sendResumable`,
  line 107).

## Verification

1. `go vet ./... && test -z "$(gofmt -l .)"`.
2. `go test -race ./internal/core/ -run Resume -count=5`, all passing.
3. `make vet test`, the CI target.
4. `nix flake check path:.`.
5. Mutation checks, each of which must make a test fail:
   - remove the `run.last` refresh: `TestResumeSlowChunkIsNotIdle`;
   - remove the ticker branch: `TestResumeCancelWithFullEvents`;
   - drop the `w.queued` check: `TestResumeQueuedPacketIsNotStalled`;
   - start the run before `checkResumable`: `TestResumeRefusalsAddNoHistory`.
