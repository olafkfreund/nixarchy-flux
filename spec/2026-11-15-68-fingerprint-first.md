---
status: draft
issue: 68
intent: intent/2026-11-15-68-fingerprint-first.md
---

# Spec: Fingerprint-first approval on Android

The owner decided the intent's questions (intent, "Decisions"):
- one tap is always needed, and no prompt opens by itself;
- the setting is per computer;
- `sudo`, `polkit-1` and `hyprlock` approvals only, never an enrollment;
- SSH signing is out of scope;
- a locked phone keeps today's flow unless a device test passes.

Paths are under `android/app/src/main/java/org/omarchy/flux/` unless
they start with `docs/`, `internal/` or `android/`.

## Design

### 1. The setting (`core/Settings.kt`)

One string set of computer IDs, next to `computerThemes`
(`Settings.kt:64-71`):

```kotlin
/** The computers whose approvals open the fingerprint prompt on the first tap. */
fun fingerprintFirst(computerId: String): Boolean =
    computerId in prefs.getStringSet("fingerprintFirst", emptySet()).orEmpty()

fun setFingerprintFirst(computerId: String, on: Boolean) {
    val ids = prefs.getStringSet("fingerprintFirst", emptySet()).orEmpty().toMutableSet()
    if (on) ids += computerId else ids -= computerId
    prefs.edit().putStringSet("fingerprintFirst", ids).apply()
}
```

- Off for every computer by default.
- The unpair (`ui/MainActivity.kt:586-591`) also calls
  `setFingerprintFirst(id, false)`, next to `ApproveKeys.delete(id)`, so a
  new pairing starts off.

### 2. The switch (`ui/ComputersScreen.kt`)

Under each paired computer that has an approval key, after the motion
row (`ComputersScreen.kt:115-118`), a `FingerprintFirstRow(d)`.

- It reuses `SyncRow` and `SyncItem` (`ui/SyncScreen.kt:55-62,369-387`),
  which become `internal`.
- Title "Fingerprint first". Detail when off: "Approve asks for the
  fingerprint after a second tap." Detail when on: "A tap on a request
  asks for the fingerprint at once. Leave it off for a computer where
  agents run `sudo`."
- On Android 14 and later (`Build.VERSION.SDK_INT >= 34`), when
  `canUseFullScreenIntent()` is false, the detail adds: "Android does
  not let Flux open requests by itself on the lock screen. Tap the
  notification." That is true today too; the row only says so.
- `ApproveKeys.has(d.id)` and `canUseFullScreenIntent()` are read again
  in a `LifecycleResumeEffect` (as in `ui/ChatScreen.kt:1200`). The
  Computers screen stays composed while an enrollment runs in
  `ApproveActivity`'s own task, so the switch appears when the user comes
  back after an enrollment, or from the Android settings.
- System services are read null-safe, and a missing one counts as "no".

### 3. Which taps count, and for which request (`core/Approvals.kt`, `ui/AppShell.kt`)

A new extra, `ApproveActivity.EXTRA_TAP`, holds the key
`"${r.computerId}/${r.id}"` of the request the user tapped. Only an
intent that a user tap starts carries it.

| Entry | Today | After |
| --- | --- | --- |
| Notification tap (`setContentIntent`, `Approvals.kt:142`) | `openScreen` (`Approvals.kt:123-126`), request code 10 | `openScreen(context, key)`, request code 10, with the key of `r` |
| Agents widget (`widget/WidgetViews.kt:181`) | `openScreen` | `openScreen(context, key)`, request code 13, with the key of `Approvals.current.value` when the widget draws, or none |
| Full-screen intent (`Approvals.kt:141`) | `openScreen` | a separate `PendingIntent`, request code 12, with no extra |
| Start while Flux is in the foreground (`Approvals.kt:148-150`) | plain intent | unchanged, no extra |
| Chat card Approve (`ui/AppShell.kt:194-199`) | plain intent | adds the key of `r` only when `(context as? MainActivity)?.touchObscured == false` |

- `PendingIntent` ignores extras when it compares intents, so each route
  needs its own request code. Otherwise `FLAG_UPDATE_CURRENT` would give
  the full-screen intent or the widget the notification's key.
- A key that is not the current request's key does nothing. So a late tap
  on an old request never opens a prompt for a new one.
