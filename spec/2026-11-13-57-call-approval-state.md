---
status: approved
issue: 57
intent: intent/2026-11-13-57-call-approval-state.md
---

# Spec: calls, waiting approvals and phone Do Not Disturb in the state snapshot

## Decisions carried from the intent's open questions (owner, 2026-11-13: "use your recommendations")

- Phone Do Not Disturb gets a new device method, `dnd.set`. The desktop's Do Not Disturb is never touched by it.
- `missedCall` stays until the next call on that device, or 10 minutes, whichever comes first.
- There is no `direction` field. "talking" covers incoming and outgoing calls.

## Snapshot semantics (applies to everything below)

`state` is a level, not an event log:
- The publisher waits 100 ms and sends only the newest snapshot (`daemon.go:1233-1261`), and a slow reader gets only
  the newest (`docs/ipc.md:101-103`).
- A transition shorter than that can be missed: ringing → talking, or an approval added and answered at once.
- Each record therefore carries a stable key (`since` or `at`), so a client can tell a new record from one it has seen,
  including after a reconnect.
- `docs/ipc.md` says so.

## Design

### 1. `call` and `missedCall` per device

**Storage.** `Device` (`internal/core/device.go:17-119`) gains two value fields, `call CallView` and `missed
MissedView`, each with an `ok bool`. They are written only under `d.mu`. The view copies them by value, so no pointer
reaches the JSON encoder after `Snapshot` unlocks (`api.go:199`).

```go
type CallView   struct { State string `json:"state"`; Caller string `json:"caller"`; Number string `json:"number"`; Since int64 `json:"since"` }
type MissedView struct { Caller string `json:"caller"`; Number string `json:"number"`; At int64 `json:"at"` }
```

- `Caller` is `strings.TrimSpace(b.ContactName)` and `Number` is `b.PhoneNumber`, each already cut to 256
  (`telephony.go:103`). Either may be empty. The popup keeps using `caller()` (`:84-92`).
- Times are Unix seconds, like `lastSeen`.

**Writes.** The view update goes inside `callEvent`'s first locked section (`telephony.go:117-136`), where it already
validates pairing and the link. That way no second lock has to re-check identity. `d.markDirty()` follows
(`daemon.go:1224`).

| Packet | View change |
|---|---|
| `ringing` | `call = {ringing, caller, number, since}`. `since` is kept across repeat packets. `missed` is cleared |
| `talking` | `call.state = talking`. `since` is kept if it was ringing, else now. `missed` is cleared |
| cancel | `call` is cleared. `missed` is untouched, so the trailing ringing-cancel after `missedCall` (`android/.../core/Calls.kt:49`) keeps it |
| `missedCall` | `call` is cleared. `missed = {caller, number, at: now}` |

- **Link drop.** `dropCall` (`:191-203`) clears `call` under the lock it takes, and keeps `missed`.
- **Unpair.** `dropTrustLocked` (`pairing.go:233-252`) clears both.
- **Ageing.** At view time, a `missed` older than 10 minutes is omitted. `time.AfterFunc(10*time.Minute, d.markDirty)`
  at the write makes the expiry reach idle subscribers. A stale timer only causes an unchanged publish, which is
  dropped by the byte compare.
- **View fields.** `DeviceView` gains `Call *CallView \`json:"call,omitempty"\`` and `MissedCall *MissedView
  \`json:"missedCall,omitempty"\``. The pointers are to copies made in `dev.view()`.

### 2. `approvals` at top level

```go
type ApprovalView struct { Kind string `json:"kind"`; Device string `json:"device"`; Since int64 `json:"since"` }
```

**Sudo** (`approve.go`):
- Each entry gains `since`, set in `ApproveRequest` (`:236`). Only `kind == "request"` entries are listed, as `"sudo"`.
  Enroll entries are not listed.
- Listed means unanswered and before the deadline, the `pending()` filter (`:91-102`).
- `add`, `deliver`, `remove` and the expiry branch of `wait` (`:154`) call `markDirty`.
- `time.AfterFunc(deadline-now, d.markDirty)` at creation pushes the expiry to idle subscribers.

**Cancel that wakes the waiter.** Today `ApproveCancel` deletes the entry, but a running `wait` holds the pointer and
blocks on `done` (`:132-159`, `:320-336`).
- A new `book.cancel(id)` sets `result = cancelled` and closes `done` under the book lock, then removes the entry.
- `ApproveCancel` and the new disconnect path both use it, so a blocked `wait` returns at once with the existing
  cancel result.

