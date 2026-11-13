---
status: draft
issue: 57
intent: intent/2026-11-13-57-call-approval-state.md
---

# Spec: calls, waiting approvals and phone Do Not Disturb in the state snapshot

## Decisions carried from the intent's open questions (owner, 2026-11-13: "use your recommendations")

- Phone Do Not Disturb gets a new device method, `dnd.set`. The desktop's Do Not Disturb is never touched by it.
- `missedCall` stays until the next call on that device or 10 minutes, whichever comes first.
- No `direction` field. "talking" covers incoming and outgoing calls.

## Design

### 1. `call` and `missedCall` per device

**Storage.** Add `call *CallView` and `missed *MissedView` to `Device` (`internal/core/device.go:17-119`), written only
under `d.mu`. They are kept separate from `callState` (`telephony.go:61-72`), which stays the media worker's own
bookkeeping.

```go
type CallView   struct { State string `json:"state"`; Caller string `json:"caller"`; Number string `json:"number"`; Since int64 `json:"since"` }
type MissedView struct { Caller string `json:"caller"`; Number string `json:"number"`; At int64 `json:"at"` }
```

- `Caller` is `strings.TrimSpace(b.ContactName)` and `Number` is `b.PhoneNumber`, each already cut to 256
  (`telephony.go:103`). Either may be empty. The popup keeps using `caller()` (`:84-92`).
- Times are Unix seconds, as `lastSeen` is (`device.go:274`).

**Writes in `callEvent`** (`telephony.go:117-184`), each followed by `d.markDirty()` (`daemon.go:1224`):

| Packet | Effect on the view |
|---|---|
| `ringing` | `call = {ringing, …, since: now}`, `since` kept on repeat packets; `missed = nil` (a new call replaces it) |
| `talking` | `call.state = talking`, `since` kept if it was ringing, else now; `missed = nil` |
| cancel | `call = nil`. `missed` is untouched, so Android's trailing ringing-cancel after `missedCall` (`android/.../core/Calls.kt:49`) can't wipe it |
| `missedCall` | `call = nil`; `missed = {caller, number, at: now}` |

- **Link drop.** `dropCall` (`:191-203`) sets `call = nil` under the lock it already takes. It keeps `missed`.
- **Ageing.** `missed` older than 10 minutes is left out of the view at `Snapshot` (`api.go:132-146`).
  `time.AfterFunc(10*time.Minute, d.markDirty)` at the `missedCall` write makes the expiry reach idle subscribers. A
  stale timer only causes one extra unchanged publish, which `publishLoop` drops by byte compare (`daemon.go:1233-1261`).
- **Unpair.** `dropTrustLocked` (`pairing.go:233-252`) clears both.

**View.** `DeviceView` (`device.go:264-299`) gains `Call *CallView \`json:"call,omitempty"\`` and
`MissedCall *MissedView \`json:"missedCall,omitempty"\``, filled in `dev.view()`.

### 2. `approvals` at top level

```go
type ApprovalView struct { Kind string `json:"kind"`; Device string `json:"device"`; Since int64 `json:"since"` }
```

- **Sudo.** Each `approvalBook` entry (`approve.go:53-66`) gains a `since time.Time`, set in `ApproveRequest` at
  `:236`. Only `kind == "request"` entries are listed, as `"sudo"`. Enroll entries (`ApproveEnroll`, `:253-282`) are
  not listed: they have no waiting program to hint for.
  - Listed means unanswered and before the deadline, the same filter as `pending()` (`:91-102`).
  - `add`, `deliver` and `remove` call `markDirty`, and so does the expiry branch of `wait` (`:154`).
  - `time.AfterFunc(deadline-now, d.markDirty)` at creation pushes the expiry to idle subscribers.
- **SSH.** `sshSign` (`ssh.go:56-61`) gains `since`, set right after the phone is asked (`:271`), not when queued
  (`:251-259`): the hint must say "now". Listed as `"ssh"` while unanswered. `add` and `remove` (`:88-103`) call
  `markDirty`. Its 30 s timeout already ends in `remove` through the `defer` at `:262`.
- **Disconnect and unpair.** A new `d.dropApprovalsLocked(devID)` cancels the device's pending sudo entries, as
  `ApproveCancel` does (`:320-336`), and is called from `dropTrustLocked` and from `receive` after `dev.link = nil`
  (`daemon.go:1088-1091`). The waiting helper then gets the existing cancel result instead of waiting out its
  deadline. SSH signs already end on a link drop (`ssh.go:276-292`).
- **Snapshot.** `state["approvals"]` is built in `Snapshot` (`api.go:156-198`) from both books under `d.mu`, sudo
  first, oldest first. It is an empty list when there are none, never null, as `commands` is (`:153`).
- **Never** the PAM service, user, command, key, fingerprint or signature.

### 3. `dnd` per device and the `dnd.set` method

The phone today sends `flux.dnd {on}` only after a local change. Every `flux.dnd` a computer receives is applied to
the desktop and passed on (`dnd.go:138-159`, `DndSync.kt:50-61`). A phone-only set and a phone state report therefore
need a marker that both sides know.

**Wire (backward compatible).** `flux.dnd` gains two optional booleans:

- `local: true` (computer → phone): "apply this on the phone only". The phone applies it through its existing
  `DndGuard` path and does not forward it to other computers. It then answers that computer with a report.
- `report: true` (phone → computer): "this is my state, don't apply it". The computer stores it as `dev.dnd` and does
  not call `applyDnd` or `sendDnd`.

**Android** (`DndSync.kt`):

