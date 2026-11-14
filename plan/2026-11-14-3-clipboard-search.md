---
status: approved
issue: 3
spec: spec/2026-11-14-3-clipboard-search.md
coders:
  2: coder   # the packet type, the fluxd handler, three pure helpers and their tests, all given in full below
  3: coder   # one condition in addClipLocked and one test, given in full
  4: coder   # the phone's protocol and state: ClipHistory.kt and its test given in full, plus small field and dispatch edits
  5: coder   # the History screen and four small UI edits, given in full; codex and agy then review the Compose code
  6: coder   # two short doc paragraphs, given in full
reviewers:
  - codex    # reads the branch diff with `codex review --base origin/master`; strongest on the request matching, the lock and the wire filter
  - agy      # independent read of the diff against this plan, including the Compose states and TalkBack labels
---

# Plan: Search the recent clipboard sends on the phone

## The approved decisions

- **What the phone sees.** Only its own entries of the computer's
  clipboard history: `Device` is the phone, or `To` holds it. No images,
  no pinned entries (nor the history original of a pinned entry), no file
  transfers, no other device's entries. Accepted gaps: a desktop copy the
  phone got on reconnect isn't listed; `To` means "meant for", not
  "received"; a phone paired again under the same id sees its old entries.
- **The packet.** One new type, `flux.clipboard.history`, that both sides
  list in both directions. The phone sends `{request, query}` or
  `{request, entry}`. `fluxd` answers with the same `request` and a
  `state`: `ok`, `disabled`, `not_found`, `too_large` or `bad_request`. A
  wire entry has only `id`, `text`, `sent` (the phone's view), `time` (Unix
  seconds), and `size` and `truncated` when cut.
- **Caps.** The 50 newest matching entries, filtered first and then cut
  to 50, with `more` when more matched. Each text cut at 1 KiB
  (`maxClipPreview`), valid UTF-8. One entry's full text on request, up to
  256 KiB (`maxSentText`); above that `too_large`. A query of at most
  1 KiB; above that `bad_request`.
- **Search** runs in `fluxd`, over the text only (not the device name),
  lower case, as a substring. An empty query lists all.
- **The gate** is the device's `clipboard` switch: off answers `disabled`
  with no entries. `auto_clipboard` doesn't matter.
- **The handler** answers only a peer that lists the type, runs on the
  link's read loop, clones the history under `d.mu`, and matches, encodes
  and sends without the lock. The fetch by id runs the same filter as the
  list. It never calls `CopyClip`.
- **The pause of #16** needs no check: a paused copy never enters the
  history. History still answers while paused (owner decision 1).
- **Duplicates** merge only within one device: `top.Device == e.Device`
  in `addClipLocked`.
- **The phone.**
  - Every answer is checked by device and request id. A list answer is
    stored only when its id is the device's current list request. One copy
    slot: a new copy replaces it; the answer is taken on the main thread
    with a compare-and-set, so a `forget` or newer copy wins.
  - Copy goes through `Plugins.putRemoteText`, so clipboard sync doesn't
    send it back; the toast "Copied to this phone" only when it returns
    true.
  - "History" in the chat's overflow menu, when the computer lists the
    type. The screen: offline, update Flux, disabled, searching, no
    answer (10 s, Retry), could not read (Retry), nothing found, the list.
    The search field stays mounted while the area under it changes, and
    refuses input past 1 KiB of UTF-8.
  - A row: text up to 3 lines, "Sent · time" or "Received · time". Tap
    copies; a cut entry fetches its full text first; an entry over 256 KiB
    isn't clickable and says "too large to copy". Open only on an uncut
    http or https link (owner decision 2); a cut link says to copy it.
- **Unchanged:** `searchClips`, `clipboard.search` and the IPC API, the
  desktop GUI (apart from the duplicate fix), what `fluxd` keeps, iOS,
  macOS, Windows, the chat log and Clear chat.

## Steps

Step 1 is the lead's. Steps 2-6 run in order on the branch, and the lead
commits each one before the next starts. There is no local Android SDK,
so CI in step 7 is the first Android compile; Go tests run locally.

`internal/core/clipboard.go` and `internal/core/clipboard_test.go` are
cited from `feat/16-pause-clipboard` (read with
`git show feat/16-pause-clipboard:<path>`), which becomes the base in
step 1, and so are the `docs/features.md` lines. #16's head can still
gain small fixes, so each place is also named by its function or
heading; find it by name if a line moved. Everything else is cited from
`master`, which #16 doesn't touch for these files.

Android paths are under `android/app/src/main/java/org/omarchy/flux/`
unless they start with `android/app/src/test/`.

