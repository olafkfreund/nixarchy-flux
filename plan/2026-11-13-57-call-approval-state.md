---
status: draft
issue: 57
spec: spec/2026-11-13-57-call-approval-state.md
coders:
  1: codex   # small, tricky locking: view writes inside callEvent's validated lock, copies before the encoder
  2: codex   # small, tricky: cancel that wakes a blocked wait, and book-then-d.mu lock order
  3: coder   # straightforward daemon plumbing: two packet types, a handler, a capability, one device method
  4: coder   # Android DndSync changes that follow step 3's wire; the same agent keeps the wire in context
  5: agy     # large mechanical doc edits across ipc.md, features.md and security.md, with no logic
reviewers:
  - codex    # independent read of the full diff against this plan
  - agy      # independent read of the full diff; it timed out on the spec review, so its absence doesn't block
---

# Plan: calls, waiting approvals and phone Do Not Disturb in the state snapshot

Issue #57 (olafkfreund/nixarchy-flux). The intent (`906afb2`) and the spec (`127e13f`) are approved. This plan stands
on its own. Work happens in the worktree `/mnt/code/Source-home/GitHub/nixarchy-flux-57`, on a new branch
`feat/57-call-approval-state` cut from `docs/57-call-approval-state`. The main checkout at
`/mnt/code/Source-home/GitHub/nixarchy-flux` holds `fix/56-air-command-approve` and stays untouched.

## Approved decisions

- **The snapshot is a level, not an event log.** Flux publishes only the newest snapshot, after a 100 ms wait
  (`internal/core/daemon.go:1233-1261`), so short transitions can be skipped. Every record carries a stable key (`since`
  or `at`). `docs/ipc.md` says so.
- **Per device `call`** = `{state: "ringing"|"talking", caller, number, since}`. `caller` is the trimmed `ContactName`,
  and `number` is `PhoneNumber`, either possibly empty.
  - It is set on ringing and talking, and cleared on cancel, on `missedCall`, on a link drop (`dropCall`) and on
    unpair. `since` is kept across repeat packets.
- **Per device `missedCall`** = `{caller, number, at}`.
  - It is set on `missedCall`, and cleared by the next ringing or talking, or by unpair.
  - It is omitted from the view after 10 minutes, and `time.AfterFunc(10m, markDirty)` publishes that expiry.
  - It survives the ringing-cancel that Android sends right after `missedCall` (`android/.../core/Calls.kt:49`).
  - No `direction` field.
- **Top-level `approvals`** = `[{kind: "sudo"|"ssh", device, since}]`, sudo first, then oldest first; `[]` when there
  are none.
  - Sudo lists only unanswered, unexpired `kind == "request"` entries; enroll is never listed.
  - SSH entries are listed from the moment the phone is asked, not while queued.
  - It never contains the PAM service, user, command, key, fingerprint or signature.
  - Expiry publishes via `time.AfterFunc`.
  - A disconnect or unpair cancels the device's sudo entries, and the cancel wakes a blocked `wait` at once.
- **Do Not Disturb.** Two new packet types sit beside the unchanged `flux.dnd` sync:
  - **`flux.dnd.set {on}`** (computer → phone). The phone applies it only to itself, doesn't forward it, then sends
    `flux.dnd.state`.
  - **`flux.dnd.state {on, by}`** (phone → computer). `on` is a bool, or null when it can't be read or set. `by` is
    one of `phone`, `sync`, `set`, `connect`. The computer stores it and never applies it to the desktop.
  - Per device, `dnd` = `{on, by, at}`, where `at` is set by fluxd on receipt. It is cleared on a link drop and on
    unpair, and ordinary `flux.dnd` doesn't touch it.
  - The capability `"dnd"` in `plugins` is present iff the device accepts `flux.dnd.set` and sends `flux.dnd.state`.
  - **Method `dnd.set {device, on}`** sends `flux.dnd.set` and returns `{"sent": true}`. It never touches the desktop
    backend, `sendDnd` or `dndGuard`. Its errors are `bad_params`, `not_supported` and `offline`.
- **Android.** The phone reports `flux.dnd.state` to all paired computers:
  - on link-up, with `connect`
  - after a change on the phone, with `phone`
  - after applying an ordinary `flux.dnd`, with `sync`
  - after `flux.dnd.set`, with `set`
  - when its sync switch or policy access changes, with the new value or null

  With its sync off, the phone ignores `flux.dnd.set` and reports null.