- `ApproveActivity` is `exported="false"` (`AndroidManifest.xml:136`), so
  only Flux can send the extra.
- The chat card is a tap inside Flux, so it gets the same overlay guard
  as **Approve**. `touchObscured` is a property of the activity
  (`MainActivity.kt:86-92,217-223`). The cast fails closed: a covered tap,
  or a context that is not `MainActivity`, opens the screen as today.

### 4. The fast path (`ui/ApproveActivity.kt`)

- `onCreate` (only when `savedInstanceState == null`) and a new
  `onNewIntent` (the activity is `singleTop`, `AndroidManifest.xml:139`)
  read `EXTRA_TAP` from the incoming intent into `tapFor`, then remove
  the extra from the intent (as `MainActivity.kt:211-212` does). An
  intent with no extra sets `tapFor = null`.
- `onResume` takes `tapFor` and sets it to `null` first, whatever
  happens next. Then it calls `start(r, tapped = true)` when `fastPath`
  is true for `r = Approvals.current.value`. So a tap that did not take
  the fast path, for example on a locked phone, cannot take it later on
  an unlock. Starting in `onResume`, not in `onNewIntent`, keeps
  `BiometricPrompt` out of a stopped activity's fragment state.
- A pure function in the companion, for the unit test:

```kotlin
internal val FAST_SERVICES = setOf("sudo", "polkit-1", "hyprlock")

internal fun fastPath(r: ApproveRequest, tapFor: String?, on: Boolean, locked: Boolean, done: String?, asking: Boolean): Boolean {
    val key = "${r.computerId}/${r.id}"
    return r.kind == ApproveRequest.Kind.Approve && r.service in FAST_SERVICES &&
        tapFor == key && on && !locked && done != key && asking
}
```

  - `FAST_SERVICES` is the list the computer allows
    (`internal/approve/pam.go:29`), checked again on the phone
    (decision 3).
  - `on`: `FluxCore.settings.fingerprintFirst(r.computerId)`.
  - `locked`: `KeyguardManager.isKeyguardLocked`, true when the service
    is missing. That includes a swipe-only keyguard, which keeps today's
    flow too (decision 5; Verification 3).
  - `done`: `Approvals.fastFor`, the key of the request that last took
    the fast path. It lives in `Approvals`, not in the activity, and
    `Approvals.clear` resets it. So Back after a cancel, then another tap,
    does not open the prompt a second time for that request.
  - `asking`: `phase.value == Phase.Ask`. After an enrollment or an error
    the screen stays on that view, and a tap opens no prompt. That is
    today's behavior, and it fails safe.
- `start(r, tapped)` skips the `touchObscured` check only when `tapped`
  is true (`ApproveActivity.kt:293-296`). The tap happened outside this
  window, so the flag here is from an older touch. **Approve** on the
  screen keeps the check.
- `Approvals.fastFor` is set before the prompt opens.

### 5. The prompt (`ApproveActivity.kt:338-350`)

On the fast path only, the description holds what the screen shows and
the prompt does not (`ApproveActivity.kt:189-227`), warning first:

```
Approve only if you just typed the command.
Terminal: pts/3 · From: 10.0.0.5
Asked at 14:02:11 by p620
```

- An empty terminal or remote host drops its part, and an empty line
  drops out, so `polkit-1` and `hyprlock` show two lines.
- Title, subtitle, `BIOMETRIC_STRONG`, `setConfirmationRequired(true)`,
  the `CryptoObject` and the signed message stay unchanged.
- The warning comes first, because the prompt covers the bottom of the
  screen, where the screen's own warning is.

### 6. Errors and the end of the request

- On the fast path, `ERROR_TIMEOUT`, `ERROR_LOCKOUT` and
  `ERROR_LOCKOUT_PERMANENT` go back to `Phase.Ask`, like a cancel
  (`ApproveActivity.kt:119-123`). The request stays open until the
  computer's timeout. Other errors fail it, as today. A field
  `fastPrompt` records that the open prompt came from the fast path; it
  is cleared with `signing`.
- After a lockout, **Approve** on the screen fails at the
  `canAuthenticate` check (`ApproveActivity.kt:298-300`), as today, and
  **Deny** works.