1. **Rebase on master after #16 merges (the lead).**
   - Wait until #16's PR is merged on `master`.
   - `git fetch origin master:master && git rebase master` on
     `feat/3-clipboard-search` (local `master` moves too, so a later
     `--base master` diff holds only #3). Only `intent/`, `spec/` and `plan/` files
     are on the branch, so it can't conflict.
   - Check the base: `git grep -n 'func (d \*Daemon) copyPausedLocked'
     internal/core/clipboard.go` finds it, and `git grep -n 'top.Text ==
     e.Text' internal/core/clipboard.go` finds `addClipLocked`'s merge line.
   - If #16 moved a cited line, record it in Corrections; no step changes.
   - Verify: `go build ./... && go test ./internal/core/...` on the
     rebased branch. In the agent sandbox some session and speaker tests
     time out and `TestKeyboardReleasesMods` fails (no `/usr/bin/cat`);
     they fail the same way on the #16 branch alone, so compare against a
     run on `master` rather than expecting all green.
   - Traps: never push with `--force` without an agent-bus note; the
     branch is the lead's.

2. **fluxd: the packet and the handler.** Files: `internal/proto/history.go`
   (new), `internal/proto/proto_test.go`, `internal/core/handlers.go`,
   `internal/core/history.go` (new), `internal/core/history_test.go` (new).
   - `internal/proto/history.go`, like `proto/speaker.go`:
     ```go
     package proto

     // TypeFluxClipboardHistory lets a phone read and search its own entries
     // of the clipboard history of this computer. The phone asks, fluxd
     // answers. docs/features.md describes it.
     const TypeFluxClipboardHistory = "flux.clipboard.history"

     func init() {
     	Incoming = append(Incoming, TypeFluxClipboardHistory)
     	Outgoing = append(Outgoing, TypeFluxClipboardHistory)
     }
     ```
   - `internal/proto/proto_test.go`, after `TestMediaGoesOneWay` (`:212-221`):
     ```go
     func TestClipboardHistoryGoesBothWays(t *testing.T) {
     	// The phone asks with the type, and fluxd answers with it.
     	if !slices.Contains(Incoming, TypeFluxClipboardHistory) || !slices.Contains(Outgoing, TypeFluxClipboardHistory) {
     		t.Error("the computer must take and send flux.clipboard.history")
     	}
     }
     ```
   - `internal/core/handlers.go`, in `handlePacket`'s `switch p.Type`,
     after the `proto.TypeFluxShortcuts` case (`:128-129`):
     ```go
     case proto.TypeFluxClipboardHistory:
     	d.handleClipHistory(dev, l, p)
     ```
   - `internal/core/history.go`:
     ```go
     package core

     import (
     	"slices"
     	"strings"
     	"time"

     	"flux/internal/proto"
     )

     const (
     	// maxHistoryAnswer is the number of entries in one answer.
     	maxHistoryAnswer = 50
     	// maxHistoryQuery is the longest query in bytes.
     	maxHistoryQuery = 1 << 10
     )

     // historyRequest is the body of a flux.clipboard.history packet from a
     // phone: a query or the id of one entry, never both.
     type historyRequest struct {
     	Request int64   `json:"request"`
     	Query   *string `json:"query"`
     	Entry   *string `json:"entry"`
     }

     // historyEntry is one entry on the wire. It has no image path, no
     // device, and no recipients.
     type historyEntry struct {
     	ID        string `json:"id"`
     	Text      string `json:"text"`
     	Sent      bool   `json:"sent"`
     	Time      int64  `json:"time"`
     	Size      int    `json:"size,omitempty"`
     	Truncated bool   `json:"truncated,omitempty"`
     }

     // handleClipHistory answers a phone's request for its own entries of
     // the clipboard history. It runs on the read loop of the link, so a
     // slow answer only delays this phone.
     func (d *Daemon) handleClipHistory(dev *Device, l streamLink, p *proto.Packet) {
     	var b historyRequest
     	if p.Decode(&b) != nil {
     		return
     	}
     	answer := func(body map[string]any) {
     		body["request"] = b.Request
     		_ = l.Send(proto.New(proto.TypeFluxClipboardHistory, body))
     	}
     	bad := (b.Query == nil) == (b.Entry == nil) || (b.Query != nil && len(*b.Query) > maxHistoryQuery)
     	d.mu.Lock()
     	if !dev.accepts(proto.TypeFluxClipboardHistory) {
     		d.mu.Unlock()
     		return // fluxd never sends the type to a peer that doesn't list it
     	}
     	if !d.permittedLocked(dev.ID, "clipboard") {
     		d.mu.Unlock()
     		answer(map[string]any{"state": "disabled"})
     		return
     	}
     	if bad {
     		d.mu.Unlock()
     		answer(map[string]any{"state": "bad_request"})
     		return
     	}
     	// The strings are shared, not copied. addClipLocked grows To into a
     	// new array, so the clone's To stays valid after the unlock.
     	clips := slices.Clone(d.clipboard)
     	pinned := make(map[string]bool, len(d.snippets))
     	now := time.Now().Unix()
     	for _, s := range d.snippets {
     		// An expired snippet counts for nothing, as in clipEntriesLocked.
     		if s.Expires == 0 || s.Expires > now {
     			pinned[s.ID] = true
     		}
     	}
     	d.mu.Unlock()
     	mine := ownClips(clips, pinned, dev.ID)
     	if b.Entry != nil {
     		answer(historyFull(mine, *b.Entry))
     		return
     	}
     	list, more := historyMatches(mine, *b.Query)
     	answer(map[string]any{"state": "ok", "entries": list, "more": more})
     }

     // ownClips returns the text entries of device: those it sent, and those
     // sent to it. Images and pinned entries stay out.
     func ownClips(clips []ClipEntry, pinned map[string]bool, device string) []ClipEntry {
     	var out []ClipEntry
     	for _, e := range clips {
     		if e.Image == "" && !pinned[e.ID] && (e.Device == device || slices.Contains(e.To, device)) {
     			out = append(out, e)
     		}
     	}
     	return out
     }

     // historyMatches returns the newest maxHistoryAnswer entries whose text
     // holds query, lower case, as searchClips matches, each cut at
     // maxClipPreview bytes. more is true when more entries matched.
     func historyMatches(mine []ClipEntry, query string) (list []historyEntry, more bool) {
     	q := strings.ToLower(query)
     	list = []historyEntry{}
     	for _, e := range mine {
     		if q != "" && !strings.Contains(strings.ToLower(e.Text), q) {
     			continue
     		}
     		if len(list) == maxHistoryAnswer {
     			return list, true
     		}
     		h := historyEntry{ID: e.ID, Text: e.Text, Sent: e.Dir == "in", Time: e.Time}
     		if len(h.Text) > maxClipPreview {
     			h.Size, h.Truncated = len(h.Text), true
     			h.Text = strings.ToValidUTF8(h.Text[:maxClipPreview], "")
     		}
     		list = append(list, h)
     	}
     	return list, false
     }

     // historyFull returns the answer body for the full text of entry id.
     func historyFull(mine []ClipEntry, id string) map[string]any {
     	for _, e := range mine {
     		if e.ID != id {
     			continue
     		}
     		if len(e.Text) > maxSentText {
     			return map[string]any{"state": "too_large"}
     		}
     		return map[string]any{"state": "ok", "entry": historyEntry{ID: e.ID, Text: e.Text, Sent: e.Dir == "in", Time: e.Time}}
     	}
     	return map[string]any{"state": "not_found"}
     }
     ```
   - `internal/core/history_test.go`:
     ```go
     package core

     import (
     	"encoding/json"
     	"fmt"
     	"slices"
     	"strings"
     	"testing"
     	"time"
     	"unicode/utf8"

     	"flux/internal/proto"
     )

     // historyClips is a history with the entries of a phone, a tablet and
     // the desktop, newest first. a6 is pinned in the tests that pass it.
     func historyClips() []ClipEntry {
     	return []ClipEntry{
     		{ID: "a1", Text: "git status", Dir: "in", Device: "phone", DeviceName: "Pixel", Time: 30},
     		{ID: "a2", Text: "from the desk", Dir: "out", DeviceName: "this pc", To: []string{"phone", "tab"}, Time: 29},
     		{ID: "a3", Text: "tablet text", Dir: "in", Device: "tab", DeviceName: "Tab", Time: 28},
     		{ID: "a4", Text: "desk only", Dir: "out", DeviceName: "this pc", Time: 27},
     		{ID: "a5", Dir: "in", Device: "phone", DeviceName: "Pixel", Image: "/run/clip-x.png", Time: 26},
     		{ID: "a6", Text: "pinned one", Dir: "out", Device: "phone", DeviceName: "this pc", Time: 25},
     	}
     }

     func historyIDs(list []historyEntry) []string {
     	var ids []string
     	for _, h := range list {
     		ids = append(ids, h.ID)
     	}
     	return ids
     }

     func TestOwnClips(t *testing.T) {
     	got := ownClips(historyClips(), map[string]bool{"a6": true}, "phone")
     	var ids []string
     	for _, e := range got {
     		ids = append(ids, e.ID)
     	}
     	// Not the tablet's, not a desktop copy without the phone, not an
     	// image, not a pinned entry.
     	if !slices.Equal(ids, []string{"a1", "a2"}) {
     		t.Fatalf("ids %q", ids)
     	}
     }

     func TestHistoryMatches(t *testing.T) {
     	mine := ownClips(historyClips(), nil, "phone")
     	if got, _ := historyMatches(mine, "GIT"); !slices.Equal(historyIDs(got), []string{"a1"}) || !got[0].Sent {
     		t.Fatalf("GIT: %+v", got)
     	}
     	// The device name is not searched: every received entry is "this pc".
     	if got, _ := historyMatches(mine, "pc"); len(got) != 0 {
     		t.Fatalf("pc: %+v", got)
     	}
     	if got, more := historyMatches(mine, ""); !slices.Equal(historyIDs(got), []string{"a1", "a2", "a6"}) || got[1].Sent || more {
     		t.Fatalf("all: %+v more %v", got, more)
     	}

     	// Filter first, then the cap.
     	many := []ClipEntry{{ID: "other", Text: "x", Dir: "in", Device: "tab"}}
     	for i := range maxHistoryAnswer + 5 {
     		many = append(many, ClipEntry{ID: fmt.Sprint(i), Text: fmt.Sprint("x", i), Dir: "in", Device: "phone"})
     	}
     	got, more := historyMatches(ownClips(many, nil, "phone"), "x")
     	if len(got) != maxHistoryAnswer || !more || got[0].ID != "0" {
     		t.Fatalf("%d entries, more %v, first %q", len(got), more, got[0].ID)
     	}
     	if _, more := historyMatches(ownClips(many[:11], nil, "phone"), "x"); more {
     		t.Fatal("10 entries can't have more")
     	}

     	// A long text comes cut, as valid UTF-8, with its full size.
     	long := "a" + strings.Repeat("é", maxClipPreview)
     	got, _ = historyMatches([]ClipEntry{{ID: "l", Text: long, Dir: "in", Device: "phone"}}, "")
     	h := got[0]
     	if !h.Truncated || h.Size != len(long) || len(h.Text) > maxClipPreview || !utf8.ValidString(h.Text) {
     		t.Fatalf("cut entry: truncated %v size %d text %d bytes", h.Truncated, h.Size, len(h.Text))
     	}
     }

     func TestHistoryFull(t *testing.T) {
     	mine := ownClips(historyClips(), map[string]bool{"a6": true}, "phone")
     	if b := historyFull(mine, "a1"); b["state"] != "ok" || b["entry"].(historyEntry).Text != "git status" {
     		t.Fatalf("a1: %v", b)
     	}
     	// Another device's, an image, a pinned entry and an unknown id look the same.
     	for _, id := range []string{"a3", "a5", "a6", "nope"} {
     		if b := historyFull(mine, id); b["state"] != "not_found" {
     			t.Fatalf("%s: %v", id, b)
     		}
     	}
     	at := []ClipEntry{{ID: "b", Text: strings.Repeat("a", maxSentText), Dir: "in", Device: "phone"}}
     	if b := historyFull(at, "b"); b["state"] != "ok" {
     		t.Fatalf("256 KiB: %v", b["state"])
     	}
     	at[0].Text += "a"
     	if b := historyFull(at, "b"); b["state"] != "too_large" {
     		t.Fatalf("over 256 KiB: %v", b["state"])
     	}
     }

     func TestClipHistoryHandler(t *testing.T) {
     	d, _ := clipDaemon(t, true)
     	d.clipboard = historyClips()
     	// a6 is pinned; a1's pin has expired, so a1 shows.
     	d.snippets = []ClipEntry{{ID: "a6", Text: "pinned one", Pinned: true}, {ID: "a1", Text: "git status", Pinned: true, Expires: 1}}
     	dev := &Device{ID: "phone", Name: "Pixel", Paired: true, Incoming: []string{proto.TypeFluxClipboardHistory}}
     	l := newFakeStreamLink()
     	sent := func() []map[string]any {
     		l.mu.Lock()
     		defer l.mu.Unlock()
     		return slices.Clone(l.sent)
     	}
     	ask := func(body map[string]any) map[string]any {
     		t.Helper()
     		n := len(sent())
     		d.handleClipHistory(dev, l, proto.New(proto.TypeFluxClipboardHistory, body))
     		all := sent()
     		if len(all) != n+1 {
     			t.Fatalf("%d answers to %v", len(all)-n, body)
     		}
     		b := all[n]
     		if b["request"] != body["request"] {
     			t.Fatalf("answer %v doesn't name request %v", b, body["request"])
     		}
     		return b
     	}

     	b := ask(map[string]any{"request": 7.0, "query": ""})
     	if b["state"] != "ok" || len(b["entries"].([]any)) != 2 || b["more"] != false {
     		t.Fatalf("list: %v", b)
     	}
     	raw, _ := json.Marshal(b)
     	// No desktop fields, and no size or truncated on uncut entries.
     	for _, key := range []string{`"image"`, `"to"`, `"device"`, `"deviceName"`, `"pinned"`, `"source"`, `"size"`, `"truncated"`} {
     		if strings.Contains(string(raw), key) {
     			t.Fatalf("the answer has %s: %s", key, raw)
     		}
     	}
     	if b := ask(map[string]any{"request": 8.0, "entry": "a1"}); b["state"] != "ok" {
     		t.Fatalf("entry: %v", b)
     	}
     	for _, body := range []map[string]any{
     		{"request": 9.0},
     		{"request": 9.0, "query": "a", "entry": "a1"},
     		{"request": 9.0, "query": strings.Repeat("a", maxHistoryQuery+1)},
     	} {
     		if b := ask(body); b["state"] != "bad_request" {
     			t.Fatalf("%v: %v", body, b)
     		}
     	}

     	d.cfg.Devices = map[string]map[string]bool{"phone": {"clipboard": false}}
     	if b := ask(map[string]any{"request": 10.0, "query": ""}); b["state"] != "disabled" || b["entries"] != nil {
     		t.Fatalf("disabled: %v", b)
     	}
     	d.cfg.Devices = nil

     	// A peer that doesn't list the type gets nothing.
     	dev.Incoming = nil
     	n := len(sent())
     	d.handleClipHistory(dev, l, proto.New(proto.TypeFluxClipboardHistory, map[string]any{"request": 11.0, "query": ""}))
     	if len(sent()) != n {
     		t.Fatal("answered a peer without the capability")
     	}
     }

     // nextHistory returns the body of the next flux.clipboard.history on ch,
     // or nil for the ping marker.
     func nextHistory(t *testing.T, ch chan *proto.Packet) map[string]any {
     	t.Helper()
     	select {
     	case p := <-ch:
     		switch p.Type {
     		case proto.TypeFluxClipboardHistory:
     			return p.Fields()
     		case proto.TypePing:
     			return nil
     		}
     		t.Fatalf("unexpected packet %s", p.Type)
     	case <-time.After(5 * time.Second):
     		t.Fatal("no packet within 5 seconds")
     	}
     	return nil
     }

     // TestClipHistoryRouting checks that handlePacket answers a paired
     // device on its link, and nothing from an unpaired device or an old link.
     func TestClipHistoryRouting(t *testing.T) {
     	d, dev, onDesk, ch := requestDaemon(t)
     	dev.Incoming = append(dev.Incoming, proto.TypeFluxClipboardHistory)
     	d.clipboard = []ClipEntry{{ID: "a1", Text: "hi", Dir: "in", Device: dev.ID}}
     	ask := func(n int) {
     		d.handlePacket(dev, onDesk, proto.New(proto.TypeFluxClipboardHistory, map[string]any{"request": n, "query": ""}))
     	}
     	ask(1)
     	if b := nextHistory(t, ch); b == nil || b["request"] != 1.0 || b["state"] != "ok" {
     		t.Fatalf("paired: %v", b)
     	}
     	dev.Paired = false
     	ask(2)
     	dev.Paired, dev.link = true, nil // onDesk is no longer the device's link
     	ask(3)
     	if err := onDesk.Send(proto.New(proto.TypePing, nil)); err != nil {
     		t.Fatal(err)
     	}
     	if b := nextHistory(t, ch); b != nil {
     		t.Fatalf("answered %v", b)
     	}
     }
     ```
   - Verify: `go vet ./internal/... && go test ./internal/proto/ ./internal/core/ -run 'ClipboardHistory|OwnClips|HistoryMatches|HistoryFull|ClipHistory' -race`, then `go test ./...`.
   - Traps:
     - `l.sent` holds `p.Fields()`, so JSON numbers are `float64`: the
       tests send `7.0` and compare `b["request"] != body["request"]`.
       `TestClipHistoryRouting` sends an `int` and compares with `1.0`
       after the round trip, which is right.
     - `historyMatches` must start with `[]historyEntry{}`, not `nil`, so
       an empty answer is `"entries": []`.
     - `handlePacket` takes `*lan.Link`; `handleClipHistory` takes
       `streamLink` (`core/stream.go:28-32`), which `*lan.Link` meets, as
       `handleShortcuts` does.
     - `requestDaemon` (`core/streamrequest_test.go:18-33`) gives its
       device `Incoming` with only `TypeFluxStreamRequest`; append, don't
       replace, in case that test helper grows.
     - Don't change `searchClips` or `clipboard.search`.

3. **fluxd: duplicates per device.** Files: `internal/core/clipboard.go`,
   `internal/core/history_test.go`.
   - In `addClipLocked` (`core/clipboard.go:74-118` on
     `feat/16-pause-clipboard`), the merge line `:77`:
     ```go
     if top.Text == e.Text && top.sum == e.sum && top.Dir == e.Dir && top.Device == e.Device {
     ```
   - Append to `internal/core/history_test.go`:
     ```go
     // TestDuplicatesMergePerDevice checks that the same text from 2 phones
     // makes 2 entries, and from 1 phone 1 entry.
     func TestDuplicatesMergePerDevice(t *testing.T) {
     	d, _ := clipDaemon(t, true)
     	d.addClipLocked(ClipEntry{Text: "same", Dir: "in", Device: "phone", Time: 1})
     	d.addClipLocked(ClipEntry{Text: "same", Dir: "in", Device: "tab", Time: 2})
     	if len(d.clipboard) != 2 || d.clipboard[0].Device != "tab" || d.clipboard[1].Device != "phone" {
     		t.Fatalf("history %+v", d.clipboard)
     	}
     	d.addClipLocked(ClipEntry{Text: "same", Dir: "in", Device: "tab", Time: 3})
     	if len(d.clipboard) != 2 || d.clipboard[0].Time != 3 {
     		t.Fatalf("the same phone again: %+v", d.clipboard)
     	}
     }
     ```
   - Verify: `go test ./internal/core/ -run 'DuplicatesMergePerDevice|LocalClipNamesItsDevices' -race`
     (`TestLocalClipNamesItsDevices`, `clipboard_test.go:389-414` on
     `feat/16-pause-clipboard`, must still pass: desktop copies have
     `Device` empty and still merge), then `go test ./...`.
   - Traps: change only the condition; the `To` merge and image removal
     below it stay.

4. **Phone: protocol and state.** Files: `protocol/Identity.kt`,
   `core/Device.kt`, `core/Model.kt`, `core/Plugins.kt`,
   `core/ClipHistory.kt` (new),
   `android/app/src/test/java/org/omarchy/flux/core/ClipHistoryTest.kt` (new).
   - `protocol/Identity.kt`: in `object Types`, after `FLUX_SHORTCUTS`
     (`:77`):
     ```kotlin
     const val FLUX_CLIPBOARD_HISTORY = "flux.clipboard.history"
     ```
     Add `Types.FLUX_CLIPBOARD_HISTORY,` as its own line before the closing
     `)` of both `INCOMING` (`:122-134`) and `OUTGOING` (`:137-147`), after
     `Types.FLUX_MOTION,`.
   - `core/Device.kt`, after `var shortcuts: ShortcutsState? = null`
     (`:99-100`):
     ```kotlin
     /** The last answer to a list request of the History screen, see ClipHistory. */
     var clipHistory: ClipHistoryAnswer? = null
     ```
     In `snapshot()` (`:155-201`), after `shortcuts = shortcuts,` (`:186`):
     ```kotlin
     clipHistorySupported = Types.FLUX_CLIPBOARD_HISTORY in identity.incoming,
     clipHistory = clipHistory,
     ```
   - `core/Model.kt`, in `DeviceUi`, after `val shortcuts: ShortcutsState? = null,`
     (`:147`):
     ```kotlin
     /** True when the computer shares this phone's clipboard history, see ClipHistory. */
     val clipHistorySupported: Boolean = false,
     /** The last answer to the History screen's list request, or null. */
     val clipHistory: ClipHistoryAnswer? = null,
     ```
   - `core/Plugins.kt`, in `handle`'s `when (p.type)`, after
     `Types.FLUX_SHORTCUTS -> d.shortcuts = Shortcuts.merge(d.shortcuts, p)`
     (`:151`):
     ```kotlin
     Types.FLUX_CLIPBOARD_HISTORY -> ClipHistory.onPacket(core, d, p)
     ```
   - `core/ClipHistory.kt`:
     ```kotlin
     package org.omarchy.flux.core

     import android.os.Handler
     import android.os.Looper
     import kotlinx.serialization.json.JsonObject
     import org.omarchy.flux.protocol.Packet
     import org.omarchy.flux.protocol.Types
     import org.omarchy.flux.protocol.bodyOf
     import org.omarchy.flux.protocol.bool
     import org.omarchy.flux.protocol.long
     import org.omarchy.flux.protocol.str
     import java.util.concurrent.ConcurrentHashMap
     import java.util.concurrent.atomic.AtomicLong
     import java.util.concurrent.atomic.AtomicReference

     /**
      * 1 entry of a computer's clipboard history. [sent] is true when this
      * phone sent it. [time] is in milliseconds. [size] is the full length in
      * bytes when [truncated].
      */
     data class HistoryEntry(val id: String, val text: String, val sent: Boolean, val time: Long, val size: Int, val truncated: Boolean) {
         /** The link to open: only a whole http or https URL, see WebUrl. */
         val link: String? get() = if (truncated) null else WebUrl.of(text)

         /** False when the whole text is more than the phone's clipboard takes. */
         val copyable: Boolean get() = !truncated || size <= Plugins.MAX_CLIPBOARD_TEXT
     }

     /** The answer to the list request [request] for [query]. [state] is "ok", "disabled" or "bad_request". */
     data class ClipHistoryAnswer(val request: Long, val query: String, val state: String, val entries: List<HistoryEntry>, val more: Boolean)

     /**
      * flux.clipboard.history: this phone's entries of a computer's clipboard
      * history. The phone asks, the computer answers. Each answer is checked
      * against the request it names and the computer that sent it, so a late
      * or foreign answer changes nothing.
      */
     object ClipHistory {
         /** The longest query in UTF-8 bytes, as fluxd. */
         const val MAX_QUERY = 1024

         enum class Kind { List, Copy, Stale }

         private val ids = AtomicLong()

         /** Per device: the list request in flight or last answered, and its query. */
         private val lists = ConcurrentHashMap<String, Pair<Long, String>>()

         /** The one copy in flight: device id and request id. A new copy replaces it. */
         private val copy = AtomicReference<Pair<String, Long>?>(null)

         // Lazy, so the unit tests never touch Looper.
         private val main by lazy { Handler(Looper.getMainLooper()) }

         /** True when [query] fits the limit. */
         fun fits(query: String): Boolean = query.toByteArray(Charsets.UTF_8).size <= MAX_QUERY

         fun listPacket(request: Long, query: String) = Packet(Types.FLUX_CLIPBOARD_HISTORY, bodyOf("request" to request, "query" to query))

         fun entryPacket(request: Long, entryId: String) = Packet(Types.FLUX_CLIPBOARD_HISTORY, bodyOf("request" to request, "entry" to entryId))

         /** Asks [deviceId] for its entries that hold [query]. It returns the request id, which is current even when [send] fails. */
         fun list(deviceId: String, query: String, send: (Packet) -> Boolean): Long {
             val id = ids.incrementAndGet()
             lists[deviceId] = id to query
             send(listPacket(id, query))
             return id
         }

         /** Asks [deviceId] for the full text of [entryId] and copies it on the answer. False when [send] fails. */
         fun copy(deviceId: String, entryId: String, send: (Packet) -> Boolean): Boolean {
             val pending = deviceId to ids.incrementAndGet()
             copy.set(pending)
             if (send(entryPacket(pending.second, entryId))) return true
             copy.compareAndSet(pending, null)
             return false
         }

         /** Drops the copy in flight, so its answer copies nothing. */
         fun forget() {
             copy.set(null)
         }

         /** What the answer [request] from [deviceId] is for. */
         fun kind(deviceId: String, request: Long): Kind {
             val c = copy.get()
             return when {
                 c != null && c.first == deviceId && c.second == request -> Kind.Copy
                 lists[deviceId]?.first == request -> Kind.List
                 else -> Kind.Stale
             }
         }

         /** Takes the copy in flight when it is still [deviceId]'s [request]. */
         fun takeCopy(deviceId: String, request: Long): Boolean {
             val c = copy.get() ?: return false
             // compareAndSet compares references, so it must get the same Pair.
             return c.first == deviceId && c.second == request && copy.compareAndSet(c, null)
         }

         /** Reads a list answer. Null for a full-text answer or one without a request or state. */
         fun parseList(p: Packet, query: String): ClipHistoryAnswer? {
             if (p.has("entry")) return null
             val request = p.long("request") ?: return null
             val state = p.string("state") ?: return null
             if (state == "not_found" || state == "too_large") return null
             val entries = p.array("entries")?.mapNotNull { (it as? JsonObject)?.let(::entryOf) }.orEmpty()
             return ClipHistoryAnswer(request, query, state, entries, p.bool("more") == true)
         }

         private fun entryOf(o: JsonObject): HistoryEntry? {
             val id = o.str("id")?.takeIf { it.isNotEmpty() } ?: return null
             val text = o.str("text") ?: return null
             return HistoryEntry(
                 id, text, o.bool("sent") == true, (o.long("time") ?: 0L) * 1000L,
                 (o.long("size") ?: 0L).toInt(), o.bool("truncated") == true,
             )
         }

         /** The list answer [p] from [deviceId] when it answers the current list request, else null. */
         fun acceptList(deviceId: String, p: Packet): ClipHistoryAnswer? {
             val request = p.long("request") ?: return null
             val (id, query) = lists[deviceId] ?: return null
             if (id != request) return null
             return parseList(p, query)
         }

         /** The toast for a copy answer that put nothing on the clipboard. */
         fun copyMessage(state: String?, from: String): String = when (state) {
             "too_large" -> "The text is too large to copy"
             "disabled" -> "Clipboard is off for this phone on $from"
             else -> "$from no longer has this entry"
         }

         /** Handles an answer. The core lock is held. */
         fun onPacket(core: FluxCore, d: Device, p: Packet) {
             val request = p.long("request") ?: return
             when (kind(d.id, request)) {
                 Kind.Copy -> {
                     val from = d.identity.deviceName
                     val state = p.string("state")
                     val text = p.obj("entry")?.str("text")
                     // On the main thread, where forget runs, so a forget that came first wins.
                     main.post {
                         if (!takeCopy(d.id, request)) return@post
                         if (state == "ok" && text != null) {
                             if (Plugins.putRemoteText(core, d.id, from, text)) core.toast("Copied to this phone")
                         } else {
                             core.toast(copyMessage(state, from))
                         }
                     }
                 }
                 Kind.List -> acceptList(d.id, p)?.let { d.clipHistory = it }
                 Kind.Stale -> Unit
             }
         }
     }
     ```
   - `android/app/src/test/java/org/omarchy/flux/core/ClipHistoryTest.kt`:
     ```kotlin
     package org.omarchy.flux.core

     import org.junit.Assert.assertEquals
     import org.junit.Assert.assertFalse
     import org.junit.Assert.assertNull
     import org.junit.Assert.assertTrue
     import org.junit.Test
     import org.omarchy.flux.protocol.INCOMING
     import org.omarchy.flux.protocol.OUTGOING
     import org.omarchy.flux.protocol.Packet
     import org.omarchy.flux.protocol.Types
     import org.omarchy.flux.protocol.bodyOf

     class ClipHistoryTest {
         private fun answer(vararg fields: Pair<String, Any?>) = Packet(Types.FLUX_CLIPBOARD_HISTORY, bodyOf(*fields))

         private fun entry(id: String, text: String, vararg more: Pair<String, Any?>) =
             mapOf("id" to id, "text" to text, "sent" to true, "time" to 1_763_123_456L) + more

         @Test
         fun capabilityIsInBothLists() {
             assertTrue(Types.FLUX_CLIPBOARD_HISTORY in INCOMING)
             assertTrue(Types.FLUX_CLIPBOARD_HISTORY in OUTGOING)
         }

         @Test
         fun parseListReadsEntries() {
             val p = answer(
                 "request" to 7L, "state" to "ok", "more" to true, "extra" to 1,
                 "entries" to listOf(entry("a", "git status"), mapOf("text" to "no id"), entry("b", "x".repeat(1024), "truncated" to true, "size" to 4096)),
             )
             val a = ClipHistory.parseList(p, "git")!!
             assertEquals(7L, a.request)
             assertEquals("git", a.query)
             assertEquals("ok", a.state)
             assertTrue(a.more)
             assertEquals(listOf("a", "b"), a.entries.map { it.id })
             assertEquals(1_763_123_456_000L, a.entries[0].time)
             assertTrue(a.entries[0].sent)
             assertTrue(a.entries[1].truncated)
             assertEquals(4096, a.entries[1].size)
         }

         @Test
         fun parseListStates() {
             assertEquals("disabled", ClipHistory.parseList(answer("request" to 1L, "state" to "disabled"), "")!!.state)
             assertNull(ClipHistory.parseList(answer("request" to 1L, "state" to "ok", "entry" to entry("a", "t")), ""))
             assertNull(ClipHistory.parseList(answer("request" to 1L, "state" to "not_found"), ""))
             assertNull(ClipHistory.parseList(answer("state" to "ok"), ""))
         }

         @Test
         fun linkAndCopyable() {
             fun e(text: String, truncated: Boolean = false, size: Int = 0) = HistoryEntry("i", text, false, 0, size, truncated)
             assertEquals("https://omarchy.org/flux", e("https://omarchy.org/flux").link)
             assertNull(e("https://omarchy.org/flux", truncated = true, size = 2000).link)
             assertNull(e("file:///etc/passwd").link)
             assertNull(e("yay -S flux-git").link)
             assertTrue(e("x", truncated = true, size = 256 * 1024).copyable)
             assertFalse(e("x", truncated = true, size = 256 * 1024 + 1).copyable)
         }

         @Test
         fun staleListAnswersAreIgnored() {
             var sent = 0
             val first = ClipHistory.list("list-pc", "a") { sent++; true }
             val second = ClipHistory.list("list-pc", "ab") { sent++; true }
             assertEquals(2, sent)
             assertEquals(ClipHistory.Kind.Stale, ClipHistory.kind("list-pc", first))
             assertEquals(ClipHistory.Kind.List, ClipHistory.kind("list-pc", second))
             // The same id from another computer is stale.
             assertEquals(ClipHistory.Kind.Stale, ClipHistory.kind("other-pc", second))
             // Only the current request's answer is taken, with its query.
             assertNull(ClipHistory.acceptList("list-pc", answer("request" to first, "state" to "ok", "entries" to emptyList<Any>())))
             assertNull(ClipHistory.acceptList("other-pc", answer("request" to second, "state" to "ok", "entries" to emptyList<Any>())))
             assertNull(ClipHistory.acceptList("list-pc", answer("request" to second, "state" to "ok", "entry" to entry("a", "t"))))
             val a = ClipHistory.acceptList("list-pc", answer("request" to second, "state" to "ok", "entries" to listOf(entry("a", "ab c"))))!!
             assertEquals("ab", a.query)
             assertEquals(listOf("a"), a.entries.map { it.id })
         }

         @Test
         fun copyMessages() {
             assertEquals("The text is too large to copy", ClipHistory.copyMessage("too_large", "desk"))
             assertEquals("Clipboard is off for this phone on desk", ClipHistory.copyMessage("disabled", "desk"))
             assertEquals("desk no longer has this entry", ClipHistory.copyMessage("not_found", "desk"))
             assertEquals("desk no longer has this entry", ClipHistory.copyMessage(null, "desk"))
         }

         @Test
         fun onlyTheCurrentCopyIsTaken() {
             var first = 0L
             assertTrue(ClipHistory.copy("copy-pc", "e1") { first = it.long("request")!!; true })
             var request = 0L
             ClipHistory.copy("copy-pc", "e2") { request = it.long("request")!!; it.string("entry") == "e2" }
             // The newer copy replaced the older one.
             assertFalse(ClipHistory.takeCopy("copy-pc", first))
             // An older copy, another computer, and a taken copy are refused.
             assertFalse(ClipHistory.takeCopy("other-pc", request))
             assertEquals(ClipHistory.Kind.Copy, ClipHistory.kind("copy-pc", request))
             assertTrue(ClipHistory.takeCopy("copy-pc", request))
             assertFalse(ClipHistory.takeCopy("copy-pc", request))
         }

         @Test
         fun forgetAndFailedSendDropTheCopy() {
             var request = 0L
             ClipHistory.copy("forget-pc", "e1") { request = it.long("request")!!; true }
             ClipHistory.forget()
             assertFalse(ClipHistory.takeCopy("forget-pc", request))
             assertFalse(ClipHistory.copy("forget-pc", "e2") { request = it.long("request")!!; false })
             assertFalse(ClipHistory.takeCopy("forget-pc", request))
         }

         @Test
         fun queryLimitCountsUtf8Bytes() {
             assertTrue(ClipHistory.fits("a".repeat(1024)))
             assertFalse(ClipHistory.fits("a".repeat(1025)))
             // 1020 bytes plus a 4-byte character fit; one more byte doesn't.
             assertTrue(ClipHistory.fits("a".repeat(1020) + "😀"))
             assertFalse(ClipHistory.fits("a".repeat(1021) + "😀"))
         }
     }
     ```
   - Verify: CI in step 7 (`:app:testDebugUnitTest` runs `ClipHistoryTest`).
     Locally, a read for each edit: `grep -n FLUX_CLIPBOARD_HISTORY` finds
     the type in `Identity.kt` (3 places), `Device.kt`, `Plugins.kt` and
     `ClipHistory.kt`.
   - Traps:
     - Unit tests run on the plain JVM without `returnDefaultValues`
       (`android/app/build.gradle.kts`); a `Handler` built when the object
       loads would throw. `main` is `by lazy`, and the tests never call
       `onPacket`.
     - `AtomicReference.compareAndSet` compares references, not
       `equals`: `takeCopy` reads the current `Pair` and passes that same
       object.
     - `ClipHistory` is a singleton, so each test uses its own device id.
     - `Packet.long` reads a JSON number, including one that came as a
       double (`protocol/Packet.kt:67`).
     - The order in `snapshot()` and `DeviceUi` doesn't matter (named
       arguments), but keep the new fields next to `shortcuts`.

