---
status: approved
issue: 3
intent: intent/2026-11-14-3-clipboard-search.md
---

# Spec: Search the recent clipboard sends on the phone

The owner approved the intent with its proposed answers:
- a phone sees only its own entries (`Device` is the phone, or `To` holds
  it), with the gaps the intent accepts;
- no pins (nor the history original of a pinned entry), no images, no
  transfers;
- the 50 newest matches, filtered first, each cut at 1 KiB; one entry's
  full text on request, up to 256 KiB; larger entries are preview only;
  the query is at most 1 KiB;
- search runs in `fluxd`, over the text only, lower case, as a substring;
- the device's `clipboard` switch is the gate, not `auto_clipboard`;
- duplicates merge only within one device (`top.Device == e.Device`);
- Go and Android unit tests, then a phone test.

**Base.** #16 (pause clipboard sync) merges first and rewrites much of
`internal/core/clipboard.go`. This design is written against
`feat/16-pause-clipboard`. The #3 branch is rebased on `master` after #16
merges, before any code. Line numbers below are from that branch for
`internal/core/clipboard.go` and `internal/core/clipboard_test.go`, and
from `master` elsewhere. #16 also adds to `docs/features.md`; the plan
cites its lines after the rebase.

Go paths are under `internal/`. Android paths are under
`android/app/src/main/java/org/omarchy/flux/` unless they start with
`android/app/src/test/`.

## Design

### 1. The packet (`proto/history.go`, new)

One type that both sides send, like `flux.speaker` (`proto/speaker.go`):

```go
// TypeFluxClipboardHistory lets a phone read and search its own entries of
// the clipboard history of this computer. The phone asks, fluxd answers.
// docs/features.md describes it.
const TypeFluxClipboardHistory = "flux.clipboard.history"

func init() {
	Incoming = append(Incoming, TypeFluxClipboardHistory)
	Outgoing = append(Outgoing, TypeFluxClipboardHistory)
}
```

Bodies. `request` is a number the phone picks; every answer carries it.

| From | Body | Meaning |
| --- | --- | --- |
| phone | `{"request": 7, "query": "git"}` | The newest entries that match. An empty query lists all. |
| phone | `{"request": 8, "entry": "Xa3k9Q"}` | The full text of one entry. |
| fluxd | `{"request": 7, "state": "ok", "entries": [...], "more": true}` | Up to 50 entries, newest first. `more` when more matched. |
| fluxd | `{"request": 8, "state": "ok", "entry": {...}}` | One entry with its full text. |
| fluxd | `{"request": n, "state": "disabled"}` | The device's `clipboard` switch is off. |
| fluxd | `{"request": 8, "state": "not_found"}` | Not this phone's entry, gone, pinned, or an image. |
| fluxd | `{"request": 8, "state": "too_large"}` | The text is over 256 KiB. |
| fluxd | `{"request": n, "state": "bad_request"}` | Neither field, both, or a query over 1 KiB. |

An entry on the wire has only these fields:

```json
{"id": "Xa3k9Q", "text": "…", "sent": true, "time": 1763123456, "size": 2048, "truncated": true}
```

- `sent` is the phone's view: true for the desktop's `Dir: "in"` (the
  phone sent it), false for `"out"`.
- `time` is Unix seconds, as `ClipEntry.Time`.
- `size` and `truncated` only when the text was cut, as in
  `clipPreviewLocked` (`core/clipboard.go:172-182` on master).
- Never `image`, `device`, `deviceName`, `to`, `source`, `pinned`.

### 2. The handler (`core/history.go`, new)

Routing, in `handlePacket` (`core/handlers.go:55` and on), after the
paired and current-link check (`core/handlers.go:19-54`):

```go
case proto.TypeFluxClipboardHistory:
	d.handleClipHistory(dev, l, p)
```

The handler runs on the link's read loop, like the refusal in
`handleShortcuts` (`core/shortcuts.go:226-228`). It only blocks this
phone's own link, and the phone waits for one answer at a time per screen.
It takes `l` as `streamLink` (`core/stream.go:28-32`), so the tests can use
`fakeStreamLink` (`core/session_test.go:27`).

