---
status: approved
issue: 77
spec: spec/2026-11-18-77-owned-clipboard-pause.md
coders:
  1: codex    # small tricky concurrency (timers, seq check, lock order, grace); spec gives the algorithm
  2: coder    # two switch cases, one params decode, one snapshot field; given in full
  3: coder    # CLI flag parsing and its JSON-forms test
  4: coder    # one QML param, one mock line, one test assertion
  5: coder    # docs text, given in full
reviewers:
  - codex     # reads `git diff` against this plan; strongest on lock order and the stale-timer rule of step 1
---

# Plan: Clipboard pauses with an owner and an expiry (#77)

## The approved decisions

From the intent and the spec, both approved 2026-11-18. Decisions: 1 = A plus
`force`, 2 = A (seconds, cap 1 h), 3 = A (replace, unique labels), 4 = A
(free-text label), 5 = A (CLI flags), 6 = B (new method names), 7 = A (owned
pauses are lost on restart, accepted), 8 = A (8 owners, 1 h total per owner).

- **State.** `clipPaused` stays the one bool all readers use
  (`clipboard.go:226, 261, 269, 438, 451, 473`; `api.go:179`). It becomes
  derived: `clipPaused = clipPlain || len(clipOwned) > 0`, set by one helper
  `setPausedLocked()`. New fields under `mu`, next to `clipPaused`
  (`daemon.go:53-68`): `clipPlain bool`; `clipOwned map[string]*ownedPause`
  with `ownedPause{first, until time.Time; timer *time.Timer; seq uint64}`;
  `pauseSeq uint64`. Times come from `time.Now()` (monotonic); timers are
  `time.AfterFunc`.
- **Methods.** Plain `clipboard.pause` and `clipboard.resume` keep their
  meaning. New: `clipboard.pauseOwned` (`owner`, `expires` seconds 1..3600;
  result `{"expires": <unix seconds>}`), `clipboard.resumeOwned` (`owner`;
  result `{}`). `clipboard.resume` also takes optional `force: true`
  (`Force bool json:"force"` in `params`, `api.go:237-260`). An older fluxd
  answers `unknown method` to the new methods and drops `force` (the outcome
  is then still right).
- **Raw owner and expires.** They are not read from `params` (`params`
  cannot tell absent from zero, and `json.Unmarshal` into a string turns bad
  UTF-8 and lone surrogate escapes into U+FFFD). The four methods
  (`pause`, `resume`, `pauseOwned`, `resumeOwned`) decode the same raw bytes
  a second time into `struct{ Owner, Expires json.RawMessage }`; nil = absent.
  The existing `Expires int64` field (`api.go:258`) is untouched.
  The plain `clipboard.pause` and `clipboard.resume` reject a present
  `owner` or `expires` (raw non-nil, `null` and `expires:0` included) with
  `bad_params` "use clipboard.pauseOwned".
- **`ownerFromRaw(raw)`**: the only owner check, shared by `pauseOwned` and
  `resumeOwned`. `utf8.Valid(raw)`; `json.Unmarshal` into a string; reject any
  U+FFFD; non-empty; `len <= 64` bytes; no rune with `unicode.IsControl`;
  any failure is `bad_params`. A missing owner or `expires` is `bad_params`;
  `expires` must be `1 <= secs <= pauseCapSecs`.
- **Constants.** `var pauseCapSecs int64 = 3600` and `var pauseUnit =
  time.Second`, unexported package vars so tests can change them; every
  duration is `n * pauseUnit`.
- **`PauseClipboard()` (plain)**: as today (marker first, then memory) plus
  `clipPlain = true; setPausedLocked()`.
- **`PauseClipboardOwned(owner, secs) (time.Time, error)`**, under `pauseMu`
  then `mu`: (a) a new owner with `len(clipOwned) >= 8` returns `too_many`
  (new code); a renewal skips this. (b) `first` = existing entry's `first`,
  else now; `until = min(now + secs*pauseUnit, first + pauseCapSecs*pauseUnit)`.
  (c) stop the old timer, `pauseSeq++`, store entry with that `seq`, start
  `time.AfterFunc(time.Until(until), func(){ d.expireOwned(owner, seq) })`.
  (d) start-of-pause effects on every call: `clipGen++`, `content.lastClip =
  ""`, `stopAutoClipSendLocked()`, `setPausedLocked()`, `markDirty()`. (e) No
  marker is written or read.
- **`expireOwned(owner, seq)`**: takes `pauseMu` then `mu`; `e, ok :=
  clipOwned[owner]; if !ok || e.seq != seq { return }` (a stale or resumed
  timer is a no-op and a missing entry must never be dereferenced); else
  delete, `endPauseLocked()`, `markDirty()`.
