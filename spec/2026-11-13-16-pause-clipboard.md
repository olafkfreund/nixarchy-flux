---
status: draft
issue: 16
intent: intent/2026-11-13-16-pause-clipboard.md
---

# Spec: Pause clipboard sync at runtime

The owner approved the intent with its proposed answers:
- a boolean pause, set by CLI and IPC; the last call wins, no timer;
- a fluxd restart keeps the pause, through a marker in the runtime folder;
- pausing clears `lastClip`;
- incoming clips work as today, and paused copies still update `lastClipAt`;
- manual sends stay allowed, and a pause never cancels one;
- `flux-cli clipboard pause|resume|status`;
- a global indicator on the Chat page and the bar mark;
- after a resume, a late record of a paused copy never syncs (Q9).

Two points change the intent's wording, not its outcome. They are listed
for the owner under "Decisions for the owner" at the end.

Paths are under `internal/core/` unless they start with another folder.

## Design

### 1. When a copy was made: a time on each watcher record (`internal/desktop/clipboard.go`)

The watcher runs a shell script per selection change and reads its output
over a pipe (`internal/desktop/clipboard.go:43,117-130`). A record can
reach fluxd after a pause or resume that came later than the copy. So each
record carries the time its script started, and fluxd compares that time,
not the arrival time, with the pause and resume.

- `watchScript` (`:43`) prints a first line with the boot clock, using
  shell builtins only:

```sh
read -r up _ </proc/uptime; printf '%s\n' "$up"; printf '%s\n' "$CLIPBOARD_STATE"; …
```

- `watchOnce` (`:117-130`) reads 4 lines. `record` parses line 0 as
  seconds with 2 decimals (`/proc/uptime`, `CLOCK_BOOTTIME`) into
  `at time.Duration`. A line that doesn't parse drops the record, like a
  bad base64 line today (`:144-147`).
- The callbacks become `onText func(text string, at time.Duration)` and
  `onImage func(data []byte, mime string, at time.Duration)`. The
  `clipboard` interface (`daemon.go:254-260`), `memClipboard.Watch`
  (`daemon.go:270-272`) and `TestClipboardWatch`
  (`internal/desktop/desktop_test.go:249-300`) follow.
- `desktop.BootTime()` returns `unix.ClockGettime(unix.CLOCK_BOOTTIME)` as
  a `time.Duration`. `golang.org/x/sys/unix` is already a dependency of the
  package (`theme.go:18`). Both sides use the same clock; it never jumps
  back, and it counts suspend.
- The script starts when `wl-paste` learns of the new selection, a few
  milliseconds after the copy. That latency is covered by `resumeGrace`
  (section 3).

### 2. Pause state (`daemon.go`)

New `Daemon` fields next to `clipSend` (`daemon.go:44-52`):

```go
// clipPaused stops the automatic sync of local copies (clipboard.pause).
// resumedAt is the boot time of the last resume. clipGen grows on each
// pause, so automatic work admitted before a pause stops at its next
// check. clipSendAuto is true while clipSend is an automatic image send.
// All under mu.
clipPaused   bool
resumedAt    time.Duration
clipGen      uint64
clipSendAuto bool
// clipPauseFile keeps the pause over a restart. It is "" in a headless
// daemon. pauseMu orders the pause and resume calls with their marker
// writes; take it before mu.
clipPauseFile string
pauseMu       sync.Mutex
```

```go
func (d *Daemon) PauseClipboard() error {
	d.pauseMu.Lock()
	defer d.pauseMu.Unlock()
	var err error
	if d.clipPauseFile != "" {
		err = writePauseFile(d.clipPauseFile) // first, so a crash after it stays paused
	}
	d.mu.Lock()
	d.clipPaused = true
	d.clipGen++
	d.content.lastClip = ""
	d.stopAutoClipSendLocked()
	d.mu.Unlock()
	d.markDirty()
	if err != nil {
		d.logf("clipboard pause: %v", err)
		return apiErr("not_saved", "Clipboard sync is paused, but a restart of fluxd would resume it: %v", err)
	}
	return nil
}

func (d *Daemon) ResumeClipboard() error {
	d.pauseMu.Lock()
	defer d.pauseMu.Unlock()
	d.mu.Lock()
	if d.clipPaused {
		d.clipPaused = false
		d.resumedAt = desktop.BootTime()
	}
	d.mu.Unlock()
	d.markDirty()
	if d.clipPauseFile == "" {
		return nil
	}
	// After the memory change, so a crash before it stays paused.
	if err := os.Remove(d.clipPauseFile); err != nil && !errors.Is(err, os.ErrNotExist) {
		d.logf("clipboard resume: %v", err)
		return apiErr("not_saved", "Clipboard sync resumed, but a restart of fluxd would pause it again: %v", err)
	}
	return nil
}
```

