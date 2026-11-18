---
status: draft
issue: 69
spec: spec/2026-11-15-69-ble-transport.md
coders:
  0: lead    # gate, baseline and branch checks; nothing to code
  1: coder   # constants, two packet types, struct fields and a list edit, given in full
  2: codex   # small tricky crypto logic (tag, challenge/hello gate, allow-list, limits) with exact vectors
  3: codex   # TLS 1.3 over any net.Conn, paired-only check before onLink, two lanes: subtle ordering and locking in link.go and provider.go
  4: coder   # config fields, devices.json fields, IPC call and status line, same shape as existing ones
  5: coder   # prose edits to three docs, content given below
  6: coder   # consent exchange, key distribution, secondary slot, per-link limits, daemon rules against the fake transport
  7: codex   # approval timeline: background send, shown, 3 s rule, 5 s rule, same packet, deadline never extended; the riskiest logic
  8: codex   # packet-to-stream wrapper over SOCK_SEQPACKET fds: small, tricky, tested on a socketpair
  9: coder   # BlueZ glue over godbus behind the same Call() seam as Avahi, with a fake BlueZ
  10: coder  # Android prototype of TLS over a Socket subclass and the address-rotation gate; lead runs the on-phone check
  11: coder  # BleBackend, key store, allow-list, one-prompt rule on Android, with shared vectors
  12: coder  # manifest, permission flow, advertising schedule, settings switch
  13: coder  # Qt GUI and shell plugin switch, device list transport line
  14: coder  # remaining docs, Nix option, Arch optional dependency, nix checks
reviewers:
  - codex    # independent read of each step's diff against this plan; strong on the TLS, lane and deadline logic
---

# Plan: Bluetooth LE as a fallback for notifications, clipboard text and approval

Issue #69 (olafkfreund/nixarchy-flux; every `gh` command uses `-R olafkfreund/nixarchy-flux`). Intent
`intent/2026-11-15-69-ble-transport.md` and spec `spec/2026-11-15-69-ble-transport.md` are approved (the spec was
re-approved on 2026-11-18 after it settled the contradictions found while planning). This plan stands on its own: it
carries every approved spec decision, so it can be implemented without opening them. Work happens in the worktree
`/mnt/code/Source-home/GitHub/nixarchy-flux-69` on branch `feat/69-ble-transport`. Android paths are under
`android/app/src/main/java/org/omarchy/flux/` (written `A/`), Android unit tests under
`android/app/src/test/java/org/omarchy/flux/` (written `T/`). Line numbers are the spec's corrected references, from
the tree before step 1 (base `edd4d2f`); they drift as steps land, so each step names the function too.

## Spec inconsistencies

None open. The 8 contradictions found while drafting this plan are settled in the spec, section "Settled while
planning (2026-11-18)", and the steps below follow those settlements. The owner confirms or changes the settled
choices before this plan is approved.

## Gate (read before anything)

- **Implementation starts only after the bjarneo/flux #185 maintainer agrees to this design** (intent decision 5).
  The only reply so far is from a non-maintainer, which is not the maintainer's agreement. The owner decides whether
  to post the spec there. This plan may be written and reviewed as `status: draft` now, but it is not approved and no
  step starts until the agreement exists (a link to the comment goes into step 0).
- **Any build or deploy on p510 needs the owner's OK first.** Local `go test` and `make` in the worktree are fine.
  No step stages, commits, pushes, deploys, switches or reboots by itself; the lead commits after each step's check
  passes (one commit per step, `feat(ble): step N ... (#69)`).
- Agents other than the lead's own session never work on `main` or in `~/.config/nixos`.
- **Address gate (owner decision before approval).** Step 10 records whether the Android random address changes with
  a tag window. The one exception to "nothing starts before approval" is a throwaway address check, run by the owner
  or the lead on a phone before approval: advertise the 21-byte advertisement and the 26-byte scan response, read
  the advertising address across 2 tag windows, and write the result into the PR draft. It changes nothing in the
  repo and ships nothing; step 10 then repeats it in its approved form. If the address does not change, the privacy
  constraint is not met and the owner decides, before approving this plan, between a shorter advertisement life (it
  stops at the end of each window and starts again later with a pause) and dropping the feature. The owner's
  approval of the plan includes this exception and the address-gate result. The feature stays off by default either
  way.

## The approved decisions

- **Scope (1).** Only notifications (show only), clipboard text and approval. No replies, actions, SSH signing, no
  files, clipboard images or history, no screen, camera, mic, speaker, remote input, SSH, SFTP, herdr, SMS, calls.
  No pairing and no approval-key enrollment over Bluetooth. Not used while IP works.
- **Platforms (2).** Linux `fluxd` and Android 12+ only. No location permission. iOS, macOS, Windows, Android 10 and
  11 unchanged (the switch is hidden on Android 10 and 11).
- **Dependency (3).** BlueZ only through `godbus/dbus/v5`, already in `go.mod`. No new Go Bluetooth dependency. No
  `Pair`, `Trust`, `SetPowered` or `RemoveDevice` on a device Flux did not create. Adapter off: report it, do not
  switch it on.
- **Default and consent (4).** Off on both sides. Desktop: global `ble = false` in `config.toml` plus a per-device
  `ble` flag in `devices.json`. Phone: a global switch plus a per-computer flag, and the Android permission. All are
  needed. `ble_key` is sent only when both ends have the global and the per-device flag on. Each side tells the other
  its effective state for the pair in a new packet `flux.ble.state` with `{"enabled":bool}` (new type in
  `internal/proto/identity.go`, added to `Incoming` and `Outgoing` so older apps never see it). The phone's state is
  true only when its global switch, its per-computer flag and the Android permission are all on; the desktop's is
  true when its global `ble` and the per-device `ble` flag are on. It is sent on every IP link after the identity, on
  every BLE link right after TLS, and again at each change. Each side keeps the peer's last state in memory only and
  takes it as `false` until it has seen one on a link since start.
  - **Admission on the desktop:** scan for and connect to a device only when its own state is on, the device has a
    `ble_key`, and the phone's last state is `true`. A kept key with either state off means no scan and no connect.
  - **Admission on the phone:** advertise a tag for a desktop only while its own state for that desktop is on; the
    gate refuses a hello whose tag belongs to a desktop that is off.
  - `finishID` for `Transport == "ble"` closes the link when the device's desktop-side flag is off or a
    `flux.ble.state` with `false` arrives, so a state that changed while the link was being made is honoured.
  - **Disable rule.** Turning the global flag off, or revoking the Android permission, stops the scan and the
    advertisement, closes every BLE link, and drops anything queued for BLE (never moved to IP). Turning off one
    device does the same for that device. An open approval then follows the "IP fails after `shown`" rule: with a
    live IP link it goes on over IP and nothing changes for the user; with no link left it is kept until its
    deadline, as for an IP loss, but no BLE connect is started, so it ends at the password. The key is kept, so
    turning it on again needs no IP link.
- **Upstream (5).** The Gate above.
- **Settled values (6).** Clipboard limit 4 KiB (4096 bytes UTF-8, and the serialized line at most 16 KiB). Switch
  time fits the default `approve_timeout` of 20 s (the rows are targets; the runtime test measures them). Supported:
  BlueZ 5.46 or later with a BLE adapter, `fluxd` as the logged-in user's `fluxd.service`; other setups are best
  effort and the feature says why it is off. Test host: a Linux host with a BLE adapter and an Android 12+ phone with
  Wi-Fi off; p510 only with the owner's OK.