- **`ResumeClipboardOwned(owner)`**: under `mu` stop the timer, delete the
  entry (unknown owner is a no-op), `endPauseLocked()`.
- **`ResumeClipboard(force bool)`**: clears `clipPlain`; with `force` also
  stops and deletes every owned entry. Marker removal and `not_saved` stay
  as today (`clipboard.go:316-324`). Without `force` owned pauses stay, so
  `clipPaused` can stay true.
- **`endPauseLocked()`**: after `setPausedLocked()`, if `clipPaused` went
  true to false, set `resumedAt = desktop.BootTime()` (the 250 ms grace
  starts only when the last pause ends). The `if d.clipPaused` guard at
  `clipboard.go:309` moves into it (keeps `TestResumeWhileSyncing`).
- **Fail closed.** A timer only runs late, never early. No sweep goroutine.
- **`loadClipPause`** (`clipboard.go:335-345`) sets `clipPlain` and calls
  `setPausedLocked()`; `clipOwned` is empty at start, so only the plain
  pause returns after a restart.
- **Snapshot (`api.go:179`).** Keep `clipboardPaused`. Add `clipboardPauses`,
  always an array (never null): `{"owner": "<label>", "expires": <unix s>}`
  for owned pauses sorted by owner (`expires = time.Now().Unix() + remaining`),
  then `{"owner": ""}` without `expires` for the plain pause. `markDirty` at
  each start, renewal and end.
- **CLI.** `clipboard pause` stays. `pause --owner NAME --for 10m` calls
  `clipboard.pauseOwned` with whole seconds (`time.ParseDuration`; reject
  non-positive, sub-second remainder, above 1 h, before the call). `--owner`
  without `--for` and `--for` without `--owner` are errors. `resume --owner
  NAME` calls `clipboard.resumeOwned`; `resume --all` sends `clipboard.resume`
  with `{"force":true}`; `--owner` with `--all` is an error. `status` prints
  `paused`/`syncing`/`off` from `clipboardPaused`; its `--json` adds
  `clipboardPauses` next to `paused`. The `len(args) != 1` check
  (`features.go:90`) becomes per-subcommand flag parsing; `status` takes none.
- **Chat page.** Resume sends `{force: true}` (a user action ends every
  pause). The banner reads `clipboardPaused`, so it shows for owned pauses.
- **Rejected** (do not build): owner on the existing methods; a wall-clock
  `expires`; persisting owned pauses; a sweep goroutine; a count or method for
  `clipPaused`; resume-without-owner clearing everything; owner tied to the
  socket peer.
- **Risks to keep in the docs.** A restart of fluxd drops owned pauses (the
  client re-checks `state` and renews). Labels are untrusted text. Chat Resume
  ends other clients' pauses (intended). An older GUI sends `{}` and ends only
  the plain pause. No deploy in this task; rolling out to p510 needs the
  owner's OK.

## Real checks (from the Makefile)

*Deviation (step 1):* on this NixOS host the full `go test -race ./internal/core`
fails 7 recorder/speaker tests and `TestBrowseDocumentsSymlinkToVisibleRoot` on
clean `master` too (`fakeRecorder` hard-codes `/usr/bin/touch`, `sleep`, `kill`).
Local runs skip exactly those with `-skip`; CI runs the whole package.
`TestOwnedPauseRenewalAndStaleTimer` uses the real pause unit (Codex review),
so no timer fires while it reads `clipOwned` without the lock.

*Deviation (step 3):* `TestRootInstall` in `cmd/flux` fails on this host the same
way: it sets `PATH=/usr/bin:/bin` and NixOS has no `mktemp` there. Local runs
add it to `-skip`; CI runs it.

*Deviation (step 4):* `/usr/lib/qt6/bin/qmltestrunner` is absent here; `make test-gui`
ran with nixpkgs' `qt6.qtdeclarative` runner (98 passed) and `make snapshot` in a
`nix-shell` with Qt and libGL. `MockBackend.qml` needed no edit.

*Deviation (step 5):* the docs spell the error `unknown_method`, as the error
table does; `too_many` already existed for addresses, and gets the pause case.

- Go: `make vet` (go vet plus `gofmt -l cmd internal scripts`), `make test`
  (`go test -race ./cmd/... ./internal/... ./scripts/...`); one test:
  `go test -race ./internal/core -run Name`.
- GUI: `make test-gui` (offscreen `qmltestrunner -input gui/tests`, needs
  `/usr/lib/qt6/bin/qmltestrunner`; override with `QMLTESTRUNNER=`). If the
  runner is missing on this host, say so; do not skip silently.