- **The prompt closes with its request.** `onCreate` collects
  `Approvals.current` in `lifecycleScope`, not tied to the started
  state. When `signing` is set and the current key differs from
  `signing`'s key, it calls `prompt.cancelAuthentication()`. This
  catches a request that ends while the activity is stopped, and an end
  followed at once by a new request, which `StateFlow` can merge into one
  change. It runs with the setting off too. It only closes a prompt
  whose signature the computer would refuse, because the signed message
  holds the request's nonce (`core/ApproveMessage.kt:62-70`).
- An enrollment never takes the fast path (`fastPath` checks the kind).

### 7. Docs (`docs/approve.md`)

- Android section (`:103-133`): one item. "Fingerprint first", per
  computer and off by default, opens `BiometricPrompt` on a tap of the
  notification, the widget or the chat card, for `sudo`, `polkit-1` and
  `hyprlock`. It never opens without a tap, never for an enrollment, and
  not while the phone is locked.
- Approval flow, step 9 (`:298-300`): "With Fingerprint first on Android,
  a tap on the request asks at once."
- Failure modes (`:406`): with Fingerprint first, a timeout or a lockout
  of the prompt goes back to the Approve screen, and the request stays
  open.
- Open risks, approval fatigue (`:435-440`): one sentence. Fingerprint
  first skips the second look at the Approve screen, so turn it on only
  for a computer where you type `sudo` yourself.

## Alternatives rejected

