---
status: approved
issue: 3
author: olafkfreund
---

# Intent: Search the recent clipboard sends on the phone

## Problem

The phone can't look back at what went between it and a computer.
- The issue (mirrored from upstream `bjarneo/flux#52`) asks for a screen on
  the phone with the recent sends and a search field, to copy an entry back
  or open a link, without going to the computer.
- `fluxd` already keeps the history, and the desktop Chat page shows it.
  The phone keeps almost none of it.

### Facts

Go paths are under `internal/`. Android paths are under
`android/app/src/main/java/org/omarchy/flux/`.

- **The desktop history** is `d.clipboard`, a list of `ClipEntry`
  (`core/clipboard.go:44-72`).
  - It is memory only. It keeps `ClipLimit()` entries: 50 by default, up to
    500 by config (`config/config.go:138-153`, `docs/ipc.md:245`), at most
    10 images, and about 16 MiB of text; the newest entry always stays
    (`core/clipboard.go:28-41,94-117`).
  - A phone's clip can be up to 1 MiB (`desktop.MaxClipboardText`,
    `core/clipboard.go:382`). A text this computer sends is at most
    256 KiB (`maxSentText`, `core/share.go:948`).
  - An image entry has `Image`, a path on the desktop, and no text
    (`core/clipboard.go:54-55,157`). The images live in the runtime folder,
    or a temp folder when headless (`core/daemon.go:317,343`).
  - Each entry has a direction (desktop's view: `in` came from a phone,
    `out` went to one), a device and a source:
    - a clip, share or link from a phone: `Dir: "in"`, `Device` = that
      phone (`core/clipboard.go:415,503`, `core/share.go:385,939`);
    - a clip, share or link this computer sent to one phone: `Dir: "out"`,
      `Device` = that phone (`core/clipboard.go:567`, `core/share.go:928`);
    - a desktop copy that clipboard sync sent: `Dir: "out"`, `Device` empty,
      `To` = the phones connected at the copy (`core/clipboard.go:251-257`).
      `To` is who it was meant for, not a receipt. A copy larger than
      256 KiB is kept with an empty `To`. With `auto_clipboard` off, desktop
      copies aren't kept. A phone that connects later gets the last copy
      (`sendConnectClipboard`, `core/clipboard.go:333-341`) but isn't added
      to `To`.
  - So the history holds every paired device's entries, not one phone's.
  - Shares and links are kept even with a device's `clipboard` switch off
    (`core/share.go:385,928,939`).
  - **Duplicates merge across devices.** A new entry with the same text and
    direction as the newest one only updates its time and adds to its `To`
    (`core/clipboard.go:74-88`). `Device` and `Source` stay those of the
    first. So when phone A and then phone B send the same text, the entry
    stays phone A's.
  - Unpairing doesn't remove a device's entries (`core/pairing.go:202`).
- **Pinned snippets** are a second list, `d.snippets`. They are saved to
  disk under the data folder (`core/daemon.go:381`, `core/snippets.go:13-34,81`),
  up to 100, and can hold images. Pinning copies an entry into the
  snippets with the same id; the history keeps the original, unpinned
  (`core/snippets.go:45-63`). `clipEntriesLocked` hides that original by id
  (`core/snippets.go:119-135`).
- **`clipboard.search`**, the local IPC call (`core/api.go:348`,
  `docs/ipc.md:398`), runs `searchClips` (`core/snippets.go:137-148`). It
  - matches `Text + " " + DeviceName`, lower case, as a substring.
    `DeviceName` is "this pc" on every `out` entry;
  - searches the pins and the history together, under `d.mu`;
  - returns whole entries: full text, image paths, pins, every device.

  The state event cuts each text at 1 KiB (`clipPreviewLocked`,
  `core/clipboard.go:172-182`); `searchClips` doesn't.
- **`CopyClip`** finds an id in pins and history, any device, and puts it
  on the **desktop** clipboard (`core/clipboard.go:601-618`).
- **Transfers** are a separate list of 100 plus running ones
  (`core/share.go:27-29,71-89`). Each holds `Path`, a path on the desktop.
  Camera scans are transfers too. The issue mentions transfers but its
  wishes are text and links.
- **Packet limits.** A paired link reads lines up to 16 MiB
  (`proto/packet.go:18`, `lan/link.go:69`). A full history with long texts
  doesn't fit in one packet. A send can block up to 30 s
  (`lan/link.go:133-145`).
- **The request shape the issue points to** is `flux.mpris.request`: the
  phone asks, `fluxd` answers on the same link, only while it is the link of
  a paired device (`core/media.go:25-60`, `core/handlers.go:108`). It
  carries no request id.
- **Capabilities.** Each side declares the types it receives and sends:
  `proto.Incoming` and `Outgoing` (`proto/identity.go:141-166`), and the
  phone's lists (`protocol/Identity.kt:122-147`). In `fluxd`, `dev.accepts`
  and `dev.supports` check the peer (`core/device.go:247-251`); `send`
  doesn't check them for you.
