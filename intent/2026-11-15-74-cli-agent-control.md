---
status: draft
issue: 74
author: olafkfreund
---

# Intent: Agent control of Flux through flux-cli and the skill

## Problem

The owner wants a coding agent, such as Claude Code or Codex, to use every
Flux feature on the computer. The agent should do this through `flux-cli`,
following the `omarchy-flux` skill, instead of through an MCP server
(owner, 2026-11-15: "if the cli is good we can use that with a proper
skill"). Today the CLI covers most features, but not all of them.

### Facts

- **Reading is covered.**
  - `flux-cli status --json` prints the whole `fluxd` state: devices with
    their notifications, SMS conversations, outbox, battery and DND, plus
    the clipboard, transfers, streams, approvals, herdr and settings.
  - `flux-cli watch` prints one JSON line for each state change.
- **Some write actions have a socket method but no CLI command.**
  Each method below is in the socket API (`docs/ipc.md:176-250`), and no
  file in `cmd/flux/*.go` calls it:
  - `notification.dismiss` for one notification. The CLI has only
    `notifications clear`, which calls `dismissAll` (`cmd/flux/main.go:884-913`).
  - `notification.reply` and `notification.action`.
  - `sms.thread` (the last 100 messages of one conversation) and
    `sms.refresh`. The CLI only sends (`cmd/flux/main.go:179-183`).
  - `dnd.set`.
  - `transfer.open` and `transfer.copy`.
  - `share.text`.
  - `settings.set` for most global keys. The CLI sets `clipboardLimit`,
    the motion keys, `remoteInput` and `remoteDesktop`
    (`cmd/flux/features.go:76`, `motion.go:33`, `streams.go:152`). It
    can't set `autoClipboard`, `notifications`, `shareHome`,
    `pauseMediaOnCall`, `syncDnd`, `herdr`, `herdrControl`,
    `herdrTerminals`, `checkUpdates`, `name` or `downloadDir`
    (`docs/ipc.md:245-247`).
- **What an agent gets back is uneven.**
  - Some commands print the JSON result (`printCall`, for example
    `snippets`).
  - Others print a sentence ("Dismissed 3 notifications"), or nothing.
  - Only `status`, `ssh` and `watch` take `--json`.
  - Every error exits with 1 and a message (`cmd/flux/main.go:293-300`).
    The socket's error code, such as `offline`, `ambiguous` or
    `not_supported` (`docs/ipc.md:298`), never reaches the caller, so an
    agent has to parse the message text.
- **The skill is written for the owner more than for an agent.**
  - `skills/omarchy-flux` covers install, diagnosis, development and
    release, and `references/runtime.md` lists commands by feature.
  - It doesn't show an agent how to find an ID in `status --json` (a
    notification, a conversation thread, a transfer) and act on it.
  - It doesn't say which actions still need a tap on the phone.
- **Docs bug:** `docs/ipc.md:188-189` lists the Streams row twice, and the
  second copy leaves out `speaker.*`.
- **The tap on the phone is the consent gate, by design.** Starting the
  camera, microphone or screen mirror, approving, SSH signing and pairing
  all need a person to tap the phone (`docs/security.md:128,141-147`). An
  agent can only ask for these.

## Proposed outcome

- **Every socket method that a person can use has a `flux-cli` command.**
  That means the ones listed above. The approval-helper methods and
  `subscribe` stay internal.
- **An agent can rely on what the CLI returns.**
  - Every command that reads or creates something can print JSON.
  - A failure gives the socket's error code in a form a script can test.
  - Existing text output, flags and exit codes stay as they are for
    people and scripts that use them today.
- **The skill teaches an agent to operate Flux.**
  - Find IDs in `status --json`, act with a command, and confirm through
    `status --json` or `watch`.
  - Which actions need a tap on the phone, and which change what other
    paired devices can do (`settings`, `input`, `desktop`), so the agent
    asks the user first.
  - An eval or two in `skills/omarchy-flux/evals` for the new tasks.
- **`docs/cli.md` and `docs/ipc.md` match the CLI**, including the fixed
  Streams row.

Out of scope:
- An MCP server.
- New `fluxd` or socket methods, protocol changes, and Android, iOS or
  macOS changes.
- Any way for an agent to skip a tap on the phone.

## Affected users and systems

- Agents and scripts on the computer that run as the user.
- `cmd/flux` (new subcommands and output), `docs/cli.md`, `docs/ipc.md`,
  and `skills/omarchy-flux` (SKILL.md, `references/runtime.md`, evals).
- Not affected: `fluxd`, the protocol, the GUI and plugin, and the phone
  apps.

## Constraints

- The CLI goes through the same socket and checks as today: the user's
  socket, and the `SO_PEERCRED` check (`docs/ipc.md:5-17`). It gets no new
  privilege.
- No change to the commands, flags, output or exit codes that exist today.
  New behaviour is opt-in, for example with `--json`.
- The skill must not tell an agent to accept a pairing, enroll approval,
  turn on remote input or desktop, or change a device's access without the
  user asking for it.
- No adb and no device runs against the owner's devices in agent runs. The
  owner runs the device tests.

## Open questions

1. **How should errors show their code?**
   - Recommendation: with `--json`, print `{"error":{"code":…,"message":…}}`
     on stdout and exit with 1. Without `--json`, keep today's message.
   - Alternative: a separate exit code for each error class. That is harder
     to keep stable.
2. **Should `--json` be a global flag, or one flag per command?**
   - Recommendation: global, read in the same places as `--device`, so
     every command accepts it.
3. **Global settings:** add `flux-cli settings [KEY VALUE]`, or extend
   `device-settings`?
   - Recommendation: a new `settings` command, because `device-settings`
     is for one device's access.
4. **Command names for the new actions.**
   - Proposal:
     - `notifications dismiss ID`, `notifications reply ID TEXT`,
       `notifications action ID ACTION`
     - `sms threads`, `sms thread ID`, `sms refresh`
     - `dnd on|off`
     - `transfers open ID`, `transfers copy ID`
     - `text TEXT` (sends text without touching the clipboard)