```go
const (
	maxHistoryAnswer = 50         // entries in one answer
	maxHistoryQuery  = 1 << 10    // bytes of a query
)

type historyRequest struct {
	Request int64   `json:"request"`
	Query   *string `json:"query"`
	Entry   *string `json:"entry"`
}

type historyEntry struct {
	ID        string `json:"id"`
	Text      string `json:"text"`
	Sent      bool   `json:"sent"`
	Time      int64  `json:"time"`
	Size      int    `json:"size,omitempty"`
	Truncated bool   `json:"truncated,omitempty"`
}

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
	clips := slices.Clone(d.clipboard)
	pinned := make(map[string]bool, len(d.snippets))
	for _, s := range d.snippets {
		pinned[s.ID] = true
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
```

Pure helpers in the same file, so the tests call them without a link:

```go
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
// holds query, as searchClips matches, each cut at maxClipPreview bytes.
func historyMatches(mine []ClipEntry, query string) ([]historyEntry, bool) {
	q := strings.ToLower(query)
	out := []historyEntry{}
	for _, e := range mine {
		if q != "" && !strings.Contains(strings.ToLower(e.Text), q) {
			continue
		}
		if len(out) == maxHistoryAnswer {
			return out, true
		}
		h := historyEntry{ID: e.ID, Text: e.Text, Sent: e.Dir == "in", Time: e.Time}
		if len(h.Text) > maxClipPreview {
			h.Size, h.Truncated = len(h.Text), true
			h.Text = strings.ToValidUTF8(h.Text[:maxClipPreview], "")
		}
		out = append(out, h)
	}
	return out, false
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

- **Lock.** Under `d.mu` it only clones the slice and collects the pin ids.
  `d.clipboard` holds at most 500 entries (`config/config.go:140-141`).
  The strings are shared, not copied. `To` is safe to read later:
  `addClipLocked` grows it with `append(slices.Clip(top.To), id)`
  (`core/clipboard.go:79-83`), a new array, never in place. Matching,
  encoding and sending run without the lock.
- **Size.** 50 entries of at most 1 KiB of text is about 60 KiB, at most
  about 310 KiB when every byte is a control character that JSON escapes
  six-fold; one full entry is at most 256 KiB of text, about 1.6 MiB
  escaped in the worst case. Both are far under the 16 MiB line limit on
  both sides (`proto/packet.go:18`, Android `net/Link.kt:24`).
- **Routing.** `handlePacket` drops a packet from an unpaired device or a
  link that is no longer the device's (`core/handlers.go:19-54`). Between
  that check and the clone the lock is free for a moment; an answer then
  still goes only to this device's own link, with its own entries.
- **The fetch by id** runs the same `ownClips` filter as the list. An id of
  another phone, of a pin, of an image, or gone since the list loaded
  answers `not_found`. It never calls `CopyClip`
  (`core/clipboard.go:601-618` on master), which searches pins and all
  devices and writes the desktop clipboard.
- **The pause of #16.** It stops the automatic sync of local copies. A
  copy made while paused never enters the history: `onLocalClipboard` and
  `onLocalImage` return before `addClipLocked` (`core/clipboard.go:350,407`
  on `feat/16-pause-clipboard`). So History lists nothing that a pause
  held back, and needs no pause check of its own. Entries recorded before
  the pause stay listed: the phone was meant to get them (`To` is intent,
  not receipt, as the intent accepts; a pause can stop a send after the
  entry is recorded, `core/clipboard.go:368,377-391`). See Owner decision 1.
- **Old phones.** `fluxd` answers only a peer that lists the type in its
  incoming capabilities (`dev.accepts`, `core/device.go:251`), and never
  sends the packet on its own.

### 3. Duplicates per device (`core/clipboard.go:77` on `feat/16-pause-clipboard`)

```go
if top.Text == e.Text && top.sum == e.sum && top.Dir == e.Dir && top.Device == e.Device {
```

- Desktop copies have `Device` empty, so they still merge and gather `To`,
  as `TestLocalClipNamesItsDevices` expects
  (`core/clipboard_test.go:389-414` on `feat/16-pause-clipboard`).
- The same text from phone A, then phone B, now makes two entries. The
  desktop Chat page shows both, one per phone.

### 4. The phone: protocol and state

- `protocol/Identity.kt`: `const val FLUX_CLIPBOARD_HISTORY =
  "flux.clipboard.history"` in `Types`, and the type in both `INCOMING`
  and `OUTGOING` (`protocol/Identity.kt:122-147`), like `FLUX_SPEAKER`.
- `core/Device.kt`: `clipHistory: ClipHistoryAnswer? = null` next to
  `shortcuts` (`Device.kt:100`), and in `snapshot()` (`Device.kt:155-201`)
  `clipHistorySupported = Types.FLUX_CLIPBOARD_HISTORY in identity.incoming`
  and `clipHistory = clipHistory`, next to `shortcutsSupported`
  (`Device.kt:185-186`). The same two fields in `DeviceUi`
  (`core/Model.kt:145-147`).
- `core/Plugins.kt:151`, in `handle`:
  `Types.FLUX_CLIPBOARD_HISTORY -> ClipHistory.onPacket(core, d, p)`.

New `core/ClipHistory.kt`, in the style of `core/Shortcuts.kt`:

```kotlin
/** 1 entry of a computer's clipboard history. [sent] is true when this phone sent it. [time] is in ms. */
data class HistoryEntry(val id: String, val text: String, val sent: Boolean, val time: Long, val size: Int, val truncated: Boolean) {
    /** The link to open: only a whole http or https URL, see WebUrl. */
    val link: String? get() = if (truncated) null else WebUrl.of(text)
    /** False when the whole text is more than the phone's clipboard takes. */
    val copyable: Boolean get() = !truncated || size <= Plugins.MAX_CLIPBOARD_TEXT
}

/** The answer to a list request: [state] is "ok", "disabled" or "bad_request". */
data class ClipHistoryAnswer(val request: Long, val query: String, val state: String, val entries: List<HistoryEntry>, val more: Boolean)

/** flux.clipboard.history: this phone's entries of a computer's clipboard history. */
object ClipHistory {
    const val MAX_QUERY = 1024 // bytes, as fluxd
    private val ids = AtomicLong()
    /** The list request in flight or last answered, per device: id and query. */
    private val lists = ConcurrentHashMap<String, Pair<Long, String>>()
    /** The one copy in flight: device id and request id. A new copy replaces it. */
    private val copy = AtomicReference<Pair<String, Long>?>(null)

    fun list(d: Device, query: String): Boolean        // new id in lists, then d.send
    fun copy(d: Device, entryId: String): Boolean      // new id in copy, then d.send; on false clears it
    fun forget()                                       // clears copy; main thread
    fun parseList(p: Packet, query: String): ClipHistoryAnswer?  // pure; null for a full-text answer
    fun onPacket(core: FluxCore, d: Device, p: Packet)
}
```

- **Every answer is checked against what was asked, by device and id.**
  `onPacket` reads `request`, then:
  - it equals `copy`'s id **and** `copy`'s device is `d.id`: a full-text
    answer. It posts to the main thread, where it takes `copy` only if it
    still holds that pair (`compareAndSet` to null), so a `forget` or a
    newer copy that came first wins. On `ok` it calls
    `Plugins.putRemoteText(core, d.id, name, text)`
    (`core/Plugins.kt:186-196`), so clipboard sync doesn't send it back, and
    toasts "Copied to this phone" only when that returns true; it already
    toasts when the text is too large. On `too_large`, `not_found` or
    `disabled` a toast says why;
  - it equals `lists[d.id]`'s id: `d.clipHistory = parseList(p, query)`
    when that isn't null;
  - otherwise it is stale or from the wrong computer: dropped. A late list
    answer, an answer after a reconnect, and a forgotten copy never touch
    `d.clipHistory` or the clipboard.
- `parseList` converts `time` from seconds to ms, skips a malformed entry,
  and returns null for a body with `entry` or for `not_found` and
  `too_large`.
- `list` and `copy` take the id first, then send; a failed `d.send`
  (`Device.kt:135-141`) clears `copy` and the screen shows the computer
  as not reachable.
- The query is never cut: the screen stops input at `MAX_QUERY` UTF-8
  bytes (section 5), so `fluxd` never sees `bad_request` from this phone.

### 5. The phone: the History screen

- `ui/Nav.kt`: `const val CLIP_HISTORY_PAGE = "history"` next to
  `OMARCHY_PAGE` (`Nav.kt:57`).
- `ui/AppShell.kt:525-552`: `page == CLIP_HISTORY_PAGE ->
  ClipHistoryScreen(device, pop)`.
- `Nav.tabOf` (`ui/Nav.kt:135-139`): `CLIP_HISTORY_PAGE` goes with the
  `Tab.Chat` pages, so Back returns to the chat.
- `ui/ChatScreen.kt`, `ComputerMenu` (`ChatScreen.kt:258-283`): a
  "History" item with `Ic.search`, before "Clear chat", when
  `d.clipHistorySupported`. Sample computers don't list the type, so it
  doesn't show for them.

New `ui/ClipHistoryScreen.kt`, built like `OmarchyScreen`
(`ui/OmarchyScreen.kt:57-79`):

```
┌ History ─────────────────────────────┐
│ ← History            Studio PC        │
│ ┌─────────────────────────────────┐   │
│ │ 🔍 Search                        │   │
│ └─────────────────────────────────┘   │
│ https://omarchy.org/flux        Open  │
│ Received · 14:02                      │
│ yay -S flux-git                       │
│ Sent · 13:55                          │
│ …                                     │
│ Showing the 50 newest. Search to find │
│ older ones.                           │
└───────────────────────────────────────┘
```

- `ScreenHeader("History", d.name, onBack)`, then by state:
  - `!d.online`: `NotReachable(d, "The clipboard history")`;
  - `!d.clipHistorySupported`: `EmptyState` "Update Flux on <name>", as
    `OmarchyScreen` does (`ui/OmarchyScreen.kt:65-69`). No request goes out;
  - `disabled`: `EmptyState` "Clipboard is off for this phone", "On <name>,
    turn on Clipboard for this phone in Flux.";
  - otherwise the search field, which stays mounted while the area under
    it changes, so typing never loses the keyboard:
    - an answer for another query, or none yet: "Searching"; 10 s after the
      request without one: "No answer from <name>" with Retry;
    - `bad_request` or an unknown state: "<name> could not read the
      search" with Retry;
    - `ok` and no entries: "Nothing found" with a query, "No clipboard
      history with <name> yet" without;
    - `ok`: the list.
- The search field: `SearchField` from `ui/ChatsScreen.kt:181-215`, made
  `internal`, with a new `placeholder` parameter that defaults to today's
  "Search agents, computers, files" and sets both the hint and the
  content description (`ChatsScreen.kt:187,202`). History passes "Search
  clipboard history". Input past `MAX_QUERY` UTF-8 bytes is refused, so
  the field always shows what is searched.
- A list request goes out on open, 300 ms after the last keystroke, on
  Retry, and when `d.online` turns true
  (`LaunchedEffect(query, d.online, d.clipHistorySupported, retry)`). The
  screen shows `d.clipHistory` only when its `query` equals the field's
  text and its `request` is `ClipHistory`'s current one for this device.
- A row: the text, up to 3 lines, and a meta line "Sent · 14:02" or
  "Received · 14:02", the time by `timeText` (`ui/ChatScreen.kt:545`). A
  tap copies:
  - not truncated: `Plugins.putRemoteText` at once; toast "Copied to this
    phone" when it returns true. This also clears a copy in flight;
  - truncated and `copyable`: `ClipHistory.copy`; the answer copies
    (section 4);
  - not `copyable`: the row isn't clickable and has no Copy action; the
    meta line ends "· too large to copy".
- Open on a row whose `link` isn't null: `ACTION_VIEW` on that URL, in
  `runCatching` with a toast when no app opens it, as `Share.receive`
  builds a link intent (`core/Share.kt:112-116`). A cut text has no Open,
  even when it starts like a link: its meta line ends "· too long to open
  here, copy it" when its preview is a link (Owner decision 2).
- `more`: a last line "Showing the 50 newest. Search to find older ones."
- On dispose the screen calls `ClipHistory.forget()` on the main thread,
  so a late answer doesn't copy after the user left.
- TalkBack: each row is one item with its text and meta line; a copyable
  row has a "Copy" click label; Open is its own button.

### 6. Docs

- `docs/features.md`, after the clipboard limits (`docs/features.md:183-188`):
  a short "History on the phone" paragraph: the phone's own entries only,
  no images, pins or files, the 50 newest per search, the `clipboard`
  switch gates it.
- `docs/android.md`, in "Clipboard from another app"
  (`docs/android.md:178-183`): History in the chat menu, and that the
  phone lists `flux.clipboard.history` in both directions.

### 7. What doesn't change

- `searchClips`, `clipboard.search` and the IPC API.
- The desktop GUI, except that the duplicate fix can show a second entry.
- What `fluxd` keeps, and for how long.
- iOS, macOS, Windows: they don't list the type, so nothing changes there.
- The phone's chat log and Clear chat.

## Owner decision

1. **History while clipboard sync is paused (#16).** Proposed: History
   still answers. The pause holds back new automatic syncs; a paused copy
   never enters the history, so History can't show it. Hiding History
   during a pause would also hide entries the phone already had. The
   intent's constraint "respect its pause" is met by that, not by a check.
2. **Open on a cut link.** Proposed: no Open; such a link is over 1 KiB,
   its row says to copy it, and Copy fetches the whole text. This narrows
   the intent's "a link also has Open" to links of up to 1 KiB.

## Alternatives rejected

- **Reusing `clipboard.search` or `searchClips` on the wire.** It returns
  pins, image paths, every device and full text (intent, Facts).
- **A request and an answer type.** One type both ways is the pattern of
  `flux.speaker` and `flux.shortcuts`, and needs one capability check.
- **Search on the phone over a fetched list.** It misses text past the
  first 1 KiB and entries past the 50 newest.
- **A queue or worker for the handler** (`runContent`,
  `core/notifications.go:105`). The work is a clone, a scan of at most 500
  entries and one send; on the read loop it only delays this phone's own
  link.
- **A per-request byte budget instead of a count.** The count with a
  per-entry cut already bounds the answer.
- **Fixing the reconnect gap** (adding the phone to `To` in
  `sendConnectClipboard`). The intent accepted the gap; it changes what
  the desktop Chat page shows.
- **Removing a device's entries on unpair.** Accepted in the intent; it
  changes the desktop history.
- **Copying with `Android.setClipboard`.** Clipboard sync would send the
  text back as a new clip (`core/Plugins.kt:186-196`).
- **Fetch then Open for a cut link.** The phone can't tell from a 1 KiB
  preview that the rest is still one URL, and launching a browser after a
  network round trip from the core can hit Android's background-start
  limits. Copy fetches the whole text.
- **Rechecking the current link in the handler.** The answer only ever
  goes to this device's own link with this device's entries.
- **A map of copies in flight.** One slot is enough: a newer tap replaces
  an older one, so a slow answer never overwrites a newer copy.
- **A History entry on the Chats list for all computers.** Each history
  belongs to one computer; the chat's menu is per computer.

## Risks

- **#16 rebase.** If #16 changes before it merges, the line numbers in
  `core/clipboard.go` move. The plan cites them after the rebase.
- **A busy link.** A phone that floods requests slows only its own link's
  read loop. Each answer is at most about 1.6 MiB.
- **Text that changes in place.** None does: entries are values, and an
  edit is a new entry. A pin or delete between list and fetch gives
  `not_found`, which the toast reports.
- **The duplicate fix** shows two entries on the desktop Chat page where
  there was one; the time of phone A's entry no longer moves with phone
  B's copy.
- **Locale.** `strings.ToLower` and Kotlin don't share case rules for every
  script; the match runs only in `fluxd`, so the phone sees one rule.
- **A clipboard manager on the phone** sees the copied text, as with any
  copy from Flux.

## Verification

1. Go, `go test ./internal/...` (CI `build.yml`). New tests in
   `internal/core/history_test.go`:
   - `ownClips`: keeps `Device == phone` and `To` holding the phone; drops
     another phone's entry, a desktop copy without the phone in `To`, an
     image, a pinned entry and the history original of a pinned one;
   - `historyMatches`: lower-case substring over the text only; "pc" doesn't
     match a received entry by its "this pc" name; filter first, then 50,
     with `more`; a 2 KiB text comes cut at 1 KiB, valid UTF-8, with
     `size` and `truncated`;
   - `historyFull`: the full text up to 256 KiB; `too_large` above;
     `not_found` for another phone's id, a pinned id, an image id, and an
     unknown id;
   - `handleClipHistory` with `fakeStreamLink`: `disabled` with the switch
     off and no entries in the body; `bad_request` for both fields,
     neither, and a 1025-byte query; no answer at all to a peer that
     doesn't list the type; every answer echoes `request`; the JSON of an
     answer has no `image`, `to`, `device` or `pinned` key;
   - `handlePacket` with the type: no answer from an unpaired device or a
     link that is no longer the device's;
   - `addClipLocked`: the same text from two phones makes two entries;
     the existing `TestLocalClipNamesItsDevices` still passes.
   - `proto`: the type is in `Incoming` and `Outgoing`.
2. Android, `./gradlew :app:testDebugUnitTest :app:lintDebug
   :app:assembleDebug` (CI). New `ClipHistoryTest`:
   - `parseList` reads `ok` with entries and `more`, `disabled`, and
     ignores unknown fields; skips a malformed entry; turns seconds into
     ms; returns null for a full-text answer;
   - `HistoryEntry.link`: a whole http or https URL only; null when cut or
     another scheme; `copyable` false above 256 KiB;
   - stale answers: a list answer for an older request, or from another
     computer with the same id, leaves `clipHistory` unchanged; a copy
     answer after `forget`, after a newer copy, or from another computer
     copies nothing;
   - the query limit: 1024 bytes counted in UTF-8, a 4-byte character at
     the edge is refused whole.
3. On the phone, with p620 and the Pixel (not p510):
   1. Copy two texts on the computer, send one from the phone, share a
      link from the computer. Open History: all four, newest first, with
      Sent and Received right.
   2. Search part of one text: only it shows. Search "pc": no received
      entry shows by name.
   3. Tap a text: it is on the phone's clipboard, and the computer's
      history gets no new entry from the phone (no echo).
   4. Open on the link opens the browser.
   5. A 100 KiB text from the computer: it shows cut; Copy puts the
      whole text. A text over 256 KiB from the phone, if the phone sends
      one: it shows cut, "too large to copy" (the Go test covers
      `too_large` either way).
   6. With a second paired device, its clip doesn't show on this phone.
   7. Turn the phone's clipboard switch off on the computer: History says
      Clipboard is off.
   8. Pause clipboard sync on the computer (#16), copy a text: History
      doesn't show it; earlier entries stay.
   9. Turn off Wi-Fi on the phone with History open: it says not
      reachable; back on, the list returns.
   10. TalkBack: a row reads its text and Sent or Received; Copy and Open
       are reachable; a too-large row offers no Copy.

## Review changes

Codex and agy reviewed the first draft, read only. Each finding was checked
against the code.

| Finding | Source | Verdict | Change |
| --- | --- | --- | --- |
| A copy answer matched by id alone; another computer could answer it | Codex (high), agy (high) | Confirmed: ids are global, the map's device was unused | The answer must match device and id |
| Stale or forgotten answers overwrite `d.clipHistory` | Codex, agy (high) | Confirmed: every non-copy answer was stored | `lists` per device; only the current id is stored; full answers never parse as lists |
| An older copy can overwrite a newer one; copies outlive reconnects | agy (high), Codex | Confirmed | One copy slot; a new tap replaces it; taken on the main thread with `compareAndSet` |
| `fluxd` doesn't check the peer's capability; the screen doesn't either | Codex, agy | Confirmed, intent constraint | `dev.accepts` in the handler; "Update Flux" state on the phone |
| `SearchField` hard-codes "Search agents, computers, files" | Codex, agy | Confirmed, `ChatsScreen.kt:187,202` | `placeholder` parameter |
| The field could unmount while searching; `bad_request` had no state; Retry didn't restart | agy | Agreed | Field stays mounted; states and effect keys spelled out |
| A cut query searches something other than what is shown | Codex | Agreed | Input stops at 1024 bytes; never cut silently |
| "Copied" toast even when `putRemoteText` refuses | agy | Confirmed, `Plugins.kt:186-190` | Toast only on true |
| `time` in seconds vs Android's ms | agy | Confirmed, `ChatScreen.kt:545` | Converted in `parseList` |
| A too-large row still had a Copy label; no reason for missing Open | Codex, agy | Agreed | Not clickable; meta line says why |
| Unpaired / stale-link and stale-answer tests missing | agy, Codex | Agreed, intent outcome | Tests added |
| `toUi` is `snapshot()`; AppShell lines; #16 also changes `clipboard_test.go` and `docs/features.md`; android.md section; link intent cite | Codex, agy | Confirmed | Corrected |
| JSON size of 50 previews up to ~310 KiB | Codex, agy | Confirmed | Corrected; still far under 16 MiB on both sides |
| `Nav.tabOf` sends the page to Control | agy | Confirmed, `Nav.kt:135-139` | Added to `Tab.Chat` |
| `isDemo` branch unreachable | agy | Confirmed: samples don't list the type | Dropped |
| `ACTION_VIEW` can throw without a browser | agy | Agreed | `runCatching` with a toast |
| Clone before `disabled`/`bad_request`; `ToLower` with an empty query | agy (low) | Agreed | Checks before the clone; empty query skips the match |
| Recheck pairing and current link in the handler | Codex | Not adopted | The answer goes only to this device's own link with its own entries; listed under Alternatives |
| Owner decision 1: "already had" overstates | Codex | Confirmed, `clipboard.go:368,377-391` on #16 | Reworded to "meant to get" |
| Owner decisions 1 and 2 | Codex, agy | Both agree | Decision 2 now names the intent exception |
