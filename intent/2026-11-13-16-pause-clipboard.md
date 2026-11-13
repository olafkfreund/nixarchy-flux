---
status: approved
issue: 16
author: olafkfreund
---

# Intent: Pause clipboard sync at runtime

## Problem

A clipboard manager's private mode can't keep a copy off the phone without
side effects.
- The issue (mirrored from upstream `bjarneo/flux#159`) asks for a runtime
  pause that only affects clipboard sync, for a clipboard manager's private
  (incognito) mode.
- Stopping `fluxd` works, but it also cuts notifications, SMS and calls.
- `settings.set autoClipboard false` rewrites `config.toml`, and the copy
  still goes to the phone later (see Facts).

### Facts

Paths are under `internal/core/` unless they start with another folder.

- **The watcher.** `Watch` (`internal/desktop/clipboard.go:82-99`) runs
  `wl-paste --watch` for text and for images. For each selection change a
  shell script prints 3 lines (`clipboard.go:43` there); `watchOnce` reads
  them and calls `record` (`:117-130`), which calls `onLocalClipboard` or
  `onLocalImage` (`daemon.go:553`). So a copy reaches fluxd some
  milliseconds after the user made it.
  - `record` already drops a copy that the app marks sensitive
    (`CLIPBOARD_STATE`), empty or too large ones, the content at the start
    of `wl-paste` (`initialWindow`, 1 s), a repeat of the last content, and
    Flux's own writes (`desktop/clipboard.go:135-160,172-187,295-297`).
    A clipboard manager's private mode isn't the app that copies, so it
    can't rely on the sensitive mark.
  - `observe` updates `lastSeen` for every copy it sees
    (`desktop/clipboard.go:183-186`). The same content copied again isn't
    reported.
- **Text copies.** `onLocalClipboard` (`clipboard.go:239-275`) sets
  `lastLocalClip`, `lastClipAt` and `content.lastClip` before it checks
  `AutoClipboard` (`clipboard.go:242-250`). So a copy made with sync off
  stays in `lastClip`.
  - With sync on, it adds the entry, with the devices in `To`, under the
    lock (`clipboard.go:251-257`), then sends the text to each link after
    the lock is released (`clipboard.go:258-272`). The loop doesn't check
    the switch again and ignores send errors.
- **Image copies.** `onLocalImage` (`clipboard.go:280-328`) also sets
  `lastLocalClip` and `lastClipAt` and clears `lastClip`
  (`clipboard.go:286-291`).
  - With sync on, after the lock is released (`:298`), `addClipImage`
    writes the file to the clip folder and then adds the entry under the
    lock (`clipboard.go:138-168`, called at `:306`). `addClipImageIf`
    checks its condition only after the write (`:145-168`).
  - Then `newClipSend` (`clipboard.go:216-225`, called at `:309`) cancels
    the previous image send and registers a new context, and a goroutine
    sends the image (`:310-327`). It checks `AutoClipboard` before each
    device (`:314`), not during a transfer.
  - `stopClipSend` (`clipboard.go:228-235`) cancels the registered
    context. Turning `autoClipboard` off calls it (`api.go:709-711`), and
    so does a SIGHUP reload with sync off (`api.go:745-748`).
  - The clip folder is `$XDG_RUNTIME_DIR/flux/clipboard` (`daemon.go:317`).
- **The connect send.** On each new link, `sendConnectClipboard`
  (`clipboard.go:333-342`, called at `daemon.go:1205`) reads `lastClip`
  under the lock and sends it after the lock is released, with
  `lastLocalClip` as its time, when `AutoClipboard` is on. This is the
  leak the issue names: turn sync off, copy a password, turn sync on, and
  the next phone that connects gets the password.
- **Staleness.** `lastClipAt` decides whether a phone's
  `clipboard.connect` clip is older than the desktop's newest clip
  (`clipboard.go:395-404`). Local copies, incoming clips and shared text
  all update it (`clipboard.go:243,287,400`, `share.go:383`). A newer
  phone clip replaces the desktop clipboard.
- **Manual sends.** `clipboard.send` (CLI `flux-cli clip`,
  `cmd/flux/main.go:170-171`; Omarchy menu "Send clipboard",
  `dist/omarchy-menu.jsonc:7`) calls `SendClipboard`
  (`clipboard.go:544-571`).
  - Without text it reads the desktop clipboard, image first
    (`clipboard.go:548-558`). It is the only code that reads the desktop
    clipboard on request; the phone can't pull it.
  - It adds the entry to the history (and an image to the clip folder) but
    doesn't touch `lastClip` (`clipboard.go:566-568,596`).
  - `sendImageTo` (`clipboard.go:575-597`) shares `newClipSend` with the
    automatic send (`:591`): each image send cancels the other, and
    `stopClipSend` cancels a manual image too.
  - It needs the device's clipboard switch, not `AutoClipboard`
    (`clipboard.go:545`).