- **Permissions.** Each device has a `clipboard` switch
  (`core/policies.go:14-17`). Incoming clips from a device with it off are
  dropped without an answer (`core/handlers.go:86,91`). The runtime pause
  of #16 isn't merged.
- **The phone today.** The chat log `SendLog` keeps 60 finished entries plus
  running files, in memory, each text cut at 400 characters
  (`core/SendLog.kt:64-82,135-143`). It keeps only **one** clip from any
  computer: a new one replaces it (`addClipIn`, `SendLog.kt:97-99`). The
  Chats search finds a computer by its newest matching entry, not the
  entries themselves (`core/ChatList.kt:119-122`). So an older clip is gone
  as soon as a newer one arrives.
- **The menu.** The chat's overflow menu is `ComputerMenu`
  (`ui/ChatScreen.kt:258-283`), which has Clear chat since #17. Clear chat
  clears the phone's log and maybe its clipboard, not `fluxd`'s history
  (`ui/ChatScreen.kt:286-295`).
- **Copy back.** The chat's Copy uses `Plugins.putRemoteText`
  (`ui/ChatScreen.kt:748-751`), which caps the size and marks the write as
  Flux's own, so clipboard sync doesn't send it back (`core/Plugins.kt:186-196`).
  A plain `Android.setClipboard` (`core/Android.kt:140`) would echo.
- **Links.** `fluxd` opens only http or https URLs with a host (`webURL`,
  `core/share.go:348-357`).

## Proposed outcome

- A computer's chat has **History** in its overflow menu. It opens a screen
  with that computer's recent clipboard entries for this phone, newest
  first.
- Each row shows the text or link, sent or received (from the phone's
  view), and the time.
- A search field filters as the user types (open question 6).
- Tapping an entry puts its full text on the phone's clipboard through
  `putRemoteText`, so it doesn't sync back, with a toast. An entry that
  holds an http or https URL also has Open, on the full URL only.
- An entry too large to fetch shows its preview; Copy and Open say why they
  aren't there. Text is never silently cut.
- The phone sees only entries of its own (open question 1). No pins, no
  images, no transfers, no other device's entries (open questions 2-4).
- The screen tells apart: computer offline, clipboard switch off, nothing
  found, and an entry gone since the list loaded.
- An older `fluxd` without the new message: History doesn't show.
- Clear chat stays local; History is the computer's list and keeps its
  entries.
- Tests cover the filter (other devices, pins and pinned originals, images
  left out), duplicate texts from two phones, the caps, the fetch by id,
  the permission check, an unpaired link, and stale answers.

## Affected users and systems

- Android users with a paired computer.
- `fluxd`: a new packet type in `internal/proto`, a handler in
  `internal/core` next to `searchClips`, the duplicate check in
  `addClipLocked` (open question 9), tests, `docs/` for the packet.
- Android: `protocol/Identity.kt`, the packet handling, a History screen,
  `ui/ChatScreen.kt` (the menu item), tests.
- Not affected: the desktop GUI, the IPC API, iOS, macOS, Windows. `fluxd`
  never sends the new packet to a peer that doesn't declare it.

## Constraints

- A dedicated handler, wire-safe. Never send `searchClips` or a `ClipEntry`
  as it is: no `Image` path, no `To` list of other devices, no pins, no id
  that is also a snippet's.
- Answer only a paired device on its current link. With its `clipboard`
  switch off, answer with "disabled" and no entries. Once #16 lands, also
  respect its pause.
- Fetch by id runs the same checks as the list: this phone's entry, in the
  history, not pinned, not an image. An id alone grants nothing; an unknown
  id answers "not found". Never reuse `CopyClip`.
- Bounded: a cap on entries, on bytes per entry, and on the query.
- Copy the entries under `d.mu`, then match, encode and send without it.
- Each answer names its request, so the phone drops a stale one after more
  typing, leaving the screen, or a reconnect.
- Declare the type on both sides in both directions, and check the peer
  before sending.
- No new storage on either side. The history stays memory only; making it
  survive a `fluxd` restart is out of scope, as the issue says.
- No new Android permission.

## Open questions

1. **Which entries does a phone see?** Proposed: only those where `Device`
   is the phone, or `To` holds it. These are texts the phone sent or was
   sent, so the list adds no content the phone wasn't meant to have. Known
   gaps, accepted to keep the scope small: a desktop copy the phone got on
   reconnect isn't listed (not in `To`); `To` means meant for, not
   received; a phone unpaired and paired again under the same id sees its
   old entries.
2. **Pinned snippets?** Proposed: no, including the history original of a
   pinned entry. They are kept on disk on purpose and aren't "recent
   sends".
3. **Images?** Proposed: no, text and links only. An image entry has only a
   desktop path; sending the image is a transfer, not a list.