5. **Phone: the History screen.** Files: `ui/Nav.kt`, `ui/AppShell.kt`,
   `ui/ChatScreen.kt`, `ui/ChatsScreen.kt`, `ui/ClipHistoryScreen.kt` (new).
   - `ui/Nav.kt`, after `OMARCHY_PAGE` (`:56-57`):
     ```kotlin
     /** This phone's entries of a computer's clipboard history, with search. */
     const val CLIP_HISTORY_PAGE = "history"
     ```
     In `tabOf` (`:135-139`), the first branch becomes:
     ```kotlin
     page == "browse" || page == CLIP_HISTORY_PAGE || page.startsWith("camera") && !page.endsWith(":webcam") -> Tab.Chat
     ```
   - `ui/AppShell.kt`, in the per-device `when` (`:525-552`), after
     `page == OMARCHY_PAGE -> OmarchyScreen(device, pop)` (`:547`):
     ```kotlin
     page == CLIP_HISTORY_PAGE -> ClipHistoryScreen(device, pop)
     ```
   - `ui/ChatScreen.kt`, in `ComputerMenu` (`:258-283`), before the
     `Clear chat` item (`:273`):
     ```kotlin
     if (d.clipHistorySupported) Triple("History", Ic.search) { actions.open(Route(d.id, CLIP_HISTORY_PAGE)) } else null,
     ```
     Update its KDoc (`:253-256`): "…a new agent or terminal, History when
     the computer shares it, and Clear chat when [onClear] is set."
   - `ui/ChatsScreen.kt`:
     - `SearchField` (`:181`): `private` → `internal`, and a new last
       parameter `placeholder: String = "Search agents, computers, files"`.
       Use it for the `contentDescription` (`:187`) and the hint text
       (`:202`) instead of the two literals. The call at `:144` doesn't
       change.
   - `ui/ClipHistoryScreen.kt`:
     ```kotlin
     package org.omarchy.flux.ui

     import android.content.Context
     import android.content.Intent
     import android.net.Uri
     import androidx.compose.foundation.clickable
     import androidx.compose.foundation.layout.Arrangement
     import androidx.compose.foundation.layout.Box
     import androidx.compose.foundation.layout.Column
     import androidx.compose.foundation.layout.Row
     import androidx.compose.foundation.layout.fillMaxSize
     import androidx.compose.foundation.layout.fillMaxWidth
     import androidx.compose.foundation.layout.imePadding
     import androidx.compose.foundation.layout.padding
     import androidx.compose.foundation.lazy.LazyColumn
     import androidx.compose.foundation.lazy.items
     import androidx.compose.runtime.Composable
     import androidx.compose.runtime.DisposableEffect
     import androidx.compose.runtime.LaunchedEffect
     import androidx.compose.runtime.getValue
     import androidx.compose.runtime.mutableIntStateOf
     import androidx.compose.runtime.mutableLongStateOf
     import androidx.compose.runtime.mutableStateOf
     import androidx.compose.runtime.remember
     import androidx.compose.runtime.saveable.rememberSaveable
     import androidx.compose.runtime.setValue
     import androidx.compose.ui.Alignment
     import androidx.compose.ui.Modifier
     import androidx.compose.ui.platform.LocalContext
     import androidx.compose.ui.semantics.Role
     import androidx.compose.ui.semantics.semantics
     import androidx.compose.ui.text.style.TextAlign
     import androidx.compose.ui.unit.dp
     import kotlinx.coroutines.delay
     import org.omarchy.flux.core.ClipHistory
     import org.omarchy.flux.core.DeviceUi
     import org.omarchy.flux.core.FluxCore
     import org.omarchy.flux.core.HistoryEntry
     import org.omarchy.flux.core.Plugins
     import org.omarchy.flux.core.WebUrl

     /**
      * This phone's entries of the clipboard history of the computer [d], with
      * a search. The computer searches; the screen shows only the answer to
      * its last request for the text in the field. A tap copies an entry; an
      * uncut link also has Open.
      */
     @Composable
     fun ClipHistoryScreen(d: DeviceUi, onBack: () -> Unit) {
         var query by rememberSaveable { mutableStateOf("") }
         var retry by remember { mutableIntStateOf(0) }
         var request by rememberSaveable { mutableLongStateOf(0L) }
         var timedOut by remember { mutableStateOf(false) }
         val ready = d.online && d.clipHistorySupported
         LaunchedEffect(query, ready, retry) {
             timedOut = false
             if (!ready) {
                 ClipHistory.forget() // a copy in flight can't be answered on a lost link
                 return@LaunchedEffect
             }
             // After a rotation the answer to the saved request still holds.
             if (d.clipHistory?.let { it.request == request && it.query == query } == true) return@LaunchedEffect
             if (query.isNotEmpty()) delay(300)
             request = ClipHistory.list(d.id, query) { FluxCore.device(d.id)?.send(it) == true }
         }
         // The no-answer timer runs per request, so a fast answer and new typing never show it.
         LaunchedEffect(request) {
             timedOut = false
             if (request == 0L) return@LaunchedEffect
             delay(10_000)
             timedOut = true
         }
         DisposableEffect(Unit) { onDispose { ClipHistory.forget() } }
         val answer = d.clipHistory?.takeIf { it.request == request && it.query == query }
         Column(Modifier.fillMaxSize().imePadding()) {
             ScreenHeader("History", d.name, onBack)
             when {
                 !d.online -> Box(Modifier.padding(horizontal = TiledGutter)) { NotReachable(d, "The clipboard history") }
                 !d.clipHistorySupported -> EmptyState(
                     Ic.search, "Update Flux on ${d.name}",
                     "This version of Flux on ${d.name} does not share its clipboard history.",
                     Modifier.padding(top = 48.dp),
                 )
                 answer?.state == "disabled" -> EmptyState(
                     Ic.search, "Clipboard is off for this phone",
                     "On ${d.name}, turn on Clipboard for this phone in Flux.",
                     Modifier.padding(top = 48.dp),
                 )
                 else -> {
                     SearchField(
                         query, {
                             if (ClipHistory.fits(it)) {
                                 query = it
                                 timedOut = false
                             }
                         },
                         Modifier.padding(start = TiledGutter, end = TiledGutter, top = 2.dp, bottom = 8.dp),
                         placeholder = "Search clipboard history",
                     )
                     when {
                         answer == null && timedOut -> HistoryStatus("No answer from ${d.name}") { timedOut = false; retry++ }
                         answer == null -> HistoryStatus("Searching", null)
                         answer.state != "ok" -> HistoryStatus("${d.name} could not read the search") { retry++ }
                         answer.entries.isEmpty() -> HistoryStatus(if (query.isEmpty()) "No clipboard history with ${d.name} yet" else "Nothing found", null)
                         else -> LazyColumn(Modifier.fillMaxWidth().weight(1f)) {
                             items(answer.entries, key = { it.id }) { e -> HistoryRow(d, e) }
                             if (answer.more) {
                                 item {
                                     T(
                                         "Showing the 50 newest. Search to find older ones.",
                                         Modifier.fillMaxWidth().padding(horizontal = TiledGutter, vertical = 16.dp),
                                         size = 13, color = Tn.sub, align = TextAlign.Center,
                                     )
                                 }
                             }
                         }
                     }
                 }
             }
         }
     }

     /** A line under the search field, with Retry when [onRetry] is set. */
     @Composable
     private fun HistoryStatus(text: String, onRetry: (() -> Unit)?) {
         Column(
             Modifier.fillMaxWidth().padding(top = 24.dp, start = TiledGutter, end = TiledGutter),
             horizontalAlignment = Alignment.CenterHorizontally,
             verticalArrangement = Arrangement.spacedBy(12.dp),
         ) {
             T(text, size = 13, color = Tn.sub, align = TextAlign.Center)
             if (onRetry != null) FluxButton("Retry", onRetry, kind = ButtonKind.Text)
         }
     }

     /** 1 entry: its text, Sent or Received with the time, and Open for a link. A tap copies it. */
     @Composable
     private fun HistoryRow(d: DeviceUi, e: HistoryEntry) {
         val context = LocalContext.current
         val meta = buildString {
             append(if (e.sent) "Sent" else "Received")
             timeText(context, e.time).takeIf { it.isNotEmpty() }?.let { append(" · ").append(it) }
             when {
                 !e.copyable -> append(" · too large to copy")
                 e.truncated && WebUrl.of(e.text) != null -> append(" · too long to open here, copy it")
             }
         }
         val tap = if (e.copyable) Modifier.clickable(onClickLabel = "Copy", role = Role.Button) { copyEntry(d, e) } else Modifier
         Row(
             // One TalkBack item per row, also when it isn't clickable; Open stays its own button.
             Modifier.fillMaxWidth().then(tap).semantics(mergeDescendants = true) {}.padding(horizontal = TiledGutter, vertical = 10.dp),
             verticalAlignment = Alignment.CenterVertically,
             horizontalArrangement = Arrangement.spacedBy(8.dp),
         ) {
             Column(Modifier.weight(1f)) {
                 T(e.text, maxLines = 3)
                 T(meta, size = 11, color = Tn.sub, maxLines = 1)
             }
             e.link?.let { link -> FluxButton("Open", { openLink(context, link) }, kind = ButtonKind.Text) }
         }
     }

     /** Copies [e]: at once when it is whole, else after the computer sends the full text. */
     private fun copyEntry(d: DeviceUi, e: HistoryEntry) {
         if (!e.truncated) {
             ClipHistory.forget()
             if (Plugins.putRemoteText(FluxCore, d.id, d.name, e.text)) FluxCore.toast("Copied to this phone")
             return
         }
         if (!ClipHistory.copy(d.id, e.id) { FluxCore.device(d.id)?.send(it) == true }) FluxCore.toast("${d.name} is not reachable")
     }

     /** Opens a web link in the browser. */
     private fun openLink(context: Context, link: String) {
         runCatching { context.startActivity(Intent(Intent.ACTION_VIEW, Uri.parse(link))) }
             .onFailure { FluxCore.toast("No app opens this link") }
     }
     ```
   - Verify: CI in step 7 (`:app:lintDebug`, both assembles).
   - Traps:
     - Only `SearchField` becomes `internal`; nothing else in
       `ChatsScreen.kt` changes.
     - `T(…, maxLines = 3)` ends with an ellipsis: `T` sets
       `TextOverflow.Ellipsis` whenever `maxLines` is set
       (`ui/Components.kt:93`).
     - The time is `timeText` (`ui/ChatScreen.kt:545`, already
       `internal`), as the spec says: time of day only.
     - `DetailScreen` pads only the system bars (`ui/AppShell.kt:221`), so
       the screen adds `imePadding()` itself, as `ChatsScreen` does
       (`ui/ChatsScreen.kt:125`).
     - A link replaced without going offline (`core/FluxCore.kt:358-360`)
       loses the answer on the old link: the screen shows "No answer" after
       10 s with Retry, as the spec says. No `FluxCore` change.
     - A rotation runs `onDispose`, so a copy in flight is dropped; the
       user taps again.
     - The search field is inside the `else` branch only, so it stays
       mounted while the list, Searching and Nothing found change under it.
     - `LaunchedEffect(query, ready, retry)` restarts on each keystroke, so
       the 300 ms delay is the debounce; an empty query asks at once.
     - Keep the strings as written: they are the house style of the
       app's other screens (`ui/OmarchyScreen.kt:65-74`).
     - `Ic.search` exists (`ui/Icons.kt:37`); no new drawable.
     - Step 4's line numbers in `ChatScreen.kt` are unchanged by step 4;
       find each place by function name if they moved.

