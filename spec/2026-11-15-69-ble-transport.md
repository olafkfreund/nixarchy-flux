---
status: approved
issue: 69
intent: intent/2026-11-15-69-ble-transport.md
---

# Spec: Bluetooth LE as a fallback for notifications, clipboard text and approval

The owner approved the intent with these decisions, which this spec carries over:
1. Only notifications (show only), clipboard text and approval. No replies, actions, SSH signing.
2. Linux `fluxd` and Android 12+ only. No location permission.
3. `fluxd` talks to BlueZ only through `godbus/dbus/v5` (`go.mod`). No new Go Bluetooth dependency.
4. Off on both sides until the user turns it on.
5. The design is agreed on upstream bjarneo/flux #185 before any building.
6. Proposals to settle here: clipboard limit, switch time, supported Linux setups, test host.

**Upstream gate (decision 5).** The only reply on #185 so far is from a
non-maintainer who agrees with the scope and trust requirements. That is
not the maintainer's agreement. Implementation, and the approval of the
`plan/` that starts it, wait for the maintainer to agree to this design on
#185. A `plan/` may be written and reviewed as `status: draft` before that,
but no step starts and the plan is not approved until the agreement exists.
Until then this spec is a proposal, and the owner decides whether to post it
there.

## Design

### 0. The idea in one paragraph

Run the same TLS link, the same identity check and the same pin check as
today, over a Bluetooth LE byte stream instead of TCP. The phone
advertises and serves a GATT service; the desktop is the central. The
stream is a `net.Conn`, so `crypto/tls` runs on it unchanged and every
property of the intent's trust constraint (end to end, pinned
certificates, fresh keys, replay and forward secrecy) comes from code that
already exists. Bluetooth bonding is never used. What is new is only the
byte stream, a cheap pre-TLS gate, and the rules for which packets and
sizes the link may carry.

### 1. Roles and the byte stream

- **Phone = GATT peripheral** (advertises, `BluetoothGattServer`).
  **Desktop = central** (scans, connects, GATT client through BlueZ).
  - Android then needs only `BLUETOOTH_ADVERTISE` and `BLUETOOTH_CONNECT`
    (Android 12+). It never scans, so it needs neither `BLUETOOTH_SCAN` nor
    location. This is how decision 2 holds.
  - The central role is the better supported BlueZ path. A desktop
    peripheral needs `GattManager1.RegisterApplication` and
    `LEAdvertisingManager1`, and many adapters allow only 1 or 2
    advertisement instances.
- **One GATT service**, with 2 characteristics and no ATT encryption
  requirement (Flux does its own, see 2):
  - `rx` (desktop to phone): write without response.
  - `tx` (phone to desktop): notify.
- **The desktop** uses `org.bluez.GattCharacteristic1.AcquireWrite` and
  `AcquireNotify` (BlueZ 5.46 and later). Both return a file descriptor, so
  the stream is two fds with kernel backpressure and no D-Bus signal per
  chunk. A new file `internal/lan/ble_linux.go` wraps them in a `net.Conn`
  (`Read`, `Write`, `Close`, deadlines through `os.File`). The wrapper turns
  packets into a stream, because the BlueZ fds are, as far as the BlueZ
  source shows, `SOCK_SEQPACKET` sockets (one read or write is one packet,
  at most the MTU that `AcquireWrite` and `AcquireNotify` return next to the
  fd). This is not yet verified on a real adapter; the fake-BlueZ test and
  the runtime test check it, and the rules below also hold for a plain
  stream fd:
  - `Write(p)` splits `p` into packets of at most the write MTU, sends them
    in order, and loops on a partial or `EAGAIN` send until all of `p` is
    sent or the deadline passes. It returns the byte count sent.
  - `Read(p)` reads 1 packet into a buffer of the read MTU and keeps the
    bytes that do not fit in `p` for the next `Read`, so a small `p` (the
    TLS record header read of 5 bytes) never loses data and never gets a
    short packet mistaken for the end of the stream. A zero-length packet is
    skipped; a closed fd is `io.EOF`.
- **The phone** writes each chunk with `notifyCharacteristicChanged` and
  waits for `onNotificationSent` before the next one. It reads
  `onCharacteristicWriteRequest` into a bounded queue. A chunk is at most
  ATT MTU minus 3 bytes.
- **No framing of its own.** A GATT connection is ordered and the link
  layer retransmits, so the stream is a reliable ordered byte stream once
  the packet rules above (split on write, keep leftovers on read) are
  applied on both sides. TLS records detect any damage. The stream adds no
  sequence numbers.
- BlueZ negotiates the ATT MTU when it connects (up to 517). The link
  works at the default 23 (20 byte chunks) but slowly. Flux logs the MTU,
  and the limits in 5 assume at least 10 KB/s.

### 2. Trust: the same TLS and the same pin, plus a gate

- **TLS 1.3 only on this transport** (`MinVersion: tls.VersionTLS13`, a
  copy of `serverConfig` and `clientConfig` in `internal/lan/tls.go:35-62`).
  TLS 1.3 encrypts both certificates. TLS 1.2 sends them in clear, and the
  certificate holds the device ID (`internal/lan/provider.go:665-673`
  compares CN with the device ID). Android 10 and later supports TLS 1.3,
  so `minSdk 29` is no problem.
- **The pre-TLS plain identity is not sent.** `plainIdentity`
  (`provider.go:205-212`) names the device. On BLE the target is already
  known from the advertisement tag, and `finishID` already accepts an
  outgoing link with no device ID and takes it from the certificate
  (`provider.go:645-653`).
