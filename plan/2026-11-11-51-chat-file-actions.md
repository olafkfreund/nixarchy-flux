---
status: approved
issue: 51
spec: spec/2026-11-11-51-chat-file-actions.md
coders:
  1: codex   # small, layout-sensitive QML: visibility rule, crowded header move
  2: codex   # small QML tests on the same member, warm context from step 1
reviewers:
  - codex    # diff review against this plan before the PR
  - agy      # second, independent diff review
---

# Plan: Open and Copy on finished file cards in the chat

Branch `feat/51-chat-file-actions`, issue #51. Step 3 (verification, reviews,
PR) is the session model's.

## Approved decisions (from the intent and spec)

- D1 `finished`: `readonly property bool finished: !!file && file.state === "done" && !!file.path`
  in `ChatEntry`. Received and sent alike; every other state, and `done`
  without a path, shows no new action.
- D2 Two `IconAction`s in the `actions` row, after the clip actions and
  before the `Row` of word actions (order on a received card: Open, Copy,
  Show in folder), each `ink: root.dim`, `visible: root.finished`:
  - `objectName: "fileOpen"`, `icon: "open"`, `tip: "Open"`,
    `onClicked: root.view.call("transfer.open", { id: root.file.id })`, no toast;
  - `objectName: "fileCopy"`, `icon: "copy"`, `tip: "Copy"`,
    `onClicked: root.copyFile()`.
- D3 `copyFile()` next to `copy()`:
  `var v = view; v.call("transfer.copy", { id: file.id }, function () { v.toast("Copied to the clipboard") })`.
  `view.call` (`gui/qml/FluxView.qml:167`) runs the callback only on
  success and toasts the error otherwise.