- **Docs**: `ipc.md`, `features.md` and `security.md`, as in step 5.

## Steps

0. **Branch.** In the worktree:

   ```sh
   git switch -c feat/57-call-approval-state
   ```

   It starts from `docs/57-call-approval-state`. Check that `go build ./...` passes before any edit.
   Traps: never `git switch` in the main checkout. The plan and spec ride along in the PR.

1. **Call views.**
   - `internal/core/device.go`:
     - Add `CallView` and `MissedView` (types as in the spec), and on `Device` (`:17-119`) the fields `call CallView;
       callOK bool; missed MissedView; missedOK bool`.
     - Add `Call *CallView \`json:"call,omitempty"\`` and `MissedCall *MissedView \`json:"missedCall,omitempty"\`` to
       `DeviceView` (`:264-299`). In `dev.view()`, fill them with pointers to fresh copies, leaving out a `missed` older
       than 10 minutes.
   - `internal/core/telephony.go` `callEvent` (`:117-184`): inside the first `d.mu` section (`:117-136`, which already
     validates pairing and the link), apply the table:
     - ringing: set `call` (keep `since` if `callOK` and it was ringing) and clear `missed`
     - talking: if there was no ringing call (an outgoing call starts here, `Calls.kt:48`), set `callOK`, `caller`,
       `number` and `since = now`; else keep `since`. Then set `state = talking` and clear `missed`. A repeat talking
       packet keeps `since`.
     - cancel: clear `call`
     - missedCall: clear `call`, set `missed` with `at = now`, and start `time.AfterFunc(10*time.Minute, d.markDirty)`

     Then call `d.markDirty()` after the unlock.
   - `dropCall` (`:191-203`) clears `callOK` and calls `d.markDirty()` after its unlock. It runs on the media worker
     after the link closes (`:138-141`), which can be after `receive`'s own `markDirty` (`daemon.go:1112`).
   - `internal/core/pairing.go` `dropTrustLocked` (`:233-252`) clears `callOK` and `missedOK`.
   - `internal/core/api.go` (`:135-140`): an unpaired device's view drops `Call`, `MissedCall` and `Dnd`, as it already
     drops `Battery`, `Notifications`, `Conversations` and `Outbox`.

   Verify with new tests in `telephony_test.go`, next to `TestHandleTelephony` (`:82`) and `TestDropCall` (`:114`), using
   `telephonyPacket` (`:78`):
   - ringing → talking → cancel
   - missedCall plus the trailing cancel
   - dropCall keeps `missed`
   - 10-minute ageing, through a package `var now = time.Now` seam
   - concurrent `Snapshot` during transitions and an unpair

   ```sh
   go test -race ./internal/core/ -run 'Telephony|DropCall|Call'
   ```

   Traps: `callEvent` writes `c.ringing` without `d.mu` (`:148,:166`). Leave those writes alone and put the new fields
   only inside the locked section. `Snapshot` unlocks before `mustJSON` (`api.go:199`), so the view must hold copies,
   never `&dev.call`.

