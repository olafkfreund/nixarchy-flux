---
status: draft
issue: 69
spec: spec/2026-11-15-69-ble-transport.md
coders:
  0: lead    # gate, baseline and branch checks; nothing to code
  1: coder   # three constants, two struct fields and a list edit, given in full
  2: codex   # small tricky crypto logic (tag, challenge/hello gate, allow-list, limits) with exact vectors
  3: codex   # TLS 1.3 over any net.Conn, paired-only check before onLink, lanes: subtle ordering and locking in link.go and provider.go
  4: coder   # config fields, devices.json fields, IPC call and status line, same shape as existing ones
  5: coder   # prose edits to three docs, content given below
  6: coder   # per-link clipboard and notification limits, key distribution, daemon rules against the fake transport
  7: codex   # approval timeline: background send, shown, 3 s rule, 5 s rule, deadline never extended; the riskiest logic
  8: coder   # BlueZ glue over godbus behind the same Call() seam as Avahi, with a fake BlueZ
  9: coder   # Android prototype of TLS over a Socket subclass; lead runs the on-phone check
  10: coder  # BleBackend, key store, allow-list, one-prompt rule on Android, with shared vectors
  11: coder  # manifest, permission flow, advertising schedule, settings switch
  12: coder  # Qt GUI and shell plugin switch, device list transport line
  13: coder  # remaining docs, Nix option, Arch optional dependency, nix checks
reviewers:
  - codex    # independent read of each step's diff against this plan; strong on the TLS, lane and deadline logic
---

# Plan: Bluetooth LE as a fallback for notifications, clipboard text and approval

Issue #69 (olafkfreund/nixarchy-flux; every `gh` command uses `-R olafkfreund/nixarchy-flux`). Intent
`intent/2026-11-15-69-ble-transport.md` and spec `spec/2026-11-15-69-ble-transport.md` are approved. This plan
stands on its own: it carries every approved spec decision, so it can be implemented without opening them. Work
happens in the worktree `/mnt/code/Source-home/GitHub/nixarchy-flux-69` on branch `feat/69-ble-transport`.
Android paths are under `android/app/src/main/java/org/omarchy/flux/` (written `A/`), Android unit tests under
`android/app/src/test/java/org/omarchy/flux/` (written `T/`). Line numbers are from the tree at `24b15c6`; they
drift as steps land, so each step names the function too.

## Spec inconsistencies found

The spec was revised in pieces. These contradictions are carried over unresolved. The owner decides each one before
the step that depends on it starts; the plan names the provisional choice it makes so that work is not blocked, and
that choice is not an approval.

1. **Plan gate vs this plan.** The spec's "Upstream gate" says that implementation "and the `plan/` that starts it"
   wait for the maintainer's agreement on bjarneo/flux #185, yet the plan is requested now. This file is a draft
   only. It must not be approved, and no step may start, before that agreement (see Gate below).
2. **BLE link only while `dev.link == nil` (spec 3) vs the `shown` fallback (spec 5).** Spec 3: "A BLE link is made
   only while `dev.link == nil`". Spec 5: when `shown` is missing at 3 s, `fluxd` keeps the IP link and in addition
   starts a BLE connect and sends the same request over BLE, so `dev.link != nil`. Provisional: step 7 allows a BLE
   link next to a live IP link only for the one device with an open, unconfirmed approval; every other case follows
   spec 3.
3. **The phone does not advertise while IP is up (spec 5, 6, Risks) vs a BLE connect for a stuck IP link (spec 5
   "IP stuck, BLE link up" timeline).** With a stuck IP link on both sides, no phone advertises, so no tag is seen
   and the connect cannot succeed. The Risks section accepts this ("Accepted for version 1"), which means the
   "unconfirmed" BLE send only works when the phone itself already saw IP go down. Provisional: step 7 implements the
   send and its tests against the fake transport, and the runtime test (step 14) measures what really happens; the
   timeline's "IP stuck" rows are targets, not promises.
4. **A2DP: no scan while audio plays (spec 7) vs the runtime test (spec 8, Verification step 6).** Spec 7 says
   "While an A2DP transport is active the desktop does not start a scan ... When it is in use, there is no scan at
   all", but the runtime test plays A2DP "during steps 2 to 4" while those steps need a scan and connect, and the
   approval timeline budgets a 2 s scan hit. Provisional: step 8 implements spec 7 (no scan while A2DP is active, an
   existing BLE link is kept) and the runtime test runs steps 2 to 4 first without audio, then with a BLE link
   already up while audio starts. Which is intended needs the owner.
5. **Step order.** Spec 6 says "step 1 of the plan is a prototype" of TLS over a GATT `Socket` on Android; spec 8
   lists the prototype first too, while this plan was asked to put the desktop Go side first. Provisional: the
   prototype is step 9, after the Go side, and the owner may move it to the front (it needs a real phone and is the
   single unproven piece). Steps 1 to 8 do not depend on it, and a failed prototype switches step 10 to the loopback
   bridge fallback, never to a new design.
6. **Pre-pairing line limit (spec 2) vs 16 KiB BLE limit (spec 4).** Spec 2 says the phone reads "at most 64 KiB
   before pairing is checked (existing `maxUnpairedLine`)"; spec 4 says a BLE link never has more than 16 KiB for
   any line. Because spec 2 also closes an unpaired BLE peer before `onLink`, the two never overlap. Provisional:
   the BLE `Link` uses 16 KiB from the start (step 3) and `maxUnpairedLine` stays for IP.
7. **Notification line limit (spec 4).** Title and text are each cut to 1 KiB and the line is at most 4 KiB, but
   JSON escaping can expand control characters up to 6 times (the spec notes this for the clipboard only), so two 1
   KiB fields can exceed 4 KiB. Provisional: the phone cuts after serializing (shrinks text until the line fits 4
   KiB) and the desktop drops a longer line (step 6, step 10).
8. **Manifest wording (spec 6).** "`android:minSdkVersion="31"` semantics (declared with `maxSdkVersion` none)":
   `<uses-permission>` has no `minSdkVersion` attribute. Provisional: declare the two permissions plainly (Android
   10 and 11 ignore unknown permissions) and gate the feature in code on `Build.VERSION.SDK_INT >= 31` (step 11).

## Gate (read before anything)

- **Implementation starts only after the bjarneo/flux #185 maintainer agrees to this design** (intent decision 5).
  The only reply so far is from a non-maintainer. The owner decides whether to post the spec there. Until the
  agreement exists (a link to the comment goes into step 0), this plan stays `status: draft`.
