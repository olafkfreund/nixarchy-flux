---
status: draft
issue: 11
author: olafkfreund
---

# Intent: Reach Codex's pending questions from the phone

## Problem

From the phone you can't get to a question that Codex leaves pending.
- The issue (mirrored from upstream `bjarneo/flux#122`) runs Codex in
  Herdr, lets Codex ask a question that stays pending while its main
  composer has focus, then opens the agent in Flux for Android.
- Codex moves to pending questions and queued input with `Shift+Left`
  and back with `Shift+Right`. The phone can't send either key.
- Adding rows of harness keys made the screen too crowded in the
  reporter's prototype. They want a compact way that fits the context.
- The issue also lists bigger goals: one structured model of pending
  requests, several requests at once, requests answered on the desktop
  that drop off the phone, and no stale answers after a reconnect. It
  asks whether to do this through Herdr, Flux adapters or something else.

### Facts

Android paths are under `android/app/src/main/java/org/omarchy/flux/`.

- **What fluxd gets from Herdr.**
  - The agent list comes from the session snapshot. Per agent it keeps
    the pane, kind, name, status, folder and title
    (`internal/herdr/herdr.go:171-180`). The phone gets pane, agent,
    status, title, project and workspace (`internal/core/herdr.go:77-84`).
  - Status is one of `idle`, `working`, `blocked`, `done`, `unknown`
    (`internal/herdr/herdr.go:43-47`).
  - fluxd subscribes to pane, tab and workspace lifecycle events and to
    `pane.agent_status_changed` per agent pane
    (`internal/core/herdr.go:529-546`). None of them is about a question.
  - Output comes from `agent.read` and `pane.read`
    (`internal/herdr/herdr.go:265,280`). Nothing else says what the
    agent is asking.
- **What Herdr offers today.** The installed Herdr is 0.9.5 (protocol 22,
  `herdr --version`, `herdr api schema --json`). Its schema has:
  - the same 5 statuses;
  - an `agent_session` field on an agent or pane (`agent`, `kind`,
    `source`, `value`), meaning a session id or path, set with
    `pane.report_agent_session`;
  - no method or event that lists an agent's pending questions or answers
    one.

  So Flux can't read a structured pending question through Herdr today.
  The reporter also found `agent_session` missing on their pane.
- **Keys from the phone.** There are 3 key lists in fluxd:
  - agent replies: `enter`, `esc`, `tab`, `shift+tab`, the 4 arrows,
    `backspace`, `space`, `y`, `n` and `0`-`9`
    (`internal/core/herdr_control.go:22-28`). fluxd checks every key
    (`herdr_control.go:91-94`) and sends them with `herdr.SendKeys`, or
    with `hc.Keys` for a Herdr app pane (`herdr_control.go:96-101`);
  - plain terminal input: the same keys without digits, plus `ctrl+a` to
    `ctrl+z` (`herdr_control.go:33-43`);
  - live input: named keys mapped to bytes, no shifted arrows
    (`internal/core/herdr_terminal.go:525-528`). An unknown key gets
    "fluxd does not know that key" (`herdr_terminal.go:570-573`).

  The phone keeps copies of the agent and live lists
  (`core/HerdrModel.kt:708-709,716-717`) and drops keys that aren't in
  them (`core/Herdr.kt:314,536`). The Mac and iPhone keep a copy of the
  agent list (`macos/Sources/FluxKit/Plugins/Herdr/HerdrWire.swift:327-328`).
  Tests assert that list's size, 22 (`HerdrReplyTest.kt:205`,
  `macos/Tests/FluxKitTests/HerdrTests.swift:230`).
- **Shifted arrows downstream.**
  - `hc keys` takes `shift+left` and `shift+right` and types
    `ESC [1;2D` and `ESC [1;2C` (checked on a scratch terminal).
  - Whether Herdr's `agent.send_keys` takes them is not checked: its
    schema takes any string, and its help lists no key names.
  - A Herdr app pane has no Live (`herdr_terminal.go:320-321`), so its
    only path is `hc.Keys`.