- **A BLE key** closes the leak that remains. An active attacker who
  connects to the phone and runs a TLS 1.3 handshake as client still
  receives the phone's certificate, because the server certificate is
  encrypted only against a passive listener.
  - The desktop creates 32 random bytes per paired device, `ble_key`, and
    sends them on an existing paired IP link in a new packet
    `flux.ble.key` (new type in `internal/proto/identity.go`, added to
    `Incoming` and `Outgoing` so older apps never see it). The desktop
    stores it next to the pin (`~/.local/share/flux/devices.json`). Android
    keeps its pairing data in plain `SharedPreferences("trusted")`
    (`TrustStore.kt:25-31`, `prefs.edit().putString` at `:61`), not in a
    Keystore store, so `ble_key` is new code there: an AndroidKeyStore
    AES-GCM key wraps it and only the ciphertext goes into that prefs file.
    An unpair deletes it on the side that unpairs at once, and on the other
    side when the unpair packet arrives (see 4 for the packet over BLE; with
    no link at all the peer's copy stays until its next link, a stated
    limit).
  - It is sent when the user turns Bluetooth on for the desktop, on each IP
    link of a paired phone, and after a re-pair. So the first Bluetooth
    use needs 1 IP link after both sides are on. That is a stated
    limit, not a hidden one.
  - **Gate (challenge and response):** on connect the phone first sends
    `challenge = ver(1) || c(16)`, 16 fresh random bytes from the phone, in a
    `tx` notification. The desktop answers with
    `hello = ver(1) || nonce(16) || HMAC-SHA256(ble_key, "flux-ble-hello" || c || nonce || tag)[:16]`.
    The phone checks it against the tag it advertised in the current and the
    previous window. Because `c` is new for each connection, a captured
    hello is useless on any other connection, on the same tag or another, so
    no nonce cache is needed. Anything else, or no hello within 3 s, closes
    the connection before any certificate is shown. Gate failures cost one
    HMAC.
  - **A spoofed tag.** An attacker that copies an advertised tag can make
    the desktop connect to it, but it cannot answer with a hello for its own
    `c` and gets nothing from the desktop but that failed attempt. If it
    relays between the real phone and the desktop, the end-to-end pinned TLS
    makes it a pure relay (a delay or a block, not a read or a change).
  - **No certificate or identity before the pin check.** On a BLE link the
    desktop (TLS client) checks the phone's certificate against the pin of
    the target device in `VerifyPeerCertificate`, which runs before
    `GetClientCertificate` (`tls.go:53-60`), so it never sends its
    certificate to the wrong peer. The phone (TLS server) learns the desktop
    only after the gate, so only a holder of `ble_key` reaches it. Android's
    `LanBackend.kt:420-428` writes its identity before the pin check
    (`:434-438`); `BleBackend.kt` moves the pin check first and writes the
    identity after.
  - The gate is not trust. The pinned certificate check after the TLS
    handshake is still what authenticates both peers, and a BLE link adds
    its own paired-only check (see Limits below) because the existing check
    does not enforce it. A holder of `ble_key` without the pinned
    certificate is refused.
- **Nothing identifies the user over the air.**
  - The air data is split over the 2 legacy packets, each at most 31 bytes
    (the limit is from the Android documentation; the Android prototype
    checks the real size). The advertisement holds the flags (3 bytes) and
    the 128-bit Flux service UUID (2 + 16 bytes), 21 bytes, which BlueZ
    needs to filter scans. The scan response holds the service data: the
    128-bit UUID as key and an 8-byte
    `tag = HMAC-SHA256(ble_key, "flux-ble-tag" || be64(t / 900 s))[:8]`,
    2 + 16 + 8 = 26 bytes. BlueZ scans actively by default, so it asks for
    the scan response. No name, no device ID. `setIncludeDeviceName(false)`.
  - One tag in a payload, never 2. With several Bluetooth-enabled paired
    desktops, the phone rotates the scan response between their tags every
    2 s (one advertising set, no extra adapter instance), or uses one set
    for each when the adapter offers them.
  - The tag changes every 15 minutes, and the desktop accepts the windows
    t-1, t and t+1 (clock skew up to 15 minutes). The phone restarts its
    advertisement at each change and asks for a new random address, so that
    the address and the tag change together. **This is a goal with a gate,
    not a fact:** Android does not promise that a restart gives a new
    address. The first Android step (the prototype) and runtime test 7 record
    the address across 2 tag windows. If the address does not change, a
    stranger can follow the phone for as long as the address lasts, whatever
    the tag does. Then the privacy constraint is not met and the owner
    decides, before the plan is approved, between a shorter life of the
    advertisement (it stops at the end of each window and starts again later
    with a pause) and dropping the feature. The feature stays off by default
    in either case. A stranger cannot link tags across windows without
    `ble_key`.
  - What stays visible: that some device near the stranger runs Flux (the
    UUID). That does not name or follow anyone. This is accepted.
  - A tag never authorizes anything. It only tells the desktop which
    paired device to try.
- **Limits on strangers.** The phone accepts 1 connection at a time (the
  GATT server refuses a second until the first has passed or failed the
  gate), drops a connection that has not passed the gate in 3 s and one
  that has not finished TLS in 10 s. A BLE link reads at most 16 KiB per
  line from the start, paired or not (see 4); the IP-only
  `maxUnpairedLine` of 64 KiB (`tls.go:23-25`) does not apply to it, and
  `SetPaired` (`link.go:67-72`) keeps the 16 KiB limit on a BLE link. The
  desktop connects only to a tag that matches a paired, Bluetooth-enabled
  device, so it never connects to a stranger.
- **Paired-only is enforced for BLE by new code, not by the old checks.**
  The pin check in `provider.go:665-673` runs only `if pinned, ok :=
  p.cfg.Trusted(...)`, so an unknown device passes it. `refuseLocked`
  (`device.go:214-227`) returns "" when `!ok`, and `onLink`
  (`daemon.go:943-946`) first calls `discoveredLocked`, which creates a
  device entry. So for `Transport == "ble"`, `finishID` requires a trusted
  device with a pin and the matching certificate, and otherwise closes the
  link before it reaches `onLink`: no device entry, no notification.

### 3. Where it fits in `internal/lan` and `internal/core`