- **Any build or deploy on p510 needs the owner's OK first.** Local `go test` and `make` in the worktree are fine.
  No step stages, commits, pushes, deploys, switches or reboots by itself; the lead commits after each step's check
  passes (one commit per step, `feat(ble): step N ... (#69)`).
- Agents other than the lead's own session never work on `main` or in `~/.config/nixos`.

## The approved decisions

- **Scope (1).** Only notifications (show only), clipboard text and approval. No replies, actions, SSH signing, no
  files, clipboard images or history, no screen, camera, mic, speaker, remote input, SSH, SFTP, herdr, SMS, calls.
  No pairing and no approval-key enrollment over Bluetooth. Not used while IP works.
- **Platforms (2).** Linux `fluxd` and Android 12+ only. No location permission. iOS, macOS, Windows, Android 10 and
  11 unchanged (the switch is hidden on Android 10 and 11).
- **Dependency (3).** BlueZ only through `godbus/dbus/v5`, already in `go.mod`. No new Go Bluetooth dependency. No
  `Pair`, `Trust`, `SetPowered` or `RemoveDevice` on a device Flux did not create. Adapter off: report it, do not
  switch it on.
- **Default (4).** Off on both sides. Desktop: global `ble = false` in `config.toml` plus a per-device `ble` flag in
  `devices.json`. Phone: a global switch plus a per-computer flag. All needed. `ble_key` is sent only when both ends
  have the global and the per-device flag on. The desktop learns the phone's side from a new IP-only packet
  `flux.ble.state` `{"on":bool}` (phone to desktop; `on` = phone global and per-computer flag both on and permission
  granted), sent after the identity on every IP link and at once on any change, also mid-session. The desktop sends
  `flux.ble.key` only after `on:true` (and again on the next `on:true` after an `on:false`); `on:false` makes the
  desktop close that device's BLE link and drop its BLE queue, keeping `ble_key`. The identity `Bluetooth` field is
  only the capability (SDK 31 or later), not consent. Turning a flag off stops scan or advertisement, closes the BLE links
  (of the device, or all), drops what is queued for BLE (never moved to IP), keeps `ble_key`.
- **Upstream (5).** The Gate above.
- **Settled values (6).** Clipboard limit 4 KiB (4096 bytes UTF-8, and the serialized line at most 16 KiB). Switch
  time fits the default `approve_timeout` of 20 s. Supported: BlueZ 5.46 or later with a BLE adapter, `fluxd` as the
  logged-in user's `fluxd.service`; other setups are best effort and the feature says why it is off. Test host: a
  Linux host with a BLE adapter and an Android 12+ phone with Wi-Fi off; p510 only with the owner's OK.
- **Roles and stream.** Phone = GATT peripheral (`BluetoothGattServer`, advertises; needs only `BLUETOOTH_ADVERTISE`
  and `BLUETOOTH_CONNECT`). Desktop = central (scans, connects). One GATT service, no ATT encryption requirement,
  2 characteristics: `rx` (desktop to phone, write without response) and `tx` (phone to desktop, notify). Desktop
  uses `GattCharacteristic1.AcquireWrite` and `AcquireNotify` (two fds) wrapped as a `net.Conn` in
  `internal/lan/ble_linux.go`. Phone writes each chunk with `notifyCharacteristicChanged` and waits for
  `onNotificationSent`; it reads `onCharacteristicWriteRequest` into a bounded queue; a chunk is at most ATT MTU
  minus 3. No framing of its own. The link works at MTU 23 but slowly; Flux logs the MTU and the limits assume at
  least 10 KB/s.
- **Trust.** TLS 1.3 only on this transport (copies of `serverConfig` and `clientConfig`, `MinVersion TLS13`). No
  pre-TLS `plainIdentity`; the outgoing link takes the device ID from the certificate (`finishID` already does).
  The desktop checks the phone's certificate against the pin of the target in `VerifyPeerCertificate`, before
  `GetClientCertificate`, so it never sends its certificate to a wrong peer. Android's `BleBackend.kt` checks the pin
  first and writes its identity after (unlike `LanBackend.kt:420-438`).
- **BLE key.** 32 random bytes per paired device, `ble_key`, made by the desktop and sent on an existing paired IP
  link in a new packet `flux.ble.key` (new type in `internal/proto/identity.go`, added to `Incoming` and
  `Outgoing`, so older apps never see it). Desktop stores it with the pin in `devices.json`. Android wraps it with
  an AndroidKeyStore AES-GCM key and stores only the ciphertext in the `trusted` `SharedPreferences`. It is sent when
  Bluetooth is turned on for the desktop, on each IP link of a paired phone whose last `flux.ble.state` is `on:true`,
  and after a re-pair, so the first
  Bluetooth use needs one IP link after both sides are on. An unpair deletes it on the unpairing side at once and on
  the peer when the unpair packet arrives (over BLE too); with no link the peer's copy stays until its next link
  (a stated limit).
- **Gate (challenge and response).** On connect the phone sends `challenge = ver(1) || c(16)` (16 fresh random
  bytes) in a `tx` notification. The desktop answers
  `hello = ver(1) || nonce(16) || HMAC-SHA256(ble_key, "flux-ble-hello" || c || nonce || tag)[:16]`. The phone checks
  it against the tag it advertised in the current and the previous window. Anything else, or no hello in 3 s, closes
  the connection before any certificate is shown. The gate is not trust: the pinned certificate check still
  authenticates both peers, and a holder of `ble_key` without the pinned certificate is refused.
- **Tag.** The advertisement packet carries the 128-bit Flux service UUID; the scan response carries, in service data
  keyed by that UUID, `tag = HMAC-SHA256(ble_key, "flux-ble-tag" || be64(t / 900 s))[:8]`. Byte budget (legacy limit
  31 per packet): advertisement = flags 3 + UUID list 2+16 = 21; scan response = 2+16+8 = 26 for ONE tag (two tags
  would be 34, over the limit). So a payload holds exactly one tag; with several enabled desktops the phone changes
  the tag in the scan response every 1 s in turn. The desktop scans actively (to get the scan response) and filters on
  the UUID. A unit test counts the bytes of both packets. No name, no device ID,
  `setIncludeDeviceName(false)`. The tag changes every 15 minutes; the desktop accepts windows t-1, t, t+1; the phone
  restarts the advertisement at each change so the random address and the tag change together. A tag never
  authorizes anything; it only tells the desktop which paired device to try.