6. **Docs.** Files: `docs/features.md`, `docs/android.md`.
   - `docs/features.md`, after the paragraph that ends "When `fluxd`
     restarts, the desktop clipboard loses text or an image that came from
     a device." (`:195-200` on `feat/16-pause-clipboard`, in "## Files,
     clipboard, and links"), with a blank line before:
     ```markdown
     The phone can search its own part of the clipboard history: **History** in a computer's chat menu.
     It lists the texts and links that the phone sent to the computer or the computer sent to the phone, newest first.
     It never shows images, saved snippets, files, or the entries of another device.
     A search finds the newest 50 matches in the full text; a tap copies an entry to the phone, and a link opens.
     It needs Clipboard on for the phone, and it works while clipboard sync is paused.
     The phone and `fluxd` both list `flux.clipboard.history`.
     ```
   - `docs/android.md`, at the end of "## Clipboard from another app"
     (`:178-183`):
     ```markdown
     **History** in a computer's chat menu searches this phone's part of the computer's clipboard history and copies an entry back.
     The phone lists `flux.clipboard.history` as a packet it sends and takes, and it needs a `fluxd` that lists it too.
     ```
   - Verify: `nix flake check path:.` passes (it runs the docs checks).
   - Traps: one sentence per line, as the rest of both files.

