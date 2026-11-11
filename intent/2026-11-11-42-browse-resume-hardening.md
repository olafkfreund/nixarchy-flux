---
status: draft
issue: 42
author: olafkfreund
---

# Intent: Close the Browse PC symlink leak and bound resumable receives

Covers #42 and #43.

## Problem

Two paths in fluxd let a **paired** device reach further than the user
allowed.

**Browse PC (#42).** Browse PC hides every dot-folder of home (`~/.ssh`,
`~/.gnupg`, `~/.config`) and the Flux folders. It checks the dot-names
relative to the root being read only (`internal/core/sftp.go:513-526`,
`visible`). When a browse root such as `~/Documents` is a symlink to
`~/.ssh`, the files under it have no dot-name relative to that root, so the
phone can read `Documents/id_ed25519`. The Home root would refuse the same
file as `.ssh/id_ed25519`. The phone cannot make the symlink itself; it must
already exist, for example from a dotfiles setup or a moved folder.

**Resumable receives (#43),** `internal/core/resume.go`:

1. **No free-space check.** The ordinary receive checks space before and
   while it writes (`checkSpace`, `spaceWriter`, `internal/core/share.go:462-503`).
   A resumable transfer declares up to 1 TiB (`resume.go:150`), writes its
   chunks to the resume folder, then makes a second full copy in the
   download folder (`resume.go:229-238`), with no check. A paired device can
   fill the disk, also by leaving partial transfers behind (pruned only after
   7 days, `resume.go:125`).
2. **Unbounded goroutines, one global lock.** Each offer or chunk starts a
   goroutine before it takes the daemon-wide `resumeMu` (`resume.go:121-123`),
   and the lock is held during a network fetch of up to a minute
   (`resume.go:186`). One slow or stalled device blocks resumable receives of
   every device, and a flood of packets piles up goroutines with their
   packets. The ordinary limit of 4 receives per device (`maxReceives`) does
   not apply.
3. **No recheck after the wait.** Pairing and the link are checked when the
   packet arrives, not after the goroutine gets the lock, so work queued
   before an unpair or a reconnect can still write files.
4. **Invisible while it runs.** The transfer record is made only after the
   file is complete (`resume.go:262`). While it receives, the user cannot see
   or cancel it, and `Busy()` misses it, so an automatic restart after an
   update can cut it.

## Proposed outcome

- Browse PC never serves a file whose real path is a dot-folder of home or a
  Flux folder, whichever root or symlink leads there. A regression test
  covers `~/Documents → ~/.ssh`.
- A resumable receive refuses an offer, and stops a running receive, when
  the disk lacks room, with the same rule and message as an ordinary
  receive. A test covers both.
- Resumable receives from one device are bounded like ordinary receives, a
  stalled device does not block other devices, and excess packets are
  dropped instead of queued without limit.
- After any wait, a resumable receive checks that the device is still paired
  and on the same link before it writes.
- A resumable receive shows as an incoming transfer while it runs, can be
  cancelled like one, and counts in `Busy()`.
- The ordinary receive, Browse PC for normal folders, and the resumable
  protocol on the wire do not change, so existing phones keep working.

## Affected users and systems

- Every Flux desktop (fluxd): the Arch package and the Nix flake.
- Phones and Macs that use Browse PC or resumable transfers: no app change.
- Code: `internal/core/sftp.go`, `internal/core/resume.go`, and their tests;
  possibly `internal/core/share.go` for shared helpers.

## Constraints

- No protocol change: Android, iOS, macOS and Windows clients must work
  unchanged.
- Reuse the existing space check, receive limit and transfer records; no new
  configuration options.
- Upstream (`bjarneo/flux`) has the same bugs; keep the change small and
  self-contained so it can also be offered upstream.
- The fixes are tested in `go test ./internal/core/...`, and pass inside the
  Nix build sandbox.

## Open questions

1. Offer the fixes upstream as a PR to `bjarneo/flux` after they land here?
   Recommended: yes, as a separate step after merge, with your OK, since it
   is outward-facing.
2. One task for both issues (this intent), or split into two? Recommended:
   one task, two commits, since both are fluxd hardening of file access by a
   paired device.