- **Limits on strangers.** The phone accepts 1 connection at a time (until it passes or fails the gate), drops a
  connection that has not passed the gate in 3 s and one that has not finished TLS in 10 s. The desktop connects
  only to a tag that matches a paired, Bluetooth-enabled device.
- **Paired-only is new code.** The old pin check passes an unknown device and `onLink` creates a device entry. So
  for `Transport == "ble"`, `finishID` requires a trusted device with a pin and the matching certificate, and
  otherwise closes the link before `onLink`: no device entry, no notification.
- **Link and daemon.** `Link.Transport` is `"ip"` or `"ble"`, set by `finishID`; `Link.Addr` may be nil. The daemon
  sees a BLE link as the device's link (`dev.link`, `send`, `featureLinks` unchanged). `Preferred` never prefers
  BLE over IP. When IP comes up while BLE is current, IP becomes `dev.link` as `onLink` does today and the BLE link
  moves to a new secondary slot `dev.bleLink` while an approval of that device is open (it closes at its end). Today
  `internal/core/handlers.go:19-30` returns early when `dev.link != l`, so packets from a retained link are dropped:
  the handler must accept `flux.approve` kinds `response`, `shown` and `cancel` from `dev.bleLink` too (nothing
  else), and `receive` drops approvals only when the link that ended was `dev.link`, so the switch cancels nothing.
  On Android `FluxCore.kt:360` (`old.close()` when a new link replaces the current one) and `FluxCore.kt:410`
  (`d.link !== link` return) get the same secondary-link rule: a BLE link stays open while a prompt of that computer
  is open, and approval packets are accepted and sent on it. `HasLink` (`internal/core/daemon.go:502-506`, returns
  `dev.link != nil`), `internal/lan/provider.go:396` (skips dialing when `HasLink`) and `daemon.go:872` (skips
  `dialKnown` when `dev.link != nil`) are made transport-aware: a BLE-only `dev.link` does not count as a link for
  them, so IP is still dialed and found while BLE is up (new `HasIPLink`; `HasLink` keeps its meaning for features).
  The device list shows `transport: "ble"` and the
  features not on Bluetooth show as unavailable.
