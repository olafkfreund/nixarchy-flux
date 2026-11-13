---
status: approved
issue: 57
author: olafkfreund
---

# Intent: calls, waiting approvals and phone Do Not Disturb in the state snapshot

## Problem

A local client reads Flux through `subscribe`, and the `state` snapshot (`internal/core/api.go:156-198`) is what it
gets. A slow reader gets only the newest snapshot (`docs/ipc.md:101-103`). Three things a desk companion needs are not
in it:

- **Calls.** fluxd tracks a ringing call (`internal/core/telephony.go:69-71`), pauses media and posts "Call from X" and
  "Missed call from X" popups. The snapshot has no call field, though.
  - Ringing and missed calls reach only automation rules (`call.ringing`, `call.missed`, `telephony.go:168,178`). Those
    run a shell command, and the same commands are listed as runnable on the phone.
  - Answered and ended calls reach nothing.
  - The caller is the contact name or the number, not both (`telephony.go:84-91`), so a client can't text a contact
    back.
- **Approvals waiting on the phone.** While `sudo` (through `flux-approve`, at most 20 s, `approve.go:25`) or an SSH
  sign (at most 30 s, `ssh.go:32`) waits, only the program that asked knows. `ssh` prints nothing while it waits
  (`docs/ssh.md`). Expired requests are cleaned up only through `approve.wait` (`approve.go:75-79,139-155`).
- **The phone's Do Not Disturb.** Sync Do Not Disturb mirrors the desktop's state to an Android phone and back
  (`docs/features.md:500-514`). A local client can't see whether the phone is silent. It can't silence the phone
  without changing the desktop's own Do Not Disturb.

The Nixi companion (olafkfreund/nixi-nixarchy#231) wants to:
- go quiet during a call, say who is calling, and offer Text back after a missed call
- show "approve on your Pixel" while a request waits
- silence the phone during meetings it senses

Today it can do none of this without automation hooks.

## Proposed outcome

`state` carries, read-only:

- **Per device, `call`**: `{state: "ringing" | "talking", caller, number, since}`, absent with no call.
  - `number` is empty when the phone sent none.
  - It clears when the call ends, the phone stops ringing, or the link that reported it drops, as `dropCall` does.
- **Per device, `missedCall`**: `{caller, number, at}` for the newest missed call.
  - It stays until the next call or for a bounded time, so a slow reader still sees it.
  - It survives the "stopped ringing" packet that Android sends right after "missed call" (`android/.../Calls.kt:49`).
- **At top level, `approvals`**: a list of `{kind: "sudo" | "ssh", device, since}`, one per request waiting on a
  phone.
  - An entry goes away when the request is approved, refused, cancelled, times out, or its device disconnects or is
    unpaired.
  - Subscribers get a new snapshot when it goes away, without waiting for another event.
- **Per device, `dnd`**: the phone's Do Not Disturb as the phone last reported it, or absent when unknown.
- **A way for a local client to set one Android phone's Do Not Disturb** without touching the desktop's.

`docs/ipc.md` documents all of it. Existing clients keep working: the Qt app (`gui/app/src/fluxbackend.cpp:198-208`),
the shell plugin, the CLI and `flux-native` ignore unknown fields.

## Affected users and systems

- Daemon: `internal/core/telephony.go`, `approve.go`, `ssh.go` (the SSH sign wait), the DND sync code, the device views
  in `api.go` and `device.go`.
- `docs/ipc.md`, `docs/features.md`, Go tests.
- The Android app, only if the phone doesn't yet report its Do Not Disturb when the desktop asks or the phone changes.
  The iOS and macOS apps don't change.
- Every IPC client sees the new fields; none has to change.

## Constraints

- **Read-only additions to `state`**: no new socket and no new event name. The Do Not Disturb change is the only new
  action, if the spec keeps it a method.
- **No secrets in `approvals`.** Never the sudo command, user, key text, fingerprint or signature: kind, device and
  time only. The wording is "approve on the phone", since SSH also accepts the phone's screen lock
  (`docs/security.md:139-140`).
- **Signature checks don't change.** The root-owned helper still verifies (`approve.go:3-6`), and the approval wire is
  untouched.
- **Caller and number are what the phone sent**, the same as the popup shows. A phone with Call alerts off sends
  nothing, so it has no `call` or `missedCall`.
- **Setting the phone's Do Not Disturb must not loop back.** Sync Do Not Disturb must not then turn the desktop's on or
  off.
- Snapshot size and the 100 ms coalescing stay as they are.

## Open questions

- Phone Do Not Disturb from a client: a new `dnd.set {device, on}` method, or no Flux action at all? With no action,
  Nixi would turn on the desktop's Do Not Disturb and let the sync carry it, which also silences the desktop.
- How long does `missedCall` stay: until the next call, a fixed 10 minutes, or until a client clears it?
- Outgoing calls also report "talking" (`Calls.kt`). Is a `direction` field worth adding, or is "talking" enough?