- **The phone's agent screen.**
  - The key row is `esc`, `tab`, `↑`, `↓`, `enter`
    (`ui/AgentScreens.kt:1889`) in one fixed 40 dp row
    (`AgentScreens.kt:1800-1806`). It is hidden by default: **Show keys**
    in the menu or **Keys** in a blocked agent's dock toggles it
    (`AgentScreens.kt:367,647`, `docs/herdr.md:359`).
  - With a computer that supports live input, Live shows a 44 dp row: the
    choice digits of a blocked agent, the same 5 keys and a keyboard key
    (`AgentScreens.kt:2035-2056`). A hardware keyboard sends Shift only
    with Tab (`ui/HerdrTerminal.kt:162-172`).
  - Neither row wraps. `KeyBar`, used elsewhere, wraps keys below 48 dp
    (`AgentScreens.kt:2077-2106`).
  - Choice buttons show only when Herdr reports `blocked`
    (`AgentScreens.kt:1697-1698,2035`). They come from numbered lines or
    a menu row near the end of the output (`core/HerdrModel.kt:182`,
    `core/TermText.kt:902-981`). A pending question that doesn't block
    Codex gets no buttons.
  - After a choice the buttons stay off until new output arrives
    (`core/HerdrModel.kt:185-192`). That stops a double tap; it doesn't
    know which question has focus. A tap on a key in the key row doesn't
    turn the buttons off (`AgentScreens.kt:1694,1803`).
  - After each successful reply the phone reads the output again
    (`core/Herdr.kt:107-117`), so the thread follows the keys.
  - A prompt to a blocked agent is refused, with **Send as answer** for
    free text (`docs/herdr.md:362`). How `agent.prompt` acts while Codex
    focuses a pending question that doesn't block it is not checked.
- **Authorization.** fluxd refuses replies unless `herdr_control = true`
  (`herdr_control.go:219-231`). The phone asks for its lock first, valid
  for 5 minutes (`docs/herdr.md:367-368`); fluxd can't check that lock.
  Nothing here changes either.

## Proposed outcome

The smallest useful step, buildable now without changes to Herdr or
Codex:

- On a Codex agent, the phone can send `Shift+Left` and `Shift+Right`.
  - The key row (behind **Show keys**) and the Live row show `⇧←` and
    `⇧→` for Codex agents only, in Herdr and in the Herdr app. Other
    agents keep today's keys.
  - The keys stay at least 48 dp wide, at large font sizes too. Where a
    row would get too narrow, it wraps or shrinks as the spec decides,
    but no row shows when it is hidden today.
  - fluxd allows `shift+left` and `shift+right` in agent replies and in
    live input. The phone's and the Mac's copies of the agent list and
    the phone's copy of the live list match. The plain terminal list
    stays as it is.
  - A hardware keyboard sends Shift+Left and Shift+Right as the shifted
    keys in Live. Shift with Up or Down stays as today.
  - TalkBack reads them as "Shift Left" and "Shift Right".
  - A tap on `⇧←` or `⇧→` turns the choice buttons off until new output
    arrives, as a choice does, so an old button can't answer the newly
    focused question.
- With that, the issue's steps work by hand: Show keys, `⇧←` to the
  pending question, answer with the arrows and Enter (or type in Live),
  `⇧→` back.
- Choice buttons still show only for a blocked agent. No guessing from
  numbered text in other states.
- `docs/herdr.md` lists the new keys where it lists agent and Live keys
  (lines 177, 225, 359, 370, 810), not the plain terminal list (448).
- Tests cover the 3 allowlists, the live bytes and the hardware key
  mapping.

Known limits, stated in the docs:
- The keys are plain key presses. If the desktop answers or cancels the
  question first, `⇧←` can land on queued input or nothing, and the next
  Enter acts there. The thread shows the result after each key.
- A new phone against an old fluxd shows the keys, and fluxd refuses
  them: "The key "shift+left" is not allowed" in the dock, "fluxd does
  not know that key" in Live. Nothing reaches the agent.

Not in this issue: a structured model of pending requests, several
requests, retiring requests answered on the desktop, and no stale
answers after reconnect. They need a source that knows the questions,
which Herdr doesn't offer (open question 1).

## Affected users and systems

- Android users who answer Codex in Herdr from the phone.
- fluxd: `internal/core/herdr_control.go`, `internal/core/herdr_terminal.go`
  and their tests.
- Android: `ui/AgentScreens.kt`, `ui/HerdrTerminal.kt`,
  `core/HerdrModel.kt`, and the tests `HerdrReplyTest.kt` and
  `HerdrTerminalFocusTest.kt` or a new one.
- Mac and iPhone: the agent list in `HerdrWire.swift` and the
  `HerdrTests.swift` count only, no UI.
- `docs/herdr.md`.
- Not affected: Herdr, Codex, the packet formats (key names are strings
  already), the desktop Qt app, plain terminal input.

## Constraints

- No change to Herdr or Codex.
- No new permission, setting or packet type.
- `ctrl+c` stays out of the agent list.
- Replies keep `herdr_control` and the phone's lock.
- No new permanent row: the keys go into rows that exist, only for Codex.
- Touch targets stay at least 48 dp.
- An unknown key is refused, never sent, in both directions of a version
  mix.
- Text follows the house style.

## Open questions

1. **Structured pending requests: Herdr, Flux adapter, or later?**
   Proposed: later, in a new issue. Herdr 0.9.5 has no API for pending
   questions, only a session reference. The reporter's probe used the
   Codex App Server beside the TUI, and they say the pane-to-session
   mapping isn't reliable. Both are large and outside this repo. This
   issue does the keys.
