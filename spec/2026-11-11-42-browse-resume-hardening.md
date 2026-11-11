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
(`sftp.go:431-459`), after all roots are open, drop each root whose own real
path is not visible by A1 against the other roots (a `download_dir` of
`/var/home/u/.ssh` when home's real path is `/var/home/u`, or `~/.config`
through any spelling). This covers the `"."` case in `resolve`
(`sftp.go:504-506`), since a hidden root never becomes a root.

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
transfers (`sendResumable`, `resume.go:42-99`); it always offers first and
waits for each reply, so at most 1 packet per transfer is in flight from a
well-behaved sender.

**B1. Per-transfer state replaces the global lock.** `resumeMu`
(`daemon.go:52`) becomes a guard for a map `d.resumeActive` of
`key → *resumeRun` (key = the existing `sha256(dev.ID:ID)` folder name). A
`resumeRun` holds a `sync.Mutex` for that transfer, the `*Transfer` record,
its `context.CancelFunc`, and an idle timer. `handleResumable`
(`resume.go:101-134`):

- takes the run's lock with `TryLock`; when it is held (a second packet for
  the same transfer while one is processed), it replies `error`
  ("the transfer is busy") and drops the packet. So each transfer has at
  most 1 goroutine, and `resumeMu` is never held across I/O.
- for a transfer that is not active yet, calls `startReceive(dev)`
  (`share.go:378-389`). Over `maxReceives` (4) it replies `error` with the
  same text as the ordinary receive (`share.go:277`). The slot is released
  (`endReceive`) when the run ends: done, error, cancel, unpair, or idle.

**B2. Visible, cancellable, idle-bounded.** On the first accepted packet of
a run, fluxd makes the record with `newTransfer(dev, name, "", "in", size)`
(`share.go:105`) and sets `t.cancel`. Progress uses `d.progress(t)`
(`share.go:144`), so the record is `active` and `Busy()`
(`daemon.go:398-405`) sees it. The idle timer is `receiveIdle` (1 min,
`share.go:37`), reset on every accepted packet. When it fires, the run ends
with `finishTransfer(t, errStalled)`, its slot is freed, and the partial data
stays on disk for a later resume, which starts a new run and record.

**B3. Cancel.** `CancelTransfer` (`share.go:198-222`) already calls
`t.cancel` and sets `canceled`. The run's cancel func also marks the state
file `canceled: true` (a new `incomingRecord` field) and removes `data`. A
later `offer` or `chunk` with that ID finds `canceled` and gets `error`
("the transfer was canceled"). The folder is pruned like any other.

**B4. Pairing and link while it runs.** Each packet is checked after the
lock: `d.stillPaired(dev)` (`device.go:220`) and `dev.link == l`. The chunk
read goes through the existing `countingReader` check
(`share.go:505-510` pattern) so an unpair stops it mid-chunk, and
`cancelOnLinkDown` (`stream.go:44`) is registered for every packet, not only
chunks. Before the final file is published (`os.Rename`), fluxd checks
pairing and the run's context once more. Hashing and the fallback copy read
through a reader that returns `ctx.Err()`, so cancel and unpair stop them.

**B5. Space.** Using the existing `checkSpace` (`share.go:554-566`):

- on `offer`: `checkSpace(resumeDir, size - offset)`.
- on each `chunk`: `checkSpace(resumeDir, size - offset)` before writing
  (1 `statfs` per chunk of at most 1 MiB, `transferChunk`).
- on completion: first `os.Rename(part, tmp)` into the download folder, so a
  same-filesystem receive needs no second copy. Only on `EXDEV` fluxd calls
  `checkSpace(destDir, size)` and copies through `spaceWriter`
  (`share.go:570-588`) with the context reader of B4.
- the temporary name in the download folder becomes
  `partPrefix + "*" + partSuffix` (`.flux-*.part`, `share.go:56-59`), so
  `removeStaleParts` (`share.go:638-652`) removes it after a crash.

**B6. Bounded retained state.**

- `incomingRecord` gains `Device string` (the device ID) and `Canceled bool`.
  Old records without `Device` count for no device and are pruned by age.
- A `chunk` with no state file is refused with `error` ("no offer for this
  transfer"); only an `offer` creates state.
- On an `offer` for a new ID, fluxd counts the state files of that device
  that are not complete and not canceled. At 16 (`maxPartials`, a new
  constant) it first prunes, then refuses with `error`
  ("too many unfinished transfers from this device").
- The folder time is refreshed only after accepted progress (a new state or a
  written chunk), not on every packet (`resume.go:156` moves).
- `pruneIncoming` (`resume.go:136-146`) keeps its 7-day rule, which is now by
  last progress because of the previous point. It also runs at daemon start,
  so it no longer depends on the next offer.

### Tests (`internal/core/sftp_test.go`, `internal/core/resume_test.go`)

- A1/A2/A3: `~/Documents → ~/.ssh` (file under it refused, listing omits it);
  a symlinked home where `download_dir` is the dot-folder through the real
  path (root dropped); `download_dir` that holds the real home (root dropped);
  a plain symlinked `~/Documents → /data/docs` still works.
- B: low free space refuses an offer and stops a chunk (`freeSpace` is
  already replaceable, `share.go:539`); a fifth concurrent transfer gets
  `error`; a second packet for a busy transfer gets `error`; a chunk without
  an offer gets `error`; the 17th unfinished offer gets `error`; an unpair
  during a chunk stops it and writes no file; the record is `active` while
  chunks arrive and `Busy()` reports it; cancel deletes the data and a
  retried offer gets `error`; idle frees the slot and a later chunk resumes
  from the kept offset; a same-filesystem completion renames without a copy.
- The existing resumable tests keep passing unchanged, as the wire format
  does not change.

## Alternatives rejected

- **Resolve and reject symlinks inside roots.** A symlinked `~/Documents` to
  another drive is a normal setup that Browse PC supports on purpose
  (`resolve`, "the root with the longest path wins").
- **Keep the global `resumeMu` and add a queue limit.** One slow device would
  still block every other device's resumable receives.
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
- **The `TryLock` refusal** could hit a legitimate retry that races the reply
  of the previous packet; the sender then fails that attempt and the outbox
  retries the transfer later, resuming from the kept offset.
- **Concurrency:** the run map and timers add lock-ordering risk with `d.mu`.
  Tests run with `-race`.

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