2. **Approvals.**
   - `internal/core/approve.go`:
     - `approval` (`:53-60`) gains `since time.Time`, set in `ApproveRequest` (`:236`).
     - New `book.cancel(id)`, under the book mutex:
       - if `a.result == nil`, set `a.result = &approvalResult{State: "failed", Error: "cancelled"}` and close `a.done`
         (the same guard `deliver` uses, `:111`)
       - then remove the entry

       "failed" already fails closed in the helper (`internal/approve/helper.go:237-238`) and in enroll
       (`enroll.go:127-128`), so no new state is introduced.
     - `ApproveCancel` (`:320-327`) uses it.
     - New `book.views()` returns the sudo list under the book mutex, using the `pending()` filter (`:91-102`).
     - `add` (`:69-89`), `deliver` (`:107-127`), `remove` (`:161-169`) and `wait`'s expiry branch (`:154`) call
       `b.dirty()`. The book gets a `dirty func()` field, set in `New` (`daemon.go:311-333`) to `d.markDirty`. Every
       call is guarded with `if b.dirty != nil`, because tests build `&Daemon{}` without `New` (`approve_test.go:20`,
       `ssh_test.go:149`). The book never takes `d.mu`.
     - At creation, call `time.AfterFunc(deadline-now, d.markDirty)`.
   - `internal/core/ssh.go`:
     - `sshSign` (`:56-61`) gains `since`. `add` (`:88-97`, called before the send at `:261`) leaves it zero, so the
       entry isn't listed yet.
     - A new `book.started(id)` sets `since = now` under the book mutex, then calls `dirty()`. It is called right
       after the send at `:271` succeeds.
     - `remove` (`:99-103`) calls `dirty()`.
     - `views()` lists unanswered entries with a non-zero `since`, under the book mutex.
   - `internal/core/daemon.go`:
     - New `d.dropApprovals(devID)` calls `cancel` for that device's request entries.
     - Call it after `d.mu` is released:
       - from `receive`'s link-drop path (`:1084-1112`, after `dev.link = nil`, `:1088-1091`)
       - from every caller of `dropTrustLocked`: `pairing.go:154`, `:205` (Unpair), `:290` and `:339`. Each caller
         collects the device id and calls `dropApprovals` after its unlock.
     - Each of those paths gets a test that a pending sudo request is cancelled.
   - `internal/core/api.go` `Snapshot` (`:113-200`): collect `approvals` from both books' `views()` before `d.mu.Lock()`
     (`:114`), and add `"approvals"` to the `state` map (`:156-198`). The order is deterministic, since the books are
     maps: `sudo` before `ssh`, then ascending `since`, then device id. It starts from `[]ApprovalView{}`, so the JSON
     is `[]`, never null.

   Verify with new tests in `approve_test.go` (helpers `approveDaemon` `:19`, `approveRaw` `:27`) and `ssh_test.go`
   (`sshSetup` `:126`, fake `sshPhone` `:35-122`), as listed in the spec. The key one: a goroutine blocked in
   `ApproveWait` returns the cancel result within 100 ms of `dropApprovals`.

   ```sh
   go test -race ./internal/core/ -run 'Approv|SSH'
   ```

   Traps: lock order is book only, and no book method may take `d.mu`. `dropApprovals` is never called with `d.mu` held.
   Enroll entries are excluded.

3. **DND state, the capability and `dnd.set`.**
   - `internal/proto/identity.go` (`:55-57`): add `TypeFluxDndSet = "flux.dnd.set"` and `TypeFluxDndState =
     "flux.dnd.state"`. Add them to the computer's advertised lists the same way `TypeFluxDnd` is (outgoing set,
     incoming state).
   - `internal/core/handlers.go` (`:116-117`): dispatch `TypeFluxDndState` to a new `handleDndState` in `dnd.go`. It
     decodes `{on *bool, by string}`, keeps `by` only if it is in the known set (else `""`), stores `dev.dnd = DndView{On,
     By, At: now}` under `d.mu` with `ok = true`, and calls `markDirty`. It never calls `applyDnd`, `sendDnd` or the
     backend.
   - `internal/core/device.go`:
     - `Device` gains `dnd DndView; dndOK bool`.
     - `DndView` is `{On *bool \`json:"on"\`; By string \`json:"by"\`; At int64 \`json:"at"\`}`. `on` has no `omitempty`,
       so an unknown state serializes as `"on": null`.
     - `DeviceView` gains `Dnd *DndView \`json:"dnd,omitempty"\`` (as a copy).
     - `plugins()` (`:235-245`) gains `{"dnd", dev.accepts(proto.TypeFluxDndSet) && dev.supports(proto.TypeFluxDndState)}`.
   - `dropTrustLocked` and the link-drop path (`daemon.go:1088-1091`) clear `dndOK`.
   - `internal/core/api.go`:
     - `params` (`:222-244`) gains `On *bool \`json:"on"\``.
     - Add `"dnd.set"` to `deviceMethods` (`:481-491`).
     - Add a case in the device switch (`:405-477`):
       - `bad_params` if `p.On == nil`
       - `not_supported` unless the device has the `dnd` capability
       - `offline` without a link
       - otherwise `d.send(dev, proto.New(proto.TypeFluxDndSet, map[string]any{"on": *p.On}))` and `{"sent": true}`

   Verify with new tests in `dnd_test.go` (`slowDnd` fake `:112`, `waitIdle`), as listed in the spec. The key ones:
   `flux.dnd.state` leaves `slowDnd.sets` empty, and `dnd.set` sends exactly one `flux.dnd.set` and leaves
   `slowDnd.sets` empty.

   Also add an `api_cli_test.go` case (next to `:187`): the snapshot decodes with `call`, `missedCall`, `dnd` and
   `approvals`, and `approvals` is `[]` (not null) when nothing is pending.

   ```sh
   go test -race ./internal/core/ -run 'Dnd|Snapshot|API'
   ```

   Traps: the ordinary `handleDnd` (`dnd.go:138-159`) must not change behaviour and must not write `dev.dnd`.

