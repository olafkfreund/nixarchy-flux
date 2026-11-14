---
status: approved
issue: 16
spec: spec/2026-11-13-16-pause-clipboard.md
coders:
  1: coder   # the time line in the watcher, one parse, one clock helper and the new callback signature on 10 call sites; given in full below
  2: coder   # the pause state, admission rule, generation checks and marker; the concurrency code is given in full, so codex and agy stay independent reviewers of it
  3: coder   # two Call cases, one snapshot field, the CLI words and their tests; given in full below
  4: coder   # the Chat line, the mock, the fixture, the bar mark and their QML tests; given in full below
  5: coder   # docs text, given in full below
reviewers:
  - codex    # reads the branch diff with `codex review --base master`; strongest on the lock order, generation and marker logic of step 2
  - agy      # independent read of the diff against this plan, including the QML in step 4 and the docs wording
---

# Plan: Pause clipboard sync at runtime

## The approved decisions

From the intent and the spec, both approved; the spec's "Decisions for the
owner" 1 and 2 are accepted as written.

- **What it is.** A boolean pause of the automatic sync of local copies,
  set by `flux-cli clipboard pause|resume|status` and the IPC methods
  `clipboard.pause` and `clipboard.resume` (no parameters, idempotent).
  The last call wins; no timer, no counting of callers. Plain
  `flux-cli clipboard` keeps printing the history.
