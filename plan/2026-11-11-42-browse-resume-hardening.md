---
status: draft
issue: 42
spec: spec/2026-11-11-42-browse-resume-hardening.md
coders:
  1: codex   # small, security-critical path logic in sftp.go with tests
  2: codex   # concurrency core: per-device worker, queue, runs, cancel handoff
  3: codex   # tricky: pairing/link checks, context reader, rename/EXDEV publish
  4: coder   # bounded records, pruning, startup cleanup: ordinary Go
  5: coder   # tests for part B in a new resume_test.go
  6: agy     # docs: mechanical prose in two files
reviewers:
  - codex    # diff review against this plan before the PR
  - agy      # second, independent diff review
---

# Plan: Close the Browse PC symlink leak and bound resumable receives

Branch `fix/42-browse-resume-hardening`, issues #42 and #43. Step 7
(verification, reviews, commits, PR) stays with the session model. One
commit per issue: steps 1 (#42), and steps 2–5 (#43); step 6 docs go with
the #43 commit or their own.

## Approved decisions (from the spec)

**Browse PC (#42)**

- A1 `visible`: a real path is visible when it is inside the root, not in a
  Flux folder, and for every root whose real path contains it, the part
  below has no dot-name. Home is root 0.
- A2 `openBrowseFS`: drop every non-Home root that is below another root
  through a dot-name, or that no root contains and whose own real path has a
  dot-name anywhere. `/data/docs` targets keep working.
- A3 `browseRoots`: its "inside home" and "holds home / in a dot-folder of
  home" filters use `filepath.EvalSymlinks` (fallback: the cleaned path).

**Resumable receives (#43)** — wire format unchanged.

- B1 one worker goroutine per device with a buffered queue of 8 in
  `d.resumeWorkers`; `handleResumable` validates, then does a non-blocking
  send; full queue → reply `error` "too many transfer packets". The worker
  handles packets in order and exits after `receiveIdle` with nothing to do.
  `resumeMu` is not held across I/O.
- B2 a run per receiving transfer: `startReceive` (refuse over 4 with the
  ordinary text), `newTransfer(dev, name, "", "in", size)`, `t.cancel`
  published under `d.mu` (already-canceled handled as a cancel), progress via
  `d.progress`. `endRun` once, on the worker: `finishTransfer` unless
  `canceled`, `endReceive`, delete the run.
- B3 idle: a ticker of `receiveIdle/4`; between packets, end runs idle for
  `receiveIdle` with `errStalled`, keep the partial.
- B4 stop vs cancel: per-packet context from the run context, with
  `cancelOnLinkDown` and `defer cancel()`. Stop keeps the partial. User
  cancel: `t.cancel` cancels the run context and non-blocking-sends a
  `cancelRun` event, takes no lock; the worker writes `canceled: true`,
  deletes `data`, ends the run; later packets for the ID get `error` "the
  transfer was canceled".
- B5 per packet under `d.mu`: still paired and `dev.link == l`. Chunk read
  through `countingReader` with the `stillPaired` check. Hash and fallback
  copy through a reader that returns the packet context's error. Check
  pairing and context again before the final rename.
- B6 `checkSpace(resumeDir, size-offset)` on offer and before each chunk.
  Completion: `uniquePath` for the final name, `os.Rename(data, final)`; on
  `EXDEV` only, `checkSpace(destDir, size)`, copy to `.flux-*.part` through
  `spaceWriter{left: size}` and the context reader, rename over the final
  name, keep `data` until then. Then save `Path`. Crash between rename and
  save re-receives once (accepted).
- B7 `incomingRecord` gains `Device`, `Canceled`. A chunk with no state →
  `error` "no offer for this transfer". New-ID offer: refuse at 16 unfinished
  records of the device (`maxPartials`), first remove the oldest finished or
  canceled records at 64 in all (`maxRecords`). Folder time refreshed only on
  accepted progress. Worker prune: only its device's folders, never an active
  run. Startup: prune all folders by age (7 days), remove every
  `.flux-*.part` and `.flux-received-*` in the download folder.
  `removeStaleParts` keeps its 1-day rule while running.

## Steps

1. **`internal/core/sftp.go`** — A1 in `visible` (lines 514–528), A2 in
   `openBrowseFS` (431–459, after the roots loop), A3 in `browseRoots`
   (372–394). Add tests to `internal/core/sftp_test.go` next to
   `TestBrowseRoots` (263): `~/Documents → ~/.ssh` (a file under it refused,
   the listing omits it); symlinked home with `download_dir` set to the
   dot-folder through the real path (root dropped); a `download_dir` that
   holds the real home (root dropped); `download_dir = <tmp>/.private`
   outside home (dropped); `~/Documents → <tmp>/share/.stversions`
   (dropped); `~/Documents → <tmp>/docs` still browses. Use `browseTree`
   (25) for the layout.
   → verify `go test -race ./internal/core/ -run 'Browse|Search' -count=1`.
   Traps: Home (root 0) is never dropped; keep `resolve`'s longest-root rule;
   browse search relies on `visible`, do not add a second check there.

2. **`internal/core/resume.go`, `internal/core/daemon.go`** — B1–B4. In
   `daemon.go` add `resumeWorkers map[string]*resumeWorker` next to
   `resumeMu` (52) and initialise it in `New` next to `resumeReplies` (376).
   In `resume.go` replace the `go func()` block of `handleResumable`
   (121–133) with the non-blocking enqueue; add the `resumeWorker` (queue,
   `runs`, ticker, events channel for `cancelRun`) and `resumeRun` types and
   the worker loop; `receiveResumable` (148) runs on the worker with a run
   and a per-packet context. Keep the reply path for `offset`/`done`/`error`
   (106–117) unchanged.
   → verify `go vet ./internal/core/ && go test -race ./internal/core/ -run 'Outbox|Resum' -count=1`
   (the existing `TestOutboxRestartAndResumableDelivery`, `outbox_test.go:19`,
   must still pass).
   Traps: `CancelTransfer` calls `t.cancel` while holding `d.mu`
   (`share.go:202-212`), so `t.cancel` must not lock or block; never hold
   `d.mu` across I/O; `finishTransfer(t, nil)` overwrites `canceled`
   (`share.go:178`), so check first; `progress` turns a record `active`
   (`share.go:157`), so do not call it after `endRun`.

3. **`internal/core/resume.go`** — B5–B6 inside `receiveResumable`: the
   paired+link check under `d.mu`, `countingReader` on the chunk payload, a
   small `ctxReader` for hashing and the fallback copy, `checkSpace` on
   offer and chunk, and the completion path: `uniquePath`, `os.Rename`, the
   `EXDEV` fallback (`errors.Is(err, syscall.EXDEV)`) with `spaceWriter` into
   a `partPrefix+"*"+partSuffix` temp, then save `Path`. Remove the old
   `os.CreateTemp(destDir, ".flux-received-*")` copy (233–247).
   → verify `go test -race ./internal/core/ -run 'Outbox|Resum' -count=1`.
   Traps: `spaceWriter.left` starts at the full `size`; the reserved name from
   `uniquePath` is an empty file that the rename replaces (as in `saveFile`,
   `share.go:465-534`); keep `data` until the fallback copy is renamed.

4. **`internal/core/resume.go`, `internal/core/daemon.go`,
   `internal/core/share.go`** — B7. `incomingRecord` (35–40) gains
   `Device string \`json:"device,omitempty"\`` and
   `Canceled bool \`json:"canceled,omitempty"\``; the chunk-without-state
   refusal; the per-device count with `maxPartials = 16` and
   `maxRecords = 64`; move the `Chtimes` at 156 to after accepted progress;
   `pruneIncoming` (136–146) takes a device filter and the active run keys.
   In `New` (`daemon.go`, after `d.resumeDir` is set at 371) call the
   startup prune of all folders and a startup cleanup of the download folder
   that removes `.flux-*.part` and `.flux-received-*` regardless of age (a
   small helper next to `removeStaleParts`, `share.go:638`).
   → verify `go test -race ./internal/core/ -run 'Outbox|Resum|Stale' -count=1`.
   Traps: records without `Device` count for no device and are only pruned by
   age; never delete a folder of an active run.

5. **`internal/core/resume_test.go`** (new) — the spec's B tests: low free
   space refuses an offer and stops a chunk (replace `freeSpace`,
   `share.go:539`); fifth concurrent transfer → `error`; full queue → `error`
   and no extra goroutine; a chunk right after an `offset` reply is accepted;
   chunk without offer → `error`; 17th unfinished offer → `error`, also when
   offers race; unpair mid-chunk stops it and keeps the partial with no final
   file; link drop keeps the partial and a later offer resumes at its offset;
   record `active` while chunks arrive and `Busy()` says "a file transfer";
   user cancel deletes `data` and a retried offer gets `error`; idle ends the
   run between packets and not during a slow hash (a slow reader); same-
   filesystem completion renames (inode of `data` equals the final file's);
   startup removes `.flux-*.part` and `.flux-received-*`.
   Reuse the daemon and link helpers of `outbox_test.go` and
   `pairing_test.go` (`phonePair`, `pairDaemon`).
   → verify `go test -race ./internal/core/ -count=1` shows no new failure
   beyond the 10 host-only ones on `master`.
   Traps: tests must pass in the Nix sandbox (no `/usr/bin`, no git); use
   `t.TempDir()`.

6. **`docs/workflows.md`, `docs/security.md`** — workflows (resumable
   section near line 265): a device has at most 4 resumable receives at
   once and 16 unfinished ones, a cancel deletes the partial and refuses
   that transfer, a stalled one keeps its partial and resumes, and space is
   checked like an ordinary receive. security: Browse PC never shows a
   dot-folder, also through a symlinked folder or `download_dir`.
   → verify `grep -n "16 unfinished\|dot-folder" docs/workflows.md docs/security.md`.
   Traps: short declarative sentences, one per line (`AGENTS.md`).

7. **Session model** — `go vet`, `gofmt`, `go test -race ./internal/core/`,
   the full suite against the `master` baseline, `nix flake check`; Opus,
   Codex and agy diff reviews; PR linking intent, spec and plan, naming who
   did each step. Then ask the owner about offering the fix upstream.

## Tests

| Command | Expected |
| --- | --- |
| `go test -race ./internal/core/ -count=1` | new tests pass, no failures beyond the host-only baseline |
| `go test ./cmd/... ./internal/... ./scripts/...` | the same 10 host-only failures as `master` |
| `nix flake check` | passes (tests run in the sandbox) |
| `go vet ./...`, `gofmt -l cmd internal scripts` | clean |
| Owner, manual | a phone browsing a `~/Documents → ~/.ssh` link sees nothing; a resumable send still completes |

## Rollback

Revert the branch's commits. No wire-format or config change, so nothing on
phones or in `config.toml` needs undoing. State files written with the new
`device`/`canceled` fields are read by the old code as before (unknown JSON
fields are ignored), and age out in 7 days.