- `Link` (`internal/lan/link.go:28,54-65`) holds a `*tls.Conn`, and
  `newLink` takes the peer address from `RemoteAddr()` as `*net.TCPAddr`.
  `tls.Client` and `tls.Server` accept any `net.Conn`, so a BLE stream
  becomes a `Link` through the same `finishID`. `Link.Addr` is already
  allowed to be `nil` (`IP()` at `link.go:106-111` returns `""`).
  Two small changes:
  - `Link.Transport` (`"ip"` or `"ble"`), set by `finishID`.
  - `Link.Send` and the receive path check an allow-list when
    `Transport == "ble"`, see 4.
- A new `internal/lan/ble.go` (types and the tag, hello and allow-list
  logic, no D-Bus, so it is testable everywhere) and `ble_linux.go` (BlueZ
  over `godbus`, same split as `usertimeout_linux.go` and
  `usertimeout_other.go`; `ble_other.go` is a stub). It uses the system bus
  the same way `mdns.go:12-21` uses Avahi, and the same retry back-off
  (`mdnsRetryMin`, `mdnsRetryMax`, `mdns.go:28-31`).
- **BlueZ calls:** `Adapter1.SetDiscoveryFilter` (transport `le`, the Flux
  UUID, duplicate data on), `StartDiscovery` and `StopDiscovery`, a watch on
  `ObjectManager.InterfacesAdded` and `PropertiesChanged` for
  `Device1.ServiceData`, then `Device1.Connect`, and the GATT objects under
  the device. `fluxd` never calls `Pair`, `Trust`, `SetPowered`, or
  `RemoveDevice` on a device it did not create. When the adapter is off
  (`Powered=false`) Flux reports it and does not switch it on.
  `BlueZ` deletes an unbonded temporary device by itself.
- **The daemon sees a BLE link as the device's link when there is no IP
  link**, so the existing code (`dev.link`, `send`, `featureLinks`,
  `internal/core/daemon.go:1347-1359` and `internal/core/policies.go:38-48`)
  keeps working there. The rules:
  - A BLE link is made only while `dev.link == nil` for a paired,
    Bluetooth-enabled device, with one exception: the one device that has
    an open approval in the "unconfirmed" state (see 5) may get a BLE link
    next to a live IP link. That link goes into a secondary slot
    (`dev.bleLink`), not into `dev.link`. `Preferred` (`link.go:85`) never
    prefers a BLE link over an IP link.
  - When an IP link comes up while a BLE link is the current link, the IP
    link becomes `dev.link` as `onLink` does today (`daemon.go:957-970`)
    and the BLE link moves to `dev.bleLink`.
    The BLE link is not closed while an approval of that device is open,
    and is closed at the end of it (at most `approve_timeout` later). Its
    answers reach `approvals.deliver`, which keys on the device and the
    request ID (`approve.go:436`), not on the link. But `handlePacket`
    (`internal/core/handlers.go:19-30`) drops every packet from a link that
    is not `dev.link`, so it also accepts `flux.approve` of kind `response`,
    `shown` and `cancel` from `dev.bleLink`, and nothing else.
  - A BLE-only `dev.link` does not count as a link for dialing IP:
    `HasLink` (`daemon.go:502-506`), the skip in `provider.go:396` and the
    `dialKnown` skip at `daemon.go:872` use a new `HasIPLink`, so IP is still
    found while BLE is up. `HasLink` keeps its meaning for features.
  - `receive` (`daemon.go:1105-1128`) only drops approvals when the link
    that ended was `dev.link`. Because a replaced link is not current, the
    switch from BLE to IP cancels nothing.
- **Consent and disable.** Two flags, both off by default, both needed:
  a global `ble = false` (desktop) and a per-computer flag on the phone, and
  a per-device `ble` flag on the desktop (in `devices.json`) with a global
  switch on the phone. `ble_key` is sent only when both ends have the global
  and the per-device flag on. Turning the global flag off, or revoking the
  Android permission, stops the scan and the advertisement, closes every
  BLE link, and drops anything queued for BLE (never moved to IP). An open
  approval then follows the "IP fails after `shown`" rule of 5: with a live IP
  link it goes on over IP and nothing changes for the user; with no link
  left it is kept until its deadline, as for an IP loss, but no BLE connect
  is started, so it ends at the password. Turning off one device does the
  same for that device. The key
  is kept, so turning it on again needs no IP link.
- **Consent exchange.** Each side tells the other its effective state for
  this pair in a new packet `flux.ble.state` with `{"enabled":bool}` (new
  type in `internal/proto/identity.go`, added to `Incoming` and `Outgoing`
  so older apps never see it). The phone's state is true only when its
  global switch, its per-computer flag and the Android permission are all
  on; the desktop's is true when its global `ble` and the per-device `ble`
  flag are on. It is sent on every IP link after the identity, on every BLE
  link right after TLS, and again at each change. Each side keeps the
  peer's last state in memory only, and takes it as `false` until it has
  seen one on a link since start.
  - **Admission on the desktop:** the desktop scans for and connects to a
    device only when its own state is on, the device has a `ble_key`, and
    the phone's last state is `true`. Having a key is not consent: a kept
    key with either state off means no scan and no connect.
  - **Admission on the phone:** it advertises a tag for a desktop only while
    its own state for that desktop is on, and the gate refuses a hello whose
    tag belongs to a desktop that is off.
  - `finishID` for `Transport == "ble"` closes the link when the device's
    desktop-side flag is off or a `flux.ble.state` with `false` arrives, so
    a state that changed while the link was being made is honoured. A
    `flux.ble.state` is in the allow-list of 4.
- **Config.** `ble = false` in `config.toml` (`internal/config/config.go`,
  next to `approve_timeout`, `config.go:94`), a `ble.enable` IPC call,
  a Bluetooth line in `flux-cli status`, and a switch in the Qt GUI and
  shell plugin settings. The device list shows `transport: "ble"`, and the
  features that are not on Bluetooth show as unavailable (`device.go:264`
  already builds the feature list from what the device supports; a BLE link
  advertises only the 3 features).

