---
status: draft
issue: 42
intent: intent/2026-11-11-42-browse-resume-hardening.md
---

# Spec: Close the Browse PC symlink leak and bound resumable receives

Approved decisions from the intent: refuse a dot-name below **any** root's
real path; a cancel deletes the partial data and refuses that transfer ID;
at most 16 partial transfers per device plus the free-space rule; prune by
last progress after 7 days; clean crashed final copies; one task, one commit
per issue; offer upstream later with the owner's OK.

## Design

### Part A: Browse PC (#42), `internal/core/sftp.go`

**A1. `visible` checks every root.** Today `visible(r, real)`
(`sftp.go:514-528`) checks dot-names only relative to `r.real`. It becomes:
a real path is visible when it is inside `r.real`, is not in a Flux folder
(unchanged), and, **for every root `x` whose `x.real` contains it**, the
part below `x.real` has no dot-name. The Home root is always root 0
(`browseRoots`, `sftp.go:374`), so `~/Documents → ~/.ssh` is refused through
the Home root's real path. Browse search uses `visible`, `resolve` and
`openIn` (`sftp_search.go:135-247`), so it is covered too.

**A2. A root that is hidden is not opened.** In `openBrowseFS`
(`sftp.go:431-459`), after all roots are open, drop each root other than
Home (root 0) when either:

- another root's real path contains it and the part below that root has a
  dot-name (a `download_dir` of `/var/home/u/.ssh` when home's real path is
  `/var/home/u`, or `~/.config` through any spelling), or
- no other root contains it and its own real path has a dot-name in any
  part (`download_dir = /mnt/nas/.private`, or
  `~/Documents → /mnt/nas/share/.stversions`). A target such as
  `/data/docs` keeps working.

This also covers the `"."` case in `resolve` (`sftp.go:504-506`) and the
root check in search (`sftp_search.go:135`), since a hidden root never
becomes a root.

