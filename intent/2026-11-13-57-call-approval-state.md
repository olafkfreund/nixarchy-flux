---
status: draft
issue: 57
author: olafkfreund
---

# Intent: call status and waiting approvals in the state snapshot

## Problem

A local client reads Flux through `subscribe`, and the `state` snapshot (`internal/core/api.go:156-198`) is all it gets.
Two things a desk companion needs are not in it:

- **Calls.** fluxd tracks a ringing call (`internal/core/telephony.go:69-71`), pauses media and posts a "Call from X"
  popup, but the snapshot has no call field. Ringing and missed calls reach only automation rules (`call.ringing`,
  `call.missed`, `telephony.go:168,178`), which run a shell command and are also listed as runnable on the phone
  (`docs/security.md:71`). Answered and ended calls reach nothing.
- **Approvals waiting on the phone.** While `sudo` (through `flux-approve`) or an SSH sign through the phone's agent
  waits for the fingerprint, only the program that asked knows (`approve.wait`, `docs/ipc.md:363-368`). The user sees a
  dim terminal line, which can be hidden.

The Nixi companion (olafkfreund/nixi-nixarchy#231) wants to go quiet during a call, say who is calling, and show "touch
the fingerprint sensor on your Pixel" while a request waits. Today it can't do any of these without polling
automation hooks.

## Proposed outcome

`state` carries, read-only:

- Per device, `call`: `{state: "ringing" | "talking", caller, since}`. It is absent with no call. It clears when the
  call ends, the phone stops ringing, or the link that reported it drops, as `dropCall` does today.
- At top level, `approvals`: a list of `{kind: "sudo" | "ssh", device, since}` for each request waiting on a phone. An
  entry goes away when the request is approved, refused, cancelled or timed out.

`docs/ipc.md` documents both, and existing clients keep working: the Qt app, shell plugin, CLI and browser extension
ignore fields they don't know.

## Affected users and systems

- `internal/core/telephony.go`, `internal/core/approve.go`, the SSH agent sign path (`internal/sshagent`),
  `internal/core/api.go` / `device.go` views, `docs/ipc.md`, Go tests.
- Every IPC client sees the new fields; none needs to change.
- The phone apps and the wire to the phone don't change.

## Constraints

- **No new method, no new socket, no new event name.** Only fields in `state`.
- **No secrets in `approvals`.** Never the sudo command, user, key text, fingerprint or signature. Kind, device and
  time only.
- **Signature checks don't change.** The root-owned helper still verifies (`internal/core/approve.go:3-6`), and the
  approval wire is untouched.
- **The caller field follows what Flux already shows.** It is the name or number the phone sent, the same text as the
  "Call from" popup. A phone with Call alerts off sends nothing, so it has no `call`.
- Snapshot size and the 100 ms coalescing stay as they are.

## Open questions

- Should `call` also cover a missed call for a short time (`state: "missed"`), or does the existing `call.missed`
  automation suffice? The Nixi side wants a missed-call card, so a short-lived `missed` would save it from watching a
  hook.
- Is an SSH sign through the phone's agent long enough to be worth an entry, or only sudo?
