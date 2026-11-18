---
status: approved
issue: 77
intent: intent/2026-11-18-77-owned-clipboard-pause.md
---

# Spec: Clipboard pauses with an owner and an expiry

## Design

Intent decisions carried over (all adopted at approval 2026-11-18):
1 = A plus `force`, 2 = A (seconds, cap 1 h), 3 = A (replace, unique labels),
4 = A (free-text label), 5 = A (CLI flags), 6 = B (new method names),
7 = A (owned pauses lost on restart, accepted), 8 = A (8 owners, 1 h total
per owner).

### 1. State model (`internal/core/daemon.go:53-68`, `clipboard.go:283-345`)

Keep `clipPaused` as the one bool every reader already uses
(`clipboard.go:226, 261, 269, 438, 451, 473`; `api.go:179`). It becomes
derived: `clipPaused = clipPlain || len(clipOwned) > 0`, recomputed by one
helper `setPausedLocked()` after each change. No reader changes.

New fields under `mu`, next to `clipPaused`:

- `clipPlain bool`: the pause without an owner (the marker's pause).
- `clipOwned map[string]*ownedPause`, with
  `ownedPause{first, until time.Time; timer *time.Timer; seq uint64}`.
- `pauseSeq uint64`: grows on every owned pause or renewal.

`first` and `until` come from `time.Now()`, which carries Go's monotonic
reading, so `time.Until`, `Add` and `Before` ignore wall-clock changes.
Timers are `time.AfterFunc`, also monotonic.

### 2. New methods (decision 6 B)

The plain `clipboard.pause` and `clipboard.resume` keep their meaning, so an
older fluxd answers `unknown method` to the new ones, with no client logic.
Two additions to the `api.go` switch at `351-354`, and `Force bool
json:"force"` in `params` (`api.go:237-260`). `owner` and `expires` are not
read from `params`: `params` decodes scalars, so an absent field and a zero
look the same, and `json.Unmarshal` into a `string` replaces invalid UTF-8 and
lone surrogate escapes with U+FFFD before any method sees them. Instead the
four methods that care decode the same raw params bytes (the ones `api.go:266`
decodes) a second time into `struct{ Owner, Expires json.RawMessage }`. A nil
field is absent; the existing `Expires int64` at line 258 stays untouched for
its current users. `expires` is then decoded into an `int64` (seconds) and
`owner` is validated from its raw bytes (see `ownerFromRaw`).

| Method | Params | Result |
| --- | --- | --- |
| `clipboard.pauseOwned` | `owner`, `expires` (seconds, 1..3600) | `{"expires": <unix seconds>}` |
| `clipboard.resumeOwned` | `owner` | `{}` |
| `clipboard.resume` | optional `force: true` | `{}` |

The plain `clipboard.pause` and `clipboard.resume` reject a present `owner` or
`expires` (raw field non-nil, `null` included, so `expires:0` is rejected too)
with `bad_params` ("use clipboard.pauseOwned"), so a typo can't turn
into a global call on a new fluxd. `force` on an older fluxd is dropped
silently: it then ends the plain pause as before, and no owned pause exists
there, so the outcome is right (this is why the Chat page can send it).

### 3. Pause and resume logic (`clipboard.go`)

`pauseMu` still orders all of it (take it before `mu`).

`PauseClipboard()` (plain): as today (marker first, then memory), plus
`clipPlain = true; setPausedLocked()`.

`PauseClipboardOwned(owner string, secs int64) (time.Time, error)`:
1. The `api.go` handler turns the raw `owner` into a string with
   `ownerFromRaw` (below); then validate `1 <= secs <= pauseCapSecs`, else
   `bad_params`; a missing `owner` or `expires` (nil raw field) is
   `bad_params`. Production values: `var pauseCapSecs int64 = 3600` and `var
   pauseUnit = time.Second`, unexported package vars so tests can change them;
   every duration below is `n * pauseUnit`. `ownerFromRaw(raw)` is the only
   owner check, shared with `resumeOwned`; it takes the raw JSON bytes: `utf8.Valid(raw)` (rejects bad bytes, which `json` would otherwise
   turn into U+FFFD), `json.Unmarshal` into a string, then reject any U+FFFD
   in it (a lone surrogate escape like `\ud800` decodes to U+FFFD, and a
   literal U+FFFD in a label is refused too, a deliberate loss), non-empty,
   `len <= 64` bytes, no rune with `unicode.IsControl`; any failure is
   `bad_params`.
2. Under `mu`: if the owner is new and `len(clipOwned) >= 8`, return
   `too_many` (new code, documented). A renewal skips this.
3. `first` is the existing entry's `first`, else `now`. `until =
   min(now + secs*pauseUnit, first + pauseCapSecs*pauseUnit)`. This is the
   total bound per owner (decision 8 A): a heartbeat never extends past `first + 1 h`. After that
   the entry expires and a later call starts a fresh `first`; that is the
   client's explicit new pause, not a silent stretch.
4. Stop the old timer, bump `pauseSeq`, store the entry with the new `seq`,
   start `time.AfterFunc(time.Until(until), func(){ d.expireOwned(owner,
   seq) })`.
5. Same start-of-pause effects as the plain pause: `clipGen++`,
   `content.lastClip = ""`, `stopAutoClipSendLocked()`, then
   `setPausedLocked()` and `markDirty()`. The effect runs on every call, a
   renewal included; it is cheap and keeps "cleared when a pause starts".
6. No marker is written or read for owned pauses (decision 7 A).

`expireOwned(owner, seq)`: takes `pauseMu`, then `mu`; does nothing unless
the entry exists and its `seq == seq`: `e, ok := clipOwned[owner]; if !ok ||
e.seq != seq { return }`. Resume deletes entries and `Timer.Stop` cannot cancel
a callback already waiting on the lock, so a missing entry is a normal case and
must not be dereferenced (a replaced or resumed timer is a no-op, which is the
generation rule). Otherwise deletes the entry, runs the shared
`endPauseLocked()`, and `markDirty()`.

`ResumeClipboardOwned(owner)` (label already checked by `ownerFromRaw`); under `mu` stop the timer,
delete the entry (an unknown owner is a no-op: idempotent), `endPauseLocked()`.

`ResumeClipboard(force bool)`: clears `clipPlain`; with `force` also stops and
deletes every owned entry. Marker removal and the `not_saved` path stay as
today (`clipboard.go:316-324`); owned pauses don't touch the marker. Without
`force`, owned pauses stay, so `clipPaused` can remain true after a plain
resume (decision 1 A).

`endPauseLocked()` is the one place for the end of a pause: after
`setPausedLocked()`, if `clipPaused` went from true to false it sets
`resumedAt = desktop.BootTime()`, so the 250 ms grace (`clipboard.go:261`)
starts only when the last pause ends. The existing `if d.clipPaused` guard in
`ResumeClipboard` (`clipboard.go:309`) moves into it, which keeps
`TestResumeWhileSyncing` (`clipboard_test.go:1119`) true.

Fail closed: a timer can only run late, never early, so a pause never ends
before `until`. A bug or panic that loses a timer leaves the entry in the map,
so `clipPaused` stays true and the snapshot shows it with a past `expires`.
To bound that, `Snapshot` and `autoAllowed` stay as they are; a periodic sweep
is not added (see Alternatives).

`loadClipPause` (`clipboard.go:334-345`) sets `clipPlain` instead of
`clipPaused` directly and calls `setPausedLocked()`. `clipOwned` is empty at
start, so after a restart only the plain pause returns.

### 4. Snapshot (`api.go:179`)

Keep `"clipboardPaused": d.clipPaused`. Add `"clipboardPauses"`, always an
array (never `null`), so its presence tells a client that this fluxd knows
owners. Entries: `{"owner": "<label>", "expires": <unix seconds>}` for owned
pauses, sorted by owner, with `expires` computed as
`time.Now().Unix() + remaining` (display only), and `{"owner": ""}` without
`expires` for the plain pause. Labels are untrusted text; the docs say so.
`markDirty` fires at each start, renewal and end, as the snapshot pushes
already do.

### 5. CLI (`cmd/flux/features.go:88-100, 163-175`)

- `clipboard pause` stays. `clipboard pause --owner NAME --for 10m` calls
  `clipboard.pauseOwned` with `expires` = whole seconds of the `--for`
  duration (`time.ParseDuration`; reject non-positive, sub-second remainders
  and above 1 h with a clear message before the call). `--owner` without
  `--for`, and `--for` without `--owner`, are errors.
- `clipboard resume` stays. `resume --owner NAME` calls
  `clipboard.resumeOwned`; `resume --all` sends `clipboard.resume` with
  `{"force":true}`. `--owner` with `--all` is an error.
- `clipboard status` still prints `paused`, `syncing` or `off`, from
  `clipboardPaused`. Its `--json` form adds the `clipboardPauses` list next to
  `paused` (`docs/cli.md:642`).
- The `len(args) != 1` check at `features.go:90` becomes per-subcommand
  flag parsing; `status` still takes none.
- `cmd/flux/json_forms_test.go:39` lists the methods; add the two new ones.

### 6. Chat-page Resume (verified)

`gui/qml/pages/Chat.qml:280` calls `v.call("clipboard.resume", {}, ...)`, so
it sends no params, as inferred in the intent. Change it to
`{force: true}`: it is a user action and must end any pause (decision 1).
The banner (`Chat.qml:70, 248`) reads `clipboardPaused`, so it shows for
owned pauses too, and a user who clicks Resume always gets out. An older
fluxd ignores `force` and works as before. `gui/qml/tools/MockBackend.qml:92`
sets `clipboardPaused` from the method name; update it to ignore `force`
(still clears) and keep `clipboardPauses: []`. The other GUI files that
matched the grep (`Backend.qml`, `main.cpp`, `fluxbackend.h`, `FluxView.qml`)
only read `clipboard` history; the implementer re-greps for
`clipboard.resume` there before the plan is approved.

`internal/ipc/ipc.go`: the intent lists it, but a grep for `clipboard` finds
nothing there. The dispatch is `api.go`; ipc.go changes only if it keeps a
method list, which the plan must confirm.

### 7. Docs

- `docs/ipc.md`: state list (line 61-62) gets `clipboardPauses`, the
  parameters table (line 219) the new methods and `force`, line 452 ("last
  call wins") is rewritten: plain methods are idempotent, a renewal replaces
  the expiry, and `resume` without `force` ends only the plain pause. Add the
  rules: an owner is a label, not a lock, and socket callers are not
  identified; use a unique label per job (`nixi:<job id>`); owned pauses do
  not survive a restart of fluxd, so read `state` before and during the job
  and renew when your entry is missing; the cap is 1 h of hold in total per
  owner; at most 8 owners; `bad_params` and `too_many` errors; labels are
  untrusted text. The `not_saved` row (line 319) says it concerns the plain
  pause only. The guarantees at 443-451 stay, with a note that the clear and
  the stop happen on each pause call and the grace after the last pause ends.
- `docs/cli.md:317-336` and `642-643`: the new flags.
- `docs/features.md:132-141`: Resume on the Chat page ends all pauses; a bare
  `clipboard resume` ends only the pause without an owner; `--all` ends all.

## Alternatives rejected

- **Owner on the existing methods** (intent A of Q6 on its own): an older
  fluxd drops the unknown field and runs a global pause or resume without an
  error, the failure the intent forbids; relying on the client to check
  `clipboardPauses` first is easy to skip.
- **One boolean plus a global expiry**: cannot tell two callers apart.
- **A wall-clock `expires` timestamp** (Q2 B): depends on the client's clock.
- **Persisting owned pauses in the marker or a second file** (Q7 B): brings
  back the file the design avoids, for a window that the client's heartbeat
  and state check already cover.
- **A sweep goroutine instead of per-pause timers**: one more moving part;
  timers with a sequence check give the same result with less code.
- **Make `clipPaused` a count or a method**: touches seven readers for no gain;
  a derived bool leaves them unchanged.
- **`resume` without owner clears everything** (Q1 B): lets one caller undo
  another's pause, the problem in the issue.
- **Tie the owner to the socket peer** (Q4 B): the socket is user-only and the
  owner's job is to avoid accidents.

## Risks

- **A restart of fluxd drops owned pauses** (accepted exception). Copies sync
  between the restart and the client's re-acquire. Affects any host with a
  client that handles secrets (p510, p620, razer). The docs and the Nixi
  follow-up task (nixoarchy-snowflake#100) must carry the check-and-renew rule.
- **A lost timer holds sync past its expiry** (fails closed, never open). Only
  possible through a bug; tests cover expiry, renewal and resume. The
  snapshot would show an `expires` in the past.
- **Lock order.** `expireOwned` takes `pauseMu` then `mu`, the same order as
  the pause calls; a timer cannot deadlock with them. Tests run with `-race`.
- **Plain methods reject `owner`/`expires`.** A script that sent stray
  `expires` to `clipboard.pause` would now fail. No such caller is known
  (the CLI sends nil params); the risk is small, and the failure is loud.
- **Chat Resume ends owned pauses of other clients.** Intended: a user action.
  A client that handles a secret sees its entry vanish and re-acquires.
- **An older GUI against a new fluxd** sends `{}`: it ends only the plain
  pause, and the banner stays while an owned pause holds. Acceptable: the
  owned pause ends by itself within the cap.
- **No deploy here.** Rolling out the new `fluxd` to p510 needs the owner's
  OK; nothing in this task switches a host.

## Verification

Tests in `internal/core/clipboard_test.go` use `clipDaemon(t, true)` and set
`clipPauseFile` to a temp path (never the real runtime directory). Expiry
tests use short real durations: a test sets `pauseUnit = time.Millisecond`
(and `pauseCapSecs` as needed) and restores both with `t.Cleanup`, so a "second"
is a millisecond and they run fast. All run with `go test -race ./...`.

- Owned pause holds `clipPaused`, ends by itself at expiry, and writes no
  marker; plain resume without `force` leaves it; `resumeOwned` ends only its
  owner; `force` ends all.
- Two holders (plain plus owned, and two owners): sync stays paused until the
  last one ends, and `resumedAt` moves only then.
- Renewal replaces the expiry; a stale timer (renewed or resumed entry) does
  not end the new pause (sequence check); a renewal is clamped to
  `first + cap`.
- Validation: owner empty, 65 bytes, invalid UTF-8 bytes in the raw JSON, a
  lone surrogate escape (`"\ud800"`), control character; `expires` 0, negative,
  missing, above the cap; plain `pause`/`resume` with `owner` or `expires:0`
  present; the 9th owner gets
  `too_many` while a renewal at 8 succeeds; plain methods reject `owner`.
- Restart: after `loadClipPause` on a fresh daemon only the plain pause
  returns (marker present), none from owned.
- Fail closed: unreadable marker still pauses (existing
  `clipboard_test.go:1156-1165` test stays green).
- Stale callback: call `expireOwned(owner, seq)` after `resumeOwned` removed the
  entry; it returns without panic and `clipPaused` is unchanged.
- Start-of-pause effects: an owned pause clears `lastClip` and stops an
  automatic image send (reuse the existing hook-based test).
- Snapshot: `clipboardPauses` is `[]` with no pause, lists owned entries and
  the plain `{"owner":""}`; `clipboardPaused` unchanged.
- CLI (`cmd/flux`): flag parsing, errors and JSON forms in
  `json_forms_test.go`, including `--for` bounds.
- Build and checks: `go build ./...`, `go vet ./...`, `go test -race ./...`;
  `gofmt -l`. GUI: the offscreen QML snapshot test for Chat still passes with
  the changed `MockBackend.qml`, and `gui/tests/tst_views.qml` (near line 1266,
  which only counts `requestsOf('clipboard.resume')`) gains an assertion that
  the recorded request's params have `force === true`, so Chat Resume omitting
  it fails the test.
- Runtime check on a dev host only (not a deploy): run a throwaway `fluxd`
  with a temp `HOME`/`XDG_RUNTIME_DIR`, call `pauseOwned` with `expires` 3 via
  the socket, read `state`, wait 4 s, read `state` again.
