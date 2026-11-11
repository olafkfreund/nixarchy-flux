---
status: draft
issue: 51
intent: intent/2026-11-11-51-chat-file-actions.md
---

# Spec: Open and Copy on finished file cards in the chat

Approved decisions from the intent: sent files get the actions too; Open and
Copy are icons placed before "Show in folder"; the image preview stays
inert; the UI sends only the transfer ID.

## Design

All of it is in `gui/qml/components/ChatEntry.qml`, the card that the Qt app
and the Omarchy shell plugin both load.

1. **When.** A new property
   `readonly property bool finished: !!file && file.state === "done" && !!file.path`.
   It is true for a received and a sent file alike, which matches the
   daemon's `transferPath` (`internal/core/share.go:235-251`: state `done`, a
   path, the file still on disk; no direction check). Every other state, and
   a `done` entry without a path, shows no new action.

2. **The actions.** Two `IconAction`s in the `actions` row (lines 157-215),
   after the clip actions and before the `Row` of word actions, so the order
   on a received card is Open, Copy, Show in folder:

   | Icon | Tip | On click |
   | --- | --- | --- |
   | `open` | "Open" | `root.view.call("transfer.open", { id: root.file.id })` |
   | `copy` | "Copy" | `root.copyFile()` |

   Both use `ink: root.dim` and `visible: root.finished`, as the clip actions
   do. The icons `open` and `copy` already exist (lines 166, 173). The
   comment at line 193 ("The actions of a file are words …") gets one more
   line: Open and Copy are icons, like the same actions of a clip.

3. **Copy feedback.** A function next to `copy()` (line 94):

   ```qml
   function copyFile() {
     var v = view
     v.call("transfer.copy", { id: file.id }, function () { v.toast("Copied to the clipboard") })
   }
   ```

   `view.call` (`gui/qml/FluxView.qml:167`) calls the callback only on
   success and turns an error, such as `not_found` for a deleted file, into
   the usual toast. Open shows no toast: the app opening is the feedback.

4. **Params.** Both calls pass `{ id }` only, never `file.path`.

5. **Width.** The card's `natural` width already adds
   `actions.implicitWidth` (line 87), so a wide window makes the card wider
   for the two icons (2 × 26 px plus spacing). On a card narrower than its
   content the header row has no wrapping; two icons add about 56 px, far
   less than two more words would. This is checked on a snapshot (below)
   rather than changed.

6. **Test.** A new `TestCase` "ChatEntry" in `gui/tests/tst_views.qml`
   creates a `ChatEntry` directly with a stand-in `view` (a `QtObject` with
   `devName`, a `call(method, params, cb)` that records the request and
   runs `cb`, and a `toast(text)` that records the text), finds the actions
   by their `tip`, and emits their `clicked` signal:
   - a received `done` file with a path: Open and Copy are visible; Open
     records `transfer.open` with params exactly `{ id: "t3" }`; Copy
     records `transfer.copy` with `{ id: "t3" }` and the toast "Copied to
     the clipboard";
   - a sent `done` file with a path: both visible;
   - `active`, `queued`, `waiting`, `failed`, `canceled`, and `done`
     without a path: neither visible;
   - a clip entry: the file actions are not visible (the clip's own Copy
     still is).

No change to the mock backend (it answers unknown methods with success,
`gui/qml/tools/MockBackend.qml:68`), the fixture (finished `t3` and `t4`
have paths, `fixture.json:284-302`), Go, or any other platform.

## Alternatives rejected

- **Word actions "Open" and "Copy".** Three words with 14 px spacing do not
  fit a narrow card next to the state word and the time (intent, question 2).
- **Bring back a Files tab** as in the upstream PR. The chat replaced it;
  a second list of the same transfers adds a page for two buttons.
- **Open the file from QML** with `Qt.openUrlExternally(file.path)`, like
  "Show in folder". It works on the Qt app but sends a path from the UI and
  skips the daemon's check that the transfer is finished and still exists;
  the reviewed backend method was made for this.
- **Make the image preview clickable.** Declined in the intent.

## Risks

- **Narrow shell popup.** If the Omarchy shell plugin shows a card narrower
  than its header, the icons could touch the time. Mitigation: the snapshot
  check below; if it overlaps, the plan adds the smallest fix (for example
  hiding the time first).
- **CI does not run the QML tests.** `build.yml` runs `make vet test` (Go)
  only, so the view test runs here: `qmltestrunner` from Nix's
  `qt6.qtdeclarative` (the Makefile's default path is Arch's). The suite
  passes here today (85 passed).
- **The clipboard and the default app** are the daemon's (`wl-copy`,
  `xdg-open`), merged and unit-tested with #27, not exercised end to end
  here; the owner checks them on the desktop.

## Verification

1. QML view tests, all passing, including the new "ChatEntry" case:
   `QT_QPA_PLATFORM=offscreen QML_XHR_ALLOW_FILE_READ=1 <qtdeclarative>/bin/qmltestrunner -input gui/tests`.
2. `nix build .#flux-gui`, then `QT_QPA_PLATFORM=offscreen flux-gui --snapshot <dir>`
   (`gui/qml/tools/Snapshot.qml`): `01-chat.png` shows Open and Copy on the
   finished `t3` (received) and `t4` (sent) cards and not on the others, and
   `06-chat-narrow.png` (560 px window) shows them with no overlap.
3. `nix flake check path:.` passes.
4. Owner, on the desktop: Open launches the default app; Copy on a
   screenshot pastes an image, and Copy on a non-image pastes into a file
   manager; both in the Qt app and the Omarchy shell plugin.