- Whole repo: `nix flake check` (`nix/checks.nix`), run last by the lead.
- `internal/ipc/ipc.go` has no `clipboard` reference (grep checked): no change
  unless step 2 finds a method list there.

## Steps

Steps run in order; the lead commits each. Line numbers are from the branch
head; a later step finds places by name when earlier steps moved lines. Tests
use `clipDaemon(t, true)` (`clipboard_test.go:34`) and a temp `clipPauseFile`,
never the real runtime directory.

1. `internal/core/daemon.go:53-68`, `internal/core/clipboard.go:283-345`,
   `internal/core/clipboard_test.go`: add the fields, `ownedPause`,
   `pauseCapSecs`, `pauseUnit`, `setPausedLocked`, `endPauseLocked`,
   `PauseClipboardOwned`, `ResumeClipboardOwned`, `expireOwned`,
   `ownerFromRaw`, `ResumeClipboard(force bool)`; adjust `PauseClipboard`
   and `loadClipPause`; update the callers of `ResumeClipboard` (`api.go`
   passes `false` for now, so the build stays green) and existing tests ->
   verify by `go build ./... && go test -race ./internal/core` and `make vet`.
   New tests (set `pauseUnit = time.Millisecond`, restore with `t.Cleanup`):
   owned pause holds and expires by itself and writes no marker; plain resume
   without force leaves it; `resumeOwned` ends only its owner; force ends all;
   plain plus owned and two owners: paused until the last ends and
   `resumedAt` moves only then; renewal replaces the expiry; stale timer after
   renewal or resume does not end the new pause; renewal clamped to
   `first + cap`; 9th owner `too_many` while a renewal at 8 succeeds;
   `ownerFromRaw` table (empty, 65 bytes, invalid UTF-8 bytes, `"\ud800"`,
   control char, literal U+FFFD); `loadClipPause` on a fresh daemon returns
   only the plain pause; `expireOwned(owner, seq)` after `resumeOwned` returns
   without panic, `clipPaused` unchanged; owned pause clears `lastClip` and
   stops an automatic image send (reuse the hook-based test at
   `clipboard_test.go:942`).
   Traps: lock order is `pauseMu` then `mu` everywhere, `expireOwned`
   included. `Timer.Stop` cannot cancel a callback already waiting on the
   lock, hence the seq check. Never write or read the marker for owned
   pauses; the `not_saved` path stays plain-only. Do not call `markDirty`
   under `mu` if existing code calls it after unlock (copy the existing
   pattern at `clipboard.go:283-300`). Keep the existing
   `clipboard_test.go:1156-1165` fail-closed marker test green. `-race`
   is mandatory.

2. `internal/core/api.go:179, 237-260, 351-354`, `internal/core/api_test.go`
   (or the file holding `TestCallClipboardPause`, `clipboard_test.go:1307`):
   add `Force bool json:"force"` to `params`; add a helper decoding
   `struct{ Owner, Expires json.RawMessage }` from the raw bytes; wire
   `clipboard.pauseOwned`, `clipboard.resumeOwned`, `clipboard.resume` with
   `p.Force`; reject present `owner`/`expires` on the plain `pause`/`resume`
   with `bad_params` "use clipboard.pauseOwned"; add `clipboardPauses` to the
   snapshot (always an array) -> verify by `go test -race ./internal/core` and
   `make vet`. Tests: `pauseOwned` result `expires`; `expires` 0, negative,
   missing, above the cap, `null`; owner missing; plain `pause`/`resume` with
   `owner` or `expires:0` present is `bad_params`; `force:true` ends owned
   pauses; snapshot `clipboardPauses` is `[]` with no pause, lists owned
   entries sorted, shows plain as `{"owner":""}`, and `clipboardPaused` is
   unchanged (extend `TestSnapshotClipboardPaused`, `:1285`).
   Traps: `[]` not `null` (use a non-nil slice). Raw `expires:0` must be
   rejected on plain methods, so test for non-nil raw, not for a zero int.
   Leave `Expires int64` at `api.go:258` alone (`clipboard.pin` uses it).
   Open `internal/ipc/ipc.go` once more to confirm there is no method list.

3. `cmd/flux/features.go:88-100, 163-175` (`clipboardCommand`,
   `clipboardStatus`), `cmd/flux/json_forms_test.go:39`, and the CLI test
   file for clipboard: per-subcommand flag parsing for `pause` and `resume`
   (`--owner`, `--for`, `--all`), the `--for` bounds and error messages,
   `status --json` with `clipboardPauses`; add `clipboard.pauseOwned` and
   `clipboard.resumeOwned` to the methods list in `newJSONFormsHandler` ->
   verify by `go test -race ./cmd/...` and `make vet`. Update the usage
   string at the end of `clipboardCommand`.
   Traps: the CLI sends nil params for plain pause today; keep that, since
   the plain methods now reject stray `owner`/`expires`. `--for 90s` is
   valid, `--for 1500ms` is not (sub-second remainder), `--for 61m` is not.
   `jsonMode` output must stay valid JSON; copy the existing
   `clipboardStatus` raw-message style and default `clipboardPauses` to `[]`
   when an older fluxd omits it.

