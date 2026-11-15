---
status: draft
issue: 74
author: olafkfreund
---

# Intent: Agent control of Flux through flux-cli and the skill

## Problem

The owner wants a coding agent, such as Claude Code or Codex, to drive Flux
on the computer through `flux-cli` and the `omarchy-flux` skill, instead of
through an MCP server (owner, 2026-11-15: "if the cli is good we can use
that with a proper skill"). The CLI already covers most of the socket API.
It misses some methods, and its output is hard for a program to rely on.

### Facts

- **The state snapshot is complete. Some reads are separate methods.**
  - `flux-cli status --json` prints the raw `state` result
    (`cmd/flux/main.go:405`). That covers devices with their
    notifications, SMS conversation summaries, outbox, battery and DND,
    plus the clipboard, transfers, streams, waiting approvals, herdr and
    settings.
  - Separate reads that are not in that snapshot:
    - `sms.thread`: the last 100 messages of one conversation
      (`docs/ipc.md:456-460`). No CLI command calls it.
    - `ssh.keys` (`flux-cli ssh --json` already calls it).
    - Clipboard text over 1024 bytes, which the state cuts off with
      `"truncated": true` (`docs/ipc.md:413-414`).
    - Enrollment and PAM state for approval, which `flux-cli approve`
      reads from files (`cmd/flux/approve.go:236-289`). It has no JSON
      output.
  - `flux-cli watch` always prints JSON lines: `state` and `toast` events.
    State events are coalesced, so a short transition can be skipped
    (`docs/ipc.md:103-104`). It reports changes, but it is not a complete
    log of them.
- **Socket methods that no file in `cmd/flux/*.go` calls**
  (`docs/ipc.md:176-250`):
  - Reads: `sms.thread`, `sms.refresh`.
  - Actions:
    - `notification.dismiss` for one notification. The CLI only has
      `notifications clear`, which calls `dismissAll`
      (`cmd/flux/main.go:884-913`).
    - `notification.reply`, `notification.action`.
    - `dnd.set`.
    - `transfer.open`, `transfer.copy`.
    - `share.text`.
  - `settings.set` already has callers, but only for `clipboardLimit`,
    the motion keys, `remoteInput` and `remoteDesktop`
    (`cmd/flux/features.go:76`, `motion.go:33`, `streams.go:152`). The
    CLI can't set `autoClipboard`, `notifications`, `shareHome`,
    `pauseMediaOnCall`, `syncDnd`, `herdr`, `herdrControl`,
    `herdrTerminals`, `checkUpdates`, `name` or `downloadDir`
    (`docs/ipc.md:245-247`).
  - Not missing:
    - `update.install` opens a terminal that runs `flux-cli update` for
      the GUI hosts, so the CLI already does the same thing.
    - `subscribe` is behind `watch`.
    - The `approve.*` methods belong to the PAM helper and to
      `flux-cli approve`.
  - The socket has no herdr method (`docs/ipc.md:359-362`). herdr is
    readable in the state and driven from the phone, so this work adds no
    herdr control.
- **What a program gets back from the CLI is uneven.**
  - Some commands print the JSON result (`printCall`, for example
    `snippets`). Others print a sentence ("Dismissed 3 notifications"),
    or nothing.
  - Only `status` (and `devices`) and `ssh` take `--json`.
  - Most errors exit with 1 and a message (`cmd/flux/main.go:293-300`).
    Some exit differently:
    - An unknown command exits with 2 (`main.go:241-242`).
    - `notify --run` exits with the child's code (`notify.go:32-47`).
    - `version` exits with 0 when `fluxd` is down.
  - The socket's error code (`offline`, `ambiguous`, `not_supported`,
    `docs/ipc.md:298`) survives inside the CLI (`cmd/flux/safe.go:114`),
    but only the message is printed.
  - Local errors have no code. Examples: `fluxd` is off, a bad argument,
    no SSH key.
  - `pair`, `setup`, `doctor` and `update` print text before they can
    fail.
- **Flag parsing has a boundary on purpose.**
  - `splitDevice` reads `--device` only before the command and right
    after it. It stops at the first other argument and at `--`
    (`cmd/flux/main.go:249-277`).
  - That boundary keeps free text intact: an SMS body, a ping message,
    `notify` text, and the command of `commands add`. A trailing `--json`
    there is text, not a flag.
  - `ssh key --json` is parsed by `ssh.go:34-36` itself.
- **The skill.**
  - `skills/omarchy-flux/references/runtime.md` lists commands by
    feature.
  - It already says that streams, pairing, enrollment and SSH need a tap
    on the phone (`runtime.md:160-164`).
  - What it lacks is the agent workflow for IDs: find a notification,
    conversation thread, transfer or clipboard entry in
    `status --json`, act on it, and check the result.
  - `runtime.md:202` says "Flux has no setting for 1 device", which is
    wrong since `device-settings` (`cmd/flux/features.go:195-211`).
