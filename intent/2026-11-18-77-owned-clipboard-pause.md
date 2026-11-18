---
status: draft
issue: 77
author: olafkfreund
---

# Intent: Clipboard pauses with an owner and an expiry

## Problem

`clipboard.pause` is one boolean with no owner. Two callers that both use it
can't tell their pauses apart, and a caller can't be sure its own pause ends.

### Facts

- **One flag, last call wins.** `clipboard.pause` and `clipboard.resume` take no
  parameters (`docs/ipc.md:219`) and set the single `Daemon.clipPaused`
  (`internal/core/daemon.go:58`, `internal/core/clipboard.go:283-312`).
  "Both methods are idempotent, and the last call wins" (`docs/ipc.md:452`).
- **A pause survives a restart of fluxd, when the marker write worked.**
  `PauseClipboard` writes a marker file first, `loadClipPause` pauses again
  when it exists, and it fails closed on any read error other than "not found"
  (`internal/core/clipboard.go:287-288`, `334-345`; `daemon.go:376`). If
  `writePauseFile` fails, `PauseClipboard` returns `not_saved`: the pause then
  holds in memory only and is lost on restart (`clipboard.go:287-299`). The
  docs say the pause survives a restart of fluxd, not a reboot
  (`docs/ipc.md:62`). Nothing ends a saved pause but a `clipboard.resume`.
- **The snapshot only says paused or not.** `clipboardPaused` is a boolean
  (`internal/core/api.go:179`, `docs/ipc.md:61-62`). A client can't see who
  paused or when it ends.
- **The CLI exposes it as is.** `flux-cli clipboard pause` and `resume` map to
  the two methods (`cmd/flux/features.go:88-94`; `resume` sends nil params).
  The user's own pause comes from there, from the Chat page Resume
  (`docs/features.md:139-140`) and from a clipboard manager's private mode
  (`docs/features.md:141`). The GUI call site of the Chat-page Resume was not
  checked: a grep found only `gui/qml/tools/MockBackend.qml:92`, so that it
  sends `clipboard.resume` without params is inferred.
- **Old daemons ignore unknown params.** The params struct in
  `internal/core/api.go` has no `Owner` field and `json.Unmarshal` drops unknown
  keys. The cases `clipboard.pause` and `clipboard.resume` (about `351-354`)
  call the parameterless `PauseClipboard` and `ResumeClipboard`. An owned call
  sent to an older fluxd is therefore a global call, with no error.
- **The pause guarantees have limits.** Per `docs/ipc.md:443-451`, the last copy
  is cleared and an automatic send is stopped when the pause starts, not when
  the last pause ends. A send that was already checked may still go out, and the
  watcher can lag up to 250 ms.

### What goes wrong