4. **Transfers?** Proposed: no. Received files are already in the phone's
   Downloads, and the issue's wishes (copy back, open a link) are about
   text. A transfer list can be a separate issue.
5. **Size caps?** Proposed: the answer holds the 50 newest matching entries
   of this phone (filter first, then cut), each text cut at 1 KiB
   (`maxClipPreview`) with its full size. A tap on a cut entry asks for
   that one entry's full text by id, up to 256 KiB (`maxSentText`). A
   larger entry (a phone's clip can be 1 MiB) is preview only. The query is
   capped at 1 KiB.
6. **Search where, on what?** Proposed: on the computer, so a match past the
   first 1 KiB or past the 50 newest still counts; the phone sends the
   query after a short pause in typing. Match the text only, lower case, as
   a substring, as `searchClips` does. Leave out `DeviceName`: it is
   "this pc" or the phone's own name, so "pc" would match every received
   entry.
7. **Does `auto_clipboard` off hide History?** Proposed: no. With question 1
   the phone only sees its own entries, and manual sends are kept with
   `auto_clipboard` off. The device's `clipboard` switch is the gate.
8. **Shares and links with the `clipboard` switch off.** They are kept in
   the history anyway. Proposed: the switch off hides all of History, as
   in the constraints; no change to what is kept.
9. **Duplicates across devices.** Proposed: fix in this issue by adding
   `top.Device == e.Device` to the merge check in `addClipLocked`
   (`core/clipboard.go:77`). One condition; desktop copies (`Device` empty)
   still merge as today. Without it, phone B's clip that matches phone A's
   is listed for A and not for B. Side effect: the desktop chat shows such
   a clip twice, once per phone.
10. **Test:** Go unit tests for the handler, the fetch by id and the merge
    check; Android unit tests for the packet and stale-answer handling.
    Then on the phone: sync a few clips both ways, share a link, open
    History, search, copy one back (it doesn't sync back), open the link;
    check a second paired device's clip doesn't show; turn the clipboard
    switch off and see the screen say so.

## Review changes

Codex and agy reviewed the first draft, read only. Each finding was checked
against the code.

| Finding | Source | Verdict | Change |
| --- | --- | --- | --- |
| Duplicate merge keeps the first `Device`; a second phone's same text is lost or misattributed | Codex (high), agy (high) | Confirmed, `clipboard.go:74-88` | Fact added; Q9 proposes a one-condition fix |
| Fetch by id must repeat every check; `CopyClip` searches pins and all devices | Codex (high), agy (high) | Confirmed, `clipboard.go:601-618` | Constraint added |
| Copy back with `setClipboard` echoes the clip to the computer | agy (high) | Confirmed, `Plugins.kt:186-196`, `ChatScreen.kt:748-751` | Fact; outcome uses `putRemoteText` |
| Silence can't tell the screen the switch is off | Codex, agy | Confirmed, `handlers.go:86,91` | Answer "disabled"; outcome lists the states |
| Phone clips up to 1 MiB exceed the 256 KiB fetch cap | Codex, agy | Confirmed, `clipboard.go:382`, `desktop/clipboard.go:27` | Fact; Q5 makes them preview only |
| Open on a cut preview or a non-web scheme | Codex, agy | Confirmed, `share.go:348-357` | Open only on a full http/https URL |
| Pinning leaves an unpinned original in the history | Codex | Confirmed, `snippets.go:45-63,119-135` | Fact; constraint and Q2 exclude it |
| `To` is intent, not receipt; reconnect send isn't recorded | Codex, agy | Confirmed, `clipboard.go:251-257,333-341` | Fact; Q1 lists accepted gaps |
| Matching `DeviceName` ("this pc") gives false hits | agy | Confirmed, `clipboard.go:256,567`, `share.go:928` | Q6 matches text only |
| Stale answers, query cap, work under `d.mu` | Codex, agy | Agreed; `searchClips` holds `d.mu` | Constraints added |
| Capabilities need both lists on both sides | Codex, agy | Confirmed, `identity.go:141-166`, `device.go:247-251` | Fact and constraint reworded |
| 50-entry cap must apply after filtering | Codex | Agreed | Q5 |
| Clear chat doesn't clear `fluxd` history | Codex, agy | Confirmed, `ChatScreen.kt:286-295` | Fact and outcome |
| Unpair keeps the device's entries | agy | Confirmed, no clipboard change in `pairing.go:202` | Fact; accepted in Q1 |
| Shares kept with the switch off | agy | Confirmed, `share.go:385,928,939` | Q8 |
| Line ranges, 60/100 exceptions, newest-entry text exemption, headless folder, "one clip across computers" | Codex, agy | Confirmed | Facts corrected |
| Q8 tested a phone-side filter that Q6 removes | agy | Confirmed | Q10 tests stale answers instead |