### 4. Which packets cross Bluetooth

An allow-list on a BLE link, in `Link.Send` and in the receive loop
(`link.go:120,150`). Anything else is dropped and logged once, and never
queued for IP:
- `flux.identity` (once, after TLS, as today),
- `flux.ping`,
- `flux.ble.state` (not `flux.ble.key`: the key goes only over IP),
- `flux.pair` with `{"pair":false}` only (an unpair, `pairing.go:195,202-226`,
  so a BLE-only link can still make the peer delete `ble_key` and the pin).
  `{"pair":true}` is dropped: pairing stays IP-only,
- `flux.notification`, and `flux.notification.request` with `cancel` only,
- `flux.clipboard`, not `flux.clipboard.connect` and not
  `flux.clipboard.image`,
- `flux.approve` with `kind` `request`, `cancel`, `response`, `shown`. Not
  `enroll` and `enrolled`: enrollment stays IP-only (intent, out of scope).
  `ApproveEnroll` (`approve.go:307-326`) refuses with "Enroll the approval
  key while the phone is on the network" when the link is BLE.

The identity lists the types per transport, so the phone enables only these
three plugins on a BLE link.

**Size limits** (also in `Link`, using `l.maxLine`, `link.go:67-74`; a BLE
link never has the 16 MiB limit):

| Packet | Limit over Bluetooth | Over IP today |
| --- | --- | --- |
| Any line | 16 KiB read, 16 KiB write | 64 KiB / 16 MiB |
| Clipboard text | 4 KiB (4096 bytes UTF-8), and the serialized line at most 16 KiB (JSON escaping can expand control characters up to 6 times, so 4096 bytes can exceed 16 KiB) | 256 KiB sent (`share.go:948`), 1 MiB read (`desktop/clipboard.go:30`) |
| Notification | title and text each cut to 1 KiB by the phone, no icon or large image, and then the phone shrinks the text until the serialized line is at most 4 KiB (JSON escaping can expand control characters up to 6 times, so two 1 KiB fields can exceed 4 KiB); the desktop drops a longer line | as the phone sends |
| Approval | request and answer are small (under 2 KiB) | same |

- **Clipboard limit: 4 KiB.** It is "a few KiB" as the owner proposed. At
  10 KB/s it takes under half a second. A longer text is never cut. On the
  desktop, `onLocalClipboard` (`internal/core/clipboard.go:351`) already has
  the "too large, did not sync" branch with a toast, but its
  `fits := len(text) <= maxSentText` check is global, not per link. It
  becomes a per-link check at send time: for a BLE link it uses the
  Bluetooth limit (bytes and serialized line) and says "over Bluetooth". The history keeps the entry
  as it does for larger texts today (`history.go:127`). On the phone the
  same text appears as a toast and in the notification of Flux. The
  receiver also refuses an oversize clip (`handleClipboard`,
  `clipboard.go:520`, gets the transport limit).
- **Stalls.** The stream has 1 writer queue per link with 2 lanes, each at
  most 8 packets and 32 KiB waiting (`maxSendQueue`, `link.go:50`, is
  32 MiB today, a BLE link gets its own small value):
  - **Control lane, sent first:** `flux.approve`, `flux.identity`,
    `flux.ble.state` and `flux.pair` (unpair). These are never dropped. A
    full control lane means the peer or a bug is flooding it, and the link is
    closed (an approval then follows the "IP fails after `shown`" rule of 5).
  - **Data lane:** `flux.notification` (and its `cancel`), `flux.clipboard`,
    `flux.ping`. When it is full, the new packet is queued and the oldest
    waiting packet of the first kind found in this order is dropped:
    notification, clipboard, ping (counted and logged once). A clipboard
    that is dropped this way gets the "did not sync" toast of the clipboard
    limit below.
  - The stream is newline-delimited JSON and `Link.Send` writes whole
    serialized lines under `wmu` (`link.go:120-145`), so a packet is never
    split to let an approval in. An approval waits for the line in flight,
    at most 16 KiB (about 1.6 s at 10 KB/s), and the lanes only order whole
    lines. The BLE write deadline is 5 s, not 30 s. Each half-received line
    has a 5 s read deadline.
- **Reconnect bursts.** On a BLE link `requestNotifications`
  (`internal/core/notifications.go:391`) is not sent, and `onLink` does not
  clear `dev.notifications` (`daemon.go:983-986`). So the phone does not
  replay its active notifications. Only a notification posted while the BLE
  link is up crosses it, at most 10 in any 10 s (the phone drops the
  rest and the desktop shows nothing for them).

### 5. Approval over Bluetooth

`docs/approve.md` must change first (the intent requires it). The root
helper, the root-owned key, the nonce, the time checks and the biometric
stay as they are. `fluxd` only carries the request.

- **The standby condition.** Today `approveDevice` fails at once when
  `dev.link == nil` (`approve.go:247-262`). That stays true unless the
  device is Bluetooth-ready: Bluetooth on, the phone has told `fluxd` that
  it supports it (a new capability in its identity), and either a BLE link
  is up (then it is `dev.link`) or the desktop saw a tag of this device in
  the last 30 s (a connect is under way). Otherwise the helper fails at
  once and PAM asks for the password, as a lost IP link does today.
- **The phone is the one that shows the transport is down.** It starts
  advertising when it has no IP link to a paired Bluetooth-enabled desktop
  (the link closes, or the Android default network is lost, through the
  existing `ConnectivityManager` callback). It stops when an IP link is up.
  So in the usual case (Wi-Fi gone, no network) a BLE link is up before
  `sudo` starts.
- **Background delivery.** The helper's `approve.request` call has a 3 s
  timeout (`docs/approve.md:374-376`), but `ApproveRequest` today calls
  `d.send` before it returns `Started` (`approve.go:293`), and a send can
  wait on `wmu` and then up to the write deadline (`link.go:133-143`). So
  `ApproveRequest` registers the request and returns `Started` at once, and
  the send, the BLE connect and every later re-send run in a goroutine under
  the request's deadline. The helper then waits for the answer as it does
  today.