- **Incoming clips** from a phone go into the history whether or not
  `AutoClipboard` is on; only putting them on the desktop clipboard needs
  `AutoClipboard` or a manual send (`clipboard.go:391,397-416,503-508`).
  Shared text and a signature go on the desktop clipboard without
  `AutoClipboard` (`share.go:381,506-512`); a phone screenshot needs it
  (`share.go:494-504`).
- **Switches today.** `AutoClipboard` (`internal/config/config.go:38`,
  state `settings.autoClipboard`, `api.go:182`) and a per-device
  `clipboard` rule. The window has no switch for `autoClipboard`: the
  access card's "Clipboard sync" toggle sets the device rule and only
  says "Off on this computer." when `autoClipboard` is off
  (`gui/qml/components/AccessCard.qml:23,81,89-95`). It shows on a
  device's page.
- **The state snapshot** is built in `Snapshot` (`api.go:113-215`), after
  #57 added `approvals` and call fields. `docs/ipc.md` lists the
  top-level fields (`:61`), the method groups and parameters
  (`:183,217-218`) and the clipboard methods (`:387-434`).
- **The CLI** has `flux-cli clipboard [clear [--all]|delete ID|limit [N]]`;
  plain `flux-cli clipboard` prints the history
  (`cmd/flux/features.go:44-68`, usage at `cmd/flux/main.go:62`), with
  tests in `cmd/flux/main_test.go:323-333`.
- **fluxd restarts by itself.**
  - After an update it restarts into the new binary once `Busy` reports
    nothing running (`cmd/fluxd/upgrade.go:53-56`, `daemon.go:409`, exit
    code 75 at `cmd/fluxd/main.go:158`).
  - systemd restarts it after a crash (`dist/fluxd.service:9`).
  - A pause that lives only in memory ends on either restart, without the
    clipboard manager knowing.
- **The runtime folder** `$XDG_RUNTIME_DIR/flux` is in memory and private
  to the user (`internal/config/config.go:224-233`). It survives a fluxd
  restart. It ends at reboot, and at logout unless lingering is on.
  A headless daemon (tests) can share it with the desktop's fluxd, which
  is why it keeps its clip images elsewhere (`daemon.go:339-344`).
- **The Omarchy shell plugin** has a bar widget: the Flux mark and a
  tooltip, which returns early with "no paired devices"
  (`gui/omarchy/BarWidget.qml:37-51`, `docs/omarchy.md:10`).
- **Clipboard managers** aren't in this repository. The issue's author has
  their own Omarchy clipboard plugin; it would call the CLI or IPC.
- **Other hosts.** macOS, Windows and iOS are separate apps that don't run
  `fluxd` (`windows/`, `macos/`). They aren't affected.

## Proposed outcome

- `flux-cli clipboard pause`, `resume` and `status`, and the IPC methods
  `clipboard.pause` and `clipboard.resume`. Both are idempotent. Plain
  `flux-cli clipboard` keeps printing the history.
- **The guarantee.** A local copy that fluxd handles while paused, text or
  image:
  - is not sent to any device automatically;
  - is not added to the history and leaves no image file;
  - doesn't set `lastClip`, so no phone gets it on connect.
  The one exception is a manual send (open question 5).
- **Pausing** stops every automatic send: no new one starts, a running
  image send is cancelled, and a text loop stops before the next device.
  It clears `lastClip` (open question 3). It can't recall a packet already
  handed to a link.
- **Resuming** sends nothing by itself, and the content still on the
  clipboard at that moment never syncs, even if its record arrives late
  (open question 9). The next new copy syncs as usual.
- The pause is never written to `config.toml`. `settings.autoClipboard`,
  the device rules, a settings change and a SIGHUP reload leave the pause
  as it is.
- The `state` snapshot has `clipboardPaused` (a boolean), and a `state`
  event goes out when it changes. The Flux window and the bar widget show
  it (open question 7).
- Incoming clips from a phone work as today (open question 4).
- A fluxd restart keeps the pause until reboot or logout (open
  question 2).
- `docs/ipc.md`, `docs/features.md` and `docs/security.md` describe the
  pause, its guarantee and the manual-send exception; the CLI usage lists
  it.