- `onPacket` with `local` applies, skips the forward at `:59`, and calls `sendReport(from)`.
- `onLinkUp` for a paired computer calls `sendReport(that computer)`, so fluxd knows the state after a connect. This is
  the gap where `start` (`:33-35`) only records the value.
- `onLocalChange` (`:38-44`) keeps its normal packet (desktops apply it as today) and adds nothing. fluxd records that
  value too (below).
- No report when the phone's Sync Do Not Disturb is off or it lacks policy access (`:52`). fluxd then has no `dnd`,
  which is the honest "unknown".

**fluxd** (`dnd.go`):

- `handleDnd` stores `dev.dnd = &on` under `d.mu` for every packet, report or not, then `markDirty`.
- A `report` packet returns right after storing.
- A normal packet goes on through `remote()`, `sendDnd`, `applyDnd` as today.
- `dropTrustLocked` and a link drop clear `dev.dnd`.

**`dnd.set {device, on}`** (`api.go`):

- Add it to `deviceMethods` (`:481-491`) and as a case in the device switch (`:405-477`). `params` gains `On *bool
  \`json:"on"\`` (`:222-244`).
- Errors:
  - `bad_params` without `on`
  - `not_supported` when the device lacks `flux.dnd` (like `ring`, `:425`)
  - `offline` when there is no link
- It sends `flux.dnd {on, local: true}` with `d.send`. It never touches `d.dnd`, `d.dndGuard` or `sendDnd`, so the
  desktop isn't changed and nothing loops.
- The result is `{"sent": true}`. The real state arrives as the phone's report in `dev.dnd`. A client that needs
  certainty watches for it.

**Old phone apps** ignore unknown fields:
- An old phone treats a `local` set as a normal one: it applies it and forwards it to other paired computers, but not
  back to this one.
- It never reports, so `dnd` stays absent until a local change.

Both cases are documented. A client should not set what it can't see, and the Nixi spec follows that rule.

**View.** `DeviceView` gains `Dnd *bool \`json:"dnd,omitempty"\``.

### 4. Docs

- `docs/ipc.md`:
  - the new fields in the snapshot keys (`:61-62`, merging the two disagreeing lines)
  - the state example (`:95-96`)
  - the method table (`:167-182`, Device row: `dnd.set`) and parameters (`:191`)
  - the approval section (`:357-371`), noting that `approvals` lists waits without their details
- `docs/features.md` Do Not Disturb (`:500-514`): the report on connect, and `dnd.set` from local clients.
- `docs/security.md`: `call` and `missedCall` put caller names and numbers into `state`. Any local client already sees
  notification text, so this widens no boundary, but say it.

## Alternatives rejected

- **Automation hooks as the call source for clients**: they run shell commands, are listed as runnable on the phone,
  have a 60 s cooldown, and have no answered or ended event.
- **A `missed` state inside `call`**: a slow reader sees only the newest snapshot, and Android's trailing cancel wipes
  it.
- **New event names (`call`, `approval`)**: the snapshot is the one contract every client already reads, and an event
  can be missed by a slow reader where a field cannot.
- **Plain `flux.dnd` for `dnd.set`**: the phone would forward it to every other computer and, with the computer's
  receive path, flip desktop Do Not Disturb.
- **Optimistic `dev.dnd` after `dnd.set`**: the phone drops the packet silently when its sync is off (`DndSync.kt:52`),
  so a guess would lie.
- **Listing enroll approvals**: nothing waits on them in a terminal.

## Risks

- **Old Android app plus `dnd.set`**: the set is forwarded to other paired computers. Mitigation: documented, and Nixi
  doesn't set without a report (an old app never reports).
- **Locking**: `callEvent` writes `c.ringing` without `d.mu` today (`telephony.go:148,166`). The new view fields must
  be written under `d.mu` only. A race test (`go test -race`) covers the writes.
- **More `markDirty` calls**: publishes are coalesced every 100 ms and deduped, so the extra cost is a byte compare.
- **Caller data in `state`**: as above, documented. No new reader gains access.
- **Hosts**: none to build here; Flux has no NixOS host step. The Android change needs an app release with the daemon.

## Verification

- `go test -race ./internal/core/... ./internal/ipc/...` with new tests next to the existing ones:
  - `telephony_test.go`:
    - ringing → talking → cancel gives the call view and then none
    - `missedCall` then the trailing ringing-cancel keeps `missedCall`
    - `dropCall` clears `call` and keeps `missedCall`
    - `missedCall` ages out after 10 minutes, using a clock seam or a short test constant
  - `approve_test.go`:
    - a request lists `sudo`, and deliver, cancel and expiry remove it
    - enroll isn't listed
    - a disconnect cancels and removes it
    - expiry publishes with no other event
  - `ssh_test.go`: `ssh` is listed only after the phone is asked, and removed on answer, timeout and hangup.
  - `dnd_test.go`:
    - a `report` packet sets `dnd` and doesn't call the backend's `Set`
    - a normal packet sets `dnd` and applies as before
    - `dnd.set` sends `local: true` and never calls `Set` or `sendDnd`
    - `not_supported`, `offline` and `bad_params`
- `go vet ./...`, and `nix flake check` for the repo's own checks (`nix/checks.nix`).
- Android: `DndGuardTest.kt` and a new `DndSyncTest` for `local` (no forward, one report) and the report on link-up.
  `./gradlew testDebugUnitTest`.
- Live, on a host with fluxd and a paired Android phone:
  - `flux-cli watch` while calling the phone, shows `call` then `missedCall`
  - `sudo -A true` with flux-approve, shows `approvals`
  - toggling DND on the phone sets `dnd`
  - an IPC `dnd.set` silences the phone and leaves the desktop unchanged (`omarchy-shell notifications dndState`)