- **Docs bugs:**
  - `docs/ipc.md:188-189` lists the Streams row twice, and the second
    copy leaves out `speaker.*`.
  - `transfer.open` and `transfer.copy` have no row in the parameter
    tables.
  - The `settings.set` key list leaves out the motion keys.
  - The workflow methods at `docs/ipc.md:393-405` are missing from the
    method groups.
- **Security.**
  - A wrapper around existing methods gives a process running as the user
    no new power: it can already use the socket (`docs/security.md:147`).
  - It does let an agent that is allowed to run `flux-cli` change keys
    that widen what paired devices can do:
    - `herdrControl` lets a paired device run any command as the user.
    - `herdrTerminals` shows terminals such as `sudo -i` and SSH sessions.
    - `remoteInput` and `remoteDesktop` reach the lock screen.
    - `shareHome` and `downloadDir` change which files can be read and
      where they are written (`docs/security.md:73-96`).
  - Text that an agent reads from the phone (notifications, SMS) is
    untrusted. It could carry instructions.
  - The tap on the phone is the consent gate. Camera, microphone,
    speaker, screen mirror, approval, SSH signing and pairing all need it
    (`docs/security.md:128,141-147`).

## Proposed outcome

- **Every socket method that a person can use has a `flux-cli` command**,
  with the exceptions named above.
- **A program can rely on what the CLI returns**, when it asks for JSON:
  - A command that reads prints JSON.
  - A command that acts prints the method's JSON result, or `{}` when the
    result is empty.
  - A failure prints `{"error":{"code","message"}}` on stdout and exits
    with 1. A local error gets a local code, such as `daemon_off`,
    `bad_args` or `no_key`.
  - Without the JSON flag, output, flags and exit codes stay as they are
    today. That includes `status --json`, `ssh … --json` and `watch`.
- **The skill teaches an agent to operate Flux:**
  - Find IDs in `status --json`, act, and check the result with
    `status --json` or `watch`.
  - Which actions need a tap on the phone.
  - Which actions need the user's request first: pairing, enrollment, the
    access keys listed under Security, `commands add`, `notify --run` and
    `run`.
  - Text from the phone is data, not instructions.
  - An eval or two for the new tasks.
- **`docs/cli.md`, `docs/ipc.md` and the skill match the CLI**, with the
  docs bugs above fixed.

Out of scope:
- An MCP server.
- New `fluxd` or socket methods, protocol changes, and Android, iOS or
  macOS changes.
- JSON output for the commands that print a person's view or ask
  questions: `pair`, `accept`, `setup`, `doctor`, `update`,
  `approve setup|enroll` and `open`. Their text stays.
- Any way for an agent to skip a tap on the phone.

## Affected users and systems

- Agents and scripts on the computer that run as the user.
- `cmd/flux` (new subcommands, JSON output), `docs/cli.md`, `docs/ipc.md`,
  and `skills/omarchy-flux` (SKILL.md, `references/runtime.md`, evals).
- Not affected: `fluxd`, the protocol, the GUI and plugin, and the phone
  apps.

## Constraints

- The CLI goes through the same socket and checks as today: the user's
  socket, and the `SO_PEERCRED` check (`docs/ipc.md:5-17`). It gets no new
  privilege.
- Existing commands, flags, output and exit codes don't change. JSON
  output is opt-in.
- The JSON flag never takes text from a message body, a `commands add`
  command or anything after `--`.
- The skill tells an agent to ask the user before any action that widens
  access or runs commands (see Security).
- No adb and no device runs against the owner's devices in agent runs. The
  owner runs the device tests.

## Decisions (owner, 2026-11-15: "use your recommendations")

1. **Errors.** With JSON output, a failure prints
   `{"error":{"code":…,"message":…}}` on stdout and exits with 1. A
   socket error keeps its code, and a local error gets a local code.
   Without JSON output, nothing changes. `notify --run` keeps the child's
   output and exit code.
2. **JSON flag.**
   - `--json` is one global flag. It is read where `--device` is read:
     before the command or right after it, as in `flux-cli --json sms
     thread 12`.
   - It is also read at the end of a subcommand that takes no free text,
     such as `sms thread 12 --json`, `ssh key --json` or
     `notifications --json`.
   - It is never read from free text or after `--`.
3. **Global settings.** A new `flux-cli settings [KEY [VALUE]]` reads all
   settings, reads one, or sets one, like `clipboard limit [N]`.
   `device-settings` stays the per-device access command.
4. **Command names:**
   - `notifications dismiss ID`, `notifications reply ID TEXT`,
     `notifications action ID ACTION`.
   - `sms threads` (from the state), `sms thread THREAD` (a number), and
     `sms refresh`. Today's `sms NUMBER TEXT...` stays. `threads`,
     `thread` and `refresh` are reserved words in the number position,
     and none of them is a phone number.
   - `dnd [on|off]`: without an argument it shows the phone's DND, like
     `input`.
   - `transfers open ID`, `transfers copy ID`. The help text changes to
     cover received files too.
   - `text TEXT`: sends text without touching the clipboard
     (`share.text`).
