---
status: draft
issue: 45
author: olafkfreund
---

# Intent: Resumable receives — history noise, false stalls, reconnects and weak tests

Follow-up to #42/#43 (`intent/2026-11-11-42-browse-resume-hardening.md`).
Revised after Codex's review of the first draft (agy's review timed out).
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
   `receiveResumable` checks it. Malformed metadata is refused earlier, before
   the queue (line 159). The packets below are refused or do nothing, yet each
   leaves an entry when a receive slot is free:
   - a chunk with no offer (`receiveResumable` line 463), which leaves "failed";
   - a retry of a canceled ID (line 454), which leaves "failed";
   - a 17th unfinished offer (`admitOffer`, line 389) or an offer refused
     for space (line 465), which leaves "failed";
   - a re-offer of a finished ID (line 457), which leaves an empty "done"
     entry with no path. The chat lists the history, so this entry shows up
     there as a second card with no file actions.

   The history keeps 100 finished entries (`maxTransfers`, `share.go:29`;
   running and waiting entries also stay above that number), so a paired
   device can push real transfers out of it with packets like these.
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
   with an extra "failed" entry. A packet can also wait in the queue for longer
   than `receiveIdle`. The worker sweeps before it handles each packet
   (line 189), so even the arrival time would already count as idle at that
   point.
3. **Reconnects end the run.** A run keeps the link it started on
   (`run.link`, line 229), and that link is never updated. When the phone
   reconnects, the sweep ends the run as soon as the old link closes
   (lines 283-285). A packet that the old link drops in the middle also ends
   the run (lines 245, 256). In both cases the transfer resumes on the new link
   as a new run, with an extra "failed" entry.
4. **Missing tests.** Nothing covers:
   - a peer that stops in the middle of a chunk's payload; the 1-minute
     payload timeout (line 498) is hard-coded, so a test cannot shorten it;
   - a copy across filesystems that fails part way (lines 580-612).
5. **Weak tests** (`internal/core/resume_test.go`):
   - `TestResumeSlowChunkIsNotIdle` (line 743) can pass when `run.last` is
     never refreshed. The next chunk can start a new run on the same partial
     file, and that run still answers "offset". The test looks at the newest
     matching entry (line 216), not at the one the transfer started with.
   - `TestResumeCancelWithFullEvents` (line 655) is meant to test a cancel that
     only the ticker sees.
     - It fills `w.events` before it cancels, so the cancel's own event is
       dropped.
     - That earlier event is still waiting, so the cleanup can run through the
       events branch (line 192) instead of the ticker.
     - The test would still pass with the ticker branch removed.
   - `TestResumeCancelRacesCompletion` (line 684) accepts either final state,
     done or canceled. That is useful race coverage, but it never shows that a
     cancel at a fixed point before publishing wins. Here "publish" is the
     moment `publish()` sets the state to "done" (line 572), before the rename;
     a cancel after that point no longer applies.

## Proposed outcome

- A refused or no-op resumable packet answers the sender as today and adds
  no history entry. It takes no receive slot beyond the time the worker
  spends on it. A run, its slot and its entry start only once the packet has
  been accepted.
- A re-offer of a finished ID answers "done" even while 4 receives run, and
  it adds no entry.
- The stall check is per run. A run is stalled only when `receiveIdle` has
  passed since its last finished packet and none of its own packets is waiting
  in the queue. Packets of other transfers, or the worker being busy with
  them, never keep a run alive. The payload deadline of a single chunk stays
  as it is.
  - Tests: run A keeps the worker busy while B has a packet waiting, and B is
    not ended. Run A keeps the worker busy while B sends nothing, and B ends
    after `receiveIdle`.
- A reconnect keeps the run and its history entry. When the run's link
  closes, the run is not ended at once; it gets `receiveIdle` to receive a
  packet on a newer link of the same, still paired device. The same applies
  when the old link drops a packet in the middle. The current-link and pairing
  checks of each packet and of publishing stay. A device that does not come
  back still ends as "failed", one `receiveIdle` later than today.
- New tests cover a payload stall and a cross-filesystem copy that fails part
  way.
  - The three tests above fail when the bug they guard comes back.
  - The race test stays, and deterministic tests are added for a cancel just
    before publishing and one just after.
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
   - Proposed: no entry. Log one line, rate-limited per device so that the
     log cannot be flooded instead, and let the sender show the error it
     receives, as it does today.
   - Otherwise a device can still flush the history by sending refused
     offers with new IDs.
   - The alternative is a desktop notification for a space refusal only. It
     is rate-limited, and nothing is added to the history.
2. **Reconnect.** Carry a run over to the new link (proposed)? Or keep
   today's behaviour, where the only cost is one extra "failed" entry per
   reconnect, and drop item 3 from this task?
   - Carrying over adds one rule, described under Proposed outcome: a run whose
     link closed waits up to `receiveIdle` for the device's next link instead
     of ending at once.
3. **Scope.** All five items in one branch and PR (proposed, because they all
   touch the same worker and its tests), or split item 3 into its own task?