- **`shown` acknowledgement.** The phone sends `{"kind":"shown","id":...}`
  when the prompt is on the screen (a new `kind`: `handleApprove`,
  `approve.go:410-438`, has none today, so it is added there). A missing
  `shown` does not prove IP is dead: it may be a slow phone or a late prompt
  on a healthy link. So for a Bluetooth-enabled phone, when `shown` has not
  come 3 s after the first send, `fluxd` does not close the IP link. It
  keeps IP, and in addition starts a BLE connect and sends the same request
  ID over BLE (the "unconfirmed" state; the BLE link sits in `dev.bleLink`
  next to the live IP link, the one exception in 3, and it needs the phone
  to be advertising, see the timeline). The phone shows 1 prompt per request
  ID, and keeps the IDs it has seen until their deadline (also after a
  cancel), so whichever copy arrives first shows it and a late duplicate,
  a second link or a cancelled request never gives a second prompt or brings
  one back. The IP link is closed only by its own failure (a half-dead TCP
  link dies in 25 to 30 s, `userTimeout` `usertimeout_linux.go:16` and
  `keepAlive` `provider.go:139`).
- **IP fails after `shown`.** The prompt is up and the answer would be
  lost. The phone holds an answer until the request deadline: it sends it
  on the link that carried the request, and when that link is gone or the
  write fails, on any other link, and it starts advertising when no link is
  left. `fluxd` does not cancel an open approval of a Bluetooth-ready device
  when its IP link ends (`dropApprovals`, `approve.go:383`, is skipped); it
  starts a BLE connect if the 5 s rule below allows and waits for the answer
  on any link, until the deadline.
- **Switch time inside the default 20 s `approve_timeout`** (decision 6).
  The rows are targets, and the runtime test measures them:
  - A BLE link already up: the prompt shows within about 1 s (up to about
    3 s behind a 16 KiB line in flight).
  - IP stuck, BLE link up: 3 s (the `shown` wait) plus about 1 s, so about
    4 s (up to 6 s), and at least 14 s remain for the user. This row holds
    only when the phone already advertises or is connected, that is, when
    the phone saw its IP link go down while the desktop still holds a
    half-dead link. If the IP link is stuck on both sides, the phone does
    not advertise (it never advertises while IP looks up), no tag is seen,
    the unconfirmed BLE send cannot connect, and PAM asks for the password
    (see Risks, accepted for version 1).
  - No BLE link yet, tag seen: at most 8 s to connect (scan hit at most 2 s,
    connect and GATT discovery at most 3 s, TLS 1.3 at most 3 s). The prompt
    then shows at about 11 s and at least 9 s remain. With an A2DP transport
    active there is no scan (see 7), so this row does not apply and PAM
    asks for the password.
  - `fluxd` starts a connect only when at least 5 s of the wait remain. A
    request is never started again after it ends, and the deadline is the
    one that `ApproveRequest` set (`approve.go:282-284`), never extended.
    The request sent over BLE is the same packet as the first send, with
    the same `id`, fields and `timeout`. It must not carry a reduced
    `timeout`: the Android side clamps the field to at least 5 s
    (`ApproveMessage.kt:110`), treats a same-ID request with different
    fields as a conflict that closes the open request (`admit()`,
    `ApproveMessage.kt:139-143`), and starts its expiry at receipt
    (`Approvals.kt:75`). An identical copy is a `Repeat` and changes
    nothing. The desktop deadline is the one that counts: `fluxd` sends
    `cancel` over every link at its deadline, drops any answer after it, and
    the helper's own nonce and time checks refuse a late one. So a prompt
    that starts late on the phone (a copy that arrived late over BLE) can
    outlive the desktop deadline by the delay of that copy, and the cancel
    closes it.
  - A switch that does not make it in time is a timeout, and PAM asks for
    the password.
- **Cancel and unpair.** A cancel goes over whichever link is current, and
  also over `dev.bleLink` while it is open. An unpair closes every
  link of the device, including the BLE one, and deletes `ble_key`
  (`docs/architecture.md:84`, `pairing.go` calls `dropApprovals`
  at `161,218,309,377,411`, unchanged). The phone answers on the link that
  carried the request, and when that link is gone it sends the answer on
  the other link; `deliver` accepts the first answer and ignores the rest.
- **Phone with the screen off and Flux in the background.** The Bluetooth
  work runs in `FluxService` (a `connectedDevice` foreground service,
  `AndroidManifest.xml:155-158`), which already keeps the IP link alive in
  that state. The approval prompt is the same full-screen activity that
  Android shows today. What fails: Flux force-stopped, the Bluetooth
  permission revoked, Bluetooth off. Each falls back to the password.

### 6. Android

- **Manifest** (`android/app/src/main/AndroidManifest.xml:5-49`): add
  `BLUETOOTH_ADVERTISE` and `BLUETOOTH_CONNECT`, declared plainly with no
  SDK attribute (`<uses-permission>` has no `minSdkVersion`; Android 10 and
  11 ignore a permission they do not know, as the manifest already does for
  `POST_NOTIFICATIONS`). The feature is gated in code on
  `Build.VERSION.SDK_INT >= 31`, and the permission request and the switch
  exist only there. Also add
  `<uses-feature android:name="android.hardware.bluetooth_le" android:required="false"/>`
  (next to `manifest:51-53`). No `BLUETOOTH_SCAN`, no location, no legacy
  `BLUETOOTH` or `BLUETOOTH_ADMIN`.
- **Permission flow.** The permissions are requested when the user turns
  Bluetooth on in the settings, and not before. On Android 10 and 11 the
  switch is not shown. A revoked permission turns the feature off and says
  so.
