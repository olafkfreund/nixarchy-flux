---
status: draft
issue: 69
author: olafkfreund
---

# Intent: Bluetooth LE as a fallback for notifications, clipboard text and approval

## Problem

Flux works only when the phone and the computer reach each other over IP.
- On a Wi-Fi network with client isolation, a captive portal, or no shared
  network at all (a train, a café, a phone hotspot that is off), the link
  never comes up.
- Then the lightweight features stop too: phone notifications on the
  desktop, clipboard text, and the `sudo` fingerprint approval. PAM falls
  back to the password after the wait.
- The phone and the computer are often next to each other at that time.
  Both have Bluetooth.
- Upstream issue bjarneo/flux #185 asks for Bluetooth LE as a fallback for
  these three features only.

### Facts

- **Discovery** is mDNS through Avahi over D-Bus
  (`internal/lan/mdns.go:12-21`) and UDP identity broadcasts on port 12100
  (`internal/lan/provider.go:220-252,295`). See `docs/architecture.md`,
  "Network direction".
- **The link** is TCP with TLS 1.2+ and mutual certificates
  (`internal/lan/tls.go:42-62`). Peers use self-signed certificates, so
  Flux compares the peer certificate with the pin after the handshake
  (`internal/lan/provider.go:665-673`). TLS 1.2 is limited to
  forward-secret AEAD suites (`internal/lan/tls.go:27-36`).
- **Off the LAN**, the desktop dials an extra address, for example a
  tailnet (`docs/tailscale.md`). On Android, `android/tsnet/main.go` runs a
  userspace tailnet node that forwards the Flux ports to the app. That
  still needs an IP route.
- **The wire format** is one JSON packet per line (`internal/lan/link.go:196-220`,
  `internal/proto/packet.go`), protocol version 8
  (`internal/proto/packet.go:14`). A line is at most 64 KiB before pairing
  (`internal/lan/tls.go:23-25`) and 16 MiB after (`packet.go:18`).
- **Pairing and trust.** `fluxd` pins each device's certificate in
  `~/.local/share/flux/devices.json`. A pairing is bound to the link it
  started on; after it, the stored pin authenticates each new link, and a
  link with another certificate is refused (`docs/architecture.md:78-81`).
  An unpair closes the link and stops all feature packets
  (`docs/architecture.md:84`).
- **The three features** are ordinary packet types on that link:
  `flux.notification*` (`internal/proto/identity.go:27-30`),
  `flux.clipboard` (`identity.go:23-24`; images are a separate
  `flux.clipboard.image`, `identity.go:81`) and `flux.approve`
  (`identity.go:72`).