- Go tests (open question 8).

## Affected users and systems

- Desktop users with clipboard sync on, and clipboard managers or scripts
  that call `flux-cli` or the IPC.
- `fluxd`: `internal/core/clipboard.go`, `api.go` (methods, `Snapshot`),
  `daemon.go` (the field, start-up), possibly
  `internal/desktop/clipboard.go` (the resume fence), and tests.
- `cmd/flux` (`features.go`, `main.go`, `main_test.go`).
- Docs: `docs/ipc.md`, `docs/features.md`, `docs/security.md`.
- Qt: a Chat page or window indicator in `gui/qml/` and
  `gui/omarchy/BarWidget.qml`.
- Not affected: the protocol, Android, iOS, macOS, Windows,
  `config.toml`.

## Constraints

- No copy that fluxd handles while paused reaches a phone automatically,
  the history, the clip folder, or `lastClip`. The pause check runs under
  `d.mu` with the state it guards, and work admitted before a pause can't
  register a send or write an entry after it.
- Never write the pause to `config.toml`, and never change
  `AutoClipboard` or a device rule for it.
- A headless daemon never reads or writes the desktop's pause marker.
- No protocol change; phones need no update.
- Notifications, SMS, calls, files and every other feature keep running.
- Shared Qt views in `gui/qml/` stay compatible with both desktop hosts
  (`AGENTS.md`).
- Don't touch the running `fluxd`, `/etc/flux` or adb devices during the
  work; a test setup is running.

## Open questions

1. **Scope of the change.** Proposed: as in the issue, a boolean pause
   for local copies, set by CLI and IPC. No timer and no counting of
   callers: the last call wins, so any client can resume. A clipboard
   manager that crashes while paused leaves sync paused, which the window
   and the bar show (question 7).
2. **What a fluxd restart does.** The triage proposed "restart resumes".
   But fluxd restarts by itself after an update and after a crash
   (Facts), so a private-mode session could resume silently. Proposed: a
   marker file in the runtime folder. fluxd reads it at start, before the
   watcher and the network, so a restart keeps the pause; reboot and
   logout (without lingering) clear it. It isn't the config. If the
   marker can't be written, the pause still holds in memory and the call
   returns an error that says a restart would resume. A headless daemon
   keeps its pause in memory only. The alternative is memory only, with
   a restart that resumes and a log line.
3. **Clear `lastClip` on pause?** Proposed: yes. The issue only asks that
   copies made while paused stay out of it. Clearing it at pause also
   drops the last copy before the pause, so a phone that connects later,
   or one that didn't get it (permission, size, a failed send), gets
   nothing. The rule stays simple: after a pause, nothing from the desktop
   goes out until the next new copy after the resume.
4. **Incoming clips and `lastClipAt` while paused.** Proposed: incoming
   clips work as today. The issue is about copies leaving the desktop. A
   phone clip still goes in the history and on the desktop clipboard, and
   can replace a private copy there. But a copy made while paused still
   updates `lastClipAt` (a time, no content), so an older
   `clipboard.connect` clip from a phone that reconnects doesn't replace a
   fresh private copy.
5. **Manual sends while paused.** Proposed: allowed, as the one stated
   exception. `flux-cli clip`, "Send clipboard" in the Omarchy menu and
   the window's Send are explicit user actions, and the issue doesn't ask
   to block them. They read the clipboard, send it, and add it to the
   history as today, and never set `lastClip`. A pause doesn't cancel a
   manual image send, so automatic and manual image sends need separate
   cancellation.
6. **CLI shape.** Proposed: `flux-cli clipboard pause|resume|status`.
   `status` prints `paused`, `syncing`, or `off` when `autoClipboard` is
   off; `paused` wins when both hold, so a pause isn't hidden. Plain
   `flux-cli clipboard` keeps printing the history (`features.go:45-46`).
7. **The indicator.** Proposed: in this issue, small, and global rather
   than per device.
   - The window: a line "Clipboard sync paused" with a Resume button on
     the Chat page, where the clipboard history shows. Not the per-device
     access card.
   - The bar widget: the mark dims while paused, and the tooltip says
     "Clipboard sync paused", also with no paired device.