- **Roles and stream.** Phone = GATT peripheral (`BluetoothGattServer`, advertises; needs only `BLUETOOTH_ADVERTISE`
  and `BLUETOOTH_CONNECT`). Desktop = central (scans, connects). One GATT service, no ATT encryption requirement,
  2 characteristics: `rx` (desktop to phone, write without response) and `tx` (phone to desktop, notify). The
  desktop uses `GattCharacteristic1.AcquireWrite` and `AcquireNotify` (BlueZ 5.46 or later): two fds, each returned
  next to its MTU, wrapped as a `net.Conn` in `internal/lan/ble_linux.go` (`Read`, `Write`, `Close`, deadlines
  through `os.File`). **Packet-to-stream wrapper.** The fds are, as far as the BlueZ source shows, `SOCK_SEQPACKET`
  sockets (one read or write is one packet, at most the returned MTU); this is not verified on a real adapter, so the
  rules hold for a plain stream fd too:
  - `Write(p)` splits `p` into packets of at most the write MTU, sends them in order, loops on a partial or `EAGAIN`
    send until all of `p` is sent or the deadline passes, and returns the byte count sent.
  - `Read(p)` reads 1 packet into a buffer of the read MTU and keeps the bytes that do not fit in `p` for the next
    `Read`, so a small `p` (the 5-byte TLS record header read) never loses data and a short packet is never taken for
    the end of the stream. A zero-length packet is skipped; a closed fd is `io.EOF`.
  The phone writes each chunk with `notifyCharacteristicChanged` and waits for `onNotificationSent` before the next;
  it reads `onCharacteristicWriteRequest` into a bounded queue; a chunk is at most ATT MTU minus 3. No framing of its
  own: a GATT connection is ordered and the link layer retransmits, so with the two packet rules applied on both
  sides the stream is reliable and ordered, and TLS records detect damage. No sequence numbers. BlueZ negotiates the
  ATT MTU on connect (up to 517); the link works at 23 (20 byte chunks) but slowly; Flux logs the MTU and the limits
  assume at least 10 KB/s.
- **Trust.** TLS 1.3 only on this transport (copies of `serverConfig` and `clientConfig` in `internal/lan/tls.go:35-62`,
  `MinVersion TLS13`). No pre-TLS `plainIdentity` (`provider.go:205-212`); the outgoing link takes the device ID from
  the certificate (`finishID` already does, `provider.go:645-653`). The desktop (TLS client) checks the phone's
  certificate against the pin of the target in `VerifyPeerCertificate`, which runs before `GetClientCertificate`
  (`tls.go:53-60`), so it never sends its certificate to a wrong peer. The phone (TLS server) learns the desktop only
  after the gate. Android's `LanBackend.kt:420-428` writes its identity before the pin check (`:434-438`);
  `BleBackend.kt` moves the pin check first and writes the identity after.
- **BLE key.** 32 random bytes per paired device, `ble_key`, made by the desktop and sent on an existing paired IP
  link in a new packet `flux.ble.key` (new type in `internal/proto/identity.go`, added to `Incoming` and `Outgoing`).
  It goes only over IP, never over BLE. Desktop stores it with the pin in `~/.local/share/flux/devices.json`. Android
  keeps pairing data in plain `SharedPreferences("trusted")` (`TrustStore.kt:25-31`, `prefs.edit().putString` at
  `:61`), so `ble_key` is new code there: an AndroidKeyStore AES-GCM key wraps it and only the ciphertext goes into
  that prefs file. It is sent when the user turns Bluetooth on for the desktop, on each IP link of a paired phone (per
  the admission rule above), and after a re-pair, so the first Bluetooth use needs one IP link after both sides are
  on (a stated limit). An unpair deletes it on the unpairing side at once and on the peer when the unpair packet
  arrives (over BLE too); with no link the peer's copy stays until its next link (a stated limit).
- **Gate (challenge and response).** On connect the phone sends `challenge = ver(1) || c(16)` (16 fresh random
  bytes) in a `tx` notification. The desktop answers
  `hello = ver(1) || nonce(16) || HMAC-SHA256(ble_key, "flux-ble-hello" || c || nonce || tag)[:16]`. The phone checks
  it against the tag it advertised in the current and the previous window. `c` is new per connection, so a captured
  hello is useless on any other connection and no nonce cache is needed. Anything else, or no hello within 3 s,
  closes the connection before any certificate is shown. Gate failures cost one HMAC. A spoofed tag gets an attacker
  only a failed attempt; if it relays, the pinned end-to-end TLS makes it a pure relay (delay or block, not read or
  change). The gate is not trust: the pinned certificate check authenticates both peers, and a holder of `ble_key`
  without the pinned certificate is refused.
- **Tag and air data.** Legacy advertising, each packet at most 31 bytes (the limit is from the Android
  documentation; the prototype checks the real size). Advertisement = flags (3) + 128-bit Flux service UUID (2+16) =
  21 bytes, which BlueZ needs to filter scans. Scan response = service data keyed by that UUID with
  `tag = HMAC-SHA256(ble_key, "flux-ble-tag" || be64(t / 900 s))[:8]`, 2+16+8 = 26 bytes. BlueZ scans actively by
  default, so it asks for the scan response. No name, no device ID, `setIncludeDeviceName(false)`. One tag in a
  payload, never 2: with several Bluetooth-enabled paired desktops the phone rotates the scan response between their
  tags every 2 s (one advertising set, no extra adapter instance), or uses one set for each when the adapter offers
  them. The tag changes every 15 minutes; the desktop accepts windows t-1, t, t+1 (clock skew up to 15 minutes); the
  phone restarts the advertisement at each change and asks for a new random address. **Rotation of the address is a
  goal with a gate, not a fact** (Gate above; step 10 and runtime test 7 record the address across 2 tag windows). A
  stranger cannot link tags across windows without `ble_key`. What stays visible: that some device nearby runs Flux
  (the UUID); accepted. A tag never authorizes anything; it only tells the desktop which paired device to try.
- **Limits on strangers.** The phone accepts 1 connection at a time (the GATT server refuses a second until the first
  has passed or failed the gate), drops a connection that has not passed the gate in 3 s and one that has not finished
  TLS in 10 s. The desktop connects only to a tag that matches a paired, Bluetooth-enabled device. A BLE link reads at
  most 16 KiB per line from the start, paired or not; `maxUnpairedLine` (64 KiB, `tls.go:23-25`) is IP-only and
  `SetPaired` (`link.go:67-72`) keeps the 16 KiB limit on a BLE link.
- **Paired-only is new code.** The pin check in `provider.go:665-673` runs only `if pinned, ok := p.cfg.Trusted(...)`,
  so an unknown device passes it; `refuseLocked` (`device.go:214-227`) returns "" when `!ok`, and `onLink`
  (`daemon.go:943-946`) first calls `discoveredLocked`, which creates a device entry. So for `Transport == "ble"`,
  `finishID` requires a trusted device with a pin and the matching certificate, and otherwise closes the link before
  `onLink`: no device entry, no notification.
- **Link and daemon.** `Link.Transport` is `"ip"` or `"ble"`, set by `finishID`; `Link.Addr` may be nil (`IP()` at
  `link.go:106-111` returns `""`). The daemon sees a BLE link as the device's link when there is no IP link, so
  `dev.link`, `send`, `featureLinks` (`internal/core/daemon.go:1347-1359`, `internal/core/policies.go:38-48`) keep
  working. `Preferred` (`link.go:85`) never prefers BLE over IP.
  - A BLE link is made only while `dev.link == nil` for a paired, Bluetooth-enabled device, with one exception: the
    one device that has an open approval in the "unconfirmed" state may get a BLE link next to a live IP link, in a
    secondary slot `dev.bleLink`, not in `dev.link`.
  - When an IP link comes up while a BLE link is current, the IP link becomes `dev.link` as `onLink` does today
    (`daemon.go:957-970`) and the BLE link moves to `dev.bleLink`. It is not closed while an approval of that device
    is open, and is closed at the end of it (at most `approve_timeout` later). Its answers reach `approvals.deliver`,
    which keys on the device and the request ID (`approve.go:436`), not on the link. But `handlePacket`
    (`internal/core/handlers.go:19-30`) drops every packet from a link that is not `dev.link`, so it also accepts
    `flux.approve` of kind `response`, `shown` and `cancel` from `dev.bleLink`, and nothing else.
  - A BLE-only `dev.link` does not count as a link for dialing IP: `HasLink` (`daemon.go:502-506`), the skip in
    `internal/lan/provider.go:396` and the `dialKnown` skip at `daemon.go:872` use a new `HasIPLink`, so IP is still
    found while BLE is up. `HasLink` keeps its meaning for features.
  - `receive` (`daemon.go:1105-1128`) only drops approvals when the link that ended was `dev.link`; a replaced link is
    not current, so the switch from BLE to IP cancels nothing.
  - On Android `FluxCore.kt:360` (`old.close()` when a new link replaces the current one) and `FluxCore.kt:410`
    (`d.link !== link` return) get the same secondary-link rule: a BLE link stays open while a prompt of that computer
    is open, and approval packets are accepted and sent on it.
  - Config: `ble = false` in `config.toml` (`internal/config/config.go`, next to `approve_timeout`, `config.go:94`), a
    `ble.enable` IPC call, a Bluetooth line in `flux-cli status`, a switch in the Qt GUI and shell plugin settings.
    The device list shows `transport: "ble"`; features not on Bluetooth show as unavailable (`device.go:264` builds
    the feature list from what the device supports; a BLE link advertises only the 3 features).
