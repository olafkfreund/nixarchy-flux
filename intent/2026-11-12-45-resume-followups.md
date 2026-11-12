---
status: draft
issue: 45
author: olafkfreund
---

# Intent: Resumable receives — history noise, false stalls, reconnects and weak tests

Follow-up to #42/#43 (`intent/2026-11-11-42-browse-resume-hardening.md`).
None of these loses data or gets past a limit. They make the transfer
history wrong or noisy, end working transfers early, and leave tests that
would pass on a broken fix.

## Problem

All in `internal/core/resume.go` unless named otherwise. One worker per
device handles that device's resumable packets one at a time
(`runResumeWorker`, lines 182-210).

1. **Entries for packets that do nothing.** `processResumePacket`
   (lines 212-243) counts a receive (`startReceive`) and adds a history
   entry (`newTransfer`) for every packet without a run, **before**
   `receiveResumable` checks it. The following packets are refused or do
   nothing, yet each leaves an entry:
   - a chunk with no offer (`receiveResumable` line 463), which leaves "failed";
   - a retry of a canceled ID (line 454), which leaves "failed";
   - a 17th unfinished offer (`admitOffer`, line 389) or an offer refused
     for space (line 465), which leaves "failed";
   - a re-offer of a finished ID (line 457), which leaves an empty "done"
     entry with no path, and a second "Received" in the chat.

   The history keeps 100 entries (`maxTransfers`, `share.go:29`), so a
   paired device can push real transfers out of it with packets like these.
   A re-offer of a finished ID also takes one of the 4 receive slots
   (`maxReceives`, `share.go:33`), so it is refused while 4 other receives
   run, and the sender sees an error for a file that is already here.
2. **Stalls that are not real.** A run ends with `errStalled` when
   `receiveIdle` (1 minute) has passed since the worker last *finished* one
   of its packets (lines 259, 290). While the worker is busy with a long
   packet of one transfer, packets of the other transfers wait in the queue.
   A long packet is a final hash of a large file, or a chunk close to its
   1-minute timeout (line 498). The next sweep can then end those other runs
   although their packets have already arrived. They resume as new runs, each
   with an extra "failed" entry.
3. **Reconnects end the run.** A run keeps the link it started on
   (`run.link`, line 229). When the phone reconnects, the sweep ends the run
   as soon as the old link closes (lines 283-285). The transfer resumes on
   the new link as a new run, with an extra "failed" entry.
4. **Missing tests.** Nothing covers:
   - a peer that stops in the middle of a chunk's payload; the 1-minute
     payload timeout (line 498) is hard-coded, so a test cannot shorten it;
   - a copy across filesystems that fails part way (lines 580-612).
5. **Weak tests** (`internal/core/resume_test.go`):
   - `TestResumeSlowChunkIsNotIdle` (line 743) also passes when `run.last`
     is never refreshed. The next packet makes a new run, which still answers
     "offset".
   - `TestResumeCancelWithFullEvents` (line 655) is meant to test the cancel
     that only the ticker sees, because the events channel is full. It sends
     another offer after the cancel, and that offer makes the worker sweep
     anyway.
   - `TestResumeCancelRacesCompletion` (line 684) passes with either outcome,
     done or canceled, so it does not show that a cancel before the file is
     published wins.

## Proposed outcome

- A refused or no-op resumable packet answers the sender as today and adds
  no history entry. It takes no receive slot beyond the time the worker
  spends on it. A run, its slot and its entry start only once the packet has
  been accepted.
- A re-offer of a finished ID answers "done" even while 4 receives run, and
  it adds no entry.
- A run counts as stalled only when no packet **arrived** for it for
  `receiveIdle`. A packet waiting in the worker's queue counts as arrived,
  however long the worker is busy elsewhere.
- A reconnect keeps the run and its history entry when the next packet comes
  on the device's new link. A device that does not come back still ends the
  run, as it does today.
- New tests cover a payload stall and a cross-filesystem copy that fails part
  way. The three tests above fail when the bug they guard comes back.
- No protocol change: senders (Android, iOS, macOS, another desktop) need
  no update.

## Affected users and systems

- `internal/core/resume.go`, `internal/core/resume_test.go`, possibly
  `internal/core/share.go` (the receive counter and the history helpers it
  shares with the ordinary receive).
- Desktop users of fluxd. Their transfer history and chat show fewer
  "failed" entries and no empty "done" entries.
- The ordinary (non-resumable) receive in `share.go` keeps its behaviour.

## Constraints

- No protocol or on-disk format change. `incoming/<hash>/state.json` and
  `data` stay as they are.
- The #42/#43 limits stay exactly as strict: `maxReceives`, `maxPartials`,
  `maxRecords`, the space checks, pairing and link checks, and cancel
  tombstones.
- One worker per device, with no new goroutine per packet.
- `go test -race ./internal/core/` passes, with no new sleeps that are long
  or flaky.

## Open questions

1. **Refused offers in the history.** If a new offer is refused because of
   space, the 17th-unfinished limit, or the 5th concurrent receive, should
   the desktop show anything?
   - Proposed: no entry. Log one line, and let the sender show the error it
     receives, as it does today.
   - Otherwise a device can still flush the history by sending refused
     offers with new IDs.
   - The alternative is a desktop notification for a space refusal only. It
     is rate-limited, and nothing is added to the history.
2. **Reconnect.** Carry a run over to the new link (proposed)? Or keep
   today's behaviour, where the only cost is one extra "failed" entry per
   reconnect, and drop item 3 from this task?
   - Carrying over adds one rule: when a run's old link closes while the
     device already has a newer link, the run waits for `receiveIdle`
     instead of ending at once.
3. **Scope.** All five items in one branch and PR (proposed, because they all
   touch the same worker and its tests), or split item 3 into its own task?