8. **Tests.** Go tests with the fake clipboard:
   - text and image copies while paused: no send, no history, no file, no
     `lastClip`, `lastClipAt` updated;
   - a pause between the image's admission and its send; a pause during
     an image send; pause and resume in quick succession;
   - a connect while paused and after resume;
   - a manual text and image send while paused, and a pause during a
     manual image send;
   - SIGHUP and `autoClipboard` changes while paused;
   - the marker: kept across a restart, a write failure, a headless
     daemon that ignores it;
   - the resume fence (question 9);
   - the CLI arguments.

   Then, with the owner's OK on the test setup, a check with the phone:
   pause, copy text and an image, reconnect the phone, resume, copy again;
   only the last copy arrives, and the history doesn't show the paused
   copies.
9. **Late records at resume.** A copy made just before resume can reach
   fluxd after the resume, since the watcher runs a shell script per copy
   (Facts). Proposed: resume reads the current clipboard and marks it as
   seen in the watcher, the way Flux's own writes are marked
   (`desktop/clipboard.go:189-198`), before the pause ends. So the content
   still on the clipboard never syncs, late or not. Copying the same
   content again after the resume doesn't sync either; a new copy does.
   The alternative is to document the race and tell clipboard managers to
   resume only after the private copy is gone.

## Review changes

Codex and agy reviewed the first draft, read only. Each finding was checked
against the code.

| Finding | Source | Verdict | Change |
| --- | --- | --- | --- |
| A copy made just before resume can reach fluxd after it and leak | Codex (high), agy (high) | Confirmed, `desktop/clipboard.go:117-130` | Watcher facts; resume fence in the outcome; new Q9 |
| `onLocalImage` can write the file and register its send after a pause; `addClipImageIf` checks after the write | Codex (high), agy (high) | Confirmed, `clipboard.go:298-309,145-168` | Facts corrected; constraint on admitted work; "leaves no image file"; tests |
| A captured connect or text send can't be recalled | Codex | Confirmed, `clipboard.go:333-342,258-272` | Outcome states the limit |
| Manual sends break the unconditional guarantee | Codex | Confirmed, `clipboard.go:548-568` | Named as the one exception everywhere |
| `stopClipSend` on pause cancels a manual image; "cancels nothing else" was wrong | Codex, agy | Confirmed, `clipboard.go:216-235,591` | Q5: separate cancellation |
| Marker: failure, start order, SIGHUP, headless tests sharing the runtime folder | Codex, agy | Confirmed, `daemon.go:339-344`, `api.go:727-751` | Q2 and constraints extended |
| Paused copies must update `lastClipAt`, or an older phone clip replaces a private copy | agy | Confirmed, `clipboard.go:395-404` | Q4 changed |
| Access card toggle is per device, not `autoClipboard` | agy, Codex | Confirmed, `AccessCard.qml:89-95` | Fact corrected; Q7 moves the indicator to the Chat page |
| Bar tooltip returns early with no paired device; tooltip only is easy to miss | agy, Codex | Confirmed, `BarWidget.qml:40` | Q7: dimmed mark, tooltip in every case |
| Incoming clips enter the history without `AutoClipboard`; shared text and signatures too | Codex, agy | Confirmed, `clipboard.go:397-416,503`, `share.go:381,506-512` | Fact corrected |
| Flux's own writes never reach `onLocalClipboard` (echo claim wrong) | Codex, agy | Confirmed, `desktop/clipboard.go:178-182,295-297` | Removed from Q4 |
| `Watch` drops sensitive, initial, repeated content | Codex | Confirmed, `desktop/clipboard.go:135-160` | Watcher facts |
| Same content copied again after resume isn't reported | Codex, agy | Confirmed, `desktop/clipboard.go:183-186` | Stated in Q9 |
| Outcome and Q6 contradicted each other on plain `flux-cli clipboard` | Codex, agy | Confirmed | Outcome fixed; Q6 adds `status` precedence |
| Indicator was both required and optional | Codex | Confirmed | Now in scope |
| Q3 assumed every phone got the last copy | Codex | Confirmed, `clipboard.go:254,271` | Reworded |
| The runtime folder survives logout with lingering | agy, Codex | Accepted, environment | Facts and Q2 qualified |
| Tests miss the race, marker, SIGHUP, connect cases | Codex, agy | Agreed | Q8 extended |
| The phone check conflicts with "don't touch the test setup" | Codex | Confirmed | Q8: with the owner's OK |
| Line ranges off by one; `ipc.md` method tables; `Busy` covers more | agy | Confirmed | Corrected |
| Stopping a text loop leaves `To` listing devices that got nothing | agy | Confirmed, `clipboard.go:251-257`; minor | None; the spec decides |
| Non-Linux hosts unaffected | both (unverified) | Confirmed: `windows/`, `macos/` are native apps without `fluxd` | None |