**SSH** (`ssh.go`):
- Each `sshSign` (`:56-61`) gains `since`, set right after the phone is asked (`:271`), not while it is queued
  (`:251-259`).
- It is listed as `"ssh"` while unanswered. `add` and `remove` (`:88-103`) call `markDirty`.

**Disconnect and unpair.** `d.dropApprovals(devID)` calls `book.cancel` for the device's sudo entries. It is called
from `dropTrustLocked` and from `receive` after `dev.link = nil` (`daemon.go:1088-1091`). SSH signs already end on a
link drop (`ssh.go:276-292`).

**Locks.** Each book has its own mutex, and `d.mu` does not cover them.
- `Snapshot` collects both lists before it takes `d.mu`, via `book.views()`, which locks only the book.
- Lock order is therefore book, then nothing. No book method takes `d.mu`, and `dropApprovals` is called after the
  caller releases `d.mu`.
- The list is sudo first, then oldest first, and an empty list rather than null.

**Never** the PAM service, user, command, key, fingerprint or signature.

### 3. Phone Do Not Disturb: `dnd`, `dnd.set`, and two new packet types

The existing `flux.dnd {on}` sync stays exactly as it is (`dnd.go`, `DndSync.kt`). It mirrors desktop and phone and
forwards to other computers. Two new packet types sit beside it, so old apps are excluded by capability and nothing
can be mistaken for a sync:

- **`flux.dnd.set {on}`** (computer → phone). The phone applies it to itself only, through the same policy call
  `DndSync` uses, with `DndGuard` marking it as remote, so `onLocalChange` doesn't broadcast it. It does not forward to
  any computer. It then sends `flux.dnd.state` to all paired computers.
- **`flux.dnd.state {on, by}`** (phone → computer). `on` is a bool, or null when the phone can't read or set it
  (policy access missing, or its Sync Do Not Disturb off). `by` says why it changed: `"phone"` for the user on the
  phone, `"sync"` after an ordinary `flux.dnd` from any computer, `"set"` after a `flux.dnd.set`, or `"connect"` for
  the report on link-up. A computer only stores it and never applies it to the desktop.