- **Allow-list on a BLE link** (in `Link.Send` and the receive loop, `link.go:120,150`; anything else is dropped,
  logged once, never queued for IP): `flux.identity` (once, after TLS); `flux.ping`; `flux.ble.state` (not
  `flux.ble.key`); `flux.pair` with `{"pair":false}` only (an unpair, `pairing.go:195,202-226`, so a BLE-only link
  can still delete `ble_key` and the pin; `{"pair":true}` is dropped, pairing stays IP-only); `flux.notification`
  (show only: no icon or large image; the desktop ignores `ReplyID` and `Actions` on a BLE link,
  `internal/core/notifications.go:238-242`) and `flux.notification.request` with `cancel` only; `flux.clipboard`
  (not `.connect`, not `.image`); `flux.approve` with `kind` `request`, `cancel`, `response`, `shown` (not `enroll`,
  `enrolled`; `ApproveEnroll`, `approve.go:307-326`, refuses with "Enroll the approval key while the phone is on the
  network" when the link is BLE). The identity lists the types per transport, so the phone enables only these three
  plugins on a BLE link.
- **Size limits** (in `Link`, using `l.maxLine`, `link.go:67-74`; a BLE link never has the 16 MiB limit):
  any line 16 KiB read and write; clipboard text 4096 bytes UTF-8 and the serialized line at most 16 KiB (JSON
  escaping can expand control characters up to 6 times); notification title and text each cut to 1 KiB by the phone,
  no icon or large image, then the phone shrinks the text until the serialized line is at most 4 KiB, and the desktop
  drops a longer line; approval request and answer under 2 KiB. A longer clipboard text is never cut:
  `onLocalClipboard` (`internal/core/clipboard.go:351`) already has the "too large, did not sync" branch with a toast,
  but its `fits := len(text) <= maxSentText` check is global; it becomes a per-link check at send time (bytes and
  serialized line for BLE), and says "over Bluetooth". The history keeps the entry (`history.go:127`). On the phone the
  same text appears as a toast and in the Flux notification. `handleClipboard` (`clipboard.go:520`) refuses an oversize
  clip with the transport limit.
- **Stalls: two lanes.** One writer queue per BLE link, each lane at most 8 packets and 32 KiB waiting (its own
  constants; `maxSendQueue`, `link.go:50`, is 32 MiB for IP and unchanged).
  - **Control lane, sent first:** `flux.approve`, `flux.identity`, `flux.ble.state`, `flux.pair` (unpair). Never
    dropped. A full control lane means the peer or a bug is flooding it, and the link is closed (an approval then
    follows the "IP fails after `shown`" rule).
  - **Data lane:** `flux.notification` (and its `cancel`), `flux.clipboard`, `flux.ping`. When full, the new packet is
    queued and the oldest waiting packet of the first kind found in this order is dropped: notification, clipboard,
    ping (counted, logged once). A clipboard dropped this way gets the "did not sync" toast.
  - Whole lines only, under `wmu` (`link.go:120-145`): a packet is never split to let an approval in, so an approval
    waits for the line in flight (at most 16 KiB, about 1.6 s at 10 KB/s). BLE write deadline 5 s (not 30 s); each
    half-received line has a 5 s read deadline.
- **Reconnect bursts.** On a BLE link `requestNotifications` (`internal/core/notifications.go:391`) is not sent, and
  `onLink` does not clear `dev.notifications` (`daemon.go:983-986`). So the phone does not replay its active
  notifications. Only a notification posted while the link is up crosses it, at most 10 in any 10 s (the phone drops
  the rest, the desktop shows nothing for them and also stops at 10).
- **Approval over Bluetooth.** `docs/approve.md` changes first (the intent requires it). The root helper, root-owned
  key, nonce, time checks and biometric stay; `fluxd` only carries the request.
  - **Standby.** `approveDevice` (`approve.go:247-262`) fails at once when `dev.link == nil`, unless the device is
    Bluetooth-ready: Bluetooth on (consent exchange both true), the phone's identity has the new capability, and either
    a BLE link is up (then it is `dev.link`) or the desktop saw a tag of this device in the last 30 s. Otherwise it
    fails at once and PAM asks for the password.
  - **The phone shows the transport is down.** It starts advertising when it has no IP link to a paired
    Bluetooth-enabled desktop (link closed, or the default network lost via the `ConnectivityManager` callback) and
    stops when IP is up.
  - **Background delivery.** The helper's `approve.request` call has a 3 s timeout (`docs/approve.md:374-376`), but
    `ApproveRequest` calls `d.send` before it returns `Started` (`approve.go:293`). So `ApproveRequest` registers the
    request and returns `Started` at once; the send, the BLE connect and every later re-send run in a goroutine under
    the request's deadline.
  - **`shown`.** The phone sends `{"kind":"shown","id":...}` when the prompt is on screen (a new kind;
    `handleApprove`, `approve.go:410-438`, has none). No `shown` 3 s after the first send does not prove IP is dead,
    so `fluxd` keeps IP and in addition starts a BLE connect and sends the same request ID over BLE ("unconfirmed";
    the BLE link sits in `dev.bleLink`, which needs the phone to be advertising). The phone shows 1 prompt per request
    ID and keeps the IDs until their deadline, also after a cancel, so a duplicate, a second link or a cancelled
    request never gives or revives a prompt. The IP link is closed only by its own failure (a half-dead TCP link dies
    in 25 to 30 s: `userTimeout` `usertimeout_linux.go:16`, `keepAlive` `provider.go:139`).
  - **IP fails after `shown`.** The phone holds an answer until the request deadline: it sends it on the link that
    carried the request, else on any other link, and advertises when no link is left. `fluxd` does not cancel an open
    approval of a Bluetooth-ready device when its IP link ends (`dropApprovals`, `approve.go:383`, is skipped), starts a
    BLE connect if the 5 s rule allows, and waits for the answer on any link until the deadline.
  - **Switch time inside 20 s** (targets, measured by the runtime test): BLE link up: prompt within about 1 s (up to
    about 3 s behind a 16 KiB line). IP stuck, BLE link up: 3 s plus about 1 s, so about 4 s (up to 6 s), at least 14 s
    left; this holds only when the phone already advertises or is connected; if IP is stuck on both sides the phone
    never advertises, no tag is seen, and PAM asks for the password (Risks, accepted for version 1). No BLE link yet,
    tag seen: at most 8 s (scan hit 2, connect and GATT discovery 3, TLS 1.3 3), prompt at about 11 s, at least 9 s
    left; with an A2DP transport active there is no scan, so this row does not apply.
  - **Retry-timeout rule.** `fluxd` starts a connect only when at least 5 s of the wait remain. A request is never
    started again after it ends, and the deadline is the one `ApproveRequest` set (`approve.go:282-284`), never
    extended. The request sent over BLE is the same packet as the first send: same `id`, fields and `timeout`, never a
    reduced `timeout`. (Android clamps the field to at least 5 s, `ApproveMessage.kt:110`; a same-ID request with
    different fields is a conflict that closes the open request, `admit()`, `ApproveMessage.kt:139-143`; its expiry
    starts at receipt, `Approvals.kt:75`; an identical copy is a `Repeat` and changes nothing.) The desktop deadline
    counts: `fluxd` sends `cancel` over every link at its deadline, drops any answer after it, and the helper's own
    nonce and time checks refuse a late one. A prompt that starts late on the phone can outlive the desktop deadline
    by the delay of that copy; the cancel closes it. A switch that does not make it in time is a timeout, and PAM asks
    for the password.
  - **Cancel and unpair.** A cancel goes over whichever link is current and over `dev.bleLink` while it is open. An
    unpair closes every link of the device, including the BLE one, and deletes `ble_key` (`docs/architecture.md:84`;
    `pairing.go` calls `dropApprovals` at `161,218,309,377,411`, unchanged). The phone answers on the link that
    carried the request, else on the other link; `deliver` takes the first answer.
  - **Phone with the screen off.** The work runs in `FluxService` (`connectedDevice` foreground service,
    `AndroidManifest.xml:155-158`), which already keeps the IP link alive; the prompt is today's full-screen activity.
    Flux force-stopped, permission revoked, Bluetooth off each fall back to the password.
- **Android.** Manifest (`android/app/src/main/AndroidManifest.xml:5-49`): add `BLUETOOTH_ADVERTISE` and
  `BLUETOOTH_CONNECT`, declared plainly with no SDK attribute (Android 10 and 11 ignore a permission they do not know,
  as the manifest does for `POST_NOTIFICATIONS`); the feature is gated in code on `Build.VERSION.SDK_INT >= 31`, and
  the permission request and the switch exist only there. Add
  `<uses-feature android:name="android.hardware.bluetooth_le" android:required="false"/>` (next to
  `manifest:51-53`). No `BLUETOOTH_SCAN`, no location, no legacy `BLUETOOTH` or `BLUETOOTH_ADMIN`. Permissions are
  asked only when the user turns Bluetooth on; a revoked permission turns the feature off and says so.
  `net/Link.kt` takes an `SSLSocket` (`Link.kt:42-46`); Conscrypt can layer
  `SSLSocketFactory.createSocket(Socket, host, port, autoClose)` over a `java.net.Socket` subclass whose streams are
  the GATT stream, so `net/BleBackend.kt` produces a `Link` through the same `LanBackend.finish` (`LanBackend.kt:420`).
  This is the one unproven piece; the first Android step is a prototype of it, before `BleBackend.kt`. Fallback: a
  loopback `ServerSocket` bridge on 127.0.0.1 with a random port and a one-time token (the `android/tsnet` pattern).
  Battery: no advertising while an IP link to a paired desktop is up or while a BLE link is up; when IP is down,
  `ADVERTISE_MODE_LOW_POWER` (about 1 s interval), no scanning, an advertisement for each Bluetooth-enabled paired
  desktop (1 tag per payload, rotated every 2 s); full for 15 minutes after the IP link is lost without a BLE
  connection, then 1 minute in every 15 (inexact alarm), and fully again on screen unlock, on opening Flux and on a
  change of the default network. Notification text limits are applied where `NotificationSync.kt:306-320` builds the
  `Packet(Types.NOTIFICATION, ...)` for a BLE link, not in `FluxService`.
- **Linux packaging.** Supported: BlueZ 5.x with a BLE adapter, `fluxd` as `dist/fluxd.service`. Older than 5.46:
  "Bluetooth is not supported on this BlueZ", IP works as before. No BlueZ, no LE dongle, container, system service,
  PipeWire-only stacks: best effort, the feature says why it is off. Access: the system bus policy for `org.bluez`
  lets a logged-in user call it; some distributions need the `bluetooth` group; no root, no capability; BlueZ denies a
  call: "BlueZ refused access" in `flux-cli status` and the GUI, keep IP. `nix/nixos.nix` gets an option to set
  `hardware.bluetooth.enable` only when the user asks (never by default); `nix/home-manager.nix` cannot set it (it
  configures only `home.packages` and `systemd.user.services`), so it gets no option and the docs say to enable BlueZ
  in the system config; `dist/arch` lists `bluez` as an optional dependency. Flux does not turn Bluetooth on itself.
- **BlueZ calls.** `Adapter1.SetDiscoveryFilter` (transport `le`, the Flux UUID, duplicate data on), `StartDiscovery`,
  `StopDiscovery`, a watch on `ObjectManager.InterfacesAdded` and `PropertiesChanged` for `Device1.ServiceData`, then
  `Device1.Connect` and the GATT objects under the device. BlueZ deletes an unbonded temporary device by itself. Uses
  the system bus the way `mdns.go:12-21` uses Avahi, with the same retry back-off (`mdnsRetryMin`, `mdnsRetryMax`,
  `mdns.go:28-31`); same file split as `usertimeout_linux.go` and `usertimeout_other.go`.
- **Audio coexistence.** The desktop scans only while a Bluetooth-enabled paired phone has no IP link and no BLE
  link. While an A2DP transport is active (`MediaTransport1.State == active` under the adapter) it does not start a
  scan and waits; an existing BLE link is kept. So while audio plays there is no scan, a BLE link not yet up cannot
  start, and the approval falls back to the password.
- **Protocol version stays 8.** The new types (`flux.ble.key`, `flux.ble.state`), kind and capability are ignored by
  older apps.
- **Alternatives rejected (do not revisit):** a Go Bluetooth library; L2CAP channels; RFCOMM; Bluetooth bonding as
  the security; a custom encrypted protocol (Noise); desktop as peripheral; a constant or named advertisement;
  always-on advertising; BLE hot standby next to a working IP link; parallel IP and BLE approvals from the start;
  a clipboard limit above 4 KiB.

## Steps

The local host may have no Android SDK; CI (`.github/workflows/build.yml`, Android job at `:128`) is then the first
Android compile (steps 10 to 12). Go commands run from the worktree root. The race detector is on in CI, so use
`make test` before the final step. The lead commits each step after its check passes and diffs each step against
the previous step's commit. **Order of work: step 5 (docs part A) first, then steps 1 to 4 and 6 onward**, because
the spec requires `docs/approve.md` to change before the code (spec "Docs to change before the code"); the step
numbers are labels, not the order. The Go steps (1 to 9) do not depend on the Android prototype; the owner may move step 10
to the front (it needs a real phone and is the only unproven piece), and a failed prototype switches step 11 to the
loopback bridge, never to a new design.

0. **Preflight** (lead). `git status --short` clean; branch `feat/69-ble-transport`; the #185 maintainer agreement
   link is in the PR draft; the owner approved this plan (`status: approved`, own commit, message
   `docs(plan): approve 2026-11-15-69-ble-transport (#69)`). Run `go build ./...` and record the set of failing tests
   of `go test ./internal/...` before any edit; some tests fail on this NixOS host for reasons outside this issue
   (fakes that call `/usr/bin/cat`, umask).
   Check: `git status --short && go build ./... && go test ./internal/lan ./internal/core ./internal/proto ./internal/config 2>&1 | tail -30`.
   Traps: never `git switch` in the main checkout `/mnt/code/Source-home/GitHub/nixarchy-flux`; no build or deploy on
   p510 without the owner's OK; the plan stays `draft` until the agreement exists.

1. **Protocol additions** (coder). `internal/proto/identity.go`:
   - near `TypeFluxApprove` (`:72`) add `TypeFluxBLEKey = "flux.ble.key"`; add it to `Incoming` (`:142-147`) and
     `Outgoing` (`:156-162`), and nowhere else (it is IP-only).
   - add `TypeFluxBLEState = "flux.ble.state"` with body `{"enabled":bool}` (a struct `BLEState{Enabled bool}`, json
     tag `enabled`), same list entries as `TypeFluxBLEKey`. It is allowed on IP and on BLE (step 2).
   - add the identity capability field `Bluetooth` (bool, `omitempty`, set by the phone) to the `Identity` struct so
     older identities parse unchanged, and a per-transport type list field (`TransportTypes map[string][]string`,
     `omitempty`) so a BLE link lists only the 3 plugins.
   - `internal/proto/proto_test.go`: both new types are in both lists, an identity without the new fields still
     unmarshals, `BLEState` marshals as `{"enabled":true}`, and `ProtocolVersion` is still 8.
   Check: `go test ./internal/proto -count=1 && make vet`.
   Traps: tabs in Go; `gofmt` clean (`make vet` lists files that need it); do not change `ProtocolVersion`.

2. **`ble.go`: tag, gate, allow-list, limits** (codex). New `internal/lan/ble.go` (no D-Bus, no build tag, so it
   tests everywhere), `internal/lan/ble_test.go`, and a shared vector file `internal/lan/testdata/ble_vectors.json`
   that Android reads in step 11.
   - `bleTag(key []byte, t time.Time) [8]byte` and `bleTagMatches(key, tag, now) bool` (windows t-1, t, t+1).
   - `bleChallenge() []byte` (`ver || c(16)`, `crypto/rand`), `bleHello(key, c, tag) []byte` and
     `bleCheckHello(key, c, tags [][8]byte, hello []byte) bool` using `hmac.Equal`; the phone side passes the tags of
     the current and the previous window; an oversize or short hello fails. Constants `bleGateTimeout = 3s`,
     `bleTLSTimeout = 10s`, `bleMaxLine = 16<<10`, `bleMaxClip = 4096`, `bleMaxNotifLine = 4<<10`,
     `bleNotifyPerWindow = 10 per 10 s`, `bleAdvRotate = 2s`, `bleTagWindow = 900s`.
   - Packet size helpers: `bleAdvPayload()` and `bleScanResponse(tag)` return the two air packets as bytes
     (advertisement 21 bytes: flags, UUID; scan response 26 bytes: UUID key plus 8-byte tag).
   - `bleAllowed(p *proto.Packet, dir) (ok bool)` implementing the allow-list (identity, ping, `flux.ble.state`,
     pair false only, notification, notification request cancel only, clipboard but not connect or image, approve
     kinds `request|cancel|response|shown`; `flux.ble.key` is not allowed) and `bleLineOK(p, line []byte) bool`
     (16 KiB any line; 4 KiB notification line; clipboard 4096 bytes and 16 KiB line).
   - `bleLane(p *proto.Packet) lane` returning control (approve, identity, `flux.ble.state`, pair) or data
     (notification, notification cancel, clipboard, ping); used by step 3.
   - `bleServerConfig` and `bleClientConfig(cert, wantPin)` as copies of `internal/lan/tls.go:35-62` with
     `MinVersion: tls.VersionTLS13`; the client sets `VerifyPeerCertificate` to compare the leaf with `wantPin`
     before `GetClientCertificate` can run.
   - Tests (vectors written to `testdata/ble_vectors.json`): same key and window same tag, windows -1, 0, +1 match and
     +-2 do not, the tag reveals no ID; the advertisement is at most 31 bytes and the scan response at most 31 bytes
     with 1 tag each; wrong HMAC, a hello replayed with a different `c`, a hello replayed on another tag, an oversize
     hello, and a 3 s silence (injected clock or short timeout variable) are all refused; each packet type outside the
     list is dropped, `flux.ble.state` passes, `flux.ble.key` does not, `{"pair":true}` is dropped; a 4096 byte clip
     passes and 4097 does not; a 4096 byte clip that serializes over 16 KiB is refused; a 5 KiB notification line is
     refused; 10 notifications pass per 10 s and the 11th does not.
   Check: `go test ./internal/lan -run 'TestBLE' -count=1 -race && make vet`.
   Traps: `crypto/rand` only; constant-time compare; no new dependency (`git diff --exit-code go.mod go.sum`).

3. **BLE `Link`, `finishID`, two lanes** (codex). `internal/lan/link.go`, `internal/lan/provider.go`,
   `internal/lan/tls.go`.
   - `link.go:67-72` `SetPaired` stores `proto.MaxPacketSize` when paired; for `Transport == "ble"` it must keep
     `bleMaxLine`; test that a paired BLE link still closes a 16 KiB + 1 line (`onLink`, `daemon.go` about `:982`,
     calls it).
   - `link.go:28` struct: add `Transport string` (`"ip"` or `"ble"`) and the BLE queue fields; `newLink`
     (`:54-65`) keeps `addr, _ := conn.RemoteAddr().(*net.TCPAddr)` (nil for BLE; `IP()` at `:106-111` already
     returns `""`) and takes the transport.
   - `Send` (`:120-145`) and the receive loop (`:150-`): when `Transport == "ble"`, check `bleAllowed` and
     `bleLineOK`, drop and log once per type, never queue for IP; write deadline 5 s (`:133`); each half-received line
     has a 5 s read deadline; `maxLine` is `bleMaxLine` from the start.
   - **Two lanes** (`bleLane` from step 2): control lane first, then the data lane; each at most 8 packets and 32 KiB
     (own constants, `maxSendQueue` `:50` unchanged for IP). Data lane full: queue the new packet and drop the oldest
     waiting packet of the first kind found in this order: notification, clipboard, ping (counted, logged once; a
     dropped clipboard calls a hook `OnDrop(kind)` so step 6 can show the "did not sync" toast). Control lane full:
     close the link. Whole lines only, under `wmu`; a line in flight is never split.
   - `provider.go`: a new method `p.OpenBLE(conn net.Conn, target proto.Identity, outgoing bool) bool` that runs the
     gate (outgoing: read challenge, send hello; incoming: send challenge, read hello within 3 s, check it against the
     tags of the current and the previous window), then `tls.Client` or `tls.Server` with the BLE configs (10 s TLS
     timeout), then `finishID` with `plainID` empty on the outgoing side. In `finishID` (`:632-`) for a BLE link: skip
     the plain identity version check on an empty `plainID` (`:645-653` already takes the ID from the certificate), and
     at `:692` compare the encrypted identity's `ProtocolVersion` with `proto.ProtocolVersion` (8) instead of
     `plainID.ProtocolVersion` (0 when empty, which would fail every handshake); require `p.cfg.Trusted(id)` to be
     `ok` with a non-nil pin that equals the certificate (the old block at `:665-673` only runs `if pinned, ok`); on any
     other result `tc.Close()` before `newLink`, so no `onLink` and no device entry; write the identity only after the
     pin check; close the link when the config callback `p.cfg.BLEAllowed(deviceID)` (desktop-side flag) is false.
   - Tests in `internal/lan/link_test.go` and a new `internal/lan/ble_link_test.go`: TLS 1.3 over `net.Pipe` ends in
     the same `finishID` result as TCP; a wrong pin is refused; a wrong CN is refused; an unpaired device is closed with
     no `onLink` call and no device entry; the desktop sends no client certificate to a peer with the wrong pin (count
     `GetClientCertificate` calls); a hello with a wrong HMAC, a replay, a spoofed tag, a 3 s silence and an oversize
     hello are closed before any TLS byte; an unpair packet over BLE reaches the handler; an approve packet jumps ahead
     of a data lane full of notifications; a full data lane drops the oldest notification, then clipboard, then ping,
     and keeps the new packet; a lane of only clipboard packets still accepts a new one; a full control lane closes the
     link; identity, unpair and approve are never dropped; a 16 KiB + 1 line closes the link; a write that blocks 5 s
     closes the link; a device-flag-off callback closes the link.
   Check: `go test ./internal/lan -count=1 -race && make vet`.
   Traps: `wmu` must not be held while waiting on the lane lock the other way round (deadlock; `-race` plus a timeout
   in the test); TCP behaviour must not change: the existing `internal/lan` tests must pass unchanged.

4. **Config, trust store, IPC** (coder).
   - `internal/config/config.go:94`, after `ApproveTimeout`, add `BLE bool \`toml:"ble,omitempty"\`` with a comment.
   - `internal/config/trust.go:15-31`, `TrustedDevice`: add `BLE bool \`json:"ble,omitempty"\`` and
     `BLEKey string \`json:"bleKey,omitempty"\`` (base64, 32 bytes). Add helper `NewBLEKey()`.
   - `internal/core/api.go:322` area: a `ble.enable` IPC call (global on or off, and per device) and `ble` in the
     `status` reply; `cmd/flux` (`flux-cli status`) prints a Bluetooth line (off, on, "not supported on this BlueZ",
     "BlueZ refused access", adapter off).
   - Tests: `internal/config/config_test.go` (round trip, default off, older `devices.json` without the fields
     loads), `internal/core/api_cli_test.go` (the call and the status line).
   Check: `go test ./internal/config ./internal/core -run 'BLE|Config|Trust|Status' -count=1 && make vet`.
   Traps: `devices.json` keeps its mode (0600) and the atomic write used by the store; `ble_key` never appears in a
   log line, an IPC reply or `flux-cli status` (assert in the test).

5. **Docs, part A, run first, before any code step** (coder; the intent and the spec require it).
   - `docs/approve.md`: transports, `shown`, the new timeline (with the target rows and the "same packet, same
     `timeout`" rule), the standby condition, the answer on any link, "What an attacker can do" (Bluetooth: block or
     delay, never read, change, replay or approve), Failure modes.
   - `docs/architecture.md`: a "Bluetooth direction" section next to "Network direction"; pairing and the BLE key; the
     consent exchange; the line near `:78-84` that an unpair also closes the BLE link.
   - `docs/security.md`: the trust model (TLS 1.3, pin, gate, tag, what a stranger sees: the UUID; the address
     rotation gate).
   Check: `grep -c 'Bluetooth' docs/approve.md docs/architecture.md docs/security.md` is above 0 for all three;
   relative links resolve: `rg -n '\]\((?!http)[^)#]*\.md' docs/approve.md docs/architecture.md docs/security.md`
   and open each target by hand (no link checker in the repo).
   Traps: docs prose rules of the repo (short sentences, no emoji); the `docs/approve.md` change is its own commit
   before step 1 (and so before step 7).

6. **Daemon: consent, key distribution, limits, rules** (coder). All against a fake BLE transport; no BlueZ yet.
   - New `internal/core/ble.go`: a `bleTransport` interface (`Start`, `Stop`, `SeenTag(deviceID) time.Time`,
     `Connect(ctx, dev) (net.Conn, error)`, `Status() string`) and the manager that owns:
     - the per-device flags and the in-memory peer state (`false` until a `flux.ble.state` was seen on a link since
       start);
     - the consent exchange: send `flux.ble.state` `{"enabled":...}` on every IP link after the identity, on every BLE
       link right after TLS, and at each change; the desktop's state is its global `ble` and the device's `ble` flag;
     - admission: scan and connect only when the desktop's state is on, the device has a `ble_key` and the phone's
       last state is `true`; a kept key with either state off means no scan and no connect;
     - key distribution: send `flux.ble.key` on an IP link of a paired device when both ends are on (on the phone's
       state packet, on `ble.enable`, and again after a re-pair);
     - the disable rule: global off or one device off stops the scan, closes the BLE links, drops the BLE queue without
       moving it to IP, keeps the key; a `flux.ble.state` of `false` from the phone closes that device's BLE link;
     - the rule that a BLE link is made only while `dev.link == nil`, except the single unconfirmed-approval device
       (step 7), which gets `dev.bleLink`.
   - Keep the IP address: `onLink` (`daemon.go:973`, `dev.IP = l.IP()`) and the `seenIP` reset (`:977`) run only for
     an IP link. A BLE link (`lan/link.go:106-111`, `IP()` returns `""` when `Addr == nil`) must not overwrite
     `dev.IP`, clear `seenIP` or persist `LastIP` (`:1020`), so `dialKnown` still has an address after a BLE attach.
     Test: an IP link, then a BLE link, then the IP link drops: `dev.IP` and the stored `LastIP` are unchanged and
     `dialKnown` dials that address.
   - Transport-aware link checks: add `HasIPLink` next to `HasLink` (`daemon.go:502-506`); use it at
     `internal/lan/provider.go:396` (the dial skip; add a config field `HasIPLink`) and `daemon.go:872` (the
     `dialKnown` skip), so a BLE link never blocks IP reconnection; `HasLink` keeps its meaning.
   - `internal/core/handlers.go:19-30` `handlePacket`: add the secondary slot `dev.bleLink` (in `Device`) and accept
     `flux.approve` `response`, `shown` and `cancel` from it, nothing else. In `onLink` (`daemon.go:943-983`) an
     arriving IP link becomes `dev.link` while a current BLE link moves to `dev.bleLink` if the device has an open
     approval, else it is closed; do not clear `dev.notifications` for a BLE link (`:983-986`); do not send
     `requestNotifications` (`notifications.go:391`) on BLE. `daemon.go:1105-1128` `receive`: no new branch, check
     that a replaced BLE link drops no approvals. `daemon.go:1347-1359` `send` and `policies.go:38-48` `featureLinks`:
     unchanged, test only.
   - `internal/core/notifications.go:238-242`: on a BLE link ignore `ReplyID` and `Actions` (show only); drop an
     eleventh notification in any 10 s.
   - `internal/core/clipboard.go:351-389` `onLocalClipboard`: make `fits` a per-link check at send time (4096 bytes and
     the 16 KiB line for BLE, `maxSentText` for IP), toast "over Bluetooth", keep the history entry (`history.go:127`);
     the step 3 `OnDrop` hook for a dropped clipboard shows the same "did not sync" toast. `clipboard.go:520`
     `handleClipboard`: the transport limit for an oversize clip.
   - `internal/core/device.go:214-264`: `transport` in the device list and the unavailable features for a BLE link;
     `refuseLocked` (`:214-227`) unchanged, the daemon never sees an unknown BLE device (step 3).
   - Unpair paths in `pairing.go` (`dropApprovals` calls at `161,218,309,377,411`) also close the BLE links
     (`dev.link` and `dev.bleLink`) and delete `ble_key`; an unpair packet received over BLE does the same.
   - Tests (a `fakeBLE` in `internal/core/ble_test.go`): key sent once per IP link and after a re-pair, only when both
     ends are on; a kept key with either state off gives no scan and no connect; no state seen since start counts as
     off; a `flux.ble.state` of `false` closes a BLE link; with a BLE link up, `HasIPLink` is false and IP is still
     dialed; an approval `response`, `shown` and `cancel` arriving on `dev.bleLink` after IP became current are
     delivered and nothing else is; a notification with `ReplyID` or `Actions` over BLE shows no reply or action;
     turning a flag off closes the BLE link and drops the BLE queue without moving it to IP and keeps the key; a 4096
     byte clip crosses BLE and 4097 does not, the history keeps it, the toast says "over Bluetooth"; a dropped
     clipboard gets the toast; a 50 notification burst shows at most 10; no `requestNotifications` over BLE; IP up
     while BLE is current switches `dev.link` and cancels nothing; unpair closes both links and deletes the key; an
     unpair packet over BLE deletes the key and pin; `ApproveEnroll` over BLE is refused with the text above.
   Check: `go test ./internal/core -run 'BLE|Clipboard|Notification|Pairing|Link' -count=1 -race && make vet`.
   Traps: a BLE link must never create a device entry for an unknown device (step 3 closes it earlier; test the
   daemon side too); `featureLinks` must show no feature as available that is not allowed on BLE.

7. **Approvals over Bluetooth** (codex). `internal/core/approve.go`, against the fake transport.
   - `:247-262` `approveDevice`: Bluetooth-ready standby (consent both on, capability, BLE link up or a tag seen in
     the last 30 s), else fail at once as today.
   - `:264-300` `ApproveRequest`: register, return `Started` at once; the send, connect and re-sends run in a
     goroutine under the request's deadline set at `:282-284`; never extended; the request sent over BLE is the same
     packet (same `id`, fields, `timeout`), never a reduced `timeout`.
   - `:410-438` `handleApprove`: also called for packets from `dev.bleLink` (step 6); new kind `shown`, recorded per
     request ID; the 3 s timer: no `shown` means keep IP and start a BLE connect and send the same packet over BLE
     (only if at least 5 s remain; the link goes into `dev.bleLink`, the one exception to the `dev.link == nil` rule);
     a request is never restarted after it ends; at the deadline send `cancel` over every link, drop any later answer.
   - `:383` `dropApprovals`: skipped when the lost link was IP and the device is Bluetooth-ready with an open
     request; the request waits on any link until its deadline. With the disable rule (consent off or Bluetooth turned
     off) and no link left, the request is kept to its deadline and no BLE connect is started.
   - `:307-326` `ApproveEnroll`: refuses on a BLE link.
   - `:131` `deliver`: first answer wins (unchanged); test that a late duplicate is ignored.
   - Tests (fake transport, fake clock): the whole approval list of the spec's Verification: a switch from IP to BLE
     keeps the request ID and the deadline and gives 1 prompt; a late duplicate and a cancelled ID do not come back;
     `shown` missing at 3 s adds a BLE send and keeps IP; the BLE send is the same packet (same `timeout`); a cancel is
     sent at the desktop deadline over every link; turning Bluetooth off with an open approval keeps it on a live IP
     link and keeps it to the deadline with no link (no connect started); a late prompt on a healthy IP link and a loss
     of IP after `shown` give 1 prompt and 1 answer; `ApproveRequest` returns `Started` inside 3 s with a blocked link;
     an IP link that returns during an open approval cancels nothing; an unpair closes both links and cancels; `enroll`
     over BLE is refused; `approveDevice` fails at once when no BLE is ready; fewer than 5 s left starts no connect.
   Check: `go test ./internal/core -run 'Approve' -count=1 -race && make vet`.
   Traps: read `docs/approve.md` first (root helper, nonce, time checks stay; `fluxd` only carries the request); the
   helper's 3 s `approve.request` timeout; the timeline in the spec is a target (the runtime test measures it); no
   change to `cmd/flux-approve`.

8. **Packet-to-stream wrapper** (codex). New `internal/lan/ble_linux.go` (first part; step 9 adds the BlueZ calls to
   it) and `internal/lan/ble_other.go` (stub returning "Bluetooth is not supported here"), same split as
   `usertimeout_linux.go` and `usertimeout_other.go`.
   - `bleConn`, a `net.Conn` over two `*os.File` (write fd with its write MTU, notify fd with its read MTU) with the
     rules of the Roles and stream decision: `Write(p)` splits into packets of at most the write MTU, sends in order,
     loops on a partial or `EAGAIN` send until all of `p` is sent or the deadline passes, returns the byte count sent;
     `Read(p)` reads 1 packet into a buffer of the read MTU and keeps the bytes that do not fit in `p` for the next
     `Read`; a zero-length packet is skipped; a closed fd is `io.EOF`; deadlines through `os.File`
     (`SetReadDeadline`, `SetWriteDeadline`, `SetDeadline`); `Close` closes both fds; `LocalAddr` and `RemoteAddr`
     return a fixed non-TCP address (so `newLink` stores a nil `Addr`).
   - Tests in `internal/lan/ble_linux_test.go`, on a `SOCK_SEQPACKET` socketpair (`syscall.Socketpair`) and on a stream
     pair (`net.Pipe` or `os.Pipe`): a write larger than the MTU arrives whole and in order; a read into a 5-byte buffer
     keeps the rest of the packet; a zero-length packet is skipped; a closed fd gives `io.EOF`; a deadline ends a
     blocked read and a blocked write; a partial send loops until done.
   Check: `go test ./internal/lan -run 'TestBLEConn' -count=1 -race && make vet && GOOS=darwin go build ./internal/...`.
   Traps: build tags (`ble_other.go` must keep the darwin build compiling); this behaviour is not yet verified on a
   real adapter, so the test also covers a plain stream fd; no new dependency (`git diff --exit-code go.mod go.sum`).

9. **BlueZ over D-Bus** (coder). `internal/lan/ble_linux.go` (second part).
   - Uses the system bus the way `mdns.go:12-21` uses Avahi (same `Call()` seam, same retry back-off `mdnsRetryMin`
     and `mdnsRetryMax`, `mdns.go:28-31`).
   - Calls: `Adapter1.SetDiscoveryFilter` (transport `le`, the Flux UUID, duplicate data on), `StartDiscovery`,
     `StopDiscovery`, a watch on `ObjectManager.InterfacesAdded` and `PropertiesChanged` for `Device1.ServiceData`,
     `Device1.Connect`, then `GattCharacteristic1.AcquireWrite` and `AcquireNotify` (each returns an fd and an MTU)
     handed to the step 8 `bleConn`. No `Pair`, `Trust`, `SetPowered`, `RemoveDevice` on a device Flux did not create.
     Adapter off (`Powered=false`): report, do not power it on. BlueZ older than 5.46: "Bluetooth is not supported on
     this BlueZ". Access denied: "BlueZ refused access". A2DP active (`MediaTransport1.State == active`): no new scan,
     keep an existing link. Scan hit at most 2 s, connect and GATT discovery at most 3 s. Log the ATT MTU.
   - It implements the step 6 `bleTransport` interface and honours its admission (no scan without consent); `cmd/fluxd`
     wires it only when `ble = true`.
   - Tests in `internal/lan/ble_linux_test.go`: an in-process fake BlueZ object behind the same `Call()` seam (the way
     `internal/lan/mdns_test.go:148-193` fakes Avahi with `fakeAvahi`, not a private bus) for the scan, connect,
     `AcquireWrite` and `AcquireNotify` path (two socketpairs), adapter off, access denied, A2DP active (no scan, link
     kept), tag windows, and that no forbidden method is ever called (the fake fails the test on `Pair`, `Trust`,
     `SetPowered`, `RemoveDevice`).
   Check: `go test ./internal/lan -run 'TestBlueZ|TestBLE' -count=1 -race && make vet && go build ./...`.
   Traps: build tags: `go vet` and `GOOS=darwin go build ./cmd/... ./internal/...` must still compile
   (`ble_other.go`); no new dependency (`git diff --exit-code go.mod go.sum`); `fluxd` still runs as the user, no
   capability.

10. **Android prototype: TLS over a GATT-stream Socket, and the address gate** (coder, lead checks on a phone). This
    is the one unproven piece.
    - New `A/net/BleStream.kt`: a `java.net.Socket` subclass whose `getInputStream` and `getOutputStream` are the GATT
      stream (for now backed by a `PipedInputStream` pair so the JVM test runs without a radio).
    - `T/net/BleStreamTest.kt`: `SSLSocketFactory.createSocket(socket, host, port, true)` from the existing `Tls`
      (`A/net/Tls.kt:30`) negotiates TLS 1.3 against a server on the other end of the pipe (asserting
      `session.protocol == "TLSv1.3"`) and `Tls.peerCertificate` returns the certificate.
    - `A/net/Tls.kt:21-22,34` forces `SSLContext.getInstance("TLSv1.2")` and `enabledProtocols = TLSv1.2`: add a
      `protocols` parameter (default TLS 1.2 so the TCP path is unchanged; BLE passes TLS 1.3, using
      `SSLContext.getInstance("TLS")`) to the helper that builds the context and the socket.
    - On a real Android 12 to 16 phone: run the same negotiation through a `BluetoothGattServer` and note the result
      per Android version in the PR. A failure here switches step 11 to the loopback bridge; it does not change the
      design.
    - **Air-data and address check (the address gate).** On the same phone, advertise the 21-byte advertisement and the
      26-byte scan response with `setIncludeDeviceName(false)`; record whether Android accepts the sizes, and the
      advertising address (from `btmon` or a scanner app) across 2 tag windows with a restart at each change. Write
      the result into the PR. If the address does not change, stop and take the owner's decision (Gate above).
    Check: `cd android && ./gradlew :app:testDebugUnitTest --tests '*BleStreamTest*' --no-daemon` (CI runs the same
    job at `build.yml:128`); the phone results are read by the lead.
    Traps: the prototype ships no manifest change and no UI; `LanBackend.kt` is not touched yet; the TCP path keeps
    TLS 1.2 (a test that `LanBackend` still negotiates 1.2).

11. **Android link code** (coder). `A/net/BleBackend.kt` (new), `A/net/LanBackend.kt:420-444`, `A/core/TrustStore.kt`.
    - `BleBackend.kt`: GATT server with the service and the `rx`/`tx` characteristics, one connection at a time (a
      second is refused until the first passed or failed the gate), the challenge and hello gate (3 s) and the TLS 1.3
      timeout (10 s), a bounded read queue, chunks of MTU minus 3 with `onNotificationSent` pacing, the tag
      advertisement from step 12's schedule; the gate refuses a hello whose tag belongs to a desktop whose state is
      off; produces a `Link` through `LanBackend.finish` after moving the pin check before the identity write (the BLE
      path checks first; the TCP path at `LanBackend.kt:420-444` is unchanged). `finish` is `private` at
      `LanBackend.kt:420`: make it `internal` (or move it to a shared helper) with no behaviour change; BLE uses the
      TLS 1.3 option added to `Tls.kt` in step 10.
    - `A/core/FluxCore.kt:360` and `:410`: a BLE link is not closed by `old.close()` nor ignored by the
      `d.link !== link` return while a prompt of that computer is open (secondary link; approval packets only).
    - `A/core/ApproveMessage.kt:139-143` `admit()`: no behaviour change. Add a test that an identical copy of an open
      request (same `id`, fields and `timeout`, as the desktop sends over BLE) is a `Repeat` and changes nothing, and
      that a same-ID request with different fields is still a `Conflict`.
    - `A/net/Link.kt:49` has one writer thread and `LinkedBlockingQueue(MAX_QUEUED_PACKETS)`: for BLE use two lanes
      like step 3 (control: approve, identity, `flux.ble.state`, unpair; data: notification, clipboard, ping; each at
      most 8 packets and 32 KiB; a full data lane drops the oldest notification, then clipboard, then ping and keeps
      the new packet; a full control lane closes the link); 5 s write deadline and 5 s deadline for a half-received
      line (tests in `T/net/LinkTest.kt`: approve jumps ahead of 8 notifications, the 9th drops the oldest, a lane of
      only clipboard accepts a new one, a stalled write closes the link, a half line closes it).
    - `A/core/NotificationSync.kt:306-320` builds the `Packet(Types.NOTIFICATION, ...)`: for a BLE link send no
      icon, large image, `ticker`, `requestReplyId` or `actions`, only title and text (each cut to 1 KiB, then the
      text shrunk until the serialized line is at most 4 KiB); at most 10 per 10 s; test the serialized BLE packet.
    - Consent: send `flux.ble.state` `{"enabled":bool}` after the identity on each IP link, right after TLS on each
      BLE link, and on every change of the switch, the permission or the per-computer flag; keep the desktop's last
      state in memory only, `false` until seen; advertise a tag for a desktop only while the phone's own state for it
      is on.
    - `TrustStore.kt:25-61`: `ble_key` wrapped with an AndroidKeyStore AES-GCM key, only the ciphertext in the
      `trusted` prefs; deleted on unpair and when the unpair packet arrives; handler for `flux.ble.key` (IP only).
    - `Link.kt:42-46`: carry the transport; the allow-list and the limits of step 2 for BLE; one prompt per request ID
      kept until the deadline also after a cancel; the phone holds an answer until the deadline and sends it on the
      carrying link, else on any other; `shown` is sent when the prompt is on screen; clipboard over 4096 bytes or a
      16 KiB line is refused on BLE (toast and Flux notification).
    - Tests: `T/net/BleGateTest.kt` and `T/net/BleTagTest.kt` read `internal/lan/testdata/ble_vectors.json` (path
      through a Gradle test resource copy, not a duplicate file) and equal the Go vectors; `T/net/LinkTest.kt`
      additions for the allow-list, the 4 KiB clip limit, the notification trim and the one-prompt rule.
    Check: `cd android && ./gradlew :app:testDebugUnitTest :app:lintDebug --no-daemon`.
    Traps: no `BLUETOOTH_SCAN`, no location anywhere (lint and `grep -rn 'ACCESS_.*LOCATION\|BLUETOOTH_SCAN'
    android/app/src/main` that finds nothing); never log `ble_key`; `LanBackend.kt` TCP behaviour unchanged.

12. **Android manifest, permissions, schedule, settings** (coder).
    - `android/app/src/main/AndroidManifest.xml:5-49`: add `BLUETOOTH_ADVERTISE` and `BLUETOOTH_CONNECT` plainly (no
      SDK attribute); the `bluetooth_le` `<uses-feature required="false">` next to `:51-53`; nothing else. Gate the
      feature in code on `Build.VERSION.SDK_INT >= 31`.
    - `FluxService` (`AndroidManifest.xml:155-158` `connectedDevice`): advertise the UUID in the advertisement (21
      bytes) and one tag in the scan response (26 bytes), `setIncludeDeviceName(false)`; several desktops take turns
      every 2 s (or one set each when the adapter offers them); restart the advertisement at each 15-minute tag window
      and ask for a new random address; start and stop advertising on the `ConnectivityManager` default-network
      callback; `ADVERTISE_MODE_LOW_POWER`; 15 minutes then 1 minute in 15 with an inexact alarm; full again on unlock,
      on opening Flux and on a default-network change; none while an IP or a BLE link is up.
    - Settings switch (global plus per computer) in `A/ui/` (the settings screen that holds the other switches):
      hidden when `SDK_INT < 31`; asks for the two permissions only on turning it on; a revoked permission turns it
      off, says so, and sends `flux.ble.state` `false`; the line "connected over Bluetooth" on the computer's row.
    - Tests: `T/service/` unit tests for the schedule (clock injected), the tag rotation every 2 s, the permission flow
      (granted, denied, revoked while on) and that Android 10 and 11 hide the switch.
    Check: `cd android && ./gradlew :app:testDebugUnitTest :app:lintDebug :app:assembleDebug --no-daemon`.
    Traps: the `connectedDevice` foreground service type is unchanged; the approval prompt activity is unchanged; no
    permission is requested at first start.

13. **Desktop UI** (coder). `gui/qml/` shared views and the shell plugin (`gui/omarchy/`): a Bluetooth switch (global
    and per device), the "connected over Bluetooth" line from `transport: "ble"`, the unavailable feature text, the
    "BlueZ refused access" and "not supported on this BlueZ" messages; the mock backend gets the new fields.
    Check: `make snapshot test-gui`, and `make build-gui`.
    Traps: keep shared QML compatible with both hosts (`AGENTS.md`); the plugin sandbox tests of
    `README.md#working-on-it` run if the plugin files change, and the PR says they passed; Chrome previews hang the
    AMD GPU on the owner's host, so use headless offscreen rendering only.

14. **Docs part B, Nix, Arch** (coder).
    - `docs/features.md`, `docs/troubleshooting.md`, `docs/configuration.md` (`ble`), `docs/android.md`.
    - `nix/nixos.nix`: an option, default off, that sets `hardware.bluetooth.enable` only when the user asks;
      `nix/home-manager.nix`: no option; `docs/nix.md` says to enable BlueZ in the system config. `dist/arch/PKGBUILD`:
      `bluez` as an `optdepends`. A check in `nix/checks.nix` that the default config does not enable Bluetooth.
    Check: `nix flake check` (builds nothing on p510; run on the owner's chosen host) and
    `git diff --stat docs nix dist | tail -3`.
    Traps: Flux must not turn Bluetooth on by itself; `nix flake check` on p510 needs the owner's OK before any build
    or deploy.

15. **Whole-repo check and runtime test** (lead).
    - `make build test vet` (race on); `go build ./... && go vet ./... && go test ./...`; `cd android && ./gradlew
      assembleDebug testDebugUnitTest`; `nix flake check`.
    - The runtime test of the spec's Verification on the owner's chosen host (p510 only with the owner's OK) and an
      Android 12+ phone with Wi-Fi off:
      1. Pair over Wi-Fi, turn Bluetooth on on both sides, see the key sent.
      2. Wi-Fi off on the phone: within 10 s the desktop and the phone show "connected over Bluetooth".
      3. A phone notification shows on the desktop; a 4 KiB clip syncs both ways, a 4.1 KiB clip does not and says so
         on both sides.
      4. `sudo true` with the phone locked, screen off, Flux in the background: the prompt shows and the fingerprint
         unlocks it inside 20 s. Repeat with the IP link stuck (firewall, not closed) and measure the time to the
         prompt (the result may be the password if the phone does not advertise); with the phone out of range, with
         Bluetooth off, and with Flux force-stopped: each asks for the password.
      5. Wi-Fi on while a request is open: 1 prompt, answered, not cancelled. Unpair during a BLE link: the prompt
         closes.
      6. Steps 2 to 4 first with no audio. Then, with a BLE link already up, start A2DP audio and listen for dropouts:
         the link is kept and no scan starts. With audio and no BLE link, no scan starts and `sudo` asks for the
         password.
      7. `btmon` or a sniffer for a day: no name, no ID, the tag changes every 15 minutes, and the random address
         changes with it (the address gate; if it does not, stop and take the owner's decision).
      8. Phone battery for 2 hours with the computer out of range.
    Check: the commands above; the PR description links intent, spec and plan, says which steps the coder did, and says
    the plugin sandbox tests passed if they apply.
    Traps: no deploy, switch or reboot without the owner's OK; a step that deviates updates this plan in the same
    commit as the code.

## Tests

Per step, as listed. The spec's Verification section maps to: Go unit tests in steps 2, 3, 6, 7, 8, 9
(`go test ./internal/lan ./internal/core ./internal/proto`): tag and air-data sizes, stream wrapper on a socketpair,
consent, queue lanes, hello gate, TLS 1.3 over `net.Pipe`, allow-list and limits, approvals, and the fake BlueZ
behind the `Call()` seam; Android unit tests with the shared vectors in steps 10 to 12; build and Nix checks in
steps 14 and 15; the runtime test in step 15; the 7 docs in steps 5 and 14. Each Go test file is built to run with
`-race` on a host with no Bluetooth adapter.

## Rollback

- Every Bluetooth path is behind `ble = false` (desktop) and the phone switch, both off by default, and lives in
  separate files (`ble.go`, `ble_linux.go`, `ble_other.go`, `BleBackend.kt`). Turning the flags off, or setting
  `ble = false` and restarting `fluxd`, returns to IP-only behaviour with no data change.
- The only data added is `ble`/`bleKey` in `devices.json` (ignored by older versions) and the wrapped key on the
  phone; deleting those fields is safe, and an unpair removes them.
- Reverting is `git revert` of the step commits in reverse order. The IP protocol version stays 8 and the new packet
  types, kind and capability are ignored by older apps, so no peer needs a matching revert.