- `pauseMu` serializes the two calls with their file writes, the way
  `cfgSaves` does for settings (`api.go:522-524`). So the marker always
  matches the last call, and the last call wins. Neither call reads the
  clipboard or the network, so each takes microseconds; `d.mu` is never
  held during file I/O.
- Both are idempotent. A resume that wasn't paused doesn't move
  `resumedAt`.
- `writePauseFile(path)`: `os.MkdirAll(filepath.Dir(path), 0o700)`, then
  `os.WriteFile(path, nil, 0o600)`.

### 3. Admission of automatic sends (`clipboard.go`)

One rule, under `d.mu`, for a local copy made at boot time `at`:

```go
// resumeGrace covers the time between a copy and the start of its watcher
// script, so a copy made just before a resume counts as paused.
const resumeGrace = 250 * time.Millisecond

// copyPausedLocked reports whether a local copy made at at is a paused copy.
func (d *Daemon) copyPausedLocked(at time.Duration) bool {
	return d.clipPaused || at <= d.resumedAt+resumeGrace
}
```

- A copy whose script started while paused, or before the last resume,
  or less than 250 ms after it, is a paused copy. That holds however late
  its record arrives, over any number of pause and resume cycles, since
  only copies older than the newest resume are dropped.
- A copy made before the pause whose record arrives after the resume is
  dropped too. It is old and was never sent; dropping it is harmless.
- A genuine copy in the first 250 ms after a resume isn't synced. The
  next copy is.

**Text** (`onLocalClipboard`, `clipboard.go:239-275`), the start:

```go
func (d *Daemon) onLocalClipboard(text string, at time.Duration) {
	fits := len(text) <= maxSentText
	d.mu.Lock()
	d.lastClipAt = time.Now()
	if d.copyPausedLocked(at) {
		d.mu.Unlock()
		return
	}
	d.lastLocalClip = d.lastClipAt
	gen := d.clipGen
	… // lastClip, auto, addClipLocked as today
	if auto {
		// The text replaces an image that is still on its way.
		d.stopAutoClipSendLocked()
	}
	d.mu.Unlock()
```

- A paused copy only updates `lastClipAt` (intent Q4): no `lastLocalClip`,
  no `lastClip`, no entry, no send, no toast.
- The stop of an automatic image moves from after the unlock
  (`clipboard.go:262-263`) into the admission, so an old callback can't
  cancel a newer image after a pause and resume.
- The rest of the function returns early when `!d.autoAllowed(gen)`, and
  the send loop (`clipboard.go:270-272`) checks it before each link:

```go
// autoAllowed reports whether automatic work admitted in generation gen
// may go on. It takes d.mu.
func (d *Daemon) autoAllowed(gen uint64) bool {
	d.mu.Lock()
	defer d.mu.Unlock()
	return d.cfg.AutoClipboard && !d.clipPaused && d.clipGen == gen
}
```

**Images** (`onLocalImage`, `clipboard.go:280-328`):

- The same start: `lastClipAt`, then `copyPausedLocked(at)` returns early;
  then `lastLocalClip`, `lastClip = ""`, `gen := d.clipGen`, the targets.
- The save becomes `addClipImageIf(…, func() bool { return !d.clipPaused && d.clipGen == gen })`.
  The predicate runs under `d.mu` (`clipboard.go:158`), so it reads the
  fields directly; it must not call `autoAllowed`.
- `addClipImageIf` (`clipboard.go:145-168`) removes the file when
  `os.WriteFile` fails (`:153`), not only when the predicate fails. So a
  pause during the save leaves no entry and no file, except after a crash
  in that window (Risks).
