---
status: draft
issue: 68
author: olafkfreund
---

# Intent: Fingerprint-first approval on Android

## Problem

On Android, approving `sudo` takes two steps: tap **Approve**, then scan the
finger. Users who approve many times a day find the extra tap slow. Some
type the password instead, so they stop using the feature.
- Upstream `bjarneo/flux#186` asks for an opt-in mode that shows the
  fingerprint prompt at once. A cancel goes back to the Approve and Deny
  screen.
- Upstream `bjarneo/flux#183` asks for the same. It also asks for the prompt
  to show with the phone locked or Flux in the background, without a tap on
  the notification.

The two asks differ in risk. In #186 the user still acts first: a tap on
the notification or the card. In #183 the prompt opens by itself, so the
first touch of the sensor can approve.

### Facts

- **Two steps today.** `ApproveActivity` shows the request with **Deny** and
  **Approve** (`ui/ApproveActivity.kt:229-236`). Only **Approve** calls
  `start`, which opens `BiometricPrompt` (`ApproveActivity.kt:291-353`).
- **The prompt shows part of the request.** Its title is the service, its
  subtitle the user and host, and its description the terminal and remote
  host (`ApproveActivity.kt:338-350`).
  - The screen also shows the computer name, the time, and the warning
    "Approve only if you just typed the command."
    (`ApproveActivity.kt:189-227`). The prompt shows none of them.
  - The system prompt covers the lower part of the screen, where the
    warning is.
- **Strong biometric, not only fingerprint.** The prompt allows
  `BIOMETRIC_STRONG` (`ApproveActivity.kt:348`). On some phones that
  includes a Class 3 face unlock. `setConfirmationRequired(true)`
  (`ApproveActivity.kt:349`) asks for a tap after a face match. It is a
  hint to the system, and `docs/approve.md` does not list it.
- **Cancel and errors.** A cancel goes back to the screen, and the request
  stays open (`ApproveActivity.kt:119-121`). Every other prompt error, such
  as a timeout or a lockout, fails the request on the computer
  (`ApproveActivity.kt:122`).
- **The signature names the request.** The signed message holds the host,
  user, service, terminal, time and nonce of the request
  (`core/ApproveMessage.kt:62-70`). A signature for one request cannot
  approve another.
- **The request closes by itself.** The phone clears it at the computer's
  timeout (`core/Approvals.kt:72-76`), 20 s by default and at most 120 s
  (`docs/approve.md:376`). The screen then finishes
  (`ApproveActivity.kt:155-157`). Nothing cancels an open prompt.
- **The tap guard.** `ApproveActivity` hides overlay windows
  (`ApproveActivity.kt:89`) and refuses a tap that another window covered
  (`ApproveActivity.kt:134-140,293-296`, `ui/Overlays.kt:31-49`). A prompt
  that opens without a tap has no tap to guard.
- **How the screen opens.**
  - `Approvals.show` posts a notification with category `CALL`, maximum
    priority, a full-screen intent and a **Deny** action
    (`core/Approvals.kt:129-147`). It has no **Approve** action.
  - It starts `ApproveActivity` itself only when Flux is in the foreground
    (`Approvals.kt:148-150`).
  - The chat card (`ui/ChatScreen.kt:557-581`, `ui/AppShell.kt:193-199`)
    and the agents widget (`widget/WidgetViews.kt:181`) open the same
    screen.
- **The lock screen.** `ApproveActivity` has `showWhenLocked` and
  `turnScreenOn` (`AndroidManifest.xml:134-142`). The manifest asks for
  `USE_FULL_SCREEN_INTENT` (`AndroidManifest.xml:16`).
  - With the permission, Android shows the screen when the phone is locked
    or its screen is off. On an unlocked phone in use, it shows a heads-up
    notification.
  - Without the permission, it shows a heads-up notification in every
    state. So today's screen does not open over the lock screen either.
  - From Android 14, Google Play takes the permission from apps that are
    not call or alarm apps. Flux does not come from Google Play
    (`docs/android-setup.md:33`). Whether a sideloaded Flux has it depends
    on the installer and the phone maker. The user can turn it off. The
    code does not check `canUseFullScreenIntent()`.
  - Android limits starts of an activity from the background. The
    full-screen intent and a tap on the notification are the routes Flux
    uses today.
