---
status: draft
issue: 51
author: olafkfreund
---

# Intent: Open and Copy on finished file cards in the chat

## Problem

Merging #27 brought in only its backend: the daemon methods `transfer.open`
and `transfer.copy` (`internal/core/share.go:234-290`, `api.go:337-340`).
They take a transfer ID and act only on a `done` transfer whose file still
exists. Nothing in the desktop UI calls them. The upstream PR put its buttons
in a Files tab that this tree no longer has; finished transfers now appear as
cards in the chat (`gui/qml/components/ChatEntry.qml`). A received file card
offers only "Show in folder", and a sent file card offers nothing, so to open
a received screenshot or paste it into another app, the user has to find it
in a file manager.

## Proposed outcome

- A finished file card in the chat (state `done`, with a path) has two more
  actions: **Open** (the default app) and **Copy** (an image goes on the
  clipboard as an image; any other file as a file URI that pastes into a
  file manager). Copy shows the toast "Copied to the clipboard".
- This applies to received and sent files. "Show in folder" stays for
  received files.
- Waiting, queued, active, failed and canceled cards are unchanged.
- The Qt app and the Omarchy shell plugin both get it, because both load the
  shared QML; the snapshot mock backend answers the two methods so
  `make snapshot` keeps working.

## Affected users and systems

- Desktop users of the Qt window and the Omarchy shell plugin.
- Files: `gui/qml/components/ChatEntry.qml`, `gui/qml/tools/MockBackend.qml`.
  No Go, protocol, Android, iOS or macOS change.

## Constraints

- The UI passes only the transfer ID; it never sends a path (the reason
  upstream asked for the rework).
- Keep the card's existing style: file actions are words (`TextAction`), as
  the comment in `ChatEntry.qml` says.
- An error from the daemon (file deleted since) shows as the usual toast
  from `view.call`.

## Open questions

1. Sent files: offer Open and Copy on a sent card too (proposed: yes, the
   daemon allows any `done` transfer, and the file is on this computer).
2. Wording: "Open" and "Copy" as words next to "Show in folder" (proposed),
   or icon buttons like the clip actions.