- `newClipSend` takes the kind and generation:

```go
// newClipSend stops the image that fluxd sends and registers the next one.
// For an automatic send it first checks that no pause came after gen, and
// returns ok false without stopping anything when one did.
func (d *Daemon) newClipSend(auto bool, gen uint64) (ctx context.Context, cancel context.CancelFunc, ok bool)
```

  Under `d.mu`, as today, it cancels the registered send (automatic or
  manual: a newer image replaces an older one, which avoids two image
  transfers to one phone at once), registers the new one and sets
  `clipSendAuto = auto`. `onLocalImage` returns when `ok` is false.
- The worker's per-device check (`clipboard.go:312-315`) adds
  `!d.clipPaused && d.clipGen == gen`.
- `stopClipSend` (`clipboard.go:228-235`) becomes `stopAutoClipSendLocked`
  (caller holds `d.mu`; it cancels only when `clipSendAuto`), plus a
  locking wrapper `stopAutoClipSend` for `setSetting` (`api.go:709-711`)
  and `Reload` (`api.go:745-748`). Turning sync off then stops an
  automatic image, not a manual one; today it stopped both.

**Connect send** (`sendConnectClipboard`, `clipboard.go:333-342`): the lock
also reads `d.clipPaused` and skips the send when paused. As the pause
clears `lastClip`, a connect after a pause sends nothing until a new copy
after the resume.

**What a pause stops, exactly.** A paused copy is never admitted
(the rule above). Work admitted before the pause stops at its next check:
before each link of a text, before each device and during each transfer of
an image (the cancelled context). A send whose check passed just before
the pause can still go out; it carries content from before the pause. The
same holds for a connect send that read `lastClip` before the pause. fluxd
doesn't hold `d.mu` across `l.Send`, which writes to the network with a
30 s deadline (`internal/lan/link.go:120-145`).

**Manual sends** (`SendClipboard`, `sendImageTo`, `clipboard.go:544-597`):
no pause check. `sendImageTo` calls `newClipSend(false, 0)`. They add
their entry to the history as today and never touch `lastClip`.

**Incoming clips** (`handleClipboard`, `receiveClipImage`, shared text,
signatures, screenshots): no change.

**Settings and reload** replace or change `d.cfg` only. The pause fields
are outside `d.cfg`, so they keep their value.

### 4. The marker file and the start order

- Path: `filepath.Join(config.RuntimeDir(), "clipboard-paused")`, an empty
  file with mode `0600`.
- In `New` (`daemon.go:298`), next to the headless switch
  (`daemon.go:339-344`):

```go
if !opts.Headless {
	d.loadClipPause(filepath.Join(config.RuntimeDir(), "clipboard-paused"))
}
```

```go
// loadClipPause sets the marker path and starts paused when it exists.
func (d *Daemon) loadClipPause(path string) {
	d.clipPauseFile = path
	if _, err := os.Stat(path); err == nil {
		d.clipPaused = true
		d.logf("clipboard sync is paused (%s)", path)
	}
}
```

- `New` only reads the marker. It runs before `Run` starts the network
  and the watcher (`daemon.go:552-553`), so the first copy and the first
  connect already see the pause. A second fluxd that `ipc.Listen` then
  stops (`cmd/fluxd/main.go:64-79`) has read it and changed nothing.
- A headless daemon (`fluxd --headless`, tests) leaves `clipPauseFile`
  empty: its pause is memory only, and it never reads the desktop's marker.
- Reboot clears the marker; logout too, unless lingering is on.

### 5. IPC and the state (`api.go`, `docs/ipc.md`)

- `Call` (`api.go:329-349`) gets two cases, with no parameters:

```go
case "clipboard.pause":
	return ok, d.PauseClipboard()
case "clipboard.resume":
	return ok, d.ResumeClipboard()
```

  They aren't in `deviceMethods` (`api.go:510-520`); they act on the
  computer.
- `Snapshot` (`api.go:113-215`) gets a top-level field in the state map
  (`api.go:169-212`), next to `"clipboard": clip` (`api.go:178`):

```go
"clipboardPaused": d.clipPaused,
```

  It is outside `settings`, because it isn't a setting of `config.toml`.
  `markDirty` in both methods sends a `state` event.
