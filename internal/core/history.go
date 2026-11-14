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