7. **CI (the lead).**
   - Before the push, `go test ./...` and `nix flake check path:.` pass.
   - Push, after an agent-bus announcement.
   - Dispatch `build.yml` on the branch:
     ```sh
     gh workflow run build.yml -R olafkfreund/nixarchy-flux --ref feat/3-clipboard-search
     ```
   - Poll every 5 minutes. The Go job and the android job must pass: unit
     tests (with `ClipHistoryTest`), `lintDebug`, both assembles, and the
     ABI check.

8. **Device tests (optional; the lead and the owner).** Only with the
   owner's OK, because they replace the running `fluxd` and the phone's
   app.

   **Setup:**
   1. Ask the owner. Announce on the agent bus that p620's `fluxd` stops.
   2. Build the branch's `fluxd` and `flux-cli` on p620 (`nix build
      .#fluxd` or `go build -o "$b/bin/" ./cmd/...` into a scratch folder
      `$b`). Not on p510.
   3. `systemctl --user stop fluxd.service`; run `$b/bin/fluxd` in the
      foreground. Use `$b/bin/flux-cli` for every command below, never
      `/etc/profiles/per-user/olafkfreund/bin/flux-cli`.
   4. Read the serials with `adb devices`; wireless ports change.
   5. The CI debug APK can't install over the installed app (another
      debug key): ask the owner, then `adb -s <serial> uninstall
      org.omarchy.flux`, `adb -s <serial> install app-debug.apk`, and pair
      again with `$b/bin/flux-cli pair` and `accept`. Same for the Galaxy
      Tab S8 Ultra if it takes part in check 6.

   **Then**, on the Pixel 11 Pro XL (`<phone>` from `$b/bin/flux-cli
   devices`):

   | # | Check | How |
   | --- | --- | --- |
   | 1 | The list | copy two texts on p620, send one from the phone, `$b/bin/flux-cli --device <phone> url https://omarchy.org/flux`. Open History: all four, newest first, Sent and Received right |
   | 2 | Search | type part of one text: only it shows. Type "pc": no received entry shows by name |
   | 3 | Copy, no echo | tap a text: toast "Copied to this phone"; `$b/bin/flux-cli clipboard` shows no new entry from the phone |
   | 4 | Open | Open on the link opens the browser |
   | 5 | A cut entry | copy a 100 KiB text on p620: it shows cut; a tap puts the whole text on the phone (paste it somewhere to check the end). If the phone can send a text over 256 KiB (share it to Flux): its row shows cut, "too large to copy", and a tap does nothing |
   | 6 | Another device | with the Tab paired, send a clip from the Tab: it doesn't show on the Pixel's History |
   | 7 | Disabled | turn the Pixel's Clipboard off for it on p620: History says "Clipboard is off for this phone". Turn it on again |
   | 8 | Pause (#16) | `$b/bin/flux-cli clipboard pause`, copy a text on p620: History doesn't show it; earlier entries stay. `clipboard resume` |
   | 9 | Offline | turn off the phone's Wi-Fi with History open: it says not reachable; on again, the list returns |
   | 10 | TalkBack | a row is one item with its text and Sent or Received; Copy and Open are reachable; a too-large row (check 5) is one item with no Copy action |
   | 11 | Keyboard | type a search: the list and the status lines stay above the keyboard; the field keeps the keyboard while results change |

   **Afterwards:** stop `$b/bin/fluxd`, `systemctl --user start
   fluxd.service`, and announce it on the agent bus. With the owner's OK,
   delete the test clips from p620's history (`flux-cli clipboard delete
   <id>`) and clear the phone's clipboard. Record the results in the PR.

9. **PR (the lead).** Open the PR linking the intent, spec and plan, with
   the CI and device results (or "device tests skipped"), and which steps
   each implementer did.

## Tests

- `go test ./...`, with `TestClipboardHistoryGoesBothWays`,
  `TestOwnClips`, `TestHistoryMatches`, `TestHistoryFull`,
  `TestClipHistoryHandler`, `TestClipHistoryRouting`,
  `TestDuplicatesMergePerDevice`, and the existing
  `TestLocalClipNamesItsDevices`. The plan's Go code was compiled and
  these tests run on a copy of `feat/16-pause-clipboard` while writing
  it.
- CI `build.yml`: the Go job, and the android job with `ClipHistoryTest`
  (nine tests), `lintDebug` and both assembles.
- `nix flake check path:.`.
- Step 8 on a real phone, when the owner allows it: the screen states,
  copy without echo, Open, and TalkBack need a device.

## Rollback

- Revert the PR's squash commit, then rebuild `fluxd` and the APK. Nothing
  is stored: the history stays memory only, and the packet adds no
  setting.
- A reverted `fluxd` with a new phone: the phone sees no
  `flux.clipboard.history` and hides History. A new `fluxd` with an old
  phone: the phone never asks. No mixed state breaks.
- The duplicate fix reverts with the rest; the desktop chat then merges
  same-text entries from two phones again.

## Review changes

Codex and agy reviewed the first draft of this plan, read only. Each
finding was checked against the code; the Go code was also compiled and
its tests run on a copy of `feat/16-pause-clipboard`.

| Finding | Source | Verdict | Change |
| --- | --- | --- | --- |
| "No answer" flashes when typing 10 s after a list loaded: the timer never stopped | agy (high) | Confirmed: `timedOut` stayed true and reset only after the 300 ms delay | A timer per request (`LaunchedEffect(request)`); reset on typing and Retry |
| Copies and list requests outlive a lost or replaced link | Codex (high), agy (high) | Partly confirmed: an old link can't deliver later answers (ids are unique), but a directly replaced link (`FluxCore.kt:358-360`) never goes offline, so the screen waits | `forget()` when not ready; the replaced-link case falls back to "No answer" and Retry, as the spec says; trap added. No `FluxCore` change |
| A failed send leaves the copy armed | agy | Refuted: `copy` already does `compareAndSet(pending, null)` when `send` fails | None |
| The tests never reach the packet handling | Codex, agy | Confirmed | `acceptList` and `copyMessage` pulled out of `onPacket` and tested; `onPacket` is only the main-thread post |
| `shortTime` instead of the spec's `timeText` | Codex, agy | Confirmed, spec section 5 | `timeText`; the `shortTime` change dropped |
| The keyboard covers the results | agy | Confirmed: `DetailScreen` pads only system bars (`AppShell.kt:221`) | `imePadding()`, device check 11 |
| A too-large row is two TalkBack items; its device checks were dropped | agy, Codex | Confirmed | `semantics(mergeDescendants = true)`; checks 5 and 10 restored |
| Device tests are optional | Codex | Kept optional at the lead's instruction (owner's OK needed to replace `fluxd` and the app) | The PR records them as skipped if not run |
| An expired pin still hides its entry | agy | Confirmed: `clipEntriesLocked` skips expired pins (`snippets.go:122-127`) | The handler skips them too; covered in `TestClipHistoryHandler` |
| `size` and `truncated` absence not checked on the wire | agy | Agreed | Added to the forbidden keys |
| Rotation resets the request | agy | Confirmed | `request` is `rememberSaveable`; no resend when the saved answer still holds |
| Unused `ClipHistory.current` | agy | Confirmed | Removed |
| `codex review --base master` sees #16 after a plain fetch | agy | Confirmed | Step 1 fetches `master:master`; the reviewer uses `origin/master` |
| `docs/features.md` insertion splits a paragraph; android.md misses "both directions" | agy, Codex | Confirmed (`:195-200` on #16) | After the paragraph; sentence added |
| Line nits: `addClipLocked` ends `:118`, `INCOMING` ends `:134`; `T` ellipsizes | agy | Confirmed (`Components.kt:93`) | Corrected |
| The Go code compiles; float64 assertions, imports and helpers are right | both | Confirmed by compiling and running it | None |

## Corrections

| # | Step | What changed and why |
| --- | --- | --- |
| P1 | review, CI | CI compile error: the screen reused the existing `internal openLink` (AboutScreen.kt) instead of a second private one. Review fixes (Opus, Codex, agy): a cached answer is reused only when `ok` (screen restore), so Retry asks again; `FluxCore.detach` clears `clipHistory`, so a reconnect refetches. `ClipHistory.onPacket` delegates to a testable `handle`. New tests: fetch by id of another device's, a desktop-only, an image and a pinned entry through the handler (not_found), a fetch with the switch off and without the capability, filter before the cap, search past the 1 KiB preview; Android: list and entry packets, the full-entry copy, forgotten or replaced copies, wrong-device answers. Not changed: entry dates still show the time of day only. |