4. **Android.**
   - `android/app/src/main/java/org/omarchy/flux/core/DndSync.kt`:
     - Add `onSet(from, on)` for `flux.dnd.set`. If sync is off or there's no policy access, ignore it and report null.
       Otherwise apply it through the existing apply path with `DndGuard` marked remote, so `onLocalChange` doesn't
       broadcast. Don't forward it, then `report("set")`.
     - Add `report(by, to = all paired)`, which sends `flux.dnd.state {on, by}`.
     - Call `report` from:
       - `onLocalChange` (`:38-44`), `by "phone"`
       - `onPacket` (`:50-61`), after applying, `by "sync"`
       - link-up for that computer (`Plugins.kt:72`), `by "connect"`
       - the sync switch change (`FluxCore.kt:668-672`) and a policy-access change (`FluxService.kt:82-86`,
         `FluxCore.kt:176`), `by "phone"`, with `on` null when sync is off or access is missing
       - a refused `onSet`, `by "set"`, with `on` null

       Only these four `by` values are ever sent, and fluxd maps anything else to `""`.
   - `Plugins.kt:130`: dispatch the new type.
   - `android/app/src/main/java/org/omarchy/flux/protocol/Identity.kt`: the type constants next to `:43`, and the
     incoming and outgoing lists (`:116-141`). Declare `flux.dnd.set` incoming and `flux.dnd.state` outgoing.

   Verify with a new `DndSyncTest.kt` next to `DndGuardTest.kt`, with the four cases in the spec.

   ```sh
   cd android && ./gradlew testDebugUnitTest
   ```

   Traps: `onPacket`'s existing forward (`:59`) stays for `flux.dnd`. The new path must not reach it.

5. **Docs** (agy).
   - `docs/ipc.md`:
     - Merge the two disagreeing snapshot-key lines (`:61-62`) into one that includes `approvals`.
     - Add a "Snapshot semantics" paragraph (level, not log; stable keys).
     - Add `call`, `missedCall` and `dnd` to the device fields, and `approvals` to the state example (`:95-96`).
     - Add `dnd.set` to the Device row of the method table (`:167-182`) and to the parameters (`:191`, `on`).
     - In the approval section (`:357-371`), note that `approvals` lists waits without their details.
   - `docs/features.md` Do Not Disturb (`:500-514`): the phone's state report, and `dnd.set` for local clients, which
     needs the new app.
   - `docs/security.md`: one sentence that `call` and `missedCall` put caller names and numbers in `state`, for the
     same local clients that already see notification text.

   Verify:

   ```sh
   nix flake check
   ```

   Also check that `grep -n 'dnd.set\|approvals\|missedCall' docs/ipc.md` finds each item.
   Traps: house doc style is short plain sentences, one per line, as the files already are. Don't touch other sections.

6. **Full check** (lead).

   ```sh
   go vet ./...
   go test -race ./...
   cd android && ./gradlew testDebugUnitTest
   nix flake check
   ```

   Then the opus review and the codex and agy reviewers on the full diff against this plan. Fix any finding and record a
   deviation here in the same commit. Then commit and open the PR, linking the intent, spec and plan, saying which steps
   which coder did, and noting the Android release coupling.
   Traps: a provider that times out is reported verbatim and skipped.

## Tests

| Command | Expected |
|---|---|
| `go test -race ./...` | all pass, no races |
| `go vet ./...` | clean |
| `./gradlew testDebugUnitTest` (in `android/`) | all pass, including `DndSyncTest` |
| `nix flake check` | passes |

Live checks need a host with fluxd and a phone on the new app. The owner names it, and nothing is deployed without the
owner's OK:
- `flux-cli watch` during an incoming and a missed call
- `sudo -A true`
- a DND toggle on the phone
- an IPC `dnd.set`, with `omarchy-shell notifications dndState` unchanged

## Rollback

Revert the PR. All fields are additive, and old clients ignore them. The Android types are new: an old daemon ignores
`flux.dnd.state`, and the phone falls back to plain `flux.dnd` behaviour.