- D4 Only `{ id }` is sent, never `file.path`.
- D5 `readonly property bool crowded: meta.implicitWidth + 16 + actions.implicitWidth > col.width`.
  When true, `actions` sits on its own line under `meta`, right-aligned,
  and the header item's height is `meta.implicitHeight + spacing +
  actions.implicitHeight`. When false, the header is exactly as today
  (both vertically centred in one row of `Math.max(...)` height).
- D6 The comment at `ChatEntry.qml:193-194` is replaced by one above the
  two new icons: Open and Copy are icons like the same actions of a clip;
  the other file actions are words because each has its own state.
  "Show in folder" becomes `visible: root.finished && !root.mine`.
- D7 No change to the mock backend, the fixture, Go, or other platforms.

## Review corrections (Codex and agy plan review — approve or strike)

| # | Draft said | Plan does | Why |
| --- | --- | --- | --- |
| R1 | (agy) switch `meta`/`actions` between `verticalCenter` and `top`/`bottom` with conditional anchors | no vertical anchors on either row; `y` bindings: `meta` `y: root.crowded ? 0 : (parent.height - height) / 2`, `actions` `y: root.crowded ? parent.height - height : (parent.height - height) / 2`; `actions` keeps `anchors.right` | swapping two vertical anchors at runtime can briefly hold both, which sets an explicit height that is not reset, so a resize would leave the icons over the state word. Not reproduced here; `y` bindings avoid it outright |
| R2 | (Codex, agy) Open's top below the state word's top | the actions row's mapped top is at least the meta row's mapped bottom; check after layout settles (`tryCompare`/`tryVerify`), then set `width` back to 500 and check `crowded` is false and both rows share one line again | a 1 px offset would pass the weaker check; a resize in both directions exercises the switch |
| R3 | (Codex) create both, set `entry` | create the entry with `createTemporaryObject(entryComponent, top, { view: standIn, entry: … })`; a fresh entry per case | `view` defaults to `null`, and a fresh entry avoids reading stale `Row` widths before polish |
| R4 | (agy) the error test in "Chat" | in "Errors" (`tst_views.qml:605`), as the spec says; `t3` is found with `mock.state.transfers.filter(…)[0]` and wrapped as `{ key: "t3", at: Date.now(), file: t3 }` | carry the spec's placement over; `transfers` is an array |
| R5 | (agy) check "Copied to the clipboard" is absent after the error shows | also check it right after the click | the error arrives later (`Qt.callLater`); a wrong synchronous toast could be overwritten before the late check |
| R7 | (agy diff review; implementation) the word `Row` follows the icons with the `actions` spacing of 2 px | the word `Row` gets `leftPadding: root.finished && !root.mine ? 4 : 0`; the received test checks that "Show in folder" is visible and its hit area starts after Copy, the sent test that it is hidden | `TextAction`'s `MouseArea` has `anchors.margins: -4`, so it covered the last 2 px of Copy and a click there opened the folder |
| R6 | (agy) header height `meta + 6 + actions` | kept: 6 is `col.spacing` (line 125), the gap between the two lines; bindings inside `card` use `root.crowded` | consistent spacing; every existing binding there is `root.`-qualified |

## Steps

1. **`gui/qml/components/ChatEntry.qml`** — D1–D6.
   - `finished` and `crowded` next to `moving` (line 29); `crowded` refers to
     `meta`, `actions` and `col`, which are ids in the same file.
   - `copyFile()` after `copy()` (lines 94-97).
   - Header item (lines 128-216): its `height` (line 130) becomes
     `root.crowded ? meta.implicitHeight + 6 + actions.implicitHeight : Math.max(meta.implicitHeight, actions.implicitHeight)`
     (6 = `col.spacing`). `meta` (line 133) and `actions` (lines 158-160)
     lose `anchors.verticalCenter` and get the `y` bindings of R1;
     `actions` keeps `anchors.right: parent.right`.
   - The two `IconAction`s (D2) after the clip trash action (lines 185-191) and
     before the word `Row` (line 195); the comment (D6); "Show in folder"
     `visible` (line 210).
   → verify: the QML view tests (command under Tests) still pass, 85 of 85,
   before step 2 adds new ones.
   Traps: no vertical anchors on `meta` or `actions` (R1). `crowded` must not depend on `card.width` through `natural`
   (`natural` reads `actions.implicitWidth`, not the header height, so no
   binding loop; do not make `natural` read `crowded`). Shared QML: both
   the Qt app and the Omarchy shell load it (AGENTS.md), so no host-only
   types. `root.file` is null on a clip entry; every new binding guards it
   through `finished`.

2. **`gui/tests/tst_views.qml`** — the spec's tests.
   - New `TestCase` "ChatEntry" (place it before "Chat", line 1007; no
     `when:` needed). A `Component` with `ChatEntry { width: 500 }` and a
     `Component` with a stand-in `QtObject { property string devName: "Pixel";
     property var requests: []; property var toasts: [];
     function call(method, params, cb) { requests.push({ method: method, params: params }); if (cb) cb() }
     function toast(text) { toasts.push(text) } }`. Create a fresh entry per case with
     `createTemporaryObject(entryComponent, top, { view: standIn, entry: { key: "t3", at: Date.now(), file: f } })` (R3).
     Find the icons with `findBy(entry, "objectName", "fileOpen")`/`"fileCopy"`
     and call `.clicked()`.
     Cases:
     - received `{ id: "t3", dir: "in", state: "done", path: "/tmp/a.png", name: "a.png", size: 1 }`:
       both visible; Open → one request, `method === "transfer.open"`,
       `JSON.stringify(params) === '{"id":"t3"}'`, no toast; Copy → request
       `transfer.copy` with `{"id":"t3"}`, toasts `["Copied to the clipboard"]`;
     - sent (`dir: "out"`), `done`, path: both visible;
     - `active`, `queued`, `waiting`, `failed`, `canceled`, each with a path,
       and `done` with `path: ""`: neither visible;
     - a clip entry `{ key: "c", at: Date.now(), clip: { id: "c", dir: "in", text: "x" } }`:
       neither visible;
     - received `done` card at `width: 280`: `tryCompare(entry, "crowded", true)`,
       then the actions row's top (`fileOpen.parent.mapToItem(entry, 0, 0).y`)
       is at least the bottom of the meta row (the `Txt` with
       `text === "Received"`: `.parent.mapToItem(entry, 0, 0).y + .parent.height`);
       then `width = 500`: `tryCompare(entry, "crowded", false)` and the
       two rows' vertical centres are equal (R2).
   - In `TestCase` "Errors" (line 605; it resets `mock.failures` in `init`
     and `cleanup`) (R4): create a real `FluxView` (`viewComponent`), find
     `t3` with `mock.state.transfers.filter(function (t) { return t.id === "t3" })[0]`,
     create a `ChatEntry` with `{ view: view, entry: { key: "t3", at: Date.now(), file: t3 }, width: 500 }`,
     set `mock.failures = { "transfer.copy": { code: "not_found", message: "a.png no longer exists" } }`,
     emit `fileCopy`'s `clicked`, check at once that no visible item has
     `message === "Copied to the clipboard"` (R5), then `tryVerify` a visible
     item with `message === "a.png no longer exists"` (as
     `test_clipboardErrorShowsInToast`, line 673) and check the success text
     is still absent.
   → verify: the QML view tests pass, 85 + the new ones, 0 failed.
   Traps: `findBy` ignores visibility; check `visible` on the found item.
   `ChatEntry` has no implicit width, so always set `width`. Do not change
   `MockBackend.qml`.

3. **Session model** — rerun the QML tests; build `.#flux-gui` and render
   the snapshots at the default size and at `FLUX_SNAPSHOT_SIZE=360x760`;
   look at `01-chat.png` (Open, Copy, Show in folder on `t3`),
   `08-chat-top.png` (Open and Copy on sent `t4`, nothing new on `t5`), and
   the 360 px chat (actions under the state word); `nix flake check
   path:.`; Codex and agy diff reviews; PR linking intent, spec and plan,
   naming who did each step; the owner's desktop checks.

## Tests

| Command | Expected |
| --- | --- |
| `Q=$(nix build --no-link --print-out-paths nixpkgs#qt6.qtdeclarative); nix shell nixpkgs#qt6.qtbase nixpkgs#qt6.qtdeclarative nixpkgs#qt6.qtsvg -c env QT_QPA_PLATFORM=offscreen QML_XHR_ALLOW_FILE_READ=1 $Q/bin/qmltestrunner -input gui/tests` | all pass, 0 failed |
| `nix build .#flux-gui` and `flux-gui --snapshot <dir>` (twice, second with `FLUX_SNAPSHOT_SIZE=360x760`) | the images described in step 3 |
| `nix flake check path:.` | passes |
| Owner, on the desktop (Qt app and Omarchy shell) | Open launches the default app; Copy on a screenshot pastes an image; Copy on a non-image pastes into a file manager |

CI does not run the QML tests (`build.yml` runs Go `make vet test`), so they
run here.

## Rollback

Revert the commits. No setting, protocol or storage change.