A client such as Nixi (olafkfreund/nixoarchy-snowflake#100) wants to pause sync
for a bounded job, for example while it handles a secret. With one boolean it
has to pick between two failures:

- It resumes at the end, and so ends a pause that the user set before or during
  its own.
- It never resumes when it is unsure, and a crash or a restart of the client
  leaves sync off, since the marker survives a restart of fluxd. The user's
  copies then stop reaching their devices without notice.

It can't do both: make sure its pause never sticks, and never undo the user's.

## Proposed outcome

- `clipboard.pause` accepts an optional `owner` (a short string) and, for an
  owned pause, a required `expires` (a positive integer duration) after which
  the pause ends by itself. Flux caps the time it accepts.
- A pause without `owner` works as today: it holds until `clipboard.resume`,
  and it survives a restart of fluxd while its marker is saved.
- An owned pause ends at its expiry. It is not kept over a restart of fluxd:
  after a restart, only the pause without an owner is back. This is an
  accepted exception to "never ended early" (see Constraints), and the client
  must check and re-acquire (see below).
- `clipboard.resume` with `owner` ends only that owner's pause. Without
  `owner` it works as today and ends the pause that has no owner. Whether it
  also ends owned pauses is open question 1.
- Sync is paused while any pause holds. Ending one pause does not resume sync
  while another holds.
- The `state` snapshot keeps `clipboardPaused` (true while any pause holds) and
  adds `clipboardPauses`, the list of pauses with their `owner` and expiry, so
  a client sees who holds the pause. The presence of `clipboardPauses` tells a
  client that this fluxd understands owners.
- A renewal (a second `pause` with the same owner) replaces the expiry and
  invalidates the earlier timer, so a stale timer can never end a renewed pause.
- `flux-cli clipboard status` and `docs/ipc.md` match the new behavior.

## Affected users and systems

- A user of `flux-cli clipboard pause` or a clipboard manager's private mode:
  nothing changes when no `owner` is given.
- The Chat page Resume and any other caller that sends `clipboard.resume`
  without params: under open question 1 option A it no longer ends an owned
  pause. The user then waits for the expiry (at most the cap) unless the call
  site uses the force path. The GUI call site must be checked in the spec.
- Nixi, the first client of owned pauses. Nixi's own change belongs to a later
  task on olafkfreund/nixoarchy-snowflake#100, not to this one.
- Code: `internal/core/clipboard.go`, `internal/core/daemon.go`,
  `internal/core/api.go`, `internal/ipc/ipc.go`, `cmd/flux/features.go`, the
  Chat-page Resume call site in the GUI, and their tests.
- Docs: `docs/ipc.md` (state, `clipboard.pause` and `clipboard.resume`
  parameters, the `not_saved` error at line 319), `docs/cli.md`,
  `docs/features.md`.
- Other clients of the socket that read `clipboardPaused` stay correct, because
  the field keeps its meaning.

## Constraints

- Must keep the current calls working: no `owner` means today's behavior,
  including the marker file and the restart. The pause then survives a restart
  only when the marker was saved; on `not_saved` it holds in memory only.
- Must keep failing closed: a copy must not leak because of a bug in the
  timing. An unreadable marker still pauses; an owned pause that can't be
  tracked is treated as held until expiry, never as ended early.
- Exception, stated on purpose: an owned pause is lost when fluxd restarts, and
  copies sync from the restart until the client re-acquires. Therefore a client
  that handles a secret must not assume its pause holds. It must read `state`
  (its own entry in `clipboardPauses`) before and during the job, and renew or
  re-pause when the entry is missing. Whether fluxd should instead hold a short
  pause after a restart is open question 7.
- Must not keep an owned pause over a restart. The marker file records only the
  pause without an owner.
- Must make an owned call fail on an older fluxd, not turn into a global one.
  A client must see `clipboardPauses` in `state` before it sends an owned
  `pause` or `resume`; how the daemon side helps is open question 6.
- Must validate every owned call and reject a bad one with `bad_params`:
  - `expires` is an integer, greater than zero, and at most the cap (zero,
    negative, missing and larger values all fail). `params.Expires` is `int64`
    today, so the check has to be explicit.
  - `owner` is non-empty, at most 64 bytes, valid UTF-8, with no control
    characters. Owner labels appear in the snapshot, so a client must treat them
    as untrusted text.
  - At most a fixed number of owners hold a pause at once (open question 8). A
    new owner beyond it fails with an error; a renewal does not.
- Must make the cap a real limit on holding sync, not only on one call. Since a
  renewal extends the pause, a heartbeat could hold sync for ever. Open question
  8 decides the total bound.
- Must keep the existing pause guarantees as `docs/ipc.md:443-451` states them,
  with their limits: the clear and the stop of an automatic send happen when a
  pause starts (not when the last one ends); the drop of copies made while
  paused and in the 250 ms after the last resume still happens when the last
  pause ends; an already-checked send and the watcher delay can still let a copy
  through.
- Must work with the clock: an expiry uses a monotonic timer, not a wall-clock
  compare, so a clock change doesn't end or stretch a pause. Each pause has a
  generation, so an expired or replaced timer can't act on a newer pause.
- Must not add a dependency.
- No deploy or host change belongs to this task. A deploy of the new `fluxd`
  to p510 needs the owner's OK.
- Tests must not touch the user's real runtime directory or live marker file
  (the existing pause tests set `clipPauseFile` to a temp path).

## Open questions

1. **Can `resume` without an owner end owned pauses?**
   - A: No. It ends only the pause without an owner. A client's pause is never
     undone by someone else's resume.
   - B: Yes. It clears everything, as "resume" means today.
   - Recommendation: A for the API, plus a `clipboard resume --all` in the CLI
     (a `force` parameter) for the user who wants out. This answers the
     issue's "never undo a pause the user set" in the other direction: the
     user can always end all of them. The Chat-page Resume is a user action,
     so it should send `force` (check the real call site first); other
     params-less callers keep A, and `docs/features.md:139-140` says so.
2. **What is the cap on `expires`, and is it a duration or a time?**
   - A: A duration in seconds, cap 1 hour.
   - B: A Unix time, cap 1 hour from now.
   - C: A duration, cap 24 hours.
   - Recommendation: A. A duration doesn't depend on the client's clock, and a
     bounded job rarely needs an hour.
3. **Does a second `pause` with the same owner replace or extend the first?**
   - A: It replaces the expiry (one pause per owner), which lets a client renew
     a pause with a heartbeat. The old timer is invalidated.
   - B: It adds another pause.
   - Recommendation: A, with a rule for overlap: two jobs must not share one
     owner string. A client uses a unique label per job (for example
     `nixi:<job id>`), so one job's resume or expiry can't end the other's.
     State this in `docs/ipc.md`.
4. **Is `owner` free text, and can a client claim any owner?**
   - A: Free text up to 64 bytes (valid UTF-8, no control characters). The
     socket has no caller identity, so an owner is a label, not a lock.
   - B: Tie it to the socket peer's process or user.
   - Recommendation: A. The socket is already limited to the user, and the
     owner's job is to avoid accidents, not to stop a hostile local client.
     State this in `docs/ipc.md`, with the note that labels are untrusted text
     in the snapshot.
5. **Does `flux-cli` get owner parameters?**
   - A: Yes: `clipboard pause --owner NAME --for 10m` and
     `clipboard resume --owner NAME`.
   - B: No, socket only for now.
   - Recommendation: A. It is a small change, and the skill and scripts use the
     CLI.
6. **How does an owned call fail on an older fluxd?**
   - A: The client checks `clipboardPauses` in `state` first and refuses to send
     an owned call when it is missing. No daemon change beyond the field.
   - B: Owned pause and resume get new method names (for example
     `clipboard.pauseOwned`), so an older fluxd answers "unknown method".
   - C: Both A and B.
   - Recommendation: B. A check-first rule is easy to skip, and an unknown
     method fails on every older daemon with no client logic. Cost: two more
     methods in `docs/ipc.md`; the plain methods keep their meaning.
7. **After a restart of fluxd, are copies synced until the client re-acquires?**
   - A: Yes, accepted. The client re-acquires from its heartbeat and a state
     check. This is the exception stated in Constraints.
   - B: fluxd holds a short pause (for example 30 s, with the same monotonic
     timer) after a start that had owned pauses before, to give clients time.
     This needs a small marker that records that an owned pause existed.
   - Recommendation: A. B brings the marker back that the intent wants to avoid,
     for a window the client's heartbeat already covers.
8. **How many owners, and what total bound on a renewed pause?**
   - A: At most 8 owners at once. A renewal may not extend an owner's pause past
     the cap measured from its first pause (so 1 hour of hold in total per
     owner); after that the client must stop or the pause ends.
   - B: At most 8 owners, renewal unbounded.
   - Recommendation: A. B lets a heartbeat that never stops hold sync for ever,
     which is the failure the cap is there to prevent.
