---
status: draft
issue: 51
author: olafkfreund
---

# Intent: Open and Copy on finished file cards in the chat

## Problem

Merging #27 brought in only its backend: the daemon methods `transfer.open`
and `transfer.copy` (`internal/core/share.go:234-281`,
`internal/core/api.go:337-340`). They take a transfer ID and act only on a
`done` transfer whose file still exists. Nothing in the desktop UI calls
them. The upstream PR put its buttons in a Files tab that this tree no longer
has; finished transfers now appear as cards in the chat
(`gui/qml/components/ChatEntry.qml`).

The desktop notification for a received file already has Open and Show in
folder (`share.go:513-516`), and a received screenshot is copied to the
clipboard when auto-clipboard is on (`share.go:493-504`). Once the
notification is gone, though, the chat is the only lasting place for the
file: a received card offers only "Show in folder", and a sent card offers
nothing. Opening an earlier file again, or copying it into another app,
means going through a file manager.

## Proposed outcome

- A finished file card in the chat (state `done`, with a path) also offers
  **Open** (the default app) and **Copy**. Copy follows the daemon's
  existing rule (`copyFile`, `share.go:271-281`): an image up to 16 MiB whose
  content is detected as an image goes on as image data; any other file,
  including a larger image, goes on as a file URI that pastes into a file
  manager. On success the card shows the toast "Copied to the clipboard";
  a daemon error (the file was deleted) shows as the usual error toast.
- Waiting, queued, active, failed and canceled cards are unchanged. The
  notifications are unchanged.
- The Qt app and the Omarchy shell plugin both get it, because both load the
  shared QML.
- The actions stay readable on a narrow card, without covering the state
  word and the time.
- A QML view test (`gui/tests/tst_views.qml`, mock backend) checks that the
  actions call the two methods with only the transfer ID, and only on a
  finished card.

## Affected users and systems

- Desktop users of the Qt window and the Omarchy shell plugin.
- `gui/qml/components/ChatEntry.qml` and `gui/tests/tst_views.qml`; the
  snapshot fixture (`gui/qml/tools/fixture.json`) already has finished
  transfers with a path, so `make snapshot` shows the new actions. The mock
  backend already answers unknown methods with success, so it needs no
  change.
- No Go, protocol, Android, iOS or macOS change.

## Constraints

- The UI passes only the transfer ID; it never sends a path (the reason
  upstream asked for the rework).
- Follow the card's existing look and the clip actions' wording.

## Open questions

1. Sent files: offer Open and Copy on a finished sent card too? Proposed:
   yes. The daemon allows any `done` transfer (`transferPath` does not check
   the direction), and a sent file is on this computer.
2. Style: words (`TextAction`, like "Show in folder"; the comment at
   `ChatEntry.qml:193` says file actions are words) or icons (`IconAction`
   "open" and "copy", like the clip actions)? Proposed: icons for Open and
   Copy, placed before "Show in folder". Three words with 14 px spacing do
   not fit a narrow card next to the state word and time; two small icons
   do.
3. Clicking the image preview of a file card: keep it inert (proposed), or
   make it Open?