- **Allow-list on a BLE link** (in `Link.Send` and the receive loop; anything else is dropped, logged once, never
  queued for IP): `flux.identity` (once, after TLS); `flux.ping`; `flux.pair` with `{"pair":false}` only (pairing
  `{"pair":true}` is dropped); `flux.notification` (show only: the phone sends none of `ticker`, `requestReplyId`,
  `actions`, `icon`, the desktop drops such a packet in `bleAllowed` and also ignores `ReplyID` and `Actions` on a BLE link, so no reply or action control is
  offered; `internal/core/notifications.go:238-242`), and `flux.notification.request` with `cancel` only;
  `flux.clipboard` (not `.connect`, not `.image`); `flux.approve` with `kind` `request`, `cancel`, `response`,
  `shown` (not `enroll`, `enrolled`; `ApproveEnroll` refuses with "Enroll the approval key while the phone is on the
  network" on a BLE link). The identity lists types per transport so the phone enables only these three plugins.
- **Size limits** (a BLE link never has the 16 MiB limit; uses `l.maxLine`): any line 16 KiB read and write;
  clipboard text 4096 bytes and the line at most 16 KiB; notification title and text each cut to 1 KiB by the
  phone, no icon or large image, line at most 4 KiB; approval request and answer under 2 KiB. A longer clipboard
  text is never cut: `onLocalClipboard` makes the `fits` check per link at send time, says "over Bluetooth", and the
  history keeps the entry. `handleClipboard` refuses an oversize clip with the transport limit.
- **Stalls.** One writer queue per BLE link with 2 lanes: approve packets first, everything else second, at most 8
  packets and 32 KiB waiting (its own value, not `maxSendQueue`'s 32 MiB). A full lane drops the oldest
  notification, never an approval. Whole lines only, under `wmu`: an approval waits for the line in flight (at most
  16 KiB, about 1.6 s). BLE write deadline 5 s, each half-received line 5 s read deadline.
- **Reconnect bursts.** On a BLE link `requestNotifications` is not sent and `onLink` does not clear
  `dev.notifications`. Only a notification posted while the link is up crosses it, at most 10 in any 10 s (the phone
  drops the rest).
- **Approval over Bluetooth.** `docs/approve.md` changes first. The root helper, key, nonce, time checks and
  biometric stay; `fluxd` only carries the request.
  - Standby: `approveDevice` still fails at once when `dev.link == nil`, unless the device is Bluetooth-ready:
    Bluetooth on, the phone's identity has the new capability and its last `flux.ble.state` is `on:true`, and either a BLE link is up or the desktop saw a tag
    of this device in the last 30 s.
  - The phone advertises when it has no IP link to a paired Bluetooth-enabled desktop (link closed or the default
    network lost via the `ConnectivityManager` callback) and stops when IP is up.
  - `ApproveRequest` registers the request and returns `Started` at once; the send, the BLE connect and every
    re-send run in a goroutine under the request's deadline (the helper's `approve.request` call has a 3 s timeout).
  - `shown`: the phone sends `{"kind":"shown","id":...}` when the prompt is on screen (new kind in
    `handleApprove`). For a Bluetooth-enabled phone, no `shown` 3 s after the first send does not close IP: `fluxd`
    keeps IP and also starts a BLE connect and sends the same request ID over BLE ("unconfirmed"). The phone shows 1
    prompt per request ID and keeps the IDs until their deadline, also after a cancel, so a duplicate, a second link
    or a cancelled request never gives or revives a prompt. The IP link is closed only by its own failure.
  - IP fails after `shown`: the phone holds the answer until the deadline, sends it on the link that carried the
    request, else on any other link, and advertises when no link is left. `fluxd` does not cancel an open approval of
    a Bluetooth-ready device when its IP link ends (`dropApprovals` skipped), starts a BLE connect if the 5 s rule
    allows, and waits for the answer on any link until the deadline.
  - Timeline inside 20 s: BLE up, prompt about 1 s (up to 3 s behind a 16 KiB line); IP stuck and BLE up, about 4 s
    (up to 6 s); no BLE link yet, tag seen, at most 8 s (scan hit 2, connect and GATT 3, TLS 1.3 3), prompt at about
    11 s. `fluxd` starts a connect only when at least 5 s of the wait remain; a request is never restarted after it
    ends; the deadline is the one `ApproveRequest` set, never extended; the `timeout` field sent over BLE is the time
    left, so the phone's `admit()` (`ApproveMessage.kt:139-143`) must not treat a changed `timeout` as a different
    request: same `id` and `computerId` with a different `timeout` is a `Repeat`, never a `Conflict` (today a
    `Conflict` makes `Approvals.kt:51-55` send `failed` and clear the prompt); the first `timeout` seen fixes the
    deadline. A switch too late is a timeout and PAM asks for the password.
  - Cancel goes over the current link and over the secondary `dev.bleLink` while it is open. Unpair closes every link of the
    device, including BLE, and deletes `ble_key`; `deliver` takes the first answer.
  - Phone with the screen off: the work runs in `FluxService` (`connectedDevice` foreground service); the prompt is
    today's full-screen activity. Flux force-stopped, permission revoked, Bluetooth off each fall back to the
    password.
- **Android.** Manifest: `BLUETOOTH_ADVERTISE`, `BLUETOOTH_CONNECT`, and
  `<uses-feature android:name="android.hardware.bluetooth_le" android:required="false"/>`; no `BLUETOOTH_SCAN`, no
  location, no legacy `BLUETOOTH` or `BLUETOOTH_ADMIN`. Permissions are asked only when the user turns Bluetooth on;
  Android 10 and 11 hide the switch; a revoked permission turns the feature off and says so. `net/BleBackend.kt`
  layers `SSLSocketFactory.createSocket(Socket, host, port, autoClose)` over a `java.net.Socket` subclass whose
  streams are the GATT stream and produces a `Link` through the same `LanBackend.finish`; fallback if the prototype
  fails: a loopback `ServerSocket` bridge on 127.0.0.1 with a random port and a one-time token (the `android/tsnet`
  pattern). Battery: no advertising while an IP link to a paired desktop is up or while a BLE link is up; when IP is
  down, `ADVERTISE_MODE_LOW_POWER`, no scanning, 1 tag per payload (see Tag); full for 15 minutes after IP is lost
  without a BLE connection, then 1 minute in every 15 (inexact alarm), and fully again on screen unlock, on opening
  Flux and on a change of the default network.
- **Linux packaging.** `nix/nixos.nix` gets an option to set `hardware.bluetooth.enable` only when asked (never by
  default). `nix/home-manager.nix` gets none; the docs say to enable BlueZ in the system config. `dist/arch` lists
  `bluez` as an optional dependency. No root, no capability. BlueZ denies a call: say "BlueZ refused access" in
  `flux-cli status` and the GUI, keep IP. Older BlueZ: "Bluetooth is not supported on this BlueZ".
- **Audio coexistence.** The desktop scans only while a Bluetooth-enabled paired phone has no IP link and no BLE
  link. While `MediaTransport1.State == active` under the adapter it does not start a scan and waits; an existing
  BLE link is kept.
- **Protocol version stays 8.** The new types (`flux.ble.key`, `flux.ble.state`), kind and capability are ignored by older apps.
- **Alternatives rejected (do not revisit):** a Go Bluetooth library; L2CAP channels; RFCOMM; Bluetooth bonding as
  the security; a custom encrypted protocol (Noise); desktop as peripheral; a constant or named advertisement;
  always-on advertising; BLE hot standby next to a working IP link; parallel IP and BLE approvals from the start;
  a clipboard limit above 4 KiB.

## Steps

The local host may have no Android SDK; CI (`.github/workflows/build.yml`, Android job at `:128`) is then the first
Android compile (steps 9 to 11). Go commands run from the worktree root. The race detector is on in CI, so use
`make test` before the final step. The lead commits each step after its check passes and diffs each step against
the previous step's commit.

0. **Preflight** (lead). `git status --short` clean; branch `feat/69-ble-transport`; the #185 maintainer agreement
   link is in the PR draft; the owner approved this plan (`status: approved`, own commit). Run `go build ./...` and
   record the set of failing tests of `go test ./internal/... ` before any edit; some tests fail on this NixOS host
   for reasons outside this issue (fakes that call `/usr/bin/cat`, umask).
   Check: `git status --short && go build ./... && go test ./internal/lan ./internal/core ./internal/proto ./internal/config 2>&1 | tail -30`.
   Traps: never `git switch` in the main checkout `/mnt/code/Source-home/GitHub/nixarchy-flux`; no build or deploy
   on p510 without the owner's OK.

1. **Protocol additions** (coder). `internal/proto/identity.go`:
   - near `TypeFluxApprove` (`:72`) add `TypeFluxBLEKey = "flux.ble.key"`; add it to `Incoming` (`:142-147`) and
     `Outgoing` (`:156-162`) lists, and nowhere else (it is IP-only).
   - add `TypeFluxBLEState = "flux.ble.state"` (`{"on":bool}`, phone to desktop, IP-only) with the same list
     entries as `TypeFluxBLEKey`, and a struct for its body.
   - add the identity capability field for Bluetooth support (a bool the phone sets, named `Bluetooth`, `omitempty`,
     so older identities parse unchanged) in the `Identity` struct, and a per-transport type list field
     (`TransportTypes map[string][]string`, `omitempty`) so a BLE link can list only the 3 plugins.
   - `internal/proto/proto_test.go`: a test that both new types are in both lists, that an identity without the new
     fields still unmarshals, and that `ProtocolVersion` is still 8.
   Check: `go test ./internal/proto -count=1 && make vet`.
   Traps: tabs in Go; `gofmt` clean (`make vet` lists files that need it); do not change `ProtocolVersion`.

2. **`ble.go`: tag, gate, allow-list, limits** (codex). New `internal/lan/ble.go` (no D-Bus, no build tag, so it
   tests everywhere) and `internal/lan/ble_test.go`, plus a shared vector file
   `internal/lan/testdata/ble_vectors.json` that Android reads in step 10.
   - `bleTag(key []byte, t time.Time) [8]byte` and `bleTagMatches(key, tag, now) bool` (windows t-1, t, t+1).
   - `bleChallenge() []byte` (`ver || c(16)`, `crypto/rand`), `bleHello(key, c, tag) []byte` and
     `bleCheckHello(key, c, tags [][8]byte, hello []byte) bool` using `hmac.Equal`; an oversize or short hello
     fails; constants `bleGateTimeout = 3s`, `bleTLSTimeout = 10s`, `bleMaxLine = 16<<10`, `bleMaxClip = 4096`,
     `bleMaxNotifLine = 4<<10`, `bleMaxNotifyPerWindow = 10 per 10 s`.
   - `bleAllowed(p *proto.Packet, dir) (ok bool)` implementing the allow-list above (pair false only, a
     notification with `ticker`, `requestReplyId`, `actions` or `icon` set is dropped, notification
     request `cancel` only, approve kinds `request|cancel|response|shown`), and `bleLineOK(p, line []byte) bool`
     (16 KiB any line, 4 KiB notification line, clipboard 4096 bytes and 16 KiB line).
   - `bleServerConfig` and `bleClientConfig(cert, wantPin)` as copies of `internal/lan/tls.go:35-62` with
     `MinVersion: tls.VersionTLS13`; the client sets `VerifyPeerCertificate` to compare the leaf with `wantPin`
     before `GetClientCertificate` can run.
   - Tests (tag and hello vectors written to `testdata/ble_vectors.json`): same key and window same tag, windows
     -1, 0, +1 match and +-2 do not, the tag reveals no ID; wrong HMAC, a hello replayed with a different `c`, an
     oversize hello, a 3 s silence (use an injected clock or a short timeout variable) are all refused; each packet
     type outside the list is dropped; 4096 byte clip passes and 4097 does not; a clip of 4096 bytes that serializes
     over 16 KiB is refused; a 5 KiB notification line is refused; 10 notifications pass per 10 s and the 11th does
     not.
   Check: `go test ./internal/lan -run 'TestBLE' -count=1 -race && make vet`.
   Traps: `crypto/rand` only; constant-time compare; no new dependency (`go.mod` and `go.sum` unchanged:
   `git diff --exit-code go.mod go.sum`).

3. **BLE `Link`, `finishID`, lanes** (codex). `internal/lan/link.go`, `internal/lan/provider.go`, `internal/lan/tls.go`.
   - `link.go:67-72` `SetPaired` stores `proto.MaxPacketSize` when paired, and `onLink` (`daemon.go` about `:982`)
     calls it: for `Transport == "ble"` `SetPaired` must keep `bleMaxLine`; test that a paired BLE link still closes
     a 16 KiB + 1 line.
   - `link.go:28` struct: add `Transport string` (`"ip"` or `"ble"`) and the BLE queue fields; `newLink`
     (`:54-65`) keeps `addr, _ := conn.RemoteAddr().(*net.TCPAddr)` (nil for BLE, `IP()` at `:106-111` already
     returns `""`); `newLink` takes the transport.
   - `Send` (`:120-145`) and `Receive` (`:150-`): when `Transport == "ble"`, check `bleAllowed` and `bleLineOK`,
     drop and log once per type, never queue for IP; the write deadline is 5 s (`:133`) and each half-received line
     has a 5 s read deadline; `maxLine` is `bleMaxLine` from the start (see inconsistency 6).
   - 2 lanes: approve first, then the rest, at most 8 packets and 32 KiB (own constants, `maxSendQueue` `:50`
     unchanged for IP); a full lane drops the oldest notification and never an approval; whole lines under `wmu`.
   - `provider.go`, a new method `p.OpenBLE(conn net.Conn, target proto.Identity, outgoing bool) bool` that runs the
     gate (outgoing: read challenge, send hello; incoming: send challenge, read hello within 3 s), then `tls.Client`
     or `tls.Server` with the BLE configs, then `finishID` with `plainID` empty on the outgoing side. In `finishID`
     (`:632-`) for a BLE link: skip the plain identity version check on an empty `plainID`, and at `:692` compare
     the encrypted identity's `ProtocolVersion` with `proto.ProtocolVersion` (8) instead of `plainID.ProtocolVersion`
     (0 when empty, which would fail every handshake); require
     `p.cfg.Trusted(id)` to be `ok` with a non-nil pin that equals the certificate (the old block at `:665-673`
     only runs `if pinned, ok`); on any other result `tc.Close()` before `newLink` so no `onLink` and no device
     entry; on a BLE link write the identity only after the pin check.
   - Tests in `internal/lan/link_test.go` and a new `internal/lan/ble_link_test.go`: TLS 1.3 over `net.Pipe` ends in
     the same `finishID` result as TCP; a wrong pin is refused; a wrong CN is refused; an unpaired device is closed
     with no `onLink` call; the desktop sends no client certificate to a peer with the wrong pin (count
     `GetClientCertificate` calls); an unpair packet over BLE reaches the handler; an approve packet jumps ahead of a
     lane full of notifications; the lane drops the oldest notification; a 16 KiB line is closed; a write that
     blocks 5 s closes the link.
   Check: `go test ./internal/lan -count=1 -race && make vet`.
   Traps: `wmu` must not be held while waiting on the lane lock the other way round (deadlock; `-race` plus a
   timeout in the test); TCP behaviour must not change: the existing `internal/lan` tests must pass unchanged.

4. **Config, trust store, IPC** (coder).
   - `internal/config/config.go:94`, after `ApproveTimeout`, add `BLE bool \`toml:"ble,omitempty"\`` with a comment.
   - `internal/config/trust.go:15-31`, `TrustedDevice`: add `BLE bool \`json:"ble,omitempty"\`` and
     `BLEKey string \`json:"bleKey,omitempty"\`` (base64, 32 bytes). Add helpers `NewBLEKey()`.
   - `internal/core/api.go:322` area: a `ble.enable` IPC call (global on or off, and per device) and `ble` in the
     `status` reply; `cmd/flux` (`flux-cli status`) prints a Bluetooth line (off, on, "not supported on this BlueZ",
     "BlueZ refused access", adapter off).
   - Tests: `internal/config/config_test.go` (round trip, default off, older `devices.json` without the fields
     loads), `internal/core/api_cli_test.go` (the call and the status line).
   Check: `go test ./internal/config ./internal/core -run 'BLE|Config|Trust|Status' -count=1 && make vet`.
   Traps: `devices.json` keeps its mode (0600) and the atomic write used by the store; `ble_key` never appears in a
   log line, an IPC reply or `flux-cli status` (assert in the test).

5. **Docs, part A, before the approve path changes** (coder; the intent requires it).
   - `docs/approve.md`: transports, `shown`, the new timeline, "What an attacker can do" (Bluetooth: block or
     delay, never read, change, replay or approve), Failure modes, the standby condition, the answer on any link.
   - `docs/architecture.md`: a "Bluetooth direction" section next to "Network direction"; pairing and the BLE key;
     the line near `:78-84` that an unpair also closes the BLE link.
   - `docs/security.md`: the trust model (TLS 1.3, pin, gate, tag, what a stranger sees: the UUID).
   Check: `grep -n 'Bluetooth' docs/approve.md docs/architecture.md docs/security.md | wc -l` is above 0 for all
   three; relative links resolve: `rg -n '\]\((?!http)[^)#]*\.md' docs/approve.md docs/architecture.md docs/security.md`
   and open each target by hand (no link checker in the repo).
   Traps: docs prose rules of the repo (short sentences, no emoji); the `docs/approve.md` change is its own commit
   before step 7.

6. **Daemon: key distribution, limits, rules** (coder). All against a fake BLE transport; no BlueZ yet.
   - New `internal/core/ble.go`: a `bleTransport` interface (`Start`, `Stop`, `SeenTag(deviceID) time.Time`,
     `Connect(ctx, dev) (net.Conn, error)`, `Status() string`) and the manager that owns the per-device flags, the
     key distribution (send `flux.ble.key` for an IP link of a paired device when both desktop flags are on and the
     phone's last `flux.ble.state` is `on:true`, i.e. on that packet, on `ble.enable`, and again after a re-pair;
     `on:false` closes the device's BLE link and drops its queue) and the rule that a BLE link is made only while `dev.link == nil` (see inconsistency 2).
   - Transport-aware link checks: add `HasIPLink` next to `HasLink` (`daemon.go:502-506`); use it at
     `internal/lan/provider.go:396` (the dial skip) and `daemon.go:872` (the `dialKnown` skip), so a BLE link never
     blocks IP reconnection. `internal/core/handlers.go:19-30`: add the secondary slot `dev.bleLink` and accept
     `flux.approve` `response`, `shown` and `cancel` from it (nothing else); in `onLink` an arriving IP link moves a
     current BLE link to `dev.bleLink` while the device has an open approval, else closes it.
   - `internal/core/notifications.go:238-242`: on a BLE link ignore `ReplyID` and `Actions` (show only, no reply or
     action control).
   - `internal/core/daemon.go:943-983` `onLink`: keep the IP link as `dev.link` when it arrives while BLE is
     current; do not clear `dev.notifications` for a BLE link (`:983-986`); do not send `requestNotifications`
     (`notifications.go:390`) on BLE. `daemon.go:1105-1128` `receive`: no new branch, check that a replaced BLE link
     drops no approvals. `daemon.go:1347-1359` `send`, `policies.go:38-48` `featureLinks`: unchanged, test only.
   - `internal/core/clipboard.go:351-389` `onLocalClipboard`: make `fits` a per-link check at send time (4096 bytes
     and the 16 KiB line for BLE, `maxSentText` for IP), toast "over Bluetooth", keep the history entry;
     `clipboard.go:510` `handleClipboard`: the transport limit for an oversize clip.
   - `internal/core/device.go:214-264`: `transport` in the device list and the unavailable features for a BLE link.
   - Unpair paths in `pairing.go` (`dropApprovals` calls at `161,218,309,377,411`) also close the BLE link and delete
     `ble_key`.
   - Tests (a `fakeBLE` in `internal/core/ble_test.go`): key sent once per IP link and after a re-pair, only when
     both desktop flags are on and the phone's `flux.ble.state` is `on:true`; a state change to `on:false`
     mid-session closes the BLE link; with a BLE link up, `HasIPLink` is false and IP is still dialed; an approval
     `response` arriving on `dev.bleLink` after IP became current is delivered; a notification with `ReplyID` or
     `Actions` over BLE shows no reply or action; turning a flag off closes the BLE link and drops the BLE queue without moving it to IP and
     keeps the key; a 4096 byte clip crosses BLE and 4097 does not, the history keeps it; a 50 notification burst
     shows at most 10; no `requestNotifications` over BLE; IP up while BLE is current switches `dev.link` and
     cancels nothing; unpair closes both links and deletes the key; `ApproveEnroll` over BLE is refused with the
     text above.
   Check: `go test ./internal/core -run 'BLE|Clipboard|Notification|Pairing|Link' -count=1 -race && make vet`.
   Traps: a BLE link must never create a device entry for an unknown device (step 3 closes it earlier; test the
   daemon side too); `featureLinks` must show no feature as available that is not allowed on BLE.

7. **Approvals over Bluetooth** (codex). `internal/core/approve.go`, against the fake transport.
   - `:248-262` `approveDevice`: Bluetooth-ready standby (flag, capability, BLE link up or a tag seen in the last
     30 s), else fail at once as today.
   - `:264-300` `ApproveRequest`: register, return `Started` at once; the send, connect and re-sends run in a
     goroutine under the request's deadline set at `:269-272`; never extended; the `timeout` field over BLE is the
     time left.
   - `:410-438` `handleApprove`: also called for packets from `dev.bleLink` (step 6); new kind `shown`; record it per request ID; the 3 s timer: no `shown` means keep IP
     and start a BLE connect and send the same ID over BLE (only if at least 5 s remain); a request is never
     restarted after it ends.
   - `:383` `dropApprovals`: skipped when the lost link was IP and the device is Bluetooth-ready with an open
     request; the request waits on any link until its deadline.
   - `:302-326` `ApproveEnroll`: refuses on a BLE link.
   - `:131` `deliver`: first answer wins (unchanged); test that a late duplicate is ignored.
   - Tests (fake transport, fake clock): the whole approval list of the spec's Verification (switch IP to BLE keeps
     the ID and deadline and gives 1 prompt; a late duplicate and a cancelled ID do not come back; `shown` missing at
     3 s adds a BLE send and keeps IP; a late prompt on a healthy IP link and a loss of IP after `shown` give 1
     prompt and 1 answer; `ApproveRequest` returns `Started` inside 3 s with a blocked link; IP returning during an
     open approval cancels nothing; unpair closes both and cancels; `enroll` over BLE refused; `approveDevice` fails
     at once with no BLE ready; fewer than 5 s left starts no connect).
   Check: `go test ./internal/core -run 'Approve' -count=1 -race && make vet`.
   Traps: read `docs/approve.md` first (root helper, nonce, time checks stay; `fluxd` only carries the request);
   the helper's 3 s `approve.request` timeout; the timeline in the spec is a target (inconsistency 3); no change to
   `cmd/flux-approve`.

8. **BlueZ over D-Bus** (coder). New `internal/lan/ble_linux.go` and `ble_other.go` (stub returning "Bluetooth is
   not supported here"), same split as `usertimeout_linux.go` and `usertimeout_other.go`.
   - Uses the system bus the way `mdns.go:12-21` uses Avahi (same `Call()` seam, same retry back-off `mdnsRetryMin`
     and `mdnsRetryMax`, `mdns.go:28-31`).
   - Calls: `Adapter1.SetDiscoveryFilter` (transport `le`, the Flux UUID, duplicate data on, active scan so the scan-response service data with the tag arrives), `StartDiscovery`,
     `StopDiscovery`, a watch on `ObjectManager.InterfacesAdded` and `PropertiesChanged` for `Device1.ServiceData`,
     `Device1.Connect`, then `GattCharacteristic1.AcquireWrite` and `AcquireNotify` and a `net.Conn` over the two
     `os.File`s with deadlines. No `Pair`, `Trust`, `SetPowered`, `RemoveDevice`. Adapter off: report, do not power
     it on. BlueZ older than 5.46: "Bluetooth is not supported on this BlueZ". Access denied: "BlueZ refused
     access". A2DP active (`MediaTransport1.State == active`): no new scan, keep an existing link. Scan timeout 2 s,
     connect and GATT 3 s.
   - It implements the step 6 `bleTransport` interface; `cmd/fluxd` wires it only when `ble = true`.
   - Tests in `internal/lan/ble_linux_test.go`: an in-process fake BlueZ object behind the same `Call()` seam (the
     way `internal/lan/mdns_test.go:148-193` fakes Avahi with `fakeAvahi`, not a private bus) for the scan, connect,
     `AcquireWrite` and `AcquireNotify` path (two `os.Pipe` pairs), adapter off, access denied, A2DP active, tag
     windows, and that no forbidden method is ever called (the fake fails the test on `Pair`, `Trust`, `SetPowered`,
     `RemoveDevice`).
   Check: `go test ./internal/lan -run 'TestBlueZ|TestBLE' -count=1 -race && make vet && go build ./...`.
   Traps: build tags: `go vet` and `GOOS=darwin go build ./cmd/... ./internal/...` must still compile
   (`ble_other.go`); no new dependency (`git diff --exit-code go.mod go.sum`); `fluxd` still runs as the user, no
   capability.

9. **Android prototype: TLS over a GATT-stream Socket** (coder, lead checks on a phone). This is the one unproven
   piece (inconsistency 5).
   - New `A/net/BleStream.kt`: a `java.net.Socket` subclass whose `getInputStream` and `getOutputStream` are the GATT
     stream (for now backed by a `PipedInputStream` pair so the JVM test runs without a radio).
   - `T/net/BleStreamTest.kt`: `SSLSocketFactory.createSocket(socket, host, port, true)` from the existing `Tls`
     (`A/net/Tls.kt:30`) negotiates TLS 1.3 against a server on the other end of the pipe (asserting
     `session.protocol == "TLSv1.3"`) and `Tls.peerCertificate` returns the certificate.
   - `A/net/Tls.kt:21-22,34` forces `SSLContext.getInstance("TLSv1.2")` and `enabledProtocols = TLSv1.2`: add a
     `protocols` parameter (default TLS 1.2 so the TCP path is unchanged; BLE passes TLS 1.3, using
     `SSLContext.getInstance("TLS")`) to the helper that builds the context and the socket.
   - On a real Android 12 to 16 phone: run the same negotiation through a `BluetoothGattServer` and note the result
     per Android version in the PR. A failure here switches step 10 to the loopback bridge (a `ServerSocket` on
     127.0.0.1 with a random port and a one-time token, as in `android/tsnet`); it does not change the design.
   Check: `cd android && ./gradlew :app:testDebugUnitTest --tests '*BleStreamTest*' --no-daemon` (CI runs the same
   job at `build.yml:128`).
   Traps: the prototype ships no manifest change and no UI; `LanBackend.kt` is not touched yet; the TCP path keeps
   TLS 1.2 (a test that `LanBackend` still negotiates 1.2).

10. **Android link code** (coder). `A/net/BleBackend.kt` (new), `A/net/LanBackend.kt:420-444`, `A/core/TrustStore.kt`.
    - `BleBackend.kt`: GATT server with the service and the `rx`/`tx` characteristics, one connection at a time,
      the challenge and hello gate (3 s) and the TLS 1.3 timeout (10 s), a bounded read queue, chunks of MTU minus 3
      with `onNotificationSent` pacing, the tag advertisement from step 11's schedule; produces a `Link` through
      `LanBackend.finish` after moving the pin check before the identity write (the BLE path checks first; the TCP
      path at `LanBackend.kt:420-444` is unchanged). `finish` is `private` at `LanBackend.kt:420`: make it `internal`
      (or move it to a shared helper) with no behaviour change; BLE uses the TLS 1.3 option added to `Tls.kt` in
      step 9.
    - `A/core/FluxCore.kt:360` and `:410`: a BLE link is not closed by `old.close()` nor ignored by the
      `d.link !== link` return while a prompt of that computer is open (secondary link; approval packets only).
    - `A/net/ApproveMessage.kt:139-143` `admit()`: same `id` and `computerId` with a different `timeout` is a
      `Repeat`, not a `Conflict`; the first `timeout` fixes the deadline (test: request, then the same request with
      less time, gives one prompt and no `failed`).
    - `A/net/Link.kt:49` has one writer thread and `LinkedBlockingQueue(MAX_QUEUED_PACKETS)`: for BLE use two lanes
      (approve first, rest second), at most 8 packets and 32 KiB, drop the oldest notification when full, never an
      approval; 5 s write deadline and 5 s deadline for a half-received line (tests in `T/net/LinkTest.kt`: approve
      jumps ahead of 8 notifications, the 9th drops the oldest, a stalled write closes the link, a half line closes
      it).
    - `A/net/NotificationSync.kt:313-320` serializes `ticker`, `requestReplyId`, `actions` and `icon`: for a BLE link
      send none of them and only title and text (each cut to 1 KiB, then the line cut to 4 KiB after
      serializing); test the serialized BLE packet has none of the four fields.
    - Send `flux.ble.state` `{"on":bool}` after the identity on each IP link and on every change of the switch,
      permission or per-computer flag.
    - `TrustStore.kt:25-61`: `ble_key` wrapped with an AndroidKeyStore AES-GCM key, only the ciphertext in the
      `trusted` prefs; deleted on unpair and when the unpair packet arrives; handler for `flux.ble.key` (IP only).
    - `Link.kt:42-46`: carry the transport; the allow-list and the limits of step 2 for BLE; one prompt per request
      ID kept until the deadline also after a cancel; the phone holds an answer until the deadline and sends it on
      the carrying link else on any other; `shown` is sent when the prompt is on screen; the notification packet for
      BLE is cut after serialization to 4 KiB.
    - Tests: `T/net/BleGateTest.kt` and `T/net/BleTagTest.kt` read `internal/lan/testdata/ble_vectors.json` (path
      through a Gradle test resource copy, not a duplicate file) and equal the Go vectors; `T/net/LinkTest.kt`
      additions for the allow-list, the 4 KiB clip limit, the notification trim and the one-prompt rule.
    Check: `cd android && ./gradlew :app:testDebugUnitTest :app:lintDebug --no-daemon`.
    Traps: no `BLUETOOTH_SCAN`, no location anywhere (lint and a `grep -rn 'ACCESS_.*LOCATION\|BLUETOOTH_SCAN' android/app/src/main`
    that finds nothing); never log `ble_key`; `LanBackend.kt` TCP behaviour unchanged.

11. **Android manifest, permissions, schedule, settings** (coder).
    - `android/app/src/main/AndroidManifest.xml:5-49`: add `BLUETOOTH_ADVERTISE` and `BLUETOOTH_CONNECT`; the
      `bluetooth_le` `<uses-feature required="false">` next to `:51-53`; nothing else (inconsistency 8).
    - `FluxService` (`AndroidManifest.xml:155-158` `connectedDevice`): advertise the UUID in the advertisement and one tag in the scan response (21 and 26 bytes;
      several desktops take turns every 1 s); start and stop advertising on the
      `ConnectivityManager` default-network callback; `ADVERTISE_MODE_LOW_POWER`; 15 minutes then 1 minute in 15
      with an inexact alarm; full again on unlock, on opening Flux and on a default-network change; none while an IP
      or a BLE link is up.
    - Settings switch (global plus per computer) in `A/ui/` (the settings screen that holds the other switches):
      hidden when `SDK_INT < 31`; asks for the two permissions only on turning it on; a revoked permission turns it
      off and says so; the line "connected over Bluetooth" on the computer's row.
    - Tests: `T/service/` unit tests for the schedule (clock injected), the permission flow (granted, denied,
      revoked while on) and that Android 10 and 11 hide the switch.
    Check: `cd android && ./gradlew :app:testDebugUnitTest :app:lintDebug :app:assembleDebug --no-daemon`.
    Traps: the `connectedDevice` foreground service type is unchanged; the approval prompt activity is unchanged;
    no permission is requested at first start.

12. **Desktop UI** (coder). `gui/qml/` shared views and the shell plugin (`gui/omarchy/`): a Bluetooth switch (global
    and per device), the "connected over Bluetooth" line from `transport: "ble"`, the unavailable feature text, the
    "BlueZ refused access" and "not supported on this BlueZ" messages; the mock backend gets the new fields.
    Check: `make snapshot test-gui`, and `make build-gui`.
    Traps: keep shared QML compatible with both hosts (`AGENTS.md`); the plugin sandbox tests of
    `README.md#working-on-it` run if the plugin files change, and the PR says they passed; Chrome previews hang the
    AMD GPU on the owner's host, so use headless offscreen rendering only.

13. **Docs part B, Nix, Arch** (coder).
    - `docs/features.md`, `docs/troubleshooting.md`, `docs/configuration.md` (`ble`), `docs/android.md`.
    - `nix/nixos.nix`: an option, default off, that sets `hardware.bluetooth.enable` only when the user asks;
      `nix/home-manager.nix`: no option; `docs/nix.md` says to enable BlueZ in the system config. `dist/arch/PKGBUILD`:
      `bluez` as an `optdepends`. A check in `nix/checks.nix` that the default config does not enable Bluetooth.
    Check: `nix flake check` (builds nothing on p510; run on the owner's chosen host) and
    `git diff --stat docs nix dist | tail -3`.
    Traps: Flux must not turn Bluetooth on by itself; `nix flake check` on p510 needs the owner's OK before any build
    or deploy.

14. **Whole-repo check and runtime test** (lead).
    - `make build test vet` (race on); `go build ./... && go vet ./... && go test ./...`; `cd android && ./gradlew
      assembleDebug testDebugUnitTest`; `nix flake check`.
    - The runtime test of the spec's Verification on the owner's chosen host (p510 only with the owner's OK) and an
      Android 12+ phone with Wi-Fi off: pair over Wi-Fi, key sent; Wi-Fi off, connected within 10 s; notification;
      4 KiB clip both ways and 4.1 KiB refused on both sides; `sudo true` with the phone locked inside 20 s, with a
      stuck IP link (firewall, not closed), out of range, Bluetooth off, Flux force-stopped (each asks for the
      password); Wi-Fi back during a request (1 prompt, not cancelled); unpair during a BLE link (prompt closes);
      A2DP (see inconsistency 4); `btmon` for a day (no name, no ID, tag changes every 15 minutes); 2 hours of
      battery with the computer out of range.
    Check: the commands above; the PR description links intent, spec and plan, says which steps the coder did, and
    says the plugin sandbox tests passed if they apply.
    Traps: no deploy, switch or reboot without the owner's OK; a step that deviates updates this plan in the same
    commit as the code.

## Tests

Per step, as listed. The spec's Verification section maps to: Go unit tests in steps 2, 3, 6, 7, 8
(`go test ./internal/lan ./internal/core ./internal/proto`); the fake BlueZ in step 8; Android unit tests with shared
vectors in steps 9 to 11; build and Nix checks in steps 13 and 14; the runtime test in step 14; the 7 docs in steps
5 and 13. Each Go test file is built to run with `-race` on a host with no Bluetooth adapter.

## Rollback

- Every Bluetooth path is behind `ble = false` (desktop) and the phone switch, both off by default, and lives in
  separate files (`ble.go`, `ble_linux.go`, `ble_other.go`, `BleBackend.kt`). Turning the flags off, or setting
  `ble = false` and restarting `fluxd`, returns to IP-only behaviour with no data change.
- The only data added is `ble`/`bleKey` in `devices.json` (ignored by older versions) and the wrapped key on the
  phone; deleting those fields is safe, and an unpair removes them.
- Reverting is `git revert` of the step commits in reverse order. The IP protocol version stays 8 and the new
  packet type, kind and capability are ignored by older apps, so no peer needs a matching revert.