- **Open the prompt with no tap (#183).** Declined by the owner
  (decision 1): an unlock touch could approve a request nobody read.
- **An "Approve" action in the notification.** Android starts the
  activity the same way as a tap on the notification. It adds a route
  and a `PendingIntent` for no gain.
- **A Boolean tap extra.** A late tap on an old request could open the
  prompt for a new one. The extra holds the request key instead.
- **Tie the prompt's end to `Screen`'s `current == null`.**
  `collectAsStateWithLifecycle` stops while the activity is stopped, and
  `StateFlow` can merge an end and a new request.
- **Keep `fastFor` in the activity.** Back after a cancel finishes the
  activity, and a new one would open the prompt again.
- **An app-wide switch.** Declined by the owner (decision 2).
- **Store the setting with the key in `ApproveKeys`.** The keystore holds
  keys, not preferences.
- **A JSON object like `computerThemes`.** A string set is the platform's
  form for a set of IDs and needs no parser.
- **Start the prompt in `onNewIntent`.** The activity can be stopped
  there, and `BiometricPrompt` adds a fragment. `onResume` is safe.
- **Change timeout and lockout for every prompt.** It would change the
  default flow, which the intent keeps.
- **Reset an Enrolled or Failed screen for a new request.** That is a
  separate, older behavior, and it already fails safe here.
- **Allow the fast path on a locked phone now.** The device test has not
  run (Verification 3).

## Risks

- **The widget tap is not overlay-checked.** It lands on the launcher,
  another app, so Flux can't read its touch flags. A fake request drawn
  over the widget leads to a system prompt that shows the real request,
  and it still needs a strong biometric. The notification tap lands on
  System UI. Accepted.
- **Approval fatigue grows on a computer with the switch on.** The user
  skips the Approve screen. The prompt carries the warning first, the
  docs say when to use the switch, and it is per computer and off by
  default.
- **A cut description.** A phone maker's prompt may cut a long
  description at a large font size. The warning is first, so it is the
  last to go. Verification 2 fails if any line is cut on the owner's
  devices; the plan then shortens the text. Flux cannot check other
  devices.
- **OEM prompts.** Some phone makers draw their own biometric dialog.
  The fast path only calls the same `authenticate` earlier. Checked on
  the owner's devices.

## Verification

1. `./gradlew :app:testDebugUnitTest :app:lintDebug :app:assembleDebug`,
   as in CI (`build.yml`).
   - A new `ApproveFastPathTest` checks `fastPath`. It is true only for
     an approval of `sudo`, `polkit-1` or `hyprlock`, with the tapped key
     equal to the request's, the switch on, the phone unlocked, the screen
     asking, and a request that has not taken the fast path. Each single
     condition turned false makes it false, including an enrollment,
     another service, another request's key and a null key.
2. **Devices** (the owner's Pixel and the Galaxy Tab S8 Ultra), with the
   #13 setup (`fluxd` on p620, paired and enrolled) and the branch's
   debug APK. Each line passes or fails.

   | Case | Pass |
   | --- | --- |
   | Switch off, tap the notification | Approve and Deny show first, as today |
   | Switch on, unlocked, Flux in the background, tap the notification | The prompt opens at once with the warning, terminal, computer and time. A fingerprint approves |
   | The same from the widget and from the chat card | The same |
   | Switch on, Flux in the foreground, a request arrives | Approve and Deny show. No prompt opens by itself |
   | Switch on, phone locked or screen off, a request arrives | The screen shows over the lock screen as today. No prompt opens by itself |
   | Then tap the notification on the lock screen | Approve and Deny show first |
   | Unlock the phone after that | No prompt opens |
   | Cancel the prompt | Approve and Deny show. No prompt opens by itself. **Approve** and **Deny** work |
   | Cancel, Back, tap the notification again | Approve and Deny show. No prompt |
   | Let the prompt time out, or fail until lockout | Approve and Deny show. The request ends at the computer's timeout |
   | Ctrl+C on the computer while the prompt is open | The prompt and the screen close |
   | Ctrl+C, then `sudo` again at once | The old prompt closes. The new request needs its own tap |
   | An enrollment with the switch on | Approve and Deny show first |
   | Switch on for one computer, a request from a second computer | Approve and Deny show first |
   | Air Command over the chat card's Approve (tablet) | The screen opens with Approve and Deny. No prompt |
   | Enroll a computer, go back to Computers | The switch is there |
   | Unpair and pair again | The switch is off |
   | Font scale 2.0, a `sudo` request with terminal and remote host | All three description lines show in full |

3. **Lock screen (decision 5).** This build keeps the `locked`
   condition. During implementation, a local build without it runs these
   checks on both devices, and the result goes in the PR:
   - lock the phone, start a request, tap the notification on the lock
     screen: the prompt shows, a fingerprint approves, and the phone stays
     locked afterwards;
   - restart the phone, and before the first unlock, start a request: no
     approval is sent without a fingerprint, and any failure goes back to
     the screen or fails the request with an error.

   Only if every check passes on both devices does a follow-up change,
   with the owner's OK, remove the condition. Otherwise a locked phone
   keeps today's flow.
4. **Regression:** SSH signing (`ui/SshSignActivity.kt`) is unchanged.
   One SSH signature with the switch on.
5. `nix flake check path:.`.

## Review changes

| Finding | Change |
| --- | --- |
| Codex 1, agy 2: the tap was not bound to a request | `EXTRA_TAP` holds the request key; the widget gets its own request code |
| Codex 2, agy 1: the prompt could outlive its request | `lifecycleScope` collector cancels the prompt when the current key differs from `signing` |
| Codex 3, agy 7: the description could be cut, and the warning hidden | The warning goes first; Verification 2 fails on a cut line |
| Codex 4, agy 7: `ERROR_LOCKOUT_PERMANENT` | Handled like `ERROR_LOCKOUT` on the fast path; Failure modes doc updated |
| Codex 5: no service check | `FAST_SERVICES`, from `internal/approve/pam.go:29` |
| Codex 6, agy 4: the switch could stay hidden after enrollment | Read again in `LifecycleResumeEffect`; null-safe services; API 34 guard |
| Codex 7, agy 8: no negative checks | Verification 2 adds foreground, full-screen, lock screen, unlock, Back, Ctrl+C, Air Command and unpair cases |
| agy 6: `tapFor` must clear on a locked resume; `touchObscured` is an instance property | `onResume` clears it first; the extra is removed; the chat card cast fails closed |
| agy 5: `fastFor` reset by Back | Moved to `Approvals`, reset in `clear` |
| Codex 8: full-screen wording; swipe keyguard | Row text says "open requests by itself"; swipe keyguard counts as locked |
| agy 8: a debug build before the plan; open-ended pass criteria | The lock-screen test runs during implementation; removing `locked` is a follow-up with the owner's OK; pass criteria stated |
| agy 3: an Enrolled or Failed screen ignores a new tap | Fails safe; not changed (Alternatives rejected) |
| agy 9: `Approvals.kt:123-127` | `123-126` |
