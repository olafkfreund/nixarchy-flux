---
status: draft
issue: 50
author: olafkfreund
---

# Intent: Fix the remaining findings on the Android tailnet node

## Problem

The tailnet node on Android (#35, #48) works, but the review of #35 left
findings open. The issue lists them unverified. This intent checks each one
against the code and the pinned tailscale source. It treats them as one
item: they share the node's lifecycle in `TsnetNode.kt` and the helper in
`main.go`, and they need the same phone test.

- The node uploads logs to Tailscale and writes verbose logs to logcat. The
  docs say neither.
- Start and stop can run out of order, and an old helper can overwrite the
  state of the new one.
- An enrolled node still needs the auth key stored forever. "Change key"
  does nothing on an enrolled node.
- Every link through the node overwrites the computer's stored address with
  `127.0.0.1`.
- After a switch between Wi-Fi and mobile data the node can keep stale
  paths for up to 10 minutes.

### Facts

Paths are under `android/app/src/main/java/org/omarchy/flux/` unless they
start with `android/tsnet/`, `docs/` or `tailscale.com/`. `tailscale.com/`
is the pinned module, v1.104.4 (`android/tsnet/go.mod:5`), read from the Go
module cache.

- **The helper.** `android/tsnet/main.go` runs a `tsnet.Server` with
  `Dir`, `Hostname`, `AuthKey` and `Logf: log.Printf` (`main.go:51-56`).
  - It prints `ready <ip>` or `error <reason>` on stdout (`main.go:74,93-97`).
    `ready` comes before the listeners, so a failed listen prints a second
    line, `error listen ...`, after `ready` (`main.go:81-85`).
  - All other output goes to stderr (`main.go:36`).
  - It is built with `GOOS=android` (`android/tsnet/build.sh:24`).
  - On SIGTERM it runs `srv.Close`, which can take up to 5 seconds
    (`main.go:57,59`; `tailscale.com/tsnet/tsnet.go:665`).
- **The app side.** `net/TsnetNode.kt` starts and stops the helper and
  holds its state (`Off`, `Starting`, `Ready`, `Failed`,
  `TsnetNode.kt:20-25`).
  - The only env var it sets is `TS_AUTHKEY` (`TsnetNode.kt:57`).
  - Every stderr line goes to logcat at info level, in release builds too
    (`TsnetNode.kt:106-110`).
  - Callers: `FluxCore.applyTsnet` and `stopTsnet` (`core/FluxCore.kt:612-637`),
    from the Remote access switch, the key dialog (`FluxCore.kt:623-633`)
    and the service start and stop (`service/FluxService.kt:243,334`).
  - App backup is off (`AndroidManifest.xml:64`), so the node state and the
    key don't leave the phone that way.

**1. Logs.**
- **Upload.** tsnet always starts a logtail logger outside tests
  (`tailscale.com/tsnet/tsnet.go:884,1100-1137`). It buffers backend log
  lines in the state dir and uploads them to `log.tailscale.com`
  (`tailscale.com/logtail/config.go:18`). The helper's `Logf` runs in
  addition, not instead (`tsnet.go:872-880`).
- `TS_NO_LOGS_NO_SUPPORT` stops the upload in this version: the upload
  transport becomes a no-op that reports success
  (`tailscale.com/logpolicy/logpolicy.go:865-866,894-897`;
  `tailscale.com/envknob/envknob.go:480-484,498-500`). The local buffer is
  still written and compressed. `logtail.Disable()` stops the buffering
  too (`tailscale.com/logtail/logtail.go:532,735-738,768`).
- A Go unit test can't prove the switch: inside tests the transport is a
  no-op anyway (`logpolicy.go:895`).
- Nothing sets it today (`TsnetNode.kt:57`).
- **Logcat.** tsnet documents `Logf` as verbose, for debugging
  (`tsnet.go:255-258`). The helper passes `log.Printf`, so peer addresses,
  endpoints and, on a node that needs login, the login URL reach logcat.
- **Docs.** `docs/security.md` doesn't mention Remote access. It says the
  apps connect to only 1 other kind of host, for album art
  (`docs/security.md:186`). With Remote access on, the phone also talks to
  Tailscale's control servers and relays.
- The upload stays off only while the code path above holds. A tailscale
  bump can change it, so the check repeats on each bump.

**2. Start and stop order.**
- `applyTsnet` and `stopTsnet` each submit to `io`, a cached thread pool
  (`FluxCore.kt:50,614,620,636`). It runs tasks in parallel, in no order.
  `TsnetNode.start` and `stop` are `@Synchronized` (`TsnetNode.kt:35,72`),
  so they don't overlap, but they can run in either order.
  - Example: Remote access off then on, quickly. If `start` runs first and
    `stop` second, the switch is on and the node is off.
  - The service stop and the next service start have the same race.
- `stopLocked` waits up to 3 seconds inside the lock, then calls
  `destroyForcibly` without waiting for the exit (`TsnetNode.kt:79-84`).
  The helper's own `Close` can take 5 seconds. So a new helper can start on
  the same state dir while the old one still writes it.
- `awaitExit` (on its own thread) and every queued start or stop (on `io`
  threads) wait for the same lock (`TsnetNode.kt:117`).

**3. A stale helper overwrites the state.**
- `readReady` checks `process === p` for `ready` but not for `error`, and
  neither check holds the lock (`TsnetNode.kt:86-104`).
- A stop sends SIGTERM (`TsnetNode.kt:82`). A helper still in `srv.Up`
  then prints `error tailnet up: ...` (`main.go:59,65-70`). That line can:
  - turn the next helper's `Starting` into `Failed`;
  - turn a plain stop's `Off` into `Failed`, which the Sync screen shows
    with the switch off (`ui/SyncScreen.kt:270-274`).
- `ready` can pass `process === p` just before a stop, and set `Ready`
  after the stop set `Off`.
- `lastError` is shared. `start` clears it (`TsnetNode.kt:39`), but an old
  reader can write it again, and it then becomes the new helper's exit
  reason (`TsnetNode.kt:120`).

**4. The auth key is needed forever.**
- The helper exits with `TS_AUTHKEY is required` when the key is empty
  (`main.go:42-45`). The app doesn't start the node without a saved key
  (`FluxCore.kt:613`, `TsnetNode.kt:40-43`).
- tsnet needs no key once the node is enrolled and doesn't need login.
  With a key and such a node it logs "Ignoring authkey"
  (`tailscale.com/tsnet/tsnet.go:997-1016`). A node that needs login again
  (node key expired, or removed in the admin console) starts login
  instead.
- tsnet writes files to the state dir before it is enrolled
  (`tsnet.go:1104-1122`), and the app creates the dir up front
  (`TsnetNode.kt:49`). So "the dir has files" doesn't mean "enrolled".
- The key stays in plain `SharedPreferences` (`core/Settings.kt:32-34`) as
  long as the node runs, though an enrolled node doesn't use it. The Sync
  screen asks for a key whenever none is saved (`ui/SyncScreen.kt:265-267`,
  `FluxCore.kt:476`).
- **"Change key"** shows only while Remote access is on
  (`SyncScreen.kt:278,295`). It saves the key and restarts the helper on
  the same state dir (`FluxCore.kt:629-633`). tsnet ignores the new key,
  and nothing tells the user.
- There is no way to forget the node on the phone, short of clearing the
  app's storage.
- **The 2-minute wait.** `srv.Up` gets a 2-minute context (`main.go:65`).
  `Up` already fails at once when the backend reports an error
  (`tsnet.go:590-592`). It waits through `NeedsLogin`, because tsnet sees
  that state briefly even with a valid key (`tsnet.go:628-633`). How long
  an invalid key on a new node takes is not checked; the review reported
  about 2 minutes.

**5. `127.0.0.1` as the computer's address.**
- The helper forwards every tailnet connection to `127.0.0.1`
  (`main.go:29,86`), so the app sees `127.0.0.1` as the peer.
- `attach` stores the link's address as `lastIp` and in the trust store
  (`FluxCore.kt:336-339`). Pairing over the node stores it too
  (`core/Device.kt:328`). The device's UI address shows it
  (`Device.kt:157`). Startup loads the stored value (`FluxCore.kt:135`), so
  phones that used the node since #48 already have it saved.
- What it breaks:
  - **Wake** sends to the broadcast addresses and to `lastIp`
    (`core/Wake.kt:69-70`). The broadcasts still work on the same LAN, but
    the unicast to the real address is lost. And a send to `127.0.0.1`
    always succeeds, so Wake says "Sent a wake signal" even with no
    network (`Wake.kt:86-102`).
  - **Unicast discovery** sends the identity to known addresses on the
    same subnet or in Tailscale's range (`net/LanBackend.kt:204-206`).
    `127.0.0.1` is neither, so the computer drops out until it connects
    from the LAN again.
  - **Per-address limits.** Incoming handshakes are capped at 4 at a time
    per source (`LanBackend.kt:48-49,294-319`), counted before the peer is
    known, for up to 10 seconds each (`LanBackend.kt:43`). All tailnet
    links share `127.0.0.1`, and so does any app on the phone that
    connects to the loopback ports. 4 stalled connections from any tailnet
    peer or local app block every paired computer over the tailnet.

**6. Network changes.**
- On `GOOS=android`, netmon always uses the polling monitor
  (`tailscale.com/net/netmon/netmon_polling.go:4,13-15`), which polls every
  10 minutes because it expects the Tailscale app to wake it
  (`tailscale.com/net/netmon/polling.go:61-66`). Nothing in Flux wakes it.
  `androidbin` gives netmon a single synthetic interface
  (`main.go:17-20`; `tailscale.com/feature/androidbin/androidbin.go`).
- So after a switch between Wi-Fi and mobile data, the node may keep stale
  endpoints for up to 10 minutes. WireGuard and DERP may recover sooner on
  their own; that needs a phone test.
- The app already gets network changes: `FluxService` has a default
  network callback (`FluxService.kt:57-67,232`). It doesn't reach the
  helper.
- tsnet can be woken without a restart: `Server.Sys()` returns the node's
  subsystems, its `NetMon` is set by the engine, and `Monitor.InjectEvent`
  forces a recheck (`tsnet.go:902,2331-2335`;
  `tailscale.com/wgengine/userspace.go:602`;
  `tailscale.com/net/netmon/netmon.go:540-569`). tsnet calls `Sys()` not a
  stable API.
- A restart of the helper ends every open link, transfer and stream
  through it (`android/tsnet/forward.go:50-61`).

## Proposed outcome

- **Logs.** The node uploads no logs and writes no backend log buffer. A
  release build doesn't send the backend's verbose log to logcat; errors
  and the helper's own lines still go there. `docs/security.md` covers
  Remote access: the hosts it talks to, that no logs go to Tailscale, what
  stays on the phone, and the tailscale version this was checked against
  (open question 1).
- **Lifecycle.** Start, stop, restart and reset run one at a time, in the
  order the user asked for them, and never block the main thread. A new
  helper starts only after the old one has exited. A stopped helper never
  changes the node's state. Turning Remote access on and off fast, or
  restarting the service, ends in the state the switch shows.
- **Key.** An enrolled node starts without a stored key. The app records
  that the node enrolled, and clears the key after the first `ready` of
  that enrollment (open question 2). If the node later needs login, the
  Sync screen says so and asks for a key.
- **Change key** works on an enrolled node: it enrolls a new node with the
  new key, and keeps the old node until the new one is up. It says that
  the phone gets a new tailnet address, which each computer must add
  again (open question 3).
- **Forget node.** The Sync screen can remove the node from the phone,
  also while Remote access is off.
- **Errors.** A key that tsnet rejects fails with its reason as soon as
  tsnet reports it. A brief `NeedsLogin` during a valid enrollment is not
  an error (open question 4).
- **Addresses.** A link through the node no longer overwrites the stored
  LAN address. A stored `127.0.0.1` is ignored by Wake and discovery and
  cleared. Wake no longer reports success from a loopback send. The
  device shows "via tailnet" only while the node is up (open question 5).
- **Network.** After a switch between Wi-Fi and mobile data, the node
  rechecks the network at once, without dropping open links (open
  question 6).
- **Tests** cover the start/stop order, stale `error` and `ready` lines, a
  stop during `Starting`, enrollment tracking, the loopback address
  (new and already stored), and Wake. A phone test covers the rest.

## Affected users and systems

- Android users with Remote access on (arm64 builds; the x86_64 debug
  libraries have no node, `docs/android.md:258`).
- App: `net/TsnetNode.kt`, `core/FluxCore.kt` (`applyTsnet`, `stopTsnet`,
  `attach`, startup), `core/Device.kt`, `core/Wake.kt`, `core/Settings.kt`,
  `service/FluxService.kt` (the network callback), `ui/SyncScreen.kt`, and
  the screen that shows a device's address. Possibly `net/LanBackend.kt`
  (open question 5).
- Helper: `android/tsnet/main.go`.
- Docs: `docs/security.md`, `docs/tailscale.md`.
- Tests in `android/app/src/test` and `android/tsnet`.
- Not changed: `fluxd`, the wire protocol, the desktop, iOS, macOS,
  Windows. After a key change, users must update the phone's address on
  each computer (`docs/tailscale.md:33-38`); that is configuration, not
  code.

## Constraints

- No new permission. The node stays userspace, with no VPN slot.
- No change to the wire protocol or to `fluxd`.
- Never log, print or upload the auth key.
- Never lose a working node by accident: the node state is deleted only by
  Forget node, or by Change key after the new node is up.
- Never run two helpers on the same state dir.
- Don't claim in the docs more than the pinned tailscale version does.
- Keep the per-address handshake cap as a protection; don't remove it to
  make forwarding work.
- Device tests use the paired phone only in a booked slot; another task
  uses it now. Don't touch `fluxd`, adb devices or `/etc/flux` until then.

## Open questions

1. **Logs: off by default, or only documented?** Proposed: off, with no
   setting to turn them on. The helper calls
   `envknob.SetNoLogsNoSupport()` and `logtail.Disable()` before it starts
   tsnet, so the app's env can't lose it. In release builds the helper
   passes no backend `Logf` and keeps `UserLogf` and its own lines. The
   docs say Tailscale support then has no logs. The check runs again on
   each tailscale bump, in the release guide's checklist.
2. **Keep the key after enrollment?** Proposed: no. The app saves an
   "enrolled" flag with the first `ready` of an enrollment, then clears
   that key, unless the user saved a newer key in between. It starts the
   node when Remote access is on and it has a key or the flag. The helper
   starts without `TS_AUTHKEY`. A node that needs login fails with "Needs
   a new auth key" and clears the flag. The docs' advice to use a key
   with an expiry stays; it limits a leaked key, not the node.
3. **What does "Change key" do on an enrolled node?** Proposed: the app
   enrolls the new key in a fresh state dir. When it is `ready`, it
   replaces the old dir and the old node is gone from the phone; if it
   fails, the old dir stays and the old node starts again. The dialog
   says the address changes and the old node stays in the admin console
   until removed there. Rejected: `TSNET_FORCE_LOGIN=1` on the old state,
   which re-logs the old node and is harder to undo. The spec checks
   whether both can run at once or the old node stops during the try.
4. **The 2-minute wait.** Proposed: fail at once on errors tsnet reports
   (`Up` already does). Without a key, treat `NeedsLogin` as "needs a new
   auth key" at once. With a key, keep waiting through `NeedsLogin` up to
   the 2 minutes. `NeedsMachineAuth` says the node waits for approval in
   the admin console. The spec measures an invalid key on a phone.
5. **The loopback address.** Proposed: `attach` and pairing keep the
   stored address when the link is from a loopback address; startup clears
   a stored loopback address; Wake skips it. The device shows "via
   tailnet" for a loopback link only while the node is `Ready`. The
   per-address cap stays. Residual risk: 4 stalled connections from a
   tailnet peer or a local app can block tailnet links for 10 seconds at a
   time; access controls (`docs/tailscale.md:50-51`) limit which peers can
   try. Rejected: passing the real peer to the app (a PROXY header or a
   side channel), which changes the link setup.
6. **Network changes.** Proposed: the app's network callback tells the
   helper on stdin, debounced, ignoring the first callback at
   registration. The helper calls `InjectEvent` through `Sys().NetMon`.
   No restart, so open links stay. If the phone test shows that isn't
   enough (for example with another VPN active, where the synthetic
   interface may not change), the spec decides on a restart. Test Wi-Fi to
   mobile and back, airplane mode and back, and both with another VPN on.
7. **One branch or two?** Proposed: one. The key, Change key and network
   work all build on the lifecycle fix.
8. **Test.**
   - Unit tests with a fake process in `TsnetNode`: start/stop order,
     stale `error` and `ready`, stop during `Starting`, enrollment flag and
     a newer key.
   - Unit tests for loopback in `attach` and Wake, and a stored
     `127.0.0.1` at startup.
   - `go test ./...` in `android/tsnet`: start without a key; no-logs mode
     checked in a subprocess, since tests make the transport a no-op
     anyway.
   - On the phone, in a booked slot:
     1. Enroll; the key is gone from settings; restart the service; the
        node comes back.
     2. Change key with a wrong key: the old node comes back. With a
        right key: new address; the old node is offline in the admin
        console.
     3. Invalid key on a fresh node: time to the error.
     4. Toggle Remote access fast several times: it ends as shown.
     5. Wi-Fi to mobile and back, airplane mode, another VPN on: time
        until the computer reconnects; an open link survives.
     6. Use the computer over the tailnet, then Wake it on the LAN.
     7. Forget node, with Remote access off.
     8. No connection to `log.tailscale.com`: watch the helper's
        connections on the phone, not only the router's DNS log, which
        misses mobile data and VPNs.

## Review changes

Codex and agy reviewed the first draft, read only. Each finding was checked
against the code and the tailscale source.

| Finding | Source | Verdict | Change |
| --- | --- | --- | --- |
| Change key wiped the state first; a bad key loses a working node | Codex, agy (high) | Confirmed, `TsnetNode.kt:49`, `tsnet.go:965-973` | Q3: enroll in a fresh dir, keep the old one until `ready`; constraint added |
| Unlocking stop lets two helpers share the state dir | Codex, agy (high) | Confirmed: `destroyForcibly` isn't awaited (`TsnetNode.kt:83`), `Close` up to 5 s (`tsnet.go:665`) | Fact 2; outcome and constraint: start only after exit |
| "Dir has files" isn't "enrolled"; node can need login again | Codex, agy | Confirmed, `tsnet.go:1008-1015,1104-1122` | Q2: explicit enrolled flag, "Needs a new auth key" |
| Clearing the key on a late `ready` can erase a newer key | Codex | Agreed | Q2 |
| `NeedsLogin` is transient with a valid key | Codex, agy | Confirmed, `tsnet.go:628-633` | Q4 split by key / no key |
| `netmon_linux.go` isn't built for Android | Codex, agy | Confirmed, `netmon_linux.go:4` `!android`; `netmon_polling.go:4` | Citation fixed |
| `Sys().NetMon.InjectEvent` is reachable | Codex, agy | Confirmed, `userspace.go:602`, `tsnet.go:902` | Fact 6; Q6 prefers it over a restart |
| A restart drops open links; callback fires at registration | agy | Confirmed, `forward.go:50-61`; registration firing is Android behaviour, unverified here | Q6: debounce, skip the first, no restart |
| Stop during `Starting` turns `Off` into `Failed`; `ready` races too | Codex, agy | Confirmed, `TsnetNode.kt:86-104` | Fact 3 and tests |
| Stored `127.0.0.1` stays after the fix | Codex, agy | Confirmed, `FluxCore.kt:135` | Outcome and Q5: clear at startup |
| Wake reports success from a loopback send | agy | Confirmed, `Wake.kt:86-102` | Fact 5 and outcome |
| Wake still has the broadcasts; "breaks" overstated | Codex, agy | Confirmed, `Wake.kt:69-70` | Reworded |
| Shared cap: 4 stalled sockets block tailnet links | Codex, agy | Confirmed, `LanBackend.kt:43,294-299` | Fact 5; Q5 keeps the cap and names the risk |
| "via tailnet" would mislabel a local app | agy | Agreed | Q5: only while the node is `Ready` |
| Verbose `Logf` and login URLs reach logcat | agy | Confirmed, `tsnet.go:255-258`, `TsnetNode.kt:108` | Fact 1, outcome, Q1 |
| `security.md:186` says 1 other kind of host | Codex, agy | Confirmed | Fact 1; docs outcome |
| No-logs mode still buffers; `logtail.Disable` stops it | agy | Confirmed, `logtail.go:532,768` | Q1 calls both |
| A Go test can't prove no-logs mode | agy | Confirmed, `logpolicy.go:895` | Q8: subprocess check |
| Router DNS log doesn't prove no upload | Codex | Agreed | Q8 step 8 |
| No Forget node; Change key hidden while off | agy | Confirmed, `SyncScreen.kt:278,295` | Forget node in the outcome |
| Helper can print `error` after `ready` | Codex, agy | Confirmed, `main.go:74-85` | Fact reworded |
| "Same two files" contradicted the list | Codex, agy | Confirmed | Reworded; affected list extended |
| 2 minutes is the context, not a hard bound on `Up` | Codex | Confirmed, `tsnet.go:573-579` | Reworded |
| Desktop config is affected by a new address | Codex | Agreed | Affected section |
| "Clearing the key makes the expiry advice true" | Codex | Agreed, misleading | Removed |
| 30-second target undefined | Codex | Agreed | Outcome now "rechecks at once"; Q6 measures |
| Backup of node state | agy (unverified) | Refuted: `allowBackup="false"`, `AndroidManifest.xml:64` | Fact added |
