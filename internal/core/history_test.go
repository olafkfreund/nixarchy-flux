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
