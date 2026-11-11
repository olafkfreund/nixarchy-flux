---
status: approved
issue: 42
author: olafkfreund
---

# Intent: Close the Browse PC symlink leak and bound resumable receives

Covers #42 and #43. Revised after Codex and agy reviewed the first draft.

## Problem

Two paths in fluxd let a **paired** device reach further than the user
allowed.

### Browse PC (#42)

Browse PC hides every dot-folder under home (`~/.ssh`, `~/.gnupg`,
`~/src/app/.git`, at any depth) and the Flux folders. The Flux folders are
already checked by their real path (`internal/core/sftp.go:452-458,
514-528`). The dot-folder check is not:

- `visible` (`sftp.go:514-528`) checks dot-names only relative to the root
  being read. When `~/Documents` is a symlink to `~/.ssh`, files under it
  have no dot-name relative to that root, so the phone can read
  `Documents/id_ed25519`. The Home root would refuse the same file.
- `browseRoots` (`sftp.go:372-394`) compares paths as text, without
  resolving symlinks. With a symlinked home (`/home/u → /var/home/u`, or a
  NixOS impermanence layout), `download_dir = /var/home/u/.ssh` is accepted
  as a root, and a `download_dir` that holds the real home is not left out.
- A root that is itself a dot-folder resolves to `"."` and is served without
  a dot-name check (`sftp.go:504-506`).

The phone cannot create these symlinks or settings; they must already exist,
from a dotfiles setup, a moved folder, or `download_dir`.

### Resumable receives (#43), `internal/core/resume.go`

1. **No free-space check, two copies.** The ordinary receive checks space
   before and while it writes (`checkSpace`, `spaceWriter`,
   `internal/core/share.go:554-588`). A resumable transfer declares up to
   1 TiB (`resume.go:150`), writes chunks under `DataDir()/incoming`, then
   copies the whole file into `DownloadPath()` (`resume.go:227-238`). The two
   may be different filesystems, and on one filesystem the copy needs twice
   the size. Nothing is checked.
2. **Unbounded retained state.** Every offer, and also a `chunk` with no
   offer, creates a folder with `state.json` and `data` (`resume.go:153-173`).
   A device can send offers without data and fill the disk or its inodes.
   Pruning runs only on an offer, after 7 days, by folder time, and every
   packet refreshes that time, also a refused one (`resume.go:125, 156`).
   Final copies interrupted by a crash (`.flux-received-*`) are never cleaned
   up, because they do not match the stale-part pattern (`share.go:56-59`).
3. **Unbounded goroutines, one global lock.** Each offer or chunk starts a
   goroutine before it takes the daemon-wide `resumeMu` (`resume.go:122-124`),
   and the lock is held during a network fetch of up to a minute and during
   hashing and copying of the whole file. One slow device blocks every
   device's resumable receives, and a flood of packets piles up goroutines.
   The limit of 4 receives per device (`maxReceives`, `share.go:33`) does not
   apply.
4. **Pairing and cancellation are not checked while it runs.** Pairing and
   the link are checked when the packet arrives, not after the wait, not while
   the chunk is read (the ordinary receive checks on every read,
   `share.go:507-512`), and not before the final file is published. Hashing
   and the final copy cannot be cancelled.
5. **Invisible while it runs.** The transfer record is made only after the
   file is complete (`resume.go:262`). While it receives, the user cannot see
   or cancel it, and `Busy()` (`daemon.go:398-436`) misses it, so an automatic
   restart after an update can cut it.

## Proposed outcome

**Browse PC**

- Browse PC never serves a path whose real path has a dot-name below the
  real home folder, or is a Flux folder, whichever root, symlink, or
  `download_dir` leads there. Roots are compared by real path.
- Regression tests cover `~/Documents → ~/.ssh`, a symlinked home with
  `download_dir` set to a dot-folder through the real path, and a root that
  holds the real home.

**Resumable receives**

- An offer is refused, and a running receive stops, when the folder it writes
  to lacks room. Both the staging folder and the download folder are checked,
  counting the final copy, with the same margin and message as an ordinary
  receive.
- The data a device can keep in partial resumable transfers is bounded in
  count and bytes, without new settings. Old and abandoned partials and
  crashed final copies are cleaned up. A refused packet does not extend
  retention.
- Resumable receives from one device count against the existing per-device
  receive limit, a slow device does not block other devices, and the number
  of queued packets is bounded.
- Over a limit, fluxd answers with the existing `error` message, so the
  sender fails fast instead of waiting a minute.
- Before it writes, while it reads a chunk, and before it publishes the final
  file, a resumable receive checks that the device is still paired and on the
  same link. Hashing and the final copy stop on cancel or unpair.
- A resumable receive shows as an incoming transfer while chunks arrive, can
  be cancelled, and counts in `Busy()`. A transfer whose sender goes quiet or
  disconnects stops counting after an idle time, like `receiveIdle`, and
  keeps its partial data so it can resume.
- The ordinary receive, Browse PC for normal folders, and the wire format of
  the resumable protocol do not change, so existing apps keep working.

## Affected users and systems

- Every Flux desktop (fluxd): the Arch package and the Nix flake.
- Phones, Macs and Windows clients that use Browse PC or resumable transfers:
  no app change.
- Code: `internal/core/sftp.go`, `internal/core/resume.go`,
  `internal/core/share.go`, and their tests. Browse search
  (`d.searchBrowse`) to be checked in the spec for its own walk.

## Constraints

- No wire-format change: Android, iOS, macOS and Windows clients must work
  unchanged. Only the existing `offset`, `done` and `error` replies are used.
- Reuse the existing space check, receive limit, transfer records and
  stale-file cleanup; no new configuration options.
- Upstream (`bjarneo/flux`) has the same bugs; keep the change small and
  self-contained so it can also be offered upstream.
- Tested in `go test ./internal/core/...`, and passing inside the Nix build
  sandbox.

## Decisions (approved 2026-11-11)

The owner approved the recommended answer to each question below.


1. **Dot-folders outside home.** Should Browse PC also refuse dot-folders
   outside home (a root such as `/mnt/nas/.private` set as `download_dir`, or
   `~/Documents → /mnt/nas/share/.stversions`)? Recommended: yes, refuse any
   dot-name below any root's real path, which also covers home.
2. **Cancel of a resumable receive.** On cancel, delete the partial data and
   answer later packets of that transfer ID with `error`, or keep the partial
   for a resume? Recommended: delete it and refuse the ID, since cancel means
   the user does not want the file.
3. **Bounds for partial data.** What limits, without settings? Recommended:
   per device at most 16 partial transfers and the free-space rule, partials
   pruned after 7 days by last real progress, and crashed final copies
   removed at start.
4. **Upstream.** Offer the fixes as a PR to `bjarneo/flux` after they land
   here? Recommended: yes, as a separate step with your OK, since it is
   outward-facing.
5. **One task.** Keep #42 and #43 in this one task? Recommended: yes, one
   commit per issue.