- **Link code.** `net/Link.kt` takes an `SSLSocket` (`Link.kt:42-46`).
  Conscrypt can layer `SSLSocketFactory.createSocket(Socket, host, port,
  autoClose)` over a `java.net.Socket` subclass whose streams are the GATT
  stream, so a new `net/BleBackend.kt` produces a `Link` through the same
  `LanBackend.finish` (`LanBackend.kt:420`) as TCP. This is the one
  piece that is not proven; the first Android step of the plan is a
  prototype of it, before `BleBackend.kt` (see Risks and 8). The fall-back is a loopback `ServerSocket` bridge on 127.0.0.1
  with a random port and a one-time token, the pattern `android/tsnet`
  already uses to forward Flux ports.
- **Battery.** No advertising while an IP link to a paired desktop is up.
  When it is down: `ADVERTISE_MODE_LOW_POWER` (about 1 s interval), no
  scanning, an advertisement for each Bluetooth-enabled paired desktop (1
  tag in a payload, rotated as in 2). It runs for 15 minutes after the IP link is
  lost without a BLE connection, then 1 minute in every 15 minutes
  (an inexact alarm), and fully again on screen unlock, on opening Flux, and
  on a change of the default network. So a computer that is out of range
  for hours costs about 1/15 of the low-power duty. While a BLE link is up
  there is no advertising.
- Notification text limits in 4 (the 1 KiB cuts, then the cut after
  serializing to a 4 KiB line) are applied where `NotificationSync.kt:306-320`
  builds the `Packet(Types.NOTIFICATION, ...)` for a BLE link, not in
  `FluxService`.

### 7. Linux packaging and supported setups

- **Supported (decision 6):** BlueZ 5.x with a BLE adapter, and `fluxd` as
  the `fluxd.service` user service of the logged-in user (`dist/fluxd.service`).
  `AcquireWrite` and `AcquireNotify` need BlueZ 5.46 or later (2017); older
  BlueZ reports "Bluetooth is not supported on this BlueZ" and IP works as
  before. Other setups (no BlueZ, a Bluetooth-only USB dongle without LE,
  a container, a system service, PipeWire-only audio stacks) are best
  effort, and the feature reports why it is off.
- **Access.** The system bus policy for `org.bluez` lets a logged-in user
  call it; some distributions need the `bluetooth` group. `fluxd` needs no
  root and no capability. When BlueZ denies a call, Flux says "BlueZ refused
  access" in `flux-cli status` and the GUI, and keeps IP.
- **Nix and Arch.** `nix/nixos.nix` gets an option to set
  `hardware.bluetooth.enable` only when the user asks (never by default).
  `nix/home-manager.nix` cannot set it (it configures only `home.packages`
  and `systemd.user.services`, and has no Bluetooth reference), so it gets
  no such option; the docs say to enable BlueZ in the system config.
  `dist/arch` lists `bluez` as an optional dependency. Flux does not turn
  Bluetooth on by itself.
- **Audio coexistence.** The desktop scans only while a Bluetooth-enabled
  paired phone has no IP link and no BLE link. While an A2DP transport is
  active (`MediaTransport1.State == active` under the adapter) the desktop
  does not start a scan and instead waits; a BLE link that already exists
  is kept. So while audio plays there is no scan at all. This is the
  mitigation for the known dropouts on single-radio adapters, and it means a
  BLE link that is not yet up cannot start during audio (the approval
  falls back to the password).

### 8. Documents and tests