- `docs/ipc.md`:
  - the field list (`:61`): `clipboardPaused` is true while
    `clipboard.pause` holds; it survives a fluxd restart, not a reboot;
  - the method groups (`:183`, "Sharing") and the parameters table
    (`:207-239`): `clipboard.pause`, `clipboard.resume`, no parameters;
  - the errors table (`:313`): `not_saved` also covers a pause marker that
    fluxd couldn't write or remove, with what a restart then does;
  - the clipboard section (`:387-434`): both methods, and the guarantee
    from section 3, including that the pause stops only outgoing automatic
    sync, that manual `clipboard.send` still works, that incoming clips
    still arrive, and the 250 ms after a resume.

### 6. CLI (`cmd/flux`)

`clipboardCommand` (`cmd/flux/features.go:44-68`):

```go
case "pause", "resume", "status":
	if len(args) != 1 {
		return fmt.Errorf("clipboard %s takes no arguments", args[0])
	}
	switch args[0] {
	case "pause":
		return call("clipboard.pause", nil)
	case "resume":
		return call("clipboard.resume", nil)
	}
	return clipboardStatus()
```

- The argument check rejects `flux-cli clipboard pause 5m`, which would
  otherwise pause without a timer.
- `clipboardStatus` reads `state` once (as `printSetting` does,
  `features.go:99`) and prints `clipboardStatusWord(paused, auto)`:
  `paused` when `clipboardPaused` is true, even with `autoClipboard` off;
  else `off` when `autoClipboard` is false; else `syncing`.
- The usage line (`cmd/flux/main.go:62`) becomes two lines, in the style
  of `motion` (`main.go:97-100`):

```
  clipboard [clear [--all]|delete ID|limit [N]]  Manage clipboard history and limit
  clipboard pause|resume|status  Stop or restart sending copies to devices, or show it
```

  The error text (`features.go:67`) lists all words. Plain
  `flux-cli clipboard` keeps printing the history.

### 7. The Chat page line (`gui/qml/pages/Chat.qml`)

Both hosts expose the whole snapshot as `backend.state`
(`gui/app/src/fluxbackend.h:19`, `gui/omarchy/Backend.qml:46`), so no
host code changes.

- `readonly property bool clipPaused: !!(view && view.backend && view.backend.state && view.backend.state.clipboardPaused)`.
- A row under the toolbar (`Chat.qml:196-245`), `objectName: "clipPaused"`,
  visible only when `clipPaused`:
  - a `Txt` "Clipboard sync paused. Copies on this computer stay here.",
    `Theme.dim`, `wrapMode: Text.Wrap`, filling the width left of the
    button, so it wraps at `compact` widths (`Chat.qml:68`);
  - an `OutlineButton` "Resume" that calls `clipboard.resume` and on
    success toasts "Clipboard sync resumed".
- The list's top anchor (`Chat.qml:257`) moves below the row when it shows.
- It is global: it shows on every device's Chat page, whatever that
  device's clipboard rule.
- `gui/qml/tools/MockBackend.qml` handles `clipboard.pause` and
  `clipboard.resume` by setting `state.clipboardPaused`, and
  `gui/qml/tools/fixture.json` gets `"clipboardPaused": false`.

### 8. The bar mark (`gui/omarchy/BarWidget.qml`)

- `readonly property bool clipPaused: up && !!backend.state && backend.state.clipboardPaused === true`.
- The mark (`BarWidget.qml:74-80`) gets `opacity: root.clipPaused ? 0.5 : 1`.
- The tooltip (`:37-51`) adds "Clipboard sync paused" as its last line in
  every case after `up`, also with no paired device (before the early
  return at `:40`).
- The header comment says so. `docs/omarchy.md:10` adds: the mark dims
  while clipboard sync is paused.

### 9. Docs

- `docs/features.md`, in "Files, clipboard, and links" (`:126`): a short
  "Pause clipboard sync" part: the commands, what a pause keeps off the
  phone, that manual sends still work, that incoming clips still arrive,
  the 250 ms after a resume, that the pause survives a fluxd restart but
  not a reboot, and an example for a clipboard manager's private mode:
  `flux-cli clipboard pause` on enter, `flux-cli clipboard resume` on
  leave.