**Android** (`DndSync.kt`, `Plugins.kt:130`, the identity's type lists):
- Declare `flux.dnd.set` incoming and `flux.dnd.state` outgoing.
- Send `flux.dnd.state` to all paired computers:
  - on each link-up, to that computer, with `by: "connect"`
  - after `onLocalChange`, with `by: "phone"`
  - after applying an ordinary `flux.dnd`, with `by: "sync"`. This fixes the stale case where the originating computer
    never hears the result.
  - after applying `flux.dnd.set`, with `by: "set"`
  - when its Sync Do Not Disturb switch or policy access changes, with the new `on` or null
- The phone's Sync Do Not Disturb switch still governs everything: off means `flux.dnd.set` is ignored and the state
  reports null.

**fluxd:**
- `Device` gains `dnd DndView{ok bool; On *bool; By string; At int64}`. A new handler for `TypeFluxDndState` stores it
  under `d.mu` with `At = now`, then `markDirty`. Dispatch goes next to `handlers.go:116-117`.
- `dropTrustLocked` and a link drop clear it.
- The ordinary `handleDnd` doesn't touch `dev.dnd`.
- `plugins()` (`device.go:235-245`) gains `{"dnd", dev.accepts(TypeFluxDndSet) && dev.supports(TypeFluxDndState)}`.
  `"dnd"` in `plugins` is the capability clients check.
- `DeviceView` gains `Dnd *DndView \`json:"dnd,omitempty"\`` as `{on, by, at}`.

**`dnd.set {device, on}`** (`api.go`):
- Add it to `deviceMethods` (`:481-491`) and as a case in the device switch (`:405-477`). `params` gains `On *bool
  \`json:"on"\`` (`:222-244`).
- Errors:
  - `bad_params` without `on`
  - `not_supported` without the `dnd` capability (as `ring` does, `:425`)
  - `offline` without a link
- It sends `flux.dnd.set {on}` with `d.send`. It never calls `handleDnd`, `sendDnd` or the desktop backend, and never
  touches `d.dndGuard`.
- The result is `{"sent": true}`. The outcome arrives as `dnd` with `by: "set"`.

Ownership a client can check. A client that sets the phone remembers the `at` of the `by: "set"` report that followed.
If a later report has a different `at`, someone else changed the phone in between: the user, another computer's sync,
or another `dnd.set`. That holds even when an off → on toggle falls between two snapshots.

### 4. Docs

- `docs/ipc.md`:
  - snapshot keys (`:61-62`, merging the two lines that disagree) and the state example (`:95-96`)
  - "Snapshot semantics", from the section above
  - the method table (`:167-182`) and parameters (`:191`) for `dnd.set`
  - the approval section (`:357-371`)
- `docs/features.md` Do Not Disturb (`:500-514`): the state report, and `dnd.set` for local clients.
- `docs/security.md`: `call` and `missedCall` put caller names and numbers into `state`. Any local client already sees
  notification text, so no boundary widens, but the doc says so.

## Alternatives rejected

- **Automation hooks as the call source**: they run shell commands, are listed as runnable on the phone, have a
  cooldown, and have no answered or ended event.
- **A `missed` state inside `call`**: the trailing cancel wipes it, and a slow reader may never see it.
- **Extending `flux.dnd` with `local` and `report` flags**: an old phone ignores the flags and forwards the set to
  other computers. It also sends ordinary changes that would look like reports, so a client couldn't tell support from
  luck. New packet types make support a capability.
- **Optimistic `dnd` after `dnd.set`**: the phone ignores the set when its sync is off, so the guess would lie.
- **New event names for calls and approvals**: the snapshot is the one contract every client reads.
- **Listing enroll approvals**: nothing waits on them in a terminal.

## Risks

- **Locking in `callEvent`**: its current unlocked writes to `c.ringing` stay (`telephony.go:148,166`); they are
  media-worker state. The new view fields are written only inside the locked section. A `-race` test covers snapshots
  during transitions and unpairing.
- **Lock order** between the approval books and `d.mu`, as above. A test calls `Snapshot` while approvals are added
  and cancelled under `-race`.
- **Caller data in `state`**: documented. No new reader gains access.
- **Release coupling**: `dnd` and `dnd.set` need the new Android app. Without it the capability is absent, and both
  stay off cleanly.
- **More `markDirty` calls**: coalesced every 100 ms and deduplicated.

## Verification

- `go test -race ./internal/core/... ./internal/ipc/...`, with new tests:
  - `telephony_test.go`:
    - ringing → talking → cancel
    - `missedCall` then the trailing cancel keeps `missedCall`
    - `dropCall` keeps `missedCall`
    - ageing at 10 minutes, with a clock seam
    - a concurrent `Snapshot` during transitions and an unpair
  - `approve_test.go`:
    - a request is listed as sudo; deliver, cancel and expiry remove it
    - enroll isn't listed
    - a disconnect wakes an already-blocked `wait` with the cancel result
    - expiry publishes with no other event
    - a concurrent `Snapshot`
  - `ssh_test.go`: listed only after the phone is asked; removed on answer, timeout and hangup.
  - `dnd_test.go`:
    - `flux.dnd.state` sets `dnd` and never calls the backend `Set`
    - ordinary `flux.dnd` doesn't touch `dnd`
    - `dnd.set` sends `flux.dnd.set` and never calls `Set` or `sendDnd`
    - `not_supported` for a device without the capability
    - `offline` and `bad_params`
    - `dnd` is cleared on a link drop and on unpair
  - `api_cli_test.go`: the snapshot decodes with the new fields; `approvals` is `[]` when there are none.
- Android: a `DndSyncTest`:
  - `flux.dnd.set` applies, doesn't forward, and reports `by: "set"`
  - ordinary `flux.dnd` reports `by: "sync"`
  - link-up reports `by: "connect"`
  - sync off reports null and ignores sets

  `./gradlew testDebugUnitTest`.
- `go vet ./...` and `nix flake check` (`nix/checks.nix`).
- Live, on a host with fluxd and a paired Android phone on the new app:
  - `flux-cli watch` during a call shows `call`, then `missedCall`
  - `sudo -A true` shows `approvals`
  - toggling DND on the phone gives `by: "phone"`
  - `dnd.set` silences the phone with `by: "set"`, while `omarchy-shell notifications dndState` stays unchanged