- **The guarantee.** A local copy, text or image, that fluxd handles while
  paused: is not sent to any device automatically, is not added to the
  history, leaves no image file, and doesn't set `lastClip`. It still
  updates `lastClipAt` (a time, no content), so an older
  `clipboard.connect` clip from a phone doesn't replace a fresh private
  copy. Manual sends (`flux-cli clip`, "Send clipboard", the window's Send)
  are the one exception: allowed, added to the history as today, never set
  `lastClip`, and a pause never cancels one.
- **Pausing** stops every automatic send: no new one starts, a running
  automatic image send is cancelled, a text loop stops before the next
  link. It clears `lastClip`. Work admitted before the pause stops at its
  next check. A send whose check passed just before the pause can still go
  out (spec decision 2, "already checked"); it carries content from before
  the pause, never a paused copy.
- **Resuming** sends nothing by itself. A copy whose watcher script started
  while paused, before the newest resume, or less than 250 ms after it, is
  a paused copy however late its record arrives (spec decision 1). A
  genuine copy in the first 250 ms after a resume isn't synced; a script
  that starts more than 250 ms after its copy, with a resume in between,
  could sync it (documented risk, accepted).
- **The record time.** The watcher script prints `CLOCK_BOOTTIME` from
  `/proc/uptime` as a new first line; fluxd compares it with the boot time
  of the last resume. No clipboard read at resume.
- **Persistence.** An empty marker file `$XDG_RUNTIME_DIR/flux/clipboard-paused`
  (mode 0600). `New` reads it before the watcher and the network start, so
  a fluxd restart keeps the pause; reboot (and logout without lingering)
  ends it. Never `config.toml`; `AutoClipboard`, device rules, settings
  changes and SIGHUP leave the pause alone. A marker that can't be written
  keeps the pause in memory and returns `not_saved`; a marker that can't be
  removed resumes in memory and returns `not_saved` (fails closed after a
  restart). A headless daemon keeps its pause in memory and never reads or
  writes the marker.
- **One image slot with a kind.** Automatic and manual image sends share
  `clipSend` (newest image wins); `clipSendAuto` marks an automatic one.
  Pausing, turning `autoClipboard` off and a SIGHUP with sync off cancel
  only an automatic image.
- **Incoming clips** (phone clips, shared text, signatures, screenshots):
  unchanged.
- **State.** Top-level `clipboardPaused` (boolean) in the `state`
  snapshot, outside `settings`; a `state` event on each change.
- **Indicator.** Global, not per device: a line "Clipboard sync paused.
  Copies on this computer stay here." with a Resume button under the Chat
  page's toolbar, on every device; the bar mark dims to 0.5 and its tooltip
  ends with "Clipboard sync paused", also with no paired device.
- **Docs**: `docs/ipc.md`, `docs/features.md`, `docs/security.md`,
  `docs/omarchy.md`, and the CLI usage.
- **Unchanged:** the protocol, Android, iOS, macOS, Windows, `config.toml`.

Refinements of the spec's sketches, each recorded in Corrections when its
step is done:

- **P1 (step 2).** The test hook is a `Daemon` field, `clipHook func(stage
  string)`, not a package variable. A package variable written by one test
  while a worker goroutine of an earlier test still reads it is a data race
  under `-race`. Stages: `"image"` (after an image's admission, before the
  save), `"image-send"` (in the worker, after a device's check, before its
  send), `"image-end"` (the worker ends), `"text"` (before each text link's
  check). It is nil in production and called without `d.mu`.
- **P2 (step 1).** A record with a bad time line is tested by calling
  `record` directly. `TestClipboardWatch`'s fake `wl-paste` runs the real
  script, which always prints a good time.
- **P3 (step 2).** `onLocalClipboard` marks the state dirty with
  `defer d.markDirty()` once it has added an entry, so the new early return
  after `autoAllowed` still sends a `state` event.
- **P4 (step 5).** `docs/cli.md:290-292` gets one line for the new words;
  the spec didn't list that file, but it documents every `clipboard` word.

## Steps

Steps 1-5 run in order on the branch, and the lead commits each one before
the next starts. Steps 1 and 2 both edit `internal/core/clipboard.go` and
`daemon.go`; step 1 only changes signatures there. Go tests run locally;
nothing in Android changes.

Paths are from the repository root. Line numbers are from the branch head
`270bef1`; a later step finds its place by function name when an earlier
step moved lines.

1. **The record time: `internal/desktop/clipboard.go`,
   `internal/desktop/desktop_test.go`, `internal/core/daemon.go`, core tests**
   - `internal/desktop/clipboard.go`:
     - Import `golang.org/x/sys/unix` (already a dependency of the package,
       `theme.go:18`).
     - Replace the comment and constant at `:38-43`:
       ```go
       // watchScript runs once for each selection change. It prints 4 lines:
       // the boot clock when the script starts, in seconds from /proc/uptime,
       // the CLIPBOARD_STATE value, the content as base64, and the MIME types
       // of the selection. The base64 keeps text with newlines in one line.
       // The script reads at most $1 bytes of the content. watchOnce sets $1
       // to 1 byte more than the limit, so record finds a large copy without
       // a read of all of it.
       const watchScript = `read -r up _ </proc/uptime; printf '%s\n' "$up"; printf '%s\n' "$CLIPBOARD_STATE"; head -c "$1" | base64 -w0; printf '\n'; wl-paste --list-types | tr '\n' ' '; printf '\n'`
       ```
     - After `clipboardLimit` (ends at `:166`), add:
       ```go
       // BootTime returns the time since boot on CLOCK_BOOTTIME, the clock of
       // /proc/uptime. It never jumps back and counts suspend.
       func BootTime() time.Duration {
       	var ts unix.Timespec
       	if err := unix.ClockGettime(unix.CLOCK_BOOTTIME, &ts); err != nil {
       		return 0
       	}
       	return time.Duration(ts.Nano())
       }
       ```
     - `Watch` (`:72-99`): the doc adds "Each callback gets at, the boot
       time when the watcher script for the copy started (BootTime)." The
       signature becomes
       `func (c *Clipboard) Watch(ctx context.Context, onText func(text string, at time.Duration), onImage func(data []byte, mime string, at time.Duration))`.
     - `watchOnce` (`:103`): the parameters become
       `onText func(string, time.Duration), onImage func([]byte, string, time.Duration)`;
       `var lines [3]string` (`:119`) becomes `var lines [4]string`.
     - `record` (`:133-157`) becomes:
       ```go
       // record handles 1 record of watchScript from the wl-paste process for the
       // type typ. initial marks the current content at the start of wl-paste.
       // A record whose time line doesn't parse is dropped.
       func (c *Clipboard) record(typ string, lines [4]string, initial bool, onText func(string, time.Duration), onImage func([]byte, string, time.Duration)) {
       	at, err := time.ParseDuration(lines[0] + "s")
       	if err != nil || at < 0 {
       		return
       	}
       	state, encoded, types := lines[1], lines[2], strings.Fields(lines[3])
       	if state == "sensitive" || state == "nil" || state == "clear" {
       		return
       	}
       	limit := clipboardLimit(typ)
       	if len(encoded) > base64.StdEncoding.EncodedLen(limit) {
       		return
       	}
       	raw, err := base64.StdEncoding.DecodeString(encoded)
       	if err != nil || len(raw) == 0 || len(raw) > limit {
       		return
       	}
       	if typ == ImageType {
       		if isImage(types) && bytes.HasPrefix(raw, pngMagic) && c.observe(contentKey(ImageType, raw), initial) {
       			onImage(raw, ImageType, at)
       		}
       		return
       	}
       	if !isImage(types) && isText(raw) && c.observe(contentKey("text", raw), initial) {
       		onText(string(raw), at)
       	}
       }
       ```
   - `internal/desktop/desktop_test.go`, `TestClipboardWatch` (`:252-`):
     - before the `go func()` at `:293`, add `from := BootTime()`; the
       channels carry the time too:
       ```go
       type got struct {
       	text  string
       	image []byte
       	at    time.Duration
       }
       recs := make(chan got, 4)
       ```
       and the `Watch` call (`:294`) becomes
       `NewClipboard().Watch(ctx, func(s string, at time.Duration) { recs <- got{text: s, at: at} }, func(b []byte, _ string, at time.Duration) { recs <- got{image: b, at: at} })`.
     - the `for range 2` loop reads `r := <-recs`, checks the text or the
       image as today, and checks
       `r.at >= from-10*time.Millisecond && r.at <= BootTime()`
       (`/proc/uptime` has 10 ms steps, so `at` can be up to 10 ms below
       the nanosecond clock read just before).
     - add a test:
       ```go
       // A record whose time line doesn't parse is dropped.
       func TestClipboardRecordTime(t *testing.T) {
       	c := NewClipboard()
       	var texts []string
       	var ats []time.Duration
       	onText := func(s string, at time.Duration) { texts, ats = append(texts, s), append(ats, at) }
       	onImage := func([]byte, string, time.Duration) { t.Fatal("an image") }
       	enc := base64.StdEncoding.EncodeToString
       	for _, bad := range []string{"", "x", "-1.00", "1e3"} {
       		c.record("text", [4]string{bad, "data", enc([]byte("bad " + bad)), "text/plain"}, false, onText, onImage)
       	}
       	c.record("text", [4]string{"12345.67", "data", enc([]byte("good")), "text/plain"}, false, onText, onImage)
       	if !slices.Equal(texts, []string{"good"}) || ats[0] != 12345*time.Second+670*time.Millisecond {
       		t.Fatalf("texts %q at %v", texts, ats)
       	}
       }
       ```
       Add `encoding/base64` to the test's imports if it isn't there.
   - `internal/core/daemon.go`:
     - the `clipboard` interface's `Watch` (`:255`) becomes
       `Watch(ctx context.Context, onText func(text string, at time.Duration), onImage func(data []byte, mime string, at time.Duration))`;
     - `memClipboard.Watch` (`:270`) becomes
       `func (m *memClipboard) Watch(ctx context.Context, _ func(string, time.Duration), _ func([]byte, string, time.Duration)) {`.
   - `internal/core/clipboard.go`: only the signatures in this step:
     `func (d *Daemon) onLocalClipboard(text string, at time.Duration)`
     (`:239`) and `func (d *Daemon) onLocalImage(data []byte, mime string, at time.Duration)`
     (`:280`). `at` is unused until step 2; a named unused parameter
     compiles.
   - Core test call sites: append `, desktop.BootTime()` to each call, and
     add `"flux/internal/desktop"` to the imports of
     `internal/core/clipboard_test.go` and
     `internal/core/clipboard_link_test.go`:
     `clipboard_test.go:355,359,369,371,392,398,404,457,458` and
     `clipboard_link_test.go:89`.

   Verify:
   ```sh
   go test -race ./internal/desktop/ ./internal/core/ && go vet ./internal/...
   grep -rn "onLocalClipboard(\|onLocalImage(\|\.Watch(" --include=*.go internal cmd
   ```
   shows the two definitions, `daemon.go:553` (method values, unchanged),
   the desktop test, and every core test call with a second time argument.

   Traps:
   - `watchScript` and `watchOnce` change together: 4 lines per record.
   - `read -r up _ </proc/uptime` redirects only `read`; `head -c` still
     reads the selection on the script's stdin. Keep it on one line in
     the Go raw string; no other quoting changes.
   - `time.ParseDuration("12345.67s")` parses; it rejects an empty line,
     `"1e3s"` and text. Keep the `at < 0` check for `"-1.00"`.
   - Pass a real boot time (`desktop.BootTime()`) in every test call, never
     `0`: from step 2, a copy at or before `resumedAt+250ms` is dropped,
     and `resumedAt` starts at 0.

2. **Pause state and admission: `internal/core/daemon.go`,
   `internal/core/clipboard.go`, `internal/core/api.go`,
   `internal/core/clipboard_test.go`**
   - `internal/core/daemon.go`, after `clipSend context.CancelFunc`
     (`:52`), add:
     ```go
     	// clipPaused stops the automatic sync of local copies
     	// (clipboard.pause). resumedAt is the boot time of the last resume.
     	// clipGen grows on each pause, so automatic work admitted before a
     	// pause stops at its next check. clipSendAuto is true while clipSend
     	// is an automatic image send. All under mu.
     	clipPaused   bool
     	resumedAt    time.Duration
     	clipGen      uint64
     	clipSendAuto bool
     	// clipPauseFile keeps the pause over a restart. It is "" in a
     	// headless daemon. pauseMu orders the pause and resume calls with
     	// their marker writes; take it before mu.
     	clipPauseFile string
     	pauseMu       sync.Mutex
     	// clipHook is nil in production. A test sets it to act at a stage of
     	// an automatic send: "image", "image-send", "image-end" or "text". It
     	// runs without mu.
     	clipHook func(stage string)
     ```
   - `internal/core/daemon.go`, `New`: the existing `if !opts.Headless {`
     block at `:358` starts with:
     ```go
     		d.loadClipPause(filepath.Join(config.RuntimeDir(), "clipboard-paused"))
     ```
     (before `desktop.NewNotifier()`; `Run` starts the watcher and the
     network later, `daemon.go:552-553`).
   - `internal/core/clipboard.go`:
     - `addClipImageIf` (`:145-168`): the write at `:153-155` becomes
       ```go
       	if err := os.WriteFile(path, data, 0o600); err != nil {
       		os.Remove(path)
       		return err
       	}
       ```
       and its doc adds "A failed save leaves no file."
     - Replace `newClipSend` and `stopClipSend` (`:214-235`) with:
       ```go
       // newClipSend stops the image that fluxd sends and registers the next
       // one. auto marks an automatic send. For an automatic send it first
       // checks that no pause came after gen, and returns ok false without
       // stopping anything when one did.
       func (d *Daemon) newClipSend(auto bool, gen uint64) (ctx context.Context, cancel context.CancelFunc, ok bool) {
       	d.mu.Lock()
       	defer d.mu.Unlock()
       	if auto && (d.clipPaused || d.clipGen != gen) {
       		return nil, nil, false
       	}
       	ctx, cancel = context.WithTimeout(d.ctx, clipImageTimeout)
       	if d.clipSend != nil {
       		d.clipSend()
       	}
       	d.clipSend, d.clipSendAuto = cancel, auto
       	return ctx, cancel, true
       }

       // stopAutoClipSendLocked stops the automatic image that fluxd sends. A
       // manual send goes on. The caller holds d.mu.
       func (d *Daemon) stopAutoClipSendLocked() {
       	if d.clipSend != nil && d.clipSendAuto {
       		d.clipSend()
       		d.clipSend = nil
       	}
       }

       // stopAutoClipSend is stopAutoClipSendLocked with the lock.
       func (d *Daemon) stopAutoClipSend() {
       	d.mu.Lock()
       	d.stopAutoClipSendLocked()
       	d.mu.Unlock()
       }

       // resumeGrace covers the time between a copy and the start of its
       // watcher script, so a copy made just before a resume counts as paused.
       const resumeGrace = 250 * time.Millisecond

       // copyPausedLocked reports whether a local copy made at the boot time at
       // is a paused copy: made while paused, before the last resume, or less
       // than resumeGrace after it. The caller holds d.mu.
       func (d *Daemon) copyPausedLocked(at time.Duration) bool {
       	return d.clipPaused || at <= d.resumedAt+resumeGrace
       }

       // autoAllowed reports whether automatic work admitted in generation gen
       // may go on. It takes d.mu.
       func (d *Daemon) autoAllowed(gen uint64) bool {
       	d.mu.Lock()
       	defer d.mu.Unlock()
       	return d.cfg.AutoClipboard && !d.clipPaused && d.clipGen == gen
       }

       // hook calls clipHook for a test.
       func (d *Daemon) hook(stage string) {
       	if d.clipHook != nil {
       		d.clipHook(stage)
       	}
       }

       // PauseClipboard stops the automatic sync of local copies until
       // ResumeClipboard. It clears the last text for a device that connects,
       // and stops an automatic image send. The marker keeps the pause over a
       // restart of fluxd.
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

       // ResumeClipboard ends a pause. It sends nothing by itself.
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

       // writePauseFile writes the empty pause marker at path.
       func writePauseFile(path string) error {
       	if err := os.MkdirAll(filepath.Dir(path), 0o700); err != nil {
       		return err
       	}
       	return os.WriteFile(path, nil, 0o600)
       }

       // loadClipPause sets the marker path and starts paused when it exists.
       func (d *Daemon) loadClipPause(path string) {
       	d.clipPauseFile = path
       	if _, err := os.Stat(path); err == nil {
       		d.clipPaused = true
       		d.logf("clipboard sync is paused (%s)", path)
       	}
       }
       ```
     - `onLocalClipboard` (`:237-275`) becomes:
       ```go
       // onLocalClipboard sends a local clipboard change to every paired device.
       // A text above maxSentText goes only to the clipboard history. A paused
       // copy only updates lastClipAt.
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
       	// A device that connects later gets no older text in place of a text
       	// that is too large.
       	d.content.lastClip = ""
       	if fits {
       		d.content.lastClip = text
       	}
       	auto := d.cfg.AutoClipboard
       	if auto {
       		var to []string
       		if fits {
       			to = d.featureDevicesLocked("clipboard")
       		}
       		d.addClipLocked(ClipEntry{Text: text, Dir: "out", DeviceName: "this pc", To: to, Time: time.Now().Unix()})
       		// The text replaces an automatic image that is still on its way.
       		d.stopAutoClipSendLocked()
       	}
       	d.mu.Unlock()
       	if !auto {
       		return
       	}
       	defer d.markDirty()
       	if !d.autoAllowed(gen) {
       		return
       	}
       	links := d.featureLinks("clipboard")
       	switch {
       	case !fits && len(links) > 0:
       		d.logf("clipboard: did not sync a text of %d bytes", len(text))
       		d.toast("The copied text is larger than %d KiB. Flux did not sync it", maxSentText>>10)
       	case fits:
       		for _, l := range links {
       			d.hook("text")
       			if !d.autoAllowed(gen) {
       				break
       			}
       			_ = l.Send(proto.New(proto.TypeClipboard, map[string]any{"content": text}))
       		}
       	}
       }
       ```
     - `onLocalImage` (`:277-328`) becomes:
       ```go
       // onLocalImage sends a local image copy to every paired device that
       // accepts clipboard images. A newer copy, an unpair, a dropped link,
       // auto_clipboard off, and a pause stop the transfer. A paused copy only
       // updates lastClipAt.
       func (d *Daemon) onLocalImage(data []byte, mime string, at time.Duration) {
       	type target struct {
       		dev *Device
       		l   *lan.Link
       	}
       	d.mu.Lock()
       	d.lastClipAt = time.Now()
       	if d.copyPausedLocked(at) {
       		d.mu.Unlock()
       		return
       	}
       	d.lastLocalClip = d.lastClipAt
       	gen := d.clipGen
       	// A device that connects later gets no older text in place of the
       	// image.
       	d.content.lastClip = ""
       	auto := d.cfg.AutoClipboard
       	var targets []target
       	for _, dev := range d.devices {
       		if dev.Paired && dev.link != nil && dev.accepts(proto.TypeFluxClipboardImage) && d.permittedLocked(dev.ID, "clipboard") {
       			targets = append(targets, target{dev, dev.link})
       		}
       	}
       	d.mu.Unlock()
       	if !auto {
       		return
       	}
       	to := make([]string, 0, len(targets))
       	for _, t := range targets {
       		to = append(to, t.dev.ID)
       	}
       	d.hook("image")
       	// current runs under d.mu: it reads the fields and must not call autoAllowed.
       	current := func() bool { return !d.clipPaused && d.clipGen == gen }
       	if err := d.addClipImageIf(ClipEntry{Dir: "out", DeviceName: "this pc", To: to, Time: time.Now().Unix()}, data, mime, current); err != nil {
       		d.logf("save clipboard image: %v", err)
       	}
       	ctx, cancel, ok := d.newClipSend(true, gen)
       	if !ok {
       		return
       	}
       	go func() {
       		defer d.hook("image-end")
       		defer cancel()
       		for _, t := range targets {
       			d.mu.Lock()
       			ok := t.dev.Paired && t.dev.link == t.l && d.cfg.AutoClipboard && !d.clipPaused && d.clipGen == gen && d.permittedLocked(t.dev.ID, "clipboard")
       			d.mu.Unlock()
       			if !ok || ctx.Err() != nil {
       				continue
       			}
       			d.hook("image-send")
       			lctx, lcancel := context.WithCancel(ctx)
       			cancelOnLinkDown(lctx, t.l, lcancel)
       			err := sendClipImage(lctx, t.l, data, mime)
       			lcancel()
       			if err != nil && ctx.Err() == nil {
       				d.logf("send clipboard image to %s: %v", t.l.Identity.DeviceName, err)
       			}
       		}
       	}()
       }
       ```
     - `sendConnectClipboard` (`:333-342`): the read at `:335-336` becomes
       ```go
       	text, ts, auto := d.content.lastClip, d.lastLocalClip.UnixMilli(), d.cfg.AutoClipboard && !d.clipPaused
       	auto = auto && d.permittedLocked(l.DeviceID(), "clipboard")
       ```
       and the doc adds "Nothing goes out while clipboard sync is paused."
     - `sendImageTo` (`:591`): `ctx, cancel, _ := d.newClipSend(false, 0)`.
       `SendClipboard` (`:544-571`) has no pause check.
   - `internal/core/api.go`: `setSetting` (`:711`) and `Reload` (`:747`)
     call `d.stopAutoClipSend()` in place of `d.stopClipSend()`; their
     comments become "An automatic image that is on its way to the phones
     stops." Neither touches the pause fields.
   - `internal/core/clipboard_test.go`:
     - `clipDaemon` (`:29-38`) adds `ctx: t.Context(),` to the literal.
     - Add at the end of the file:
       ```go
       // phoneTexts starts reading the link and returns the contents of its
       // packets of type typ, in order.
       func phoneTexts(l *lan.Link, typ string) <-chan string {
       	out := make(chan string, 16)
       	go l.Receive(func(p *proto.Packet) {
       		var b struct {
       			Content string `json:"content"`
       		}
       		if p.Type == typ && p.Decode(&b) == nil {
       			out <- b.Content
       		}
       	})
       	return out
       }

       // next returns the next text, or fails after 5 seconds.
       func next(t *testing.T, ch <-chan string) string {
       	t.Helper()
       	select {
       	case s := <-ch:
       		return s
       	case <-time.After(5 * time.Second):
       		t.Fatal("no packet within 5 seconds")
       		return ""
       	}
       }

       // afterGrace is the boot time of a copy made just after the grace of the
       // last resume.
       func afterGrace(d *Daemon) time.Duration {
       	d.mu.Lock()
       	defer d.mu.Unlock()
       	return d.resumedAt + resumeGrace + time.Millisecond
       }

       // A paused text copy goes nowhere: no send, no entry, no last text. It
       // still updates lastClipAt. The next copy after the resume syncs.
       func TestPausedText(t *testing.T) {
       	onDesk, onPhone, _, phoneID := linkPair(t, t.Context())
       	got := phoneTexts(onPhone, proto.TypeClipboard)
       	d, _ := clipDaemon(t, true)
       	d.devices[phoneID] = &Device{ID: phoneID, Name: "Pixel 8", Paired: true, link: onDesk}
       	d.onLocalClipboard("before", desktop.BootTime())
       	if err := d.PauseClipboard(); err != nil {
       		t.Fatal(err)
       	}
       	if d.content.lastClip != "" {
       		t.Fatalf("the pause kept the last text %q", d.content.lastClip)
       	}
       	local, at := d.lastLocalClip, d.lastClipAt
       	d.onLocalClipboard("secret", desktop.BootTime())
       	if len(d.clipboard) != 1 || d.clipboard[0].Text != "before" || d.content.lastClip != "" || !d.lastLocalClip.Equal(local) || d.lastClipAt.Before(at) {
       		t.Fatalf("history %+v, last text %q", d.clipboard, d.content.lastClip)
       	}
       	if err := d.ResumeClipboard(); err != nil {
       		t.Fatal(err)
       	}
       	d.onLocalClipboard("after", afterGrace(d))
       	for _, want := range []string{"before", "after"} {
       		if s := next(t, got); s != want {
       			t.Fatalf("the phone got %q, want %q", s, want)
       		}
       	}
       }

       // A paused image copy leaves no entry, no file, and starts no send.
       func TestPausedImage(t *testing.T) {
       	d, _ := clipDaemon(t, true)
       	d.devices["phone"] = &Device{ID: "phone", Paired: true}
       	if err := d.PauseClipboard(); err != nil {
       		t.Fatal(err)
       	}
       	d.onLocalImage(testPNG(1), "image/png", desktop.BootTime())
       	if len(d.clipboard) != 0 || clipFiles(t, d.clipDir) != 0 || d.clipSend != nil || d.lastClipAt.IsZero() {
       		t.Fatalf("history %+v, %d files", d.clipboard, clipFiles(t, d.clipDir))
       	}
       }

       // A record that arrives after the resume but was made before it, or in
       // the grace after it, is a paused copy, over any number of cycles.
       func TestLateRecordsAfterResume(t *testing.T) {
       	d, _ := clipDaemon(t, true)
       	before := desktop.BootTime()
       	_ = d.PauseClipboard()
       	paused := desktop.BootTime()
       	_ = d.ResumeClipboard()
       	for _, at := range []time.Duration{before, paused, d.resumedAt, d.resumedAt + resumeGrace} {
       		d.onLocalClipboard(fmt.Sprint("late ", at), at)
       		d.onLocalImage(testPNG(1), "image/png", at)
       	}
       	_ = d.PauseClipboard()
       	_ = d.ResumeClipboard()
       	d.onLocalClipboard("first cycle", paused)
       	if len(d.clipboard) != 0 || clipFiles(t, d.clipDir) != 0 || d.content.lastClip != "" {
       		t.Fatalf("history %+v", d.clipboard)
       	}
       	d.onLocalClipboard("new", afterGrace(d))
       	if len(d.clipboard) != 1 || d.clipboard[0].Text != "new" || d.content.lastClip != "new" {
       		t.Fatalf("history %+v, last text %q", d.clipboard, d.content.lastClip)
       	}
       }

       // The pause clears the last text: a connect while paused and after the
       // resume sends nothing until a new copy.
       func TestConnectAfterPause(t *testing.T) {
       	onDesk, onPhone, _, _ := linkPair(t, t.Context())
       	got := phoneTexts(onPhone, proto.TypeClipboardConnect)
       	d, _ := clipDaemon(t, true)
       	d.onLocalClipboard("before", desktop.BootTime())
       	_ = d.PauseClipboard()
       	d.sendConnectClipboard(onDesk)
       	_ = d.ResumeClipboard()
       	d.sendConnectClipboard(onDesk)
       	d.onLocalClipboard("after", afterGrace(d))
       	d.sendConnectClipboard(onDesk)
       	if s := next(t, got); s != "after" {
       		t.Fatalf("the phone got %q on connect", s)
       	}
       }

       // A pause after an image's admission leaves no entry and no file and
       // starts no send, also with a resume right after it.
       func TestPauseAfterImageAdmission(t *testing.T) {
       	for _, resume := range []bool{false, true} {
       		d, _ := clipDaemon(t, true)
       		d.clipHook = func(stage string) {
       			if stage == "image" {
       				_ = d.PauseClipboard()
       				if resume {
       					_ = d.ResumeClipboard()
       				}
       			}
       		}
       		d.onLocalImage(testPNG(2), "image/png", desktop.BootTime())
       		if len(d.clipboard) != 0 || clipFiles(t, d.clipDir) != 0 || d.clipSend != nil {
       			t.Fatalf("resume %v: history %+v", resume, d.clipboard)
       		}
       	}
       }

       // A pause between 2 links of a text stops before the second.
       func TestPauseBetweenTextLinks(t *testing.T) {
       	desk1, phone1, _, id1 := linkPair(t, t.Context())
       	desk2, phone2, _, id2 := linkPair(t, t.Context())
       	got1, got2 := phoneTexts(phone1, proto.TypeClipboard), phoneTexts(phone2, proto.TypeClipboard)
       	d, _ := clipDaemon(t, true)
       	d.devices[id1] = &Device{ID: id1, Paired: true, link: desk1}
       	d.devices[id2] = &Device{ID: id2, Paired: true, link: desk2}
       	n := 0
       	d.clipHook = func(stage string) {
       		if stage == "text" {
       			if n++; n == 2 {
       				_ = d.PauseClipboard()
       			}
       		}
       	}
       	d.onLocalClipboard("one link", desktop.BootTime())
       	d.clipHook = nil
       	_ = d.ResumeClipboard()
       	d.onLocalClipboard("both", afterGrace(d))
       	first := []string{next(t, got1), next(t, got2)}
       	slices.Sort(first)
       	if !slices.Equal(first, []string{"both", "one link"}) {
       		t.Fatalf("the first texts %q", first)
       	}
       }

       // A pause during an automatic image send cancels it, and the next text
       // still arrives.
       func TestPauseDuringImageSend(t *testing.T) {
       	ctx := t.Context()
       	onDesk, onPhone, deskID, phoneID := linkPair(t, ctx)
       	a, _ := clipDaemon(t, true)
       	b, clipB := clipDaemon(t, true)
       	phoneDev := &Device{ID: phoneID, Name: "phone", Paired: true, link: onDesk, Incoming: []string{proto.TypeFluxClipboardImage}}
       	deskDev := &Device{ID: deskID, Name: "desk", Paired: true, link: onPhone, Incoming: []string{proto.TypeFluxClipboardImage}}
       	a.devices[phoneID], b.devices[deskID] = phoneDev, deskDev
       	go onDesk.Receive(func(p *proto.Packet) { a.handlePacket(phoneDev, onDesk, p) })
       	go onPhone.Receive(func(p *proto.Packet) { b.handlePacket(deskDev, onPhone, p) })
       	ended := make(chan struct{})
       	a.clipHook = func(stage string) {
       		switch stage {
       		case "image-send":
       			_ = a.PauseClipboard()
       		case "image-end":
       			close(ended)
       		}
       	}
       	a.onLocalImage(testPNG(3), "image/png", desktop.BootTime())
       	<-ended
       	if err := a.SendClipboard(phoneDev, "marker"); err != nil {
       		t.Fatal(err)
       	}
       	waitFor(t, "the marker", func() bool {
       		clipB.mu.Lock()
       		defer clipB.mu.Unlock()
       		return clipB.text == "marker"
       	})
       	clipB.mu.Lock()
       	defer clipB.mu.Unlock()
       	if clipB.image != nil {
       		t.Fatal("the phone got the image after the pause")
       	}
       }

       // An old text callback held after its admission can't cancel a newer
       // automatic image. The image is registered directly, so the phone's
       // handling of the late text can't cancel its fetch and blur the check.
       func TestOldTextKeepsNewerImage(t *testing.T) {
       	onDesk, onPhone, _, phoneID := linkPair(t, t.Context())
       	got := phoneTexts(onPhone, proto.TypeClipboard)
       	d, _ := clipDaemon(t, true)
       	d.devices[phoneID] = &Device{ID: phoneID, Paired: true, link: onDesk}
       	held, release := make(chan struct{}), make(chan struct{})
       	d.clipHook = func(stage string) {
       		if stage == "text" {
       			close(held)
       			<-release
       		}
       	}
       	done := make(chan struct{})
       	go func() { d.onLocalClipboard("old text", desktop.BootTime()); close(done) }()
       	<-held
       	d.mu.Lock()
       	gen := d.clipGen
       	d.mu.Unlock()
       	ictx, icancel, ok := d.newClipSend(true, gen)
       	defer icancel()
       	close(release)
       	<-done
       	if !ok || ictx.Err() != nil {
       		t.Fatal("the old text cancelled the newer image")
       	}
       	if s := next(t, got); s != "old text" {
       		t.Fatalf("the phone got %q", s)
       	}
       }

       // A failed image save leaves no file and no entry.
       func TestFailedImageSave(t *testing.T) {
       	d, _ := clipDaemon(t, true)
       	if err := os.Chmod(d.clipDir, 0o500); err != nil {
       		t.Fatal(err)
       	}
       	t.Cleanup(func() { os.Chmod(d.clipDir, 0o700) })
       	if err := d.addClipImage(ClipEntry{Dir: "out"}, testPNG(5), "image/png"); err == nil {
       		t.Fatal("the save worked in a read-only folder")
       	}
       	if len(d.clipboard) != 0 || clipFiles(t, d.clipDir) != 0 {
       		t.Fatalf("history %+v", d.clipboard)
       	}
       }

       // Manual sends work while paused and enter the history; they never set
       // the last text.
       func TestManualSendWhilePaused(t *testing.T) {
       	ctx := t.Context()
       	onDesk, onPhone, deskID, phoneID := linkPair(t, ctx)
       	a, clipA := clipDaemon(t, true)
       	b, clipB := clipDaemon(t, true)
       	phoneDev := &Device{ID: phoneID, Name: "phone", Paired: true, link: onDesk, Incoming: []string{proto.TypeFluxClipboardImage}}
       	deskDev := &Device{ID: deskID, Name: "desk", Paired: true, link: onPhone, Incoming: []string{proto.TypeFluxClipboardImage}}
       	a.devices[phoneID], b.devices[deskID] = phoneDev, deskDev
       	go onDesk.Receive(func(p *proto.Packet) { a.handlePacket(phoneDev, onDesk, p) })
       	go onPhone.Receive(func(p *proto.Packet) { b.handlePacket(deskDev, onPhone, p) })
       	_ = a.PauseClipboard()
       	if err := a.SendClipboard(phoneDev, "manual text"); err != nil {
       		t.Fatal(err)
       	}
       	img := append(testPNG(6), bytes.Repeat([]byte{0xef}, 100_000)...)
       	_ = clipA.SetImage(img, "image/png")
       	if err := a.SendClipboard(phoneDev, ""); err != nil {
       		t.Fatal(err)
       	}
       	if got := waitImage(t, clipB); !bytes.Equal(got, img) {
       		t.Fatalf("the phone got %d bytes", len(got))
       	}
       	if len(a.clipboard) != 2 || a.content.lastClip != "" {
       		t.Fatalf("history %+v, last text %q", a.clipboard, a.content.lastClip)
       	}
       }

       // A pause cancels only an automatic image. An automatic image replaces
       // a manual one, and a stale generation registers nothing.
       func TestClipSendKinds(t *testing.T) {
       	d, _ := clipDaemon(t, true)
       	mctx, mcancel, _ := d.newClipSend(false, 0)
       	defer mcancel()
       	_ = d.PauseClipboard()
       	if mctx.Err() != nil {
       		t.Fatal("the pause cancelled a manual image")
       	}
       	_ = d.ResumeClipboard()
       	if _, _, ok := d.newClipSend(true, d.clipGen-1); ok || mctx.Err() != nil {
       		t.Fatal("a stale automatic send registered or cancelled the manual one")
       	}
       	actx, acancel, ok := d.newClipSend(true, d.clipGen)
       	defer acancel()
       	if !ok || mctx.Err() == nil {
       		t.Fatal("an automatic image did not replace the manual one")
       	}
       	_ = d.PauseClipboard()
       	if actx.Err() == nil {
       		t.Fatal("the pause did not cancel the automatic image")
       	}
       }

       // Settings and SIGHUP leave the pause as it is.
       func TestPauseSurvivesSettings(t *testing.T) {
       	t.Setenv("XDG_CONFIG_HOME", t.TempDir())
       	d, _ := clipDaemon(t, true)
       	_ = d.PauseClipboard()
       	for _, on := range []bool{false, true} {
       		if err := d.setSetting("autoClipboard", on); err != nil {
       			t.Fatal(err)
       		}
       	}
       	if err := d.Reload(); err != nil {
       		t.Fatal(err)
       	}
       	if !d.clipPaused {
       		t.Fatal("a setting or a reload ended the pause")
       	}
       }

       // The marker keeps the pause over a restart, and a failed write or
       // removal gives not_saved with the pause in memory as asked.
       func TestClipPauseMarker(t *testing.T) {
       	path := filepath.Join(t.TempDir(), "flux", "clipboard-paused")
       	d, _ := clipDaemon(t, true)
       	d.loadClipPause(path)
       	if d.clipPaused {
       		t.Fatal("paused without a marker")
       	}
       	if err := d.PauseClipboard(); err != nil {
       		t.Fatal(err)
       	}
       	if fi, err := os.Stat(path); err != nil || fi.Mode().Perm() != 0o600 {
       		t.Fatalf("marker %v %v", fi, err)
       	}
       	e, _ := clipDaemon(t, true)
       	e.loadClipPause(path)
       	if !e.clipPaused {
       		t.Fatal("a restart resumed")
       	}
       	if err := d.ResumeClipboard(); err != nil {
       		t.Fatal(err)
       	}
       	if err := d.ResumeClipboard(); err != nil {
       		t.Fatal("a second resume:", err)
       	}
       	if _, err := os.Stat(path); !errors.Is(err, os.ErrNotExist) {
       		t.Fatalf("the marker stays: %v", err)
       	}

       	ro := t.TempDir()
       	d.clipPauseFile = filepath.Join(ro, "clipboard-paused")
       	if err := os.Chmod(ro, 0o500); err != nil {
       		t.Fatal(err)
       	}
       	t.Cleanup(func() { os.Chmod(ro, 0o700) })
       	if err := d.PauseClipboard(); errCode(err) != "not_saved" || !d.clipPaused {
       		t.Fatalf("pause in a read-only folder: %v, paused %v", err, d.clipPaused)
       	}
       	_ = os.Chmod(ro, 0o700)
       	_ = d.ResumeClipboard()
       	_ = d.PauseClipboard()
       	_ = os.Chmod(ro, 0o500)
       	if err := d.ResumeClipboard(); errCode(err) != "not_saved" || d.clipPaused {
       		t.Fatalf("resume in a read-only folder: %v, paused %v", err, d.clipPaused)
       	}
       }

       // Concurrent calls end with the marker matching the pause.
       func TestClipPauseRace(t *testing.T) {
       	d, _ := clipDaemon(t, true)
       	d.clipPauseFile = filepath.Join(t.TempDir(), "clipboard-paused")
       	var wg sync.WaitGroup
       	for i := range 100 {
       		wg.Add(1)
       		go func() {
       			defer wg.Done()
       			if i%2 == 0 {
       				_ = d.PauseClipboard()
       			} else {
       				_ = d.ResumeClipboard()
       			}
       		}()
       	}
       	wg.Wait()
       	_, err := os.Stat(d.clipPauseFile)
       	if (err == nil) != d.clipPaused {
       		t.Fatalf("marker %v, paused %v", err, d.clipPaused)
       	}
       }

       // A daemon without a marker path (headless) pauses in memory only.
       func TestClipPauseHeadless(t *testing.T) {
       	d, _ := clipDaemon(t, true)
       	if d.clipPauseFile != "" {
       		t.Fatal("a marker path")
       	}
       	if err := d.PauseClipboard(); err != nil || !d.clipPaused {
       		t.Fatal(err)
       	}
       	if err := d.ResumeClipboard(); err != nil || d.clipPaused {
       		t.Fatal(err)
       	}
       }
       ```
     - Imports to add to `clipboard_test.go`: `flux/internal/desktop` (from
       step 1) and `flux/internal/lan`. `bytes`, `errors`, `fmt`, `os`,
       `path/filepath`, `slices`, `sync`, `time`, `proto` are there
       (`:3-21`).

   Verify:
   ```sh
   go test -race -count=3 -run 'Pause|Paused|Late|Connect|ClipSend|Manual|FailedImage|OldText|Clip' ./internal/core/
   go test -race ./internal/... && go vet ./internal/... && gofmt -l internal cmd
   grep -rn "stopClipSend\b\|newClipSend()" --include=*.go internal
   ```
   The last command prints nothing.

   Traps:
   - Lock order: `pauseMu` before `d.mu`, never the other way. Never hold
     `d.mu` across file I/O, `l.Send`, `sendClipImage` or `d.hook`.
   - `current` (the `addClipImageIf` predicate) runs under `d.mu`
     (`clipboard.go:158`); it reads the fields directly. Calling
     `autoAllowed` there deadlocks.
   - `break` inside `for _, l := range links` within the `switch` ends the
     loop, which is the intent; don't turn it into `return` (the deferred
     `markDirty` runs either way).
   - `stopAutoClipSendLocked` in `onLocalClipboard` moves into the locked
     admission; don't leave a second call after the unlock or in the loop
     (it would let an old callback cancel a newer image). With the call in
     the admission, `TestOldTextKeepsNewerImage` guards against one added
     after the `"text"` hook; it can't see one between the unlock and the
     hook.
   - `newClipSend` now holds `d.mu` while it creates the context;
     `context.WithTimeout` doesn't call back into the daemon, so that is
     safe. `d.ctx` is set at construction; tests get it from `clipDaemon`.
   - `copyPausedLocked` reads `resumedAt`, which `ResumeClipboard` writes
     under `d.mu`; keep both under the lock.
   - `ResumeClipboard` leaves `resumedAt` alone when not paused, so a
     stray resume doesn't open a new grace window.
   - `PauseClipboard` and `ResumeClipboard` return the `error` interface;
     return a literal `nil`, never a nil `*Error`.
   - `TestClipPauseMarker` and `TestFailedImageSave` chmod a folder; CI runs
     the tests as the user `builder` (`.github/workflows/build.yml:61-69`),
     not root, so the chmod holds.
   - `Reload` in `TestPauseSurvivesSettings` reads `config.toml` from the
     temporary `XDG_CONFIG_HOME` that `setSetting` just saved. If a helper
     that `Reload` calls needs a field the bare `clipDaemon` lacks, set it
     the way `browseSessionDaemon` (`sftp_test.go`) does; don't drop the
     `Reload` check.
   - `TestPauseDuringImageSend` pauses after a device's check; the hook is
     the documented gap of decision 2, and the cancelled context must stop
     the transfer. If the image still arrives, the cancellation is broken,
     not the test.