- **The key.** It needs a strong biometric for each use and no time window
  (`core/ApproveKeys.kt:83-100`, `docs/approve.md:105-116`). It does not ask
  for an unlocked device. Whether a locked phone can show the prompt and
  sign is not tested.
- **Approval fatigue.** `docs/approve.md:435-440` names it as an open risk.
  Code that runs as the user can start `sudo` and wait for a real request.
  The advice is to approve only right after you typed the command, and the
  Approve screen carries it. The threat model puts "a user who approves a
  request without reading it" out of scope, but says the design reduces the
  risk (`docs/approve.md:63-68`).
- **Other platforms.** On the Mac and on Windows, **Approve** in the
  notification asks for the biometric at once (`docs/approve.md:177-180,219`,
  `macos/Sources/FluxKit/Plugins/Approve/ApprovePlugin.swift:157-165`). The
  user still taps **Approve** first. Nothing opens a prompt by itself.
- **Settings.** `core/Settings.kt` holds app-wide settings in
  `SharedPreferences`. No approval setting exists. The only setting for
  each computer there is its theme, kept as one JSON string
  (`Settings.kt:64-71`).
- **SSH signing.** `ui/SshSignActivity.kt` has the same two-step flow for
  SSH key requests.

## Proposed outcome

- A new setting, off by default, turns on fingerprint-first approval.
- With the setting on, a request opens the biometric prompt after one user
  action, with no tap on **Approve**.
  - The action is a tap on the notification, the chat card or the widget.
  - The user sees the whole request before or in the prompt: service, user,
    host, terminal, computer, time and the warning. Large text keeps it
    readable.
  - A strong biometric approves the request, as today.
  - A cancel, a timeout or a lockout of the prompt shows the Approve and
    Deny screen. The request stays open until the computer's timeout.
  - After a cancel, the prompt does not open again by itself for that
    request.
  - When the request ends, its prompt closes.
- Whether the prompt also opens with no action at all, over the lock screen
  or when Flux is on screen, is open question 1.
- With the setting off, nothing changes.
- An enrollment always keeps the two steps. It shows a key code to compare.
- When Android does not allow the full-screen intent, the setting says so.
- `docs/approve.md` describes the setting and its trade-off.

## Affected users and systems

- Android users who approve `sudo`, polkit or the `hyprlock` lock screen
  with Flux.
- `ui/ApproveActivity.kt`, `core/Approvals.kt`, `core/Settings.kt`, the
  settings screen, and their tests.
- Docs: `docs/approve.md`.
- Not affected: `fluxd`, the PAM helper, the protocol, iOS, macOS,
  Windows.

## Constraints

- The key and its signing stay as they are: a strong biometric for each
  signature, through `BiometricPrompt` with a `CryptoObject`, no PIN, and
  the confirmation after a face match.
- Off by default. The current flow stays the default.
- No new permission. No way around the Android rules for starting an
  activity from the background.
- Nothing approves without a strong biometric. A cancel, an error or a
  timeout never approves.
- A prompt belongs to one request. It never signs for another, and it does
  not stay open after its request ends.
- The overlay hiding stays. The tap guard stays for every tap that still
  approves, such as **Approve** after a cancel.

## Open questions

1. **Open the prompt with no user action?** #183 asks for it over the lock
   screen and when Flux is in the background. Proposed: no.
   - A finger on the sensor to unlock the phone would then approve a
     request the user has not read. Code that runs as the user can start
     that request.
   - The overlay guard has no tap to check.
   - The Mac and Windows also need a tap first.
   - If yes: one setting for both, or a second one?
2. **For each computer, or for the whole app?** Proposed: for each
   computer, so the user can leave it off for a computer where agents run.
   It does not stop other code on the computer from asking.
3. **Which requests?** Proposed: every approval (`sudo`, polkit and
   `hyprlock`), but not an enrollment. A `hyprlock` approval unlocks the
   screen and does not give root.
4. **SSH signing too?** Proposed: not now. It has its own screen and its
   own risks. A separate issue can follow.
5. **Lock screen test.** Does the prompt show and sign over the lock screen
   on the owner's devices: after a tap on the full-screen screen, and after
   a reboot before the first unlock? Does the phone stay locked after the
   approval? This needs a test before the spec. If it fails, the locked
   phone keeps today's flow.