- `docs/security.md:65`, the "Clipboard sync" row's "turn it off" cell
  adds: "To stop only outgoing copies for a while:
  `flux-cli clipboard pause` (manual sends and incoming clips still
  work)."

## Alternatives rejected

- **Reuse `autoClipboard`.** It is saved in `config.toml`, and the issue
  asks for a pause that never sticks by accident.
- **A pause in memory only.** fluxd restarts itself after an update and
  after a crash; private mode would end silently (intent Q2).
- **Save the pause in `config.toml` or the state folder.** It would
  survive a reboot, which the issue doesn't want.
- **Read the clipboard at resume and mark its content as seen** (the
  first draft, and the mechanism in intent Q9). Both reviews found leaks
  and false drops in it:
  - a clipboard cleared, or holding the other kind, at resume leaves a
    late record unguarded;
  - content seen before under the same sum (A, B, A) disables the guard;
  - `Get` doesn't apply the watcher's filters (sensitive, HTML with an
    image, size), so the fence waits for a record that never comes;
  - a timed fence lets a record later than its timer through;
  - the read takes up to 10 s and races with copies made during it.

  A time on each record gives the boundary directly, with no clipboard
  read.
- **Stop the watcher while paused.** It doesn't stop records already in
  the pipe, it loses the `lastClipAt` update, and restarting `wl-paste`
  makes its first content "initial" by a 1-second heuristic
  (`internal/desktop/clipboard.go:47,127`).
- **Arrival time in Go** in place of the script's time. A late record is
  exactly the case where the two differ.
- **The wall clock (`date +%s%N`)** in the script. It can jump back (NTP,
  a manual change); a jump between a copy and a resume would let a paused
  copy count as later. `CLOCK_BOOTTIME` doesn't jump.
- **Hold `d.mu` across `l.Send`** to close the last gap before a send.
  `Send` can block up to 30 s on the network; the gap only carries content
  from before the pause.
- **Separate cancellation slots for manual and automatic images.** Two
  image sends could then run to one phone at once. One slot with a kind
  keeps "newest image wins" and lets a pause cancel only automatic sends.
- **Refcounted pauses or a timer.** Rejected in the intent (Q1).

## Risks

- **A watcher script that starts more than 250 ms after its copy**, on a
  very loaded machine, while a resume came in between: that copy would
  sync. A clipboard manager resumes when the user leaves private mode,
  normally seconds after the last private copy. Documented.
- **`/proc/uptime` has a resolution of 10 ms.** The grace is 25 times
  that.
- **A genuine copy within 250 ms after a resume isn't synced.** The next
  copy is. Documented.
- **The record format changes from 3 to 4 lines.** `watchScript` and
  `watchOnce` change together in one file; `TestClipboardWatch` covers it.
- **A crash between the image write and its removal** leaves one file in
  `$XDG_RUNTIME_DIR/flux/clipboard`. `Run` removes every clip image at
  start (`daemon.go:552`), and the folder is in memory. Accepted.
- **Turning `autoClipboard` off no longer stops a manual image.** It only
  stopped it by sharing the slot; the manual send is a user action.
- **A crashed clipboard manager leaves sync paused**, and the marker keeps
  it over a restart. The Chat line and the dimmed mark show it; `resume`
  ends it.
- **`not_saved`** when the runtime folder can't be written: the pause
  holds until fluxd restarts, and the error says so. A failed removal on
  resume pauses again after a restart (fails closed).
- **A text loop stopped by a pause** leaves `To` of that entry naming
  devices that got nothing (`clipboard.go:251-257`). Accepted: `To` is
  "sent to", and send errors are already ignored.
- **Snapshot churn:** one field, changes only on pause and resume.

## Verification