- Docs to change before the code (the intent requires it): `docs/approve.md`
  (transports, `shown`, the new timeline, "What an attacker can do", Failure
  modes), `docs/architecture.md` (a "Bluetooth direction" next to "Network
  direction"; pairing and the BLE key) and `docs/security.md`. The other
  docs (`docs/features.md`, `docs/troubleshooting.md`,
  `docs/configuration.md` (`ble`), `docs/android.md`) and the Nix and Arch
  parts come last, in the same PR. The protocol version stays 8: the new type and
  kind are ignored by older apps (the intent's "no change to the IP
  protocol").
- Plan steps are not set here. Order: the Go side first (`ble.go` with tag,
  gate, allow-list and limits, and tests; the BLE `Link` and `finishID`;
  then the daemon rules and the approval timeline; then BlueZ in
  `ble_linux.go`), then the Android prototype of TLS over GATT (needs a real
  phone), then the Android link code, service and settings, then docs and
  the Nix and Arch parts. The Go steps do not depend on the prototype. The
  prototype is the only unproven piece, so the owner may move it to the
  front; a failed prototype switches `BleBackend.kt` to the loopback bridge
  and never changes the design.

## Alternatives rejected

- **A Go Bluetooth library (for example `tinygo-org/bluetooth`).** Decision
  3 excludes a new Bluetooth dependency, and `godbus` already speaks to
  BlueZ.
- **L2CAP connection-oriented channels** (a socket with `AF_BLUETOOTH`,
  better throughput). Needs `golang.org/x/sys/unix` sockets, which bypasses
  BlueZ and the D-Bus-only decision, and Android's `listenUsingL2capChannel`
  (API 29) is less reliable across phones. GATT with `AcquireWrite` is
  enough for these 3 small features.
- **Bluetooth Classic RFCOMM.** Needs a classic pairing and a visible
  discoverable state, which changes host Bluetooth settings (a constraint),
  and phones restrict it more.
- **Bluetooth pairing and bonding as the security.** It does not give the
  pinned Flux identity, depends on the user's adapter and stack, and
  changes the host's pairings. Flux keeps TLS with the pin and uses no
  bonding.
- **A custom encrypted protocol** (for example Noise) over GATT. It would
  need a new implementation on 2 platforms and a new review, when `crypto/tls`
  and Conscrypt already give the same properties with the code that the
  pin check already uses.
- **Desktop as the peripheral and phone as the central.** The phone would
  need `BLUETOOTH_SCAN` (and a scan on a phone drains more), while a
  desktop peripheral needs BlueZ GATT application and advertising instances,
  which many adapters limit.
- **A constant service data or a Bluetooth name that names the device.**
  Violates the privacy constraint: a stranger could follow the phone.
- **Advertising all the time on the phone.** Breaks the battery constraint.
  Advertising only while the IP link is down, with the 15-minute cap, is
  the compromise (see Risks).
- **Keeping BLE up next to a working IP link** (a hot standby, faster
  switching). The intent excludes using Bluetooth when IP works and the
  phone would advertise all the time. The `shown` check covers the only
  case where this would help (a stuck IP link).
- **Running approvals in parallel on IP and BLE from the start.** Doubles
  the radio use and the duplicate handling for no gain when IP works.
- **A big clipboard limit (64 KiB or more).** A BLE link at 10 KB/s would
  block an approval for seconds. 4 KiB covers a password, a URL, a command.

## Risks

- **Upstream does not agree.** The maintainer may want another scope or a
  different design. Nothing is built until #185 has the agreement. Mitigation:
  keep the Bluetooth code in separate files and behind `ble = false`, so a
  smaller version can ship.
- **TLS over a custom `Socket` on Android (Conscrypt).** Not yet shown to
  work on all Android 12 to 16 builds. Phone makers patch Conscrypt. The
  loopback bridge is the fallback (it exposes a loopback port, so it needs
  the token and a short life). Prove it on a real phone before the Android
  link code is written.
- **BlueZ differences.** `AcquireWrite` and `AcquireNotify` behave
  differently across BlueZ 5.46 to 5.7x. Some adapters (and their firmware)
  cap LE connections or scanning while Bluetooth audio runs. Flux's scan
  only runs when needed, but a dropout on a single-radio adapter can still
  happen on the first scan. This affects Linux desktops that use Bluetooth
  headphones, and is why the runtime test starts A2DP audio while a BLE
  link is already up (see Verification).
- **Phone makers kill background Bluetooth.** Some vendors stop advertising
  from a service when the battery saver is on. Flux says so in the settings,
  and falls back to the password. The approval is never blocked by it.
- **A half-dead IP link plus a phone that does not know.** The phone
  advertises only when it sees its IP link down. When the IP link is stuck on
  both sides with no signal, no BLE link exists, and the approval waits for
  the password. A 3 s `shown` check does not help without an advertisement.
  Accepted for version 1.
- **Out-of-range phone and the 15-minute cap.** After the cap, a `sudo` can
  find the phone not advertising (at most 14 minutes of every 15) and
  fall back to the password. This is the price of the battery limit.
  The first test measures the current draw to see if the cap can be less
  strict.
- **Tag clock skew.** More than 15 minutes of difference between the phone
  and the desktop clock means no match. The phone already refuses a request
  from a clock more than 10 minutes away (`docs/approve.md:356-368`), so the
  limit is the same order.
- **A stranger with the Flux UUID in range** can see that Flux runs, and
  can open 1 connection that costs the phone one HMAC. It can also make the
  phone refuse another connection for up to 3 s. That is a block, not a
  read or a change, as the intent allows.
- **Replay.** TLS 1.3 gives a fresh session each time, so a recorded
  `request` or `cancel` is useless on a new connection. The hello is bound
  to the phone's per-connection challenge, so a captured hello is useless
  on any other connection. The `nonce` of an approval is unchanged.
- **Memory and queues on the phone.** The GATT server read queue and the
  writer lanes are bounded in 4 above. A mistake here would show as a stall
  or out-of-memory only when both sides are on, so tests must cover it.
- **Hosts.** Linux desktops with Bluetooth (the owner's desktops) and an
  Android 12+ phone. Not iOS, macOS, Windows, Android 10 and 11, which are
  unchanged. p510 is not touched until the owner agrees.

## Verification

- **Go unit tests (no hardware, `go test ./internal/lan ./internal/core ./internal/proto`):**
  - tag: same key and window give the same tag, windows t-1, t, t+1 match,
    others do not, no field reveals the ID; the advertisement is at most 31
    bytes and the scan response at most 31 bytes, with 1 tag each;
  - stream wrapper (on a `SOCK_SEQPACKET` socketpair, and on a stream pair):
    a write larger than the MTU arrives whole and in order, a read into a
    5-byte buffer keeps the rest of the packet, a zero-length packet is
    skipped, a deadline ends a blocked read and write;
  - consent: a kept key with either state off gives no scan, no connect and
    no advertised tag; a `flux.ble.state` of `false` closes a BLE link; no
    state seen since start counts as off;
  - queue: a full data lane drops the oldest notification, then clipboard,
    then ping, and keeps the new packet; a full control lane closes the link;
    identity, unpair and approve are never dropped; a lane of only clipboard
    packets still accepts a new one;
  - hello gate: a wrong HMAC, a captured hello replayed on the same tag and
    on another tag (the new challenge differs), a spoofed tag, a 3 s silence
    and an oversize hello are all closed before any TLS byte; on a BLE link
    the desktop sends no client certificate to a peer with the wrong pin, and
    an unknown (unpaired) device is closed before `onLink` and leaves no
    device entry; an unpair packet over BLE deletes the key and pin;
  - TLS 1.3 over an in-memory `net.Pipe` ends in the same `finishID` result
    as TCP, a wrong pin is refused, a wrong CN is refused;
  - allow-list and limits: each type outside the list is dropped, a clip of
    4096 bytes passes and 4097 does not (and the history keeps it), a
    5 KiB notification line is dropped, a 16 KiB line is closed, approval
    jumps ahead of a queue full of notifications, the lane drops the oldest
    notification, a burst of 50 notifications shows at most 10;
  - approvals: a switch from IP to BLE keeps the request ID and the deadline
    and shows 1 prompt, a late duplicate and a cancelled ID do not come back,
    `shown` missing at 3 s adds a BLE send and keeps IP, the BLE send is the
    same packet (same `timeout`) and the Android `admit()` gives `Repeat`,
    not `Conflict`, a cancel is sent at the desktop deadline, turning
    Bluetooth off with an open approval keeps it on a live IP link and keeps
    it to the deadline with no link, a late prompt on a
    healthy IP link and a loss of IP after `shown` give 1 prompt and 1
    answer, `ApproveRequest` returns `Started` inside 3 s with a blocked
    link, an IP link that returns during
    an open approval cancels nothing, an unpair closes both links and
    cancels, `enroll` over BLE is refused, `approveDevice` fails at once when
    no BLE is ready.
  - an in-process fake BlueZ object behind the same D-Bus `Call()` seam (the
    way `mdns_test.go:148-193` fakes Avahi with `fakeAvahi`, not a private
    bus) for the scan, connect, `AcquireWrite` and `AcquireNotify` path,
    adapter off, access denied, and A2DP active.
- **Android unit and instrumented tests:** the tag and hello vectors equal
  the Go vectors (a shared test vector file in the repo), the one-prompt
  rule per request ID, the 4 KiB clip limit and the notification trim, the
  permission flow (granted, denied, revoked while on), Android 10 and 11
  hide the switch.
- **Build:** `go build ./... && go vet ./... && go test ./...`, the Android
  `./gradlew assembleDebug testDebugUnitTest` as the repo's Makefile and CI
  do, and the Nix checks (`nix/checks.nix`). The PR says the plugin sandbox
  tests were run (`README.md#working-on-it`) if they apply.
- **Runtime test, on a Linux host with a BLE adapter and an Android 12+
  phone with Wi-Fi off** (the owner's choice of host; p510 only with the
  owner's OK before any build or deploy):
  1. Pair over Wi-Fi, turn Bluetooth on on both sides, see the key sent.
  2. Turn Wi-Fi off on the phone: within 10 s the desktop shows "connected
     over Bluetooth", the phone shows the same.
  3. A phone notification shows on the desktop. A 4 KiB clip syncs both
     ways, a 4.1 KiB clip does not and says so on both sides.
  4. `sudo true` with the phone locked, screen off, Flux in the background:
     the prompt shows, the fingerprint unlocks it, inside 20 s. Repeat with
     the IP link made stuck (block the traffic with the firewall, do not
     close it) and measure the time to the prompt; the result may be the
     password if the phone does not advertise (see 5). Repeat with the phone
     out of range, with Bluetooth off, and with Flux force-stopped: each
     asks for the password.
  5. Turn Wi-Fi on while a request is open: 1 prompt, answered, not
     cancelled. Unpair during a BLE link: the prompt closes.
  6. Run steps 2 to 4 first with no audio. Then, with a BLE link already
     up, start A2DP audio and listen for dropouts: the existing link is
     kept and no scan starts. With audio playing and no BLE link, check
     that no scan starts and `sudo` asks for the password.
  7. Capture the air with a sniffer or `btmon` for a day: no name, no ID, the
     tag changes every 15 minutes, and the random address changes with it
     (the gate of 2; if it does not, stop and take the decision there).
  8. Measure the phone battery for 2 hours with the computer out of range.
- **Docs:** the 7 files in 8 are changed in the same PR, and the review
  checks them against `plan/`.

## Revised after review (2026-11-18)

Findings that changed this spec: a packet-to-stream rule for the BlueZ
`SOCK_SEQPACKET` fds (1); the 31-byte air layout, 1 tag per payload (2); the
address-rotation gate (3); the `flux.ble.state` consent exchange and
admission (4); an unchanged same-`timeout` approval copy and the desktop
deadline as the authority (5); one rule for Bluetooth off during an open
approval (6); the queue lanes and their full-lane policy (7); corrected code
references (8). The BlueZ `SOCK_SEQPACKET` claim, the 31-byte limit and the
Android address behaviour were not verified here; each has a test above.

## Settled while planning (2026-11-18)

The draft plan listed 8 contradictions in this spec. Each is settled above
with the plan's provisional choice, unless noted. The owner confirms or
changes them before the plan is approved.

1. **Plan gate.** A `plan/` may exist as `status: draft` now. Its approval
   and every implementation step wait for the maintainer's agreement on
   bjarneo/flux #185. The upstream gate is unchanged.
2. **BLE link only while `dev.link == nil` vs the `shown` fallback.** The
   rule holds, with one exception: the one device with an open
   "unconfirmed" approval may get a BLE link next to a live IP link, in a
   secondary slot `dev.bleLink`. The code shows the spec was incomplete
   here: `handlePacket` (`handlers.go:19-30`) drops packets from any link
   that is not `dev.link`, so `dev.bleLink` must be accepted for approve
   `response`, `shown` and `cancel`, and `HasIPLink` is needed so a BLE-only
   link does not stop IP dialing (see 3).
3. **Phone does not advertise while IP is up vs a connect for a stuck IP
   link.** The phone's battery rule wins (the intent). The "IP stuck" rows
   are targets: they work only when the phone saw its IP link go down, and
   a link stuck on both sides ends at the password (Risks, accepted for
   version 1). The runtime test measures it.
4. **A2DP: no scan vs the runtime test.** Section 7 stands (the intent wants
   no audio dropouts). The runtime test runs steps 2 to 4 without audio,
   then starts audio with a BLE link already up. With audio and no BLE link
   there is no scan and the approval asks for the password.
5. **Step order.** Go side first, then the Android prototype (first Android
   step, before `BleBackend.kt`), as in the plan. The owner may move the
   prototype to the front. A failed prototype uses the loopback bridge.
6. **64 KiB before pairing vs 16 KiB on BLE.** A BLE link reads 16 KiB per
   line from the start, and `SetPaired` keeps that. `maxUnpairedLine` is
   IP-only.
7. **Notification line limit.** The phone cuts title and text to 1 KiB, then
   shrinks the text until the serialized line is at most 4 KiB. The desktop
   drops a longer line.
8. **Manifest wording.** The two permissions are declared plainly, as the
   manifest already does for `POST_NOTIFICATIONS`. The feature is gated in
   code on `SDK_INT >= 31`.