3. **IPC, state and CLI: `internal/core/api.go`, `cmd/flux/features.go`,
   `cmd/flux/main.go`, tests**
   - `internal/core/api.go`:
     - `Call`: after the `clipboard.search` case (`:348-349`), add:
       ```go
       	case "clipboard.pause":
       		return ok, d.PauseClipboard()
       	case "clipboard.resume":
       		return ok, d.ResumeClipboard()
       ```
       Not in `deviceMethods` (`:510-520`).
     - `Snapshot`: after `"clipboard": clip,` (`:178`), add
       `"clipboardPaused": d.clipPaused,` (the lock is held there,
       `:213`).
   - `cmd/flux/features.go`, `clipboardCommand` (`:44-68`): before
     `case "clear":` (`:49`), add:
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
     and the error at `:67` becomes
     `fmt.Errorf("use: flux-cli clipboard [clear [--all]|delete ID|limit [N]|pause|resume|status]")`.
     After `clipboardCommand`, add:
     ```go
     // clipboardStatus prints whether local copies go to the devices.
     func clipboardStatus() error {
     	var state struct {
     		ClipboardPaused bool `json:"clipboardPaused"`
     		Settings        struct {
     			AutoClipboard bool `json:"autoClipboard"`
     		} `json:"settings"`
     	}
     	if err := callInto("state", nil, &state); err != nil {
     		return err
     	}
     	fmt.Println(clipboardStatusWord(state.ClipboardPaused, state.Settings.AutoClipboard))
     	return nil
     }

     // clipboardStatusWord is "paused" while clipboard.pause holds, also with
     // automatic sync off, else "off" without automatic sync, else "syncing".
     func clipboardStatusWord(paused, auto bool) string {
     	switch {
     	case paused:
     		return "paused"
     	case !auto:
     		return "off"
     	}
     	return "syncing"
     }
     ```
   - `cmd/flux/main.go:62`: the usage line becomes two:
     ```
       clipboard [clear [--all]|delete ID|limit [N]]  Manage clipboard history and limit
       clipboard pause|resume|status  Stop or restart sending copies to devices, or show it
     ```
   - `cmd/flux/main_test.go`, after `TestClipboardCommandArgs` (`:322-335`):
     ```go
     func TestClipboardPauseArgs(t *testing.T) {
     	for _, args := range [][]string{{"pause", "5m"}, {"resume", "now"}, {"status", "x"}} {
     		if err := clipboardCommand(args); err == nil {
     			t.Errorf("clipboard %q should fail", args)
     		}
     	}
     }

     func TestClipboardStatusWord(t *testing.T) {
     	for _, c := range []struct {
     		paused, auto bool
     		want         string
     	}{{true, true, "paused"}, {true, false, "paused"}, {false, false, "off"}, {false, true, "syncing"}} {
     		if got := clipboardStatusWord(c.paused, c.auto); got != c.want {
     			t.Errorf("paused %v auto %v: %q, want %q", c.paused, c.auto, got, c.want)
     		}
     	}
     }
     ```
   - `internal/core/clipboard_test.go`, add:
     ```go
     // The state shows the pause outside the settings.
     func TestSnapshotClipboardPaused(t *testing.T) {
     	d := testDaemon()
     	for _, want := range []bool{true, false} {
     		if want {
     			_ = d.PauseClipboard()
     		} else {
     			_ = d.ResumeClipboard()
     		}
     		var s struct {
     			Paused   *bool          `json:"clipboardPaused"`
     			Settings map[string]any `json:"settings"`
     		}
     		if err := json.Unmarshal(d.Snapshot(), &s); err != nil {
     			t.Fatal(err)
     		}
     		if s.Paused == nil || *s.Paused != want || s.Settings["clipboardPaused"] != nil {
     			t.Fatalf("state %+v", s)
     		}
     	}
     }

     // The IPC methods take no parameters and act on the computer.
     func TestCallClipboardPause(t *testing.T) {
     	d := testDaemon()
     	if _, err := d.Call(context.Background(), "clipboard.pause", nil); err != nil || !d.clipPaused {
     		t.Fatal(err)
     	}
     	if _, err := d.Call(context.Background(), "clipboard.resume", nil); err != nil || d.clipPaused {
     		t.Fatal(err)
     	}
     }
     ```
     Add `encoding/json` to the imports. If `Snapshot` needs a field that
     `testDaemon` (`notifications_test.go:43-50`) lacks, use
     `approveDaemon()` as `approve_test.go:128` does. Check the `Call`
     signature (`grep -n "func (d \*Daemon) Call" internal/core/api.go`)
     and match its parameters.

   Verify:
   ```sh
   go test -race ./cmd/... ./internal/core/ && go vet ./cmd/... ./internal/...
   go run ./cmd/flux help 2>&1 | grep -n "clipboard"
   ```
   shows both usage lines.

   Traps:
   - `call(..., nil)` sends no params; `Call` skips the unmarshal for an
     empty `raw` (`api.go:262-268`), so `nil` in the test is fine.
   - The status word reads `state` once; don't add a new IPC method for
     it.
   - Plain `flux-cli clipboard` (no args, `:45-46`) stays the history.