- **Approval.** `flux-approve` (the PAM caller, root for `sudo` and
  polkit) makes a request with a 32-byte
  nonce. `fluxd` only carries it to the phone as a `flux.approve` packet
  (`internal/core/approve.go:257,293,326,405`). The phone shows it, asks
  for the strong biometric and signs with a hardware key. The helper
  checks the signature against the root-owned
  `/etc/flux/approve/<user>.pub` (`docs/approve.md`, "Trust anchor",
  "Messages", "Replay protection").
  - So the signature does not depend on the transport. But today TLS with
    pinned certificates is what stops a network attacker from reading or
    changing the request the phone shows (`docs/approve.md`, "What an
    attacker can do").
  - The nonce stops an old signature being reused. It does not stop a
    replayed notification, clipboard update, approval prompt or cancel;
    TLS does. The phone only refuses a request more than 10 minutes from
    its clock (`docs/approve.md:356-368`).
  - When the phone has no link, `fluxd` fails the request at once and PAM
    asks for the password (`internal/core/approve.go:254-256`,
    `docs/approve.md:292,398`). So a fallback link must already be up
    when `sudo` starts.
  - The wait is `approve_timeout`, 20 s by default, 5 to 120 s
    (`internal/core/approve.go:22-27`).
  - A lost link or lost trust cancels the open requests and closes the
    prompt on the phone (`internal/core/approve.go:379-397`,
    `docs/approve.md:380-387`).
  - Enrollment of the approval key also travels as `flux.approve`
    (`internal/core/approve.go:318-326`).
  - On iOS, **Approve** needs an unlocked iPhone with Flux on screen
    (`docs/approve.md:176-179`). The iOS app declares only the `audio`
    background mode (`ios/project.yml:55-56`).
- **No Bluetooth transport exists.** `go.mod` has no BLE or BlueZ library; it
  has `godbus/dbus/v5`, which BlueZ also uses. The Android manifest has no
  `BLUETOOTH_*` permission (`android/app/src/main/AndroidManifest.xml:5-49`).
  The iOS and macOS `project.yml` have `NSLocalNetworkUsageDescription`
  but no `NSBluetoothAlwaysUsageDescription` (`ios/project.yml:43`,
  `macos/project.yml:37`). A repo grep finds Bluetooth only as an audio
  output name (`docs/camera.md:131`, `core/DebugDemo.kt:168`).
- **Android** runs `FluxService` as a `connectedDevice` foreground service
  (`AndroidManifest.xml:155-158`), with `minSdk 29` and `targetSdk 36`
  (`android/app/build.gradle.kts:39-40`). Android does not let Flux read
  the clipboard in the background: phone-to-desktop text is sent from the
  Quick Settings tile or the service notification
  (`AndroidManifest.xml:111-122`).
- **The apps own their link code:** Android `net/LanBackend.kt` (TLS
  sockets), FluxKit `Net/` and `Core/FluxCore.swift` for iOS and macOS,
  and `windows/FluxKit` for Windows.

## Proposed outcome

For a Linux desktop and an Android 12+ phone (see Decisions), when the
IP link to a paired device is down and both have Bluetooth on:

- A phone notification still shows on the desktop.
- Copied text up to an agreed size still syncs both ways, with the same
  triggers as over IP today. Longer text is never cut: it does not sync,
  and the user is told.
- A `sudo` or polkit approval still reaches the phone, and an approved
  request still unlocks it within `approve_timeout`. This holds with the
  phone screen off and Flux in the background, as it does over IP today.
  Phone states that cannot work (Flux force-stopped, Bluetooth permission
  revoked) fall back to the password, as a lost IP link does today.
- The desktop and the phone show that the device is connected over
  Bluetooth, so the user knows why other features are unavailable.
- When the IP link comes back, Flux uses it again with no user action.
- No new pairing: only a device that is already paired over IP can use
  Bluetooth, and it is the same trusted device.
- Bluetooth is off until the user turns it on, on the desktop and on the
  phone.

Out of scope for this version:

- Pairing over Bluetooth, and enrollment of the approval key
  (`flux-cli approve enroll`).
- iOS, macOS, Windows, and Android 10–11.
- Files, clipboard images, clipboard history, screen, camera, mic,
  speaker, remote input, SSH, SFTP, herdr, SMS and calls.
- Notification replies and actions, and SSH signing.
- Using Bluetooth when the IP link works.

## Affected users and systems

- Users whose phone and computer are close but share no working IP
  network.
- `fluxd`: its network side, and its access to BlueZ on Linux.
- Flux for Android: its link code, manifest and permission flow.
- Desktop hosts: the Qt GUI and the shell plugin show the link type.
- Docs: `docs/architecture.md`, `docs/security.md`, `docs/approve.md`,
  `docs/features.md`, `docs/troubleshooting.md`.
- Packaging: the Nix module and `dist/` may need BlueZ and the user's
  access to it.

## Constraints

- **Same trust as today.** Over Bluetooth, only a paired device with the
  pinned identity can send or receive these packets. Traffic is encrypted
  and authenticated end to end with that identity, not only by Bluetooth
  pairing. A Bluetooth attacker nearby gets no more than a network
  attacker gets today: block or delay, never read, change, replay, or
  approve.
  - The link keeps what TLS gives today: confidentiality, integrity, a
    fresh session each time, replay resistance and forward secrecy.
  - Being near, an advertisement or a Bluetooth device name never
    establishes trust or authorizes anything.
- **Revocation and cancellation hold across both links.** An unpair stops
  Bluetooth traffic too. A cancelled or expired request never comes back
  after a reconnect or a switch between IP and Bluetooth. A switch never
  extends the deadline and never shows a second prompt for one request.
  When IP comes back during an open approval, that approval is not
  cancelled by the switch.
- **Privacy.** In public places, what the desktop or phone sends over the
  air must not name the user or the device, and must not let a stranger
  track them from place to place.
- A nearby stranger can hold only a few Bluetooth connections. A peer
  that does not prove a pinned identity is dropped within seconds.
- The approval design in `docs/approve.md` stays: the root helper, the
  root-owned key, the nonce, the time checks, the biometric on the phone.
  `fluxd` still only carries the request. That document must be updated
  before the approve path changes.
- Same limits for untrusted peers as on IP: no unbounded memory, no
  desktop notifications from unknown devices.
- BLE throughput is low and its packets are small, while a paired IP line
  can be 16 MiB. Each feature gets a size limit over Bluetooth. Buffers
  and half-received messages stay bounded in size and time. A large
  notification or clipboard text must not stall an approval, nor may a
  burst of old notifications on reconnect.
- Out-of-scope features are shown as unavailable over Bluetooth. They do
  not try IP ports or pile up waiting for IP.
- Battery: on the phone, no constant scanning or high-duty advertising
  while the IP link is up, nor for hours when the computer is out of
  range.
- Android: the new permissions (`BLUETOOTH_CONNECT`, and `BLUETOOTH_SCAN`
  or `BLUETOOTH_ADVERTISE`, Android 12+) are asked only when the user
  turns the feature on. No location permission. On Android 10–11
  (`minSdk 29`) the feature is not offered, and IP works as before. Background limits must still let an approval arrive while
  the screen is off, as the foreground service does today.
- Linux: `fluxd` runs as the user. It must not need root or extra
  capabilities for Bluetooth, and it must not change the host's Bluetooth
  settings or pairings beyond what the user turned on. When BlueZ denies
  access or the adapter cannot do what is needed, Flux says so and IP
  works as before. Bluetooth audio and input devices on the computer keep
  working without dropouts.
- No change to the IP protocol for peers that do not use Bluetooth.
  Older apps keep working.

## Decisions (owner, 2026-11-15)

1. **Features:** only notifications (show only), clipboard text and
   approval. Notification replies/actions and SSH signing stay out.
2. **Platforms:** Linux `fluxd` and Android 12+ only, so Flux never asks
   for location. iOS, macOS and Windows each get their own issue later.
3. **Dependency:** `fluxd` talks to BlueZ only through the D-Bus library
   it already has (`godbus/dbus/v5`). No new Go Bluetooth dependency.
4. **Default:** off on both sides until the user turns it on.
5. **Upstream:** the design is agreed on bjarneo/flux #185 before any
   building.
6. **To settle in spec** (proposals kept):
   - Clipboard text limit: a few KiB; longer text is refused and shown
     as such.
   - Switch time: the switch to Bluetooth fits inside the default 20 s
     `approve_timeout`.
   - Linux setups: BlueZ 5 with a BLE adapter, `fluxd` as the logged-in
     user's service. Others are best effort.
   - Test host: a Linux host with a Bluetooth adapter and an Android 12+
     phone with Wi-Fi off. p510 needs the owner's OK before any build or
     deploy.