4. `gui/qml/pages/Chat.qml:280`, `gui/qml/tools/MockBackend.qml:92`,
   `gui/qml/tools/fixture.json:319`, `gui/tests/tst_views.qml:1251-1270`:
   Chat Resume sends `{force: true}`; the mock keeps clearing
   `clipboardPaused` on resume and ignores `force`, and the fixture gets
   `"clipboardPauses": []`; the test asserts the recorded request's params
   have `force === true` (after `compare(requestsOf("clipboard.resume")
   .length, 1)`, check `requestsOf("clipboard.resume")[0].params.force`
   with the recorder's real field names; read `requestsOf` first). Re-grep
   `clipboard.resume` in `gui/` and `android/`, `ios/`, `macos/`,
   `windows/`, `browser/` for other senders; report any -> verify by
   `make test-gui` and `make snapshot` (spec: the Chat snapshot still passes
   with the changed `MockBackend.qml`).
   Traps: `Backend.qml`, `main.cpp`, `fluxbackend.h`, `FluxView.qml` only read
   clipboard history per the spec; do not edit them unless the re-grep finds a
   `clipboard.resume` sender. The runner may be absent on this host; report
   that verbatim.

5. `docs/ipc.md` (state list near 61-62; parameters table near 219; `not_saved`
   row near 319; guarantees near 436-451; error table), `docs/cli.md:316-336`
   and `642-643`, `docs/features.md:132-141`: document `clipboardPauses`, the
   two new methods, `force`, error code `too_many`, `bad_params` for owner and
   expires; rewrite "last call wins" (plain methods are idempotent, a renewal
   replaces the expiry, `resume` without `force` ends only the plain pause);
   the rules: an owner is a label, not a lock, socket callers are not
   identified, use a unique label per job (`nixi:<job id>`), owned pauses do
   not survive a restart of fluxd so read `state` before and during the job and
   renew when your entry is missing, cap is 1 h of hold in total per owner, at
   most 8 owners, labels are untrusted text; `not_saved` concerns the plain
   pause only; the clear and stop happen on each pause call and the 250 ms
   grace after the last pause ends. CLI docs: new flags. Features: Chat Resume
   ends all pauses, bare `clipboard resume` ends only the plain pause, `--all`
   ends all -> verify by reading the rendered diff and
   `grep -rn "last call wins" docs` (no stale line left).
   Traps: keep the existing guarantees list (`ipc.md:443-451`) and add only
   the note; one sentence per line, as the surrounding docs do.

## Tests

- Per step as listed; final gate by the lead: `make vet`, `make test`,
  `make test-gui`, `make snapshot` (the offscreen QML snapshot for Chat
  with the changed `MockBackend.qml`, as the spec requires), `nix flake check`.
- Runtime check on a dev host only, not a deploy: a throwaway `fluxd` set up as in
  `docs/development.md` "Isolated daemon": a temp root with `XDG_CONFIG_HOME`,
  `XDG_DATA_HOME`, `XDG_CACHE_HOME`, `XDG_RUNTIME_DIR` (mode 700) and
  `FLUX_SOCKET` all set (`HOME` alone is not enough: `xdg()` in
  `internal/config/config.go` reads the XDG variable first, and `SocketPath`
  reads `FLUX_SOCKET` first), run `-headless` with test ports, and use the
  same `FLUX_SOCKET` for the CLI; never the live runtime dir; call `clipboard.pauseOwned` with `expires` 3 through the
  socket, read `state`, wait 4 s, read `state` again; `clipboardPauses` goes
  from one entry to `[]`.
- Review: a fresh `model: "opus"` agent given this plan path and `git diff`;
  plus the codex reviewer above.

## Rollback

Nothing is deployed, persisted or migrated: owned pauses live in memory only
and the marker format is unchanged. Revert the commits of steps 1-5 (they are
separate; steps 4 and 5 can go alone because they do not depend on 1-3 at
runtime (an older fluxd ignores `force`). Step 2 needs step 1 (the new methods
and `ResumeClipboard(force bool)`) and step 3 needs step 2, so revert 3, 2, 1
newest first.) A fluxd that is rolled back answers `unknown method` to the new
calls. Clients must treat that as an error, not as "no owned pauses": an owned
call must fail on an older fluxd, never turn into a global pause or continue
unprotected (intent).