4. **The indicator: `gui/qml/pages/Chat.qml`, `gui/qml/tools/MockBackend.qml`,
   `gui/qml/tools/fixture.json`, `gui/tests/tst_views.qml`,
   `gui/omarchy/BarWidget.qml`**
   - `gui/qml/pages/Chat.qml`:
     - after `readonly property bool compact: width < 560` (`:68`), add:
       ```qml
         // Clipboard sync is paused for every device (clipboard.pause).
         readonly property bool clipPaused: !!(view && view.backend && view.backend.state && view.backend.state.clipboardPaused)
       ```
     - after the toolbar `Item` (ends at `:243`), add:
       ```qml
         // A paused clipboard sync, with Resume. It shows on every device.
         Item {
           id: pausedRow
           objectName: "clipPaused"
           visible: root.clipPaused
           anchors.top: toolbar.bottom
           anchors.topMargin: 10
           width: parent.width
           height: Math.max(pausedText.implicitHeight, resumeButton.implicitHeight)

           Txt {
             id: pausedText
             objectName: "clipPausedText"
             anchors.left: parent.left
             anchors.right: resumeButton.left
             anchors.rightMargin: 12
             anchors.verticalCenter: parent.verticalCenter
             text: "Clipboard sync paused. Copies on this computer stay here."
             color: Theme.dim
             font.pixelSize: 12
             wrapMode: Text.Wrap
           }
           OutlineButton {
             id: resumeButton
             objectName: "clipResume"
             anchors.right: parent.right
             anchors.verticalCenter: parent.verticalCenter
             text: "Resume"
             fontSize: 12
             padY: 6
             onClicked: {
               var v = root.view
               v.call("clipboard.resume", {}, function () { v.toast("Clipboard sync resumed") })
             }
           }
         }
       ```
     - the list's `anchors.top: toolbar.bottom` (`:257`) becomes
       `anchors.top: pausedRow.visible ? pausedRow.bottom : toolbar.bottom`.
   - `gui/qml/tools/MockBackend.qml`: after the `clipboard.clear` branch
     (`:91`), add:
     ```qml
         else if (method === "clipboard.pause" || method === "clipboard.resume") setState(function (s) { s.clipboardPaused = method === "clipboard.pause" })
     ```
   - `gui/qml/tools/fixture.json`: before `"settings": {` (`:319`) in
     `state`, add `  "clipboardPaused": false,` at the same indent.
   - `gui/tests/tst_views.qml`, in the `"Chat"` `TestCase` (`:1205-`),
     add:
     ```qml
         // A pause of clipboard sync shows on every device's chat, above the
         // list, and Resume ends it.
         function test_clipboardPausedLine() {
           mock.setState(function (s) { s.clipboardPaused = true })
           var view = chat()
           var row = findBy(page(view), "objectName", "clipPaused")
           verify(row.visible)
           var list = findBy(page(view), "objectName", "chatList")
           verify(list.y >= row.y + row.height)
           var text = findBy(page(view), "objectName", "clipPausedText")
           var button = findBy(page(view), "objectName", "clipResume")
           verify(text.x + text.width <= button.x)
           view.selectedId = top.iphone
           tryVerify(function () { var r = findBy(page(view), "objectName", "clipPaused"); return !!r && r.visible })
           findBy(page(view), "objectName", "clipResume").clicked()
           compare(requestsOf("clipboard.resume").length, 1)
           tryVerify(function () { return !findBy(page(view), "objectName", "clipPaused").visible })
         }

         function test_noPausedLineWhileSyncing() {
           var view = chat()
           verify(!findBy(page(view), "objectName", "clipPaused").visible)
         }

         // At a compact width the text wraps beside the button.
         function test_clipboardPausedLineCompact() {
           mock.setState(function (s) { s.clipboardPaused = true })
           var view = chat()
           page(view).width = 420
           tryVerify(function () { return page(view).compact })
           var text = findBy(page(view), "objectName", "clipPausedText")
           var button = findBy(page(view), "objectName", "clipResume")
           verify(text.lineCount > 1)
           verify(text.x + text.width <= button.x)
         }
       ```
   - `gui/omarchy/BarWidget.qml`:
     - the header comment (`:7-11`) adds "The mark dims while clipboard
       sync is paused, and the tooltip says so."
     - after `readonly property bool linked` (`:35`), add:
       ```qml
         readonly property bool clipPaused: up && !!backend.state && backend.state.clipboardPaused === true
       ```
     - the tooltip (`:37-51`) becomes:
       ```qml
         readonly property string tooltip: {
           if (!backend) return "Flux"
           if (!up) return "Flux · fluxd is not running"
           var paused = root.clipPaused ? "\nClipboard sync paused" : ""
           if (paired.length === 0) return "Flux · no paired devices" + paused
           var lines = []
           // The connected device comes first.
           var sorted = paired.slice().sort(function (a, b) {
             if (a === root.primary) return -1
             if (b === root.primary) return 1
             return (b.online ? 1 : 0) - (a.online ? 1 : 0)
           })
           for (var i = 0; i < sorted.length; i++)
             lines.push(sorted[i].name + " · " + (sorted[i].online ? "connected" : "offline"))
           return lines.join("\n") + paused
         }
       ```
     - the `FluxUi.FluxMark` (`:74-80`) adds `opacity: root.clipPaused ? 0.5 : 1`.

   Verify:
   ```sh
   Q=$(nix build --no-link --print-out-paths nixpkgs#qt6.qtdeclarative); nix shell nixpkgs#qt6.qtbase nixpkgs#qt6.qtdeclarative nixpkgs#qt6.qtsvg -c env QT_QPA_PLATFORM=offscreen QML_XHR_ALLOW_FILE_READ=1 $Q/bin/qmltestrunner -input gui/tests
   ```
   all pass, 0 failed (`make test-gui` uses `/usr/lib/qt6/bin/qmltestrunner`,
   which isn't on this machine). The bar mark is checked by eye with
   `gui/omarchy/tools/test-offscreen.sh` only if it runs here; it needs a
   Quickshell setup and is optional.

   Traps:
   - `gui/qml/` stays host-neutral: both hosts expose `backend.state`
     (`gui/app/src/fluxbackend.h:19`, `gui/omarchy/Backend.qml:46`); no
     host code changes.
   - The row's `visible: false` keeps its height, so the list anchor must
     switch on `pausedRow.visible`, as written; don't anchor the list to
     `pausedRow.bottom` alone.
   - `Txt`, `OutlineButton` and `Theme` are already used in `Chat.qml`.
   - The mock's `call` passes `cb(null, result)` via `Qt.callLater`
     (`MockBackend.qml:142`), so the toast and the state change come a
     tick later; the tests use `tryVerify`.
   - If `page(view).width = 420` is overridden by the page's anchors,
     create the view narrower instead
     (`createTemporaryObject(viewComponent, top, { width: 700 })`) until
     `compact` is true; the check is that the text wraps and doesn't
     overlap the button.

5. **Docs: `docs/ipc.md`, `docs/features.md`, `docs/security.md`,
   `docs/omarchy.md`, `docs/cli.md`**
   - `docs/ipc.md`:
     - `:60`, the field list: add `clipboardPaused` after `clipboard`; then
       a new line after `:60`:
       ```markdown
       `clipboardPaused` is true while `clipboard.pause` holds. It survives a restart of fluxd, not a reboot.
       ```
     - `:183`, the "Sharing" row: add `clipboard.pause`, `clipboard.resume`
       after `clipboard.copy`.
     - the parameters table (`:207-239`), after the `clipboard.copy` row:
       ```markdown
       | `clipboard.pause`, `clipboard.resume` | None |
       ```
     - the errors table, the `not_saved` row (`:314`) becomes:
       ```markdown
       | `not_saved` | `pair.unpair` could not save `devices.json`; the device is unpaired only until fluxd restarts. Or `clipboard.pause` or `clipboard.resume` could not write or remove the pause marker; the call took effect, but a restart of fluxd brings back the state before it. |
       ```
     - the Clipboard section (`:389-`), before `## Text messages`, add:
       ```markdown
       `clipboard.pause` stops the automatic sync of copies on this computer until `clipboard.resume`.
       A copy that fluxd handles while paused goes to no device, stays out of the history, and is not sent to a device that connects later.
       A pause also clears the last copy that a device would get when it connects.
       It stops an automatic image send at once, and a text send before its next device.
       A send that passed its check just before the pause can still go out; it holds a copy from before the pause.
       A copy made less than 250 ms before a resume, or in the 250 ms after it, counts as paused.
       Manual `clipboard.send` still works while paused, and clips from devices still arrive.
       Both methods are idempotent, and the last call wins.
       ```
   - `docs/features.md`, in "Files, clipboard, and links" (`:126`), after
     its first paragraph (`:128`), add:
     ```markdown
     ### Pause clipboard sync

     `flux-cli clipboard pause` stops sending what you copy on this computer to your devices, until `flux-cli clipboard resume`.
     `flux-cli clipboard status` prints `paused`, `syncing`, or `off` when `auto_clipboard` is off.
     While paused, a copy stays on this computer: no device gets it, the history doesn't keep it, and a device that connects later doesn't get it.
     Sending the clipboard by hand (`flux-cli clip`, Send clipboard) still works, and clips from your devices still arrive.
     The pause doesn't change `config.toml`. It survives a restart of fluxd, and ends at reboot.
     A copy made less than 250 ms before or after a resume isn't synced; copy again.
     The Chat page shows "Clipboard sync paused" with Resume, and the bar mark dims.

     A clipboard manager's private mode can run `flux-cli clipboard pause` when it starts and `flux-cli clipboard resume` when it ends.
     ```
   - `docs/security.md:65`, the "Clipboard sync" row, the last cell
     becomes:
     ```markdown
     `auto_clipboard = false`. To stop only outgoing copies for a while: `flux-cli clipboard pause` (manual sends and incoming clips still work).
     ```
   - `docs/omarchy.md:10`, the `bar-widget` row: after "is connected." add
     "The mark dims while clipboard sync is paused, and the tooltip says
     so."
   - `docs/cli.md`, after `:292` (`clipboard limit`), add (P4):
     ```markdown
     `flux-cli clipboard pause` and `resume` stop and restart the automatic sync of copies to your devices; `status` prints `paused`, `syncing`, or `off`. See [pause clipboard sync](features.md#pause-clipboard-sync).
     ```

   Verify:
   ```sh
   grep -rn "clipboard.pause\|clipboard pause\|clipboardPaused" docs | wc -l
   ```
   at least 8 lines, and the anchor `features.md#pause-clipboard-sync`
   matches the new heading.

   Traps:
   - Line numbers in `docs/ipc.md` shift by 1 after the first edit; find
     each place by its text.
   - Say "already checked", never "already sent" or "handed to the link"
     (decision 2).

6. **Local checks (the lead)**
   - `make vet test` (gofmt, `go vet`, `go test -race`).
   - The QML command of step 4.
   - `nix flake check path:.`.

7. **CI (the lead).**
   - Push, after an agent-bus announcement.
   - `gh workflow run build.yml -R olafkfreund/nixarchy-flux --ref feat/16-pause-clipboard`.
   - Poll every 5 minutes. The Arch job must pass `make vet test` and the
     package build. The Android, macOS and iOS jobs build unchanged code;
     a failure there that also fails on `master` is not this change's.

8. **Device test (the lead and the owner). Optional, only with the owner's
   OK.** The Android app doesn't change, so no APK is installed. p620 runs
   the real `fluxd.service` (Home Manager user unit,
   `ExecStart=/etc/profiles/per-user/olafkfreund/bin/fluxd`); the test runs
   the branch's fluxd in its place and puts the unit back after.

   **Setup**
   1. Announce on the agent bus that fluxd on p620 stops for a test.
   2. Build: `s=<scratchpad>/i16; go build -o $s/fluxd ./cmd/fluxd && go build -o $s/flux-cli ./cmd/flux`.
   3. `systemctl --user stop fluxd`, then
      `systemd-run --user --unit fluxd-i16 $s/fluxd`. It uses the same
      config, keys and pairing. Use `$s/flux-cli` for every command; the
      one on `PATH` doesn't know `pause`.
   4. Read the serials with `adb devices`; wait until
      `$s/flux-cli devices` shows the Pixel 11 Pro XL connected. Clipboard
      sync is on, on the phone and on p620.

   **Then**

   | # | Check | How |
   | --- | --- | --- |
   | 1 | Paused copies stay here | `$s/flux-cli clipboard pause`; `wl-copy test-private-1`; copy an image (`wl-copy -t image/png < some.png`); toggle Wi-Fi on the phone so it reconnects; `$s/flux-cli clipboard resume`; `wl-copy test-public-1`. Only `test-public-1` reaches the phone; `$s/flux-cli clipboard` shows neither paused copy |
   | 2 | The resume race | `$s/flux-cli clipboard pause; wl-copy test-private-2; $s/flux-cli clipboard resume` in one line: `test-private-2` never arrives |
   | 3 | Restart keeps it | `$s/flux-cli clipboard pause`; `systemctl --user stop fluxd-i16`; run the `systemd-run` line again; `$s/flux-cli clipboard status` prints `paused`; `$s/flux-cli clipboard resume` |
   | 4 | Manual send while paused | pause; `$s/flux-cli clip test-manual-1` arrives; resume |
   | 5 | Indicator | with the window open, pause: the Chat line shows on two devices' pages, the bar mark dims, its tooltip ends with "Clipboard sync paused"; Resume in the window ends it |

   **Afterwards**
   - `$s/flux-cli clipboard resume`, then check
     `$XDG_RUNTIME_DIR/flux/clipboard-paused` is gone.
   - `systemctl --user stop fluxd-i16 && systemctl --user start fluxd`;
     `systemctl --user is-active fluxd` prints `active`.
   - With the owner's OK, delete the `test-*` clips from the history
     (`flux-cli clipboard delete <id>`) and clear the phone's clipboard.
   - Record the results in the PR.

9. **PR (the lead).** Open the PR linking the intent, spec and plan, with
   the local, CI and device results, and which steps each implementer did.

## Tests

- `make vet test`, with:
  - `internal/desktop`: `TestClipboardWatch` (records carry a boot time),
    `TestClipboardRecordTime`;
  - `internal/core`: `TestPausedText`, `TestPausedImage`,
    `TestLateRecordsAfterResume`, `TestConnectAfterPause`,
    `TestPauseAfterImageAdmission`, `TestPauseBetweenTextLinks`,
    `TestPauseDuringImageSend`, `TestOldTextKeepsNewerImage`,
    `TestFailedImageSave`, `TestManualSendWhilePaused`,
    `TestClipSendKinds`, `TestPauseSurvivesSettings`,
    `TestClipPauseMarker`, `TestClipPauseRace`, `TestClipPauseHeadless`,
    `TestSnapshotClipboardPaused`, `TestCallClipboardPause`, and the
    existing clipboard tests with their new time argument;
  - `cmd/flux`: `TestClipboardPauseArgs`, `TestClipboardStatusWord`, and
    the existing `TestClipboardCommandArgs`.
- QML: `test_clipboardPausedLine`, `test_noPausedLineWhileSyncing`,
  `test_clipboardPausedLineCompact`, and every existing view test.
- `nix flake check path:.`; CI `build.yml`.
- Optional: the device checks of step 8.

Not covered by an automatic test: the bar widget (Quickshell), a watcher
script that starts more than 250 ms late, and `New` reading the marker
(the tests call `loadClipPause`, because `New` touches the real config,
downloads and D-Bus).

## Rollback

- Revert the PR's squash commit and rebuild fluxd, flux-cli and the GUI.
  The changed signatures (`Watch`, `onLocalClipboard`, `onLocalImage`,
  `newClipSend`, `record`) and their callers revert together.
- A marker file left in `$XDG_RUNTIME_DIR/flux/clipboard-paused` is ignored
  by the old fluxd and goes at reboot; remove it by hand to tidy up.
- No `config.toml` or protocol change, so nothing on the phones or in the
  config needs undoing.

## Corrections

| # | Step | What changed and why |
| --- | --- | --- |
| P1 | 1 | Also updates the existing callers in `internal/desktop/desktop_test.go` the step missed (the `record` helper, the callbacks in four record tests, `TestClipboardWatch`, `TestWatchScriptCapsSize`), so the package compiles; adds `TestClipboardRecordTime`. Found by the Codex and agy plan reviews. |
