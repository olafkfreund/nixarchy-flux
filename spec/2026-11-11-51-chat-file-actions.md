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

5. **Width.** A file card's `natural` width is at least 444 px
   (`content = file ? 420`, line 87), and the two icons (2 × 26 px plus
   spacing, about 56 px) fit in that. It is narrower only when the chat is:
   `maxWidth` goes down to 280 px (`gui/qml/pages/Chat.qml:317`), which the
   Qt app reaches at its 360 px minimum window width
   (`gui/app/qml/Main.qml:12`); the Omarchy shell window is at least 900 px
   wide (`gui/omarchy/Panel.qml:81`). At 280 px a received finished card's
   header needs about 280 px (state word and time about 120, gap 16, icons
   56, "Show in folder" about 85) of its 256 px, so the actions would cover
   the time. Fix: a property
   `readonly property bool crowded: meta.implicitWidth + 16 + actions.implicitWidth > col.width`;
   when it is true, the `actions` row moves to its own line under `meta`
   (anchored to the right, below it) and the header item's height is the
   sum of both rows. When it is false, the header is exactly as today.

6. **Order of the comment and the word action.** The comment at line 193
   ("The actions of a file are words …") is replaced by one above the two
   new icons: Open and Copy are icons like the same actions of a clip; the
   other file actions are words, because each one has its own state.
   "Show in folder" reuses the new property:
   `visible: root.finished && !root.mine`.

7. **Test.** In `gui/tests/tst_views.qml`. The two icons get `objectName`s
   `fileOpen` and `fileCopy` (the clip's Copy also has `tip: "Copy"`, and
   `findBy` ignores visibility, so a lookup by tip would find the clip's
   hidden one).
   - A new `TestCase` "ChatEntry" creates a `ChatEntry` directly, parented
     to `top`, with `width: 500` (it has no implicit width) and a stand-in
     `view`: a `QtObject` with `devName`, a `call(method, params, cb)` that
     records `{method, params}` and runs `cb` only when given, and a
     `toast(text)` that records the text. It emits the icons' `clicked`
     signal (mouse and keyboard handling are `IconAction`'s, unchanged):
     - a received `done` file with a path: both visible; Open records
       `transfer.open` with params exactly `{ id: "t3" }` and no toast;
       Copy records `transfer.copy` with exactly `{ id: "t3" }` and the
       toast "Copied to the clipboard";
     - a sent `done` file with a path: both visible;
     - `active`, `queued`, `waiting`, `failed` and `canceled`, each with a
       path (the daemon sets it before the end), and `done` without a path:
       neither visible;
     - a clip entry: neither visible;
     - with `width: 280`: `crowded` is true and the actions row's `y` is
       below `meta`; with `width: 500` it is false.
   - In the existing `TestCase` "Errors" (with `FluxView` and the mock
     backend, `tst_views.qml:673`): with `mock.failures["transfer.copy"]`
     set to `{ code: "not_found", message: "… no longer exists" }`, Copy on
     a finished card through the real `view.call` shows that message as a
     toast and not "Copied to the clipboard".

No change to the mock backend (it answers unknown methods with success,
`gui/qml/tools/MockBackend.qml:68,142`), the fixture (finished `t3` and
`t4` have paths, `fixture.json:283-306`), Go, or any other platform.

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
- **Hide the time, or elide the state word, on a crowded card.** Loses
  information the card has today; moving the actions to a second line keeps
  all of it.

## Risks

- **The narrow Qt window.** Covered by `crowded` (design 5) and its test;
  at widths between, the header has the same single row as today.
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
2. `nix build .#flux-gui`, then
   `QT_QPA_PLATFORM=offscreen flux-gui --snapshot <dir>` and the same with
   `FLUX_SNAPSHOT_SIZE=360x760` (`gui/qml/tools/Snapshot.qml:13`):
   `01-chat.png` shows Open, Copy and Show in folder on the received `t3`;
   `08-chat-top.png` shows Open and Copy on the sent `t4` and nothing new on
   `t5` (`done`, no path); the 360 px set shows the actions under the state
   word, not over it.
3. `nix flake check path:.` passes.
4. Owner, on the desktop: Open launches the default app; Copy on a
   screenshot pastes an image, and Copy on a non-image pastes into a file
   manager; both in the Qt app and the Omarchy shell plugin.