1. `go test -race ./...` and `go vet ./...`. The test helpers:
   - `clipDaemon` (`internal/core/clipboard_test.go:29-38`) gets
     `ctx: t.Context()`, so `newClipSend` works, and an optional
     `clipPauseFile` in `t.TempDir()`.
   - Copies are fed with an explicit `at`: `desktop.BootTime()` for "now",
     and values before or after `resumedAt` for late records. No sleeps.
   - A test-only hook, `var clipTestHook func(stage string)` (nil in
     production), is called after an image's admission and before each
     text send, so a test can pause at that exact point. It is called
     without `d.mu` held.
   - Image sends use the real link pair `linkPair`
     (`internal/core/clipboard_link_test.go:17`), as the existing image
     tests do.

   New tests in `internal/core/clipboard_test.go`:
   - **paused text:** no send, no entry, `lastClip` "", `lastClipAt`
     updated, `lastLocalClip` unchanged;
   - **paused image:** no send, no entry, no file in the clip folder;
   - **late records:** after pause and resume, a text and an image with
     `at` before the resume, or within 250 ms after it, are dropped; one
     after that syncs; two pause and resume cycles drop a record from the
     first; a record from before the pause arriving after the resume is
     dropped;
   - **pause clears `lastClip`:** a connect while paused and one after
     resume, before a new copy, send nothing; after a new copy, the
     connect sends it;
   - **pause after admission** (the hook): after an image's admission, a
     pause leaves no entry, no file, and starts no send; a pause during a
     transfer cancels it; a pause and resume at the hook still stop the old
     generation; a pause between two text links stops before the second;
     an old text callback held at the hook doesn't cancel a newer image;
   - **a failed image write** leaves no file;
   - **manual sends while paused:** a text and an image go out and enter
     the history; a pause during a manual image doesn't cancel it; an
     automatic image still replaces a manual one when not paused;
   - **SIGHUP and settings:** `Reload` and `setSetting("autoClipboard", …)`
     keep `clipPaused`;
   - **the marker:** pause writes it, resume removes it, `loadClipPause`
     with it starts paused, a missing folder is created; a read-only folder
     gives `not_saved` with the pause still on; concurrent pause and resume
     calls (`-race`, 100 rounds) end with the marker matching
     `clipPaused`; a headless daemon has no marker path. These call
     `loadClipPause` directly, not `New`, which would touch the real
     config, downloads and D-Bus.
   - **state:** `Snapshot` has `clipboardPaused`.

   `internal/desktop/desktop_test.go`, `TestClipboardWatch` (its fake
   `wl-paste` runs the real script): each callback gets an `at` between
   `BootTime()` before and after the test; a record with a bad time line
   is dropped.

   `cmd/flux/main_test.go`: `clipboardStatusWord` for all four cases
   (`paused` wins over `off`); `clipboard pause 5m` and `status x` fail;
   an unknown word still fails.
2. **QML:** `make test-gui`. In `gui/tests/tst_views.qml`, the "Chat"
   case: with `clipboardPaused` true the `clipPaused` row shows on two
   devices' pages, Resume sends `clipboard.resume`, and the row is gone
   after it; with false it doesn't show and the list starts under the
   toolbar; at a compact width the text wraps and doesn't overlap the
   button. The bar mark is checked by eye with
   `gui/omarchy/tools/test-offscreen.sh` (dimmed mark, tooltip with and
   without paired devices).
3. `nix flake check path:.`.
4. **Optional, only with the owner's OK:** the check with the phone. The
   real `fluxd.service` runs on p620, so this replaces it with the
   branch's fluxd for the test and puts it back after.
   - Pause, copy text and an image, reconnect the phone, resume, copy
     again: only the last copy arrives, and the history shows none of the
     paused copies.
   - `flux-cli clipboard pause; wl-copy secret; flux-cli clipboard resume`
     in one line: `secret` never arrives.
   - `systemctl --user restart fluxd` while paused:
     `flux-cli clipboard status` still says `paused`.

## Decisions for the owner

1. **The resume guard uses the copy's time, not a clipboard read** (intent
   Q9 named a clipboard read). The outcome is the one approved: a late
   record of a paused copy never syncs. The cost is a 250 ms window after
   each resume in which a new copy isn't synced, and a copy whose watcher
   script starts more than 250 ms late can slip through. Proposed: accept.
2. **"Already handed to a link" becomes "already checked".** The intent
   says a pause can't recall a packet already handed to a link. In code,
   a send checks the pause, releases the lock, then calls `l.Send`; a
   pause in that gap can't stop it. Only content from before the pause can
   be in that gap; no paused copy ever is. Proposed: accept, and word the
   docs that way.