2. **Which agents get `⇧←` and `⇧→`?** Proposed: only agents whose kind is
   `codex`, so other agents keep a 5-key row. fluxd allows the keys for
   every agent, as it does `shift+tab`; only the phone hides them.
3. **Which key lists change?** Proposed: agent replies and live input.
   The plain terminal list (`herdr_control.go:33-43`,
   `HerdrModel.kt:712`, `HerdrWire.swift:330-331`) stays.
4. **Show choice buttons when Codex isn't `blocked`?** Proposed: no. The
   issue asks not to guess from numbered text, and a wrong button answers
   the wrong question.
5. **Claude's multi-select and free-text dialogs.** Proposed: out of
   scope. The issue doesn't report them as failing.
6. **Test:**
   1. First, before the spec is approved: on a scratch Herdr pane, check
      that `herdr agent send-keys <pane> shift+left` types `ESC [1;2D`. If
      it doesn't, the dock path for Herdr agents can't work without a
      Herdr change, and the scope shrinks to Live and the Herdr app.
   2. Unit tests: fluxd allowlists and live bytes (`1b 5b 31 3b 32 44`,
      `1b 5b 31 3b 32 43`), unknown keys still refused; Android key sets,
      hardware mapping and TalkBack names; the Swift agent list.
   3. On the Pixel against the running fluxd, Codex with a pending
      question: Show keys, `⇧←`, answer, `⇧→`; the same in Live, and with
      a hardware keyboard; once in the Herdr app; once at a large font
      size with choice digits in the Live row.

## Review changes

Codex and agy reviewed the first draft, read only. Each finding was
checked against the code.

| Finding | Source | Verdict | Change |
| --- | --- | --- | --- |
| Herdr's `send_keys` and `hc keys` may not take `shift+left` | agy (high), Codex | `hc`: refuted, it types `ESC [1;2D` (scratch terminal). Herdr: unverified | Fact added; Q6.1 checks Herdr before the spec is approved |
| Two more keys in non-wrapping rows fall below 48 dp | agy (high), Codex | Confirmed, `AgentScreens.kt:1801,2037` vs `KeyBar` `:2077-2106` | Outcome and Constraints require 48 dp; spec picks wrap or shrink |
| Thread doesn't refresh after a key outside Live | agy (high) | Refuted: a successful reply rereads, `Herdr.kt:107-117` | Fact added |
| Text answer outside Live may go to the main composer | agy | Unverified | Outcome answers with arrows and Enter, or text in Live; stated in Facts |
| "Waiting for new output" overstated as wrong-question protection; keys don't close choices | Codex, agy | Confirmed, `HerdrModel.kt:185-192`, `AgentScreens.kt:1694,1803` | Fact reworded; `⇧←`/`⇧→` now close the choices; limit stated |
| Herdr app path (`hc.Keys`) not in the test | Codex, agy | Confirmed, `herdr_control.go:96-101`, `herdr_terminal.go:320-321` | Added to outcome and Q6.3 |
| Hardware Shift with any arrow vs only Left and Right | agy | Confirmed contradiction | Narrowed to Left and Right |
| Docs lines: 448 is plain terminal; 177, 225, 359, 810 missing | agy, Codex | Confirmed | Listed in the outcome |
| Old fluxd refuses in 2 ways; new keys need new fluxd | agy, Codex | Confirmed, `herdr_control.go:93`, `herdr_terminal.go:572` | Known limits added |
| fluxd subscribes to more than status events | Codex, agy | Confirmed, `herdr.go:529-546` | Reworded |
| `HerdrWire.swift:330-331` is the terminal list; tests assert 22 keys | agy | Confirmed, `HerdrTests.swift:230`, `HerdrReplyTest.kt:205` | Cites fixed; tests in Affected |
| Q3 mixed Live and plain terminal input | Codex, agy | Confirmed, 3 lists | Q3 rewritten |
| Choices also come from menu rows | Codex, agy | Confirmed, `TermText.kt:946-981` | Reworded |
| `herdr_control.go:62` is the message, not the check; fluxd can't check the lock | Codex, agy | Confirmed, `herdr_control.go:219-231` | Reworded |
| Live row needs input support, not "always" | agy | Confirmed, `docs/herdr.md:175` | Reworded |
| `agent_session` is a field; `report_agent_session` sets it | agy | Confirmed in the schema | Reworded |
| `send-keys --help` lists no key names | Codex | Confirmed | Reworded |
| Q1 to Q5 proposals | both | Agreed | Kept |
| Enter in Live clears the unsent-text banner; `⇧` glyph rendering | agy (low) | Unverified | Left for the spec |