**A3. `browseRoots` compares real paths.** Its two filters
(`sftp.go:383-388`: "inside home" and "holds home or is in a dot-folder of
home") use `filepath.EvalSymlinks` of `home` and of each candidate, falling
back to the cleaned path when the path does not exist. Then a `download_dir`
that holds the real home (`/var/home`, `/`) is left out, as the comment at
`sftp.go:369-370` already promises.

No change to `resolve`, `openIn`, `fdPath`, or the SFTP handlers.

### Part B: Resumable receives (#43), `internal/core/resume.go`

The wire format stays: `offer`, `chunk`, and the replies `offset`, `done`,
`error` (`resumeMessage`, `resume.go:25-33`). Only fluxd sends resumable
transfers (`sendResumable`, `resume.go:42-99`): it always offers first,
waits for each reply, and sends the next chunk at once after an `offset`
reply.

**B1. One worker per device, all state on that worker.** The daemon-wide
`resumeMu` (`daemon.go:52`) is no longer held across I/O. Instead each device
with resumable traffic has one worker goroutine and a buffered queue
(`resumeQueue`, 8 entries), kept in `d.resumeWorkers[dev.ID]`, guarded by
`d.mu` only for the map lookup. `handleResumable` (`resume.go:101-134`):

- decodes and validates the packet on the caller's goroutine, then does a
  **non-blocking send** to that device's queue. When the queue is full, it
  replies `error` ("too many transfer packets") at once. No goroutine is
  started per packet, so a device holds at most 1 worker and 8 queued
  packets.
- The worker handles packets **one at a time, in order**. A sender's next
  chunk, sent right after its `offset` reply, waits in the queue instead of
  being refused, so an unchanged sender never sees a new `error`.
- The worker exits after `receiveIdle` with an empty queue and no active run,
  and removes itself from the map under `d.mu`.

Because one goroutine owns every run, record and state file of a device,
counting, pruning, idle expiry and cancel cannot race with each other inside
that device. Different devices use different folders (the folder name is
`sha256(dev.ID:ID)`, `resume.go:154`), so their workers do not share state.

**B2. Runs, visibility and the receive limit.** The worker keeps
`runs map[key]*resumeRun`. A run is one transfer that is receiving now: its
`*Transfer`, a `lastPacket` time, and a run context. On the first accepted
packet of a transfer, the worker:

- calls `startReceive(dev)` (`share.go:378-389`); over `maxReceives` (4) it
  replies `error` with the ordinary receive's text (`share.go:277`).
- makes the record with `newTransfer(dev, name, "", "in", size)`
  (`share.go:105`) and publishes `t.cancel` under `d.mu`. A record that is
  already `canceled` when the cancel func is set is handled as a cancel, as
  the ordinary receive does (`share.go:411`).
- updates progress with `d.progress(t)` (`share.go:144`), so the record is
  `active` and `Busy()` (`daemon.go:398-405`) counts it.

A run ends exactly once, on the worker, in `endRun(run, err)`: it calls
`finishTransfer(t, err)` unless the record is already `canceled`
(`finishTransfer(t, nil)` would overwrite it, `share.go:178`), calls
`endReceive(dev)`, and deletes the run.

**B3. Idle expiry.** The worker uses a ticker of `receiveIdle / 4`. Between
packets it ends each run whose `lastPacket` is older than `receiveIdle`
(1 min, `share.go:37`) with `errStalled`, and keeps its partial data. A
later offer for that ID starts a new run and record from the kept offset.
Expiry runs only between packets on the worker, so it never ends a run that
is still hashing or copying.

**B4. Stop versus cancel.** Two different things:

- **Stop** (link down, unpair, idle, error): ends the packet's context and
  the run, keeps the partial data, and the sender can resume. Each packet
  gets its own context from the run context, with `cancelOnLinkDown`
  (`stream.go:44`) on that packet context and a `defer cancel()`, so the
  watcher goroutine ends with the packet.
- **Cancel** (the user, through `CancelTransfer`, `share.go:198-222`):
  `t.cancel` only cancels the run context and does a non-blocking send of a
  `cancelRun` event to the worker. It takes no lock, so calling it under
  `d.mu`, as `CancelTransfer` does, cannot deadlock. The worker, on the event
  or when it next sees the run context canceled with the record `canceled`,
  writes `canceled: true` in the state file (a new `incomingRecord` field),
  deletes `data`, and ends the run. A later `offer` or `chunk` for that ID
  gets `error` ("the transfer was canceled").

**B5. Pairing and link while it runs.** For each packet the worker checks,
under `d.mu`, that the device is still paired and `dev.link == l`
(`stillPaired` alone does not cover the link, `device.go:220-223`). The
chunk read goes through the existing `countingReader` with the same
`stillPaired` check as the ordinary receive (`share.go:505-510`), so an
unpair stops it mid-chunk. Hashing and the fallback copy read through a
reader that returns the packet context's error, so cancel, link down and
unpair stop them. Before the final rename, the worker checks pairing and the
context once more; the rename itself is atomic.

**B6. Space and publishing.** Using `checkSpace` (`share.go:554-566`):

- on `offer` and before each `chunk`: `checkSpace(resumeDir, size - offset)`.
  A chunk is at most 1 MiB (`transferChunk`), so this is 1 `statfs` per MiB.
- on completion, after the hash matches: reserve the final name with
  `uniquePath` (`share.go`, as `saveFile` does), then `os.Rename(data, final)`.
  On one filesystem this needs no second copy and has no window in which the
  data is under another name. On `EXDEV` only, the worker checks
  `checkSpace(destDir, size)`, copies to a `partPrefix + "*" + partSuffix`
  temporary file in the download folder through `spaceWriter` with
  `left = size` (`share.go:570-588`) and the context reader of B5, renames it
  over the reserved name, and keeps `data` until then, so an interrupted copy
  can resume.
- then it saves the state file with `Path`. A crash between the rename and
  that save leaves a complete file and a state with no `Path`; the next offer
  finds no `data`, starts at offset 0, and receives the file again under a
  new name. This window is milliseconds and is accepted.

**B7. Bounded retained state.**

- `incomingRecord` gains `Device string` and `Canceled bool`. Old records
  without `Device` belong to no device.
- A `chunk` with no state file is refused with `error` ("no offer for this
  transfer"); only an `offer` creates state.
- On an `offer` for a new ID, the worker counts that device's records. It
  refuses with `error` ("too many unfinished transfers from this device")
  when 16 are unfinished (no `Path`, not `canceled`) (`maxPartials`). When the
  device has 64 records in all (`maxRecords`), it first removes its oldest
  finished or canceled records. Counting and creating happen on the one
  worker, so two offers cannot both pass the limit.
- The folder time is refreshed only after accepted progress (a new state or a
  written chunk), not on every packet (`resume.go:156` moves).
- Pruning (`pruneIncoming`, `resume.go:136-146`) keeps its 7-day rule, now by
  last progress. On a worker it removes only that device's folders and never
  a folder of an active run. At daemon start, before any worker exists, it
  prunes every folder by age, including records without `Device`.
- Crashed final copies: at daemon start, fluxd removes every
  `.flux-*.part` and legacy `.flux-received-*` file in the download folder.
  No transfer runs at that point, so age does not matter. `removeStaleParts`
  (`share.go:638-652`) keeps its 1-day rule while fluxd runs.

### Tests (`internal/core/sftp_test.go`, `internal/core/resume_test.go`)

- A1/A2/A3: `~/Documents → ~/.ssh` (file under it refused, listing omits it);
  a symlinked home where `download_dir` is the dot-folder through the real
  path (root dropped); `download_dir` that holds the real home (root dropped);
  a plain symlinked `~/Documents → /data/docs` still works.
- A2: `download_dir = <tmp>/.private` outside home is dropped;
  `~/Documents → <tmp>/share/.stversions` is dropped.
- B: low free space refuses an offer and stops a chunk (`freeSpace` is
  already replaceable, `share.go:539`); a fifth concurrent transfer gets
  `error`; a full queue gets `error` and no goroutine per packet is started;
  a chunk sent right after an `offset` reply is accepted (the unchanged
  sender); a chunk without an offer gets `error`; 16 unfinished offers then
  the 17th gets `error`, and parallel offers cannot pass 16; an unpair
  during a chunk stops it, keeps the partial, and writes no final file; a
  link drop keeps the partial and a later offer resumes at its offset; the
  record is `active` while chunks arrive and `Busy()` reports it; a user
  cancel deletes the data and a retried offer gets `error`; idle ends the
  run between packets, frees the slot, and never during a long hash; a
  same-filesystem completion renames without a copy; startup removes
  `.flux-*.part` and `.flux-received-*` in the download folder.
- The existing resumable tests keep passing unchanged, as the wire format
  does not change.

## Alternatives rejected

- **Resolve and reject symlinks inside roots.** A symlinked `~/Documents` to
  another drive is a normal setup that Browse PC supports on purpose
  (`resolve`, "the root with the longest path wins").
- **Keep the global `resumeMu` and add a queue limit.** One slow device would
  still block every other device's resumable receives.
- **A lock per transfer with `TryLock`** (the first draft). Refused the
  unchanged sender's next chunk when it raced the reply, still started a
  goroutine per packet, and let idle expiry, cancel and counting race.
  Rejected after the Codex review.
- **Drop excess packets silently.** The sender then waits a minute for an
  ack (`resume.go:57`); an `error` reply fails it at once.
- **A new `cancel` wire message.** Would change the protocol; the `error`
  reply to the next packet is enough.
- **Count partials in memory.** Lost on restart; the state files already
  exist and are few (at most 16 per device).

## Risks

- **A1 hides more than before.** A root under another root's dot-folder, and
  files reached through a symlink into any dot-folder of a root, are now
  hidden. That is the intended behavior; a user who browsed such a path
  loses it.
- **EvalSymlinks on a slow or missing mount** in `browseRoots`: falls back to
  the cleaned path, as today.
- **Upgrade with partials on disk:** old state files have no `Device`, so they
  do not count against the new limit and age out in 7 days.
- **Serial per device:** one device's resumable transfers share one worker,
  so its 4 transfers progress chunk by chunk, not in parallel. Throughput per
  device stays bounded by one disk writer, which matches the current global
  lock but no longer blocks other devices.
- **Lock order:** the worker takes `d.mu` only for short reads and record
  updates and never holds it across I/O; `t.cancel` takes no lock. Tests run
  with `-race`.
- **Crash between rename and state save** re-receives the file once (B6).

## Verification

1. `go test -race ./internal/core/... -run 'Browse|Resum|Search|Share'` passes,
   including the new tests.
2. The full `go test ./cmd/... ./internal/...` has no failure beyond the 10
   host-only failures on `master`.
3. `nix flake check` passes (the tests run in the sandbox).
4. `go vet` and `gofmt` are clean.
5. Manual, by the owner: a phone browses `~/Documents` when it is a symlink to
   `~/.ssh` and sees nothing from it; a desktop-to-desktop resumable send
   still completes.
