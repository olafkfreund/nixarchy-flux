---
status: approved
issue: 68
spec: spec/2026-11-15-68-fingerprint-first.md
coders:
  1: coder   # a preference pair, an unpair line, two visibility changes and one composable, given in full below
  2: coder   # three launch routes and a field, mechanical, given in full below
  3: codex   # small, tricky lifecycle logic: fastPath, onResume, the error mapping and the cancel collector
  4: agy     # four doc edits in docs/approve.md, mechanical
reviewers:
  - codex    # reads the branch diff with `codex review --base master`; it wrote step 3, so agy and the Opus review read that step too
  - agy      # independent read of the diff against this plan
---

# Plan: Fingerprint-first approval on Android

## The approved decisions

- **One tap is always needed.** The prompt never opens by itself: not
  from the full-screen intent, not when Flux is in the foreground, not
  over the lock screen without a tap (owner, decision 1).
- **Per computer.** A string set `fingerprintFirst` of computer IDs in
  `Settings`, off by default, cleared on unpair (decision 2).
- **Scope.** Only `Kind.Approve` for `sudo`, `polkit-1` and `hyprlock`, the
  list in `internal/approve/pam.go:29`. Never an enrollment (decision 3).
  SSH signing is unchanged (decision 4).
- **Locked phone.** `KeyguardManager.isKeyguardLocked` (or a missing
  service) keeps today's flow. A swipe keyguard counts as locked. Dropping
  this check is a later change with the owner's OK, after the lock-screen
  check in step 6 (decision 5).
- **Which taps count, for which request.** An extra holds the key
  `"<computerId>/<id>"` of the tapped request. Only a tap carries it:
  - the notification's content intent, request code 10;
  - the agents widget, request code 13, with the key of the request its
    row shows;
  - the chat card's Approve, only when `MainActivity.touchObscured` is
    false (the cast fails closed).

  The full-screen intent gets its own `PendingIntent`, request code 12,
  with no extra. The foreground start stays without one. The key is also
  the tap intent's data URI, so an old `PendingIntent` is never updated to
  a newer request. A key that isn't the current request's does nothing.
- **The fast path** runs in `onResume`, once per request. `onResume`
  clears the tapped key first, whatever happens next. `Approvals.fastFor`
  records the request that took it, and `Approvals.clear` resets it. It
  needs `phase == Ask`. The tapped start skips the in-window
  `touchObscured` check; **Approve** on the screen keeps it.
- **The prompt** keeps its title, subtitle, `BIOMETRIC_STRONG`,
  `setConfirmationRequired(true)`, `CryptoObject` and signed message. On
  the fast path only, the description is, with empty parts and lines
  dropped:
  ```
  Approve only if you just typed the command.
  Terminal: <tty> · From: <rhost>
  Asked at <HH:mm:ss> by <computer name>
  ```
- **Errors.** On the fast path, `ERROR_TIMEOUT`, `ERROR_LOCKOUT` and
  `ERROR_LOCKOUT_PERMANENT` go back to the Approve screen, like a cancel.
  Other errors fail the request, as today.
- **The prompt closes with its request.** A collector of
  `Approvals.current` in `lifecycleScope` cancels the prompt when the
  current request is no longer the one being signed. This runs with the
  switch off too.
- **The switch.** A "Fingerprint first" row under each paired computer
  with an approval key, built from `SyncRow`. It re-reads the key and the
  full-screen permission on resume.
- **Docs.** `docs/approve.md`: the Android section, flow step 9, failure
  modes and approval fatigue.

## Steps

Steps 1-4 run in order on `feat/68-fingerprint-first`. The lead reviews
and commits each one before the next starts, because step 3 uses step 2's
`Approvals.EXTRA_TAP`, `requestKey` and `fastFor`. There is no local
Android SDK, so the first Kotlin compile is the CI android job in step 5.
The lead pushes once, after steps 1-4 are committed, and runs that job.
"Verify: CI in step 5" in a step means that one run.

Paths are under `android/app/src/main/java/org/omarchy/flux/` unless
they start with `android/`, `docs/` or `internal/`.

1. **The setting and the switch**
   - `core/Settings.kt`, after `computerThemes` (lines 64-71):
     ```kotlin
     /** The computers whose approvals open the fingerprint prompt on the first tap. See docs/approve.md. */
     fun fingerprintFirst(computerId: String): Boolean =
         computerId in prefs.getStringSet("fingerprintFirst", emptySet()).orEmpty()

     fun setFingerprintFirst(computerId: String, on: Boolean) {
         val ids = prefs.getStringSet("fingerprintFirst", emptySet()).orEmpty().toMutableSet()
         if (on) ids += computerId else ids -= computerId
         prefs.edit().putStringSet("fingerprintFirst", ids).apply()
     }
     ```
   - `ui/MainActivity.kt:589`: after `ApproveKeys.delete(id)`, add
     `FluxCore.settings.setFingerprintFirst(id, false)`.
   - `ui/SyncScreen.kt:55` `private class SyncItem` and `:371`
     `private fun SyncRow` become `internal`. Nothing else changes there.
   - `ui/ComputersScreen.kt:117`: after
     `if (showsMotion(d)) MotionRow(d, state)`, add `FingerprintFirstRow(d)`.
     Add the composable after `ThemeRow` (line 464):
     ```kotlin
     /** Fingerprint first for 1 computer with an approval key. docs/approve.md describes it. */
     @Composable
     private fun FingerprintFirstRow(d: DeviceUi) {
         val context = LocalContext.current
         var keyed by remember(d.id) { mutableStateOf(false) }
         var fullScreen by remember { mutableStateOf(true) }
         var on by remember(d.id) { mutableStateOf(FluxCore.settings.fingerprintFirst(d.id)) }
         // An enrollment runs in its own task, and the Android settings are another app, so read both again on return.
         LifecycleResumeEffect(d.id) {
             keyed = ApproveKeys.has(d.id)
             fullScreen = Build.VERSION.SDK_INT < 34 ||
                 context.getSystemService(NotificationManager::class.java)?.canUseFullScreenIntent() == true
             onPauseOrDispose { }
         }
         if (!keyed) return
         val detail = (if (on) "A tap on a request asks for the fingerprint at once. Leave it off for a computer where agents run sudo." else "Approve asks for the fingerprint after a second tap.") +
             (if (fullScreen) "" else " Android does not let Flux open requests by itself on the lock screen. Tap the notification.")
         SyncRow(SyncItem("fingerprintFirst", R.drawable.ic_fingerprint, "Fingerprint first", detail, on) {
             on = !on
             FluxCore.settings.setFingerprintFirst(d.id, on)
         })
     }
     ```
     Imports, those the file lacks: `android.app.NotificationManager`,
     `android.os.Build`, `androidx.compose.ui.platform.LocalContext`,
     `androidx.lifecycle.compose.LifecycleResumeEffect`,
     `org.omarchy.flux.R`, `org.omarchy.flux.core.ApproveKeys`, and the
     `remember`, `mutableStateOf`, `getValue` and `setValue` runtime
     imports.

   Verify: CI in step 5.

   Traps:
   - `SyncItem.onToggle` is a `() -> Unit`; the row's `toggleable` calls
     it.
   - `canUseFullScreenIntent` is API 34: keep the `SDK_INT` check in the
     same expression, or lint fails with `NewApi`.
   - `LifecycleResumeEffect` must end with `onPauseOrDispose { }`.
   - Don't call `ApproveKeys.has` during composition; it reads the
     Keystore.

2. **The launch routes** (`core/Approvals.kt`, `widget/WidgetModel.kt`, `widget/WidgetViews.kt`, `ui/AppShell.kt`)
   - `core/Approvals.kt`, in `object Approvals` after `current`
     (line 30):
     ```kotlin
     /** The extra of an intent that a tap on a request starts. It holds the request's [requestKey]. */
     const val EXTRA_TAP = "tap"

     /** The key of the request that took the fast path, so it opens the prompt once. Main thread only. */
     var fastFor: String? = null
     ```
     and a top-level function at the end of the file:
     ```kotlin
     /** The key of [r] for the fast path: its computer and its id. */
     internal fun requestKey(r: ApproveRequest): String = "${r.computerId}/${r.id}"
     ```
   - `clear` (lines 112-116): after `_current.value = null`, add
     `fastFor = null`.
   - `openScreen` (lines 118-126) becomes:
     ```kotlin
     /**
      * Opens the Approve screen after a tap: the approval notification
      * (request code 10) and the agents widget (13). [key] is the tapped
      * request's [requestKey], or null. PendingIntent ignores extras when
      * it compares intents, so each route has its own request code, and the
      * key is also the data URI: an old token keeps its own request's key
      * and is never updated to a newer request's.
      */
     fun openScreen(context: Context, key: String?, code: Int = 10): PendingIntent = PendingIntent.getActivity(
         context, code,
         Intent(context, ApproveActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
             .apply { if (key != null) putExtra(EXTRA_TAP, key).setData(Uri.fromParts("flux-approve", key, null)) },
         PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
     )
     ```
     with `import android.net.Uri`.
   - In `show` (lines 129-151): `val open = openScreen(context, requestKey(r))`,
     and a new `full` for the full-screen intent, which gets no extra:
     ```kotlin
     // The full-screen intent opens the screen with no tap, so it carries no key.
     val full = PendingIntent.getActivity(
         context, 12, Intent(context, ApproveActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK),
         PendingIntent.FLAG_IMMUTABLE or PendingIntent.FLAG_UPDATE_CURRENT,
     )
     ```
     `.setFullScreenIntent(full, true)` (line 141). `.setContentIntent(open)`
     stays. The foreground start (lines 148-150) stays unchanged.
   - The widget row carries the key of the request it shows, not the
     current one at click time:
     - `widget/WidgetModel.kt:44` becomes
       `data class Approval(val computerId: String, val key: String) : WidgetTarget`,
       with the KDoc "The approval screen of the request [key] from the
       computer [computerId]."
     - `widget/WidgetModel.kt:136` becomes
       `target = WidgetTarget.Approval(q.computerId, requestKey(q)),`, with
       `import org.omarchy.flux.core.requestKey`.
     - `android/app/src/test/java/org/omarchy/flux/widget/WidgetModelTest.kt:84`
       becomes `assertEquals(WidgetTarget.Approval("pc", "pc/r1"), sudo.target)`
       (the request at `:50-53` has `id = "r1"`).
   - `widget/WidgetViews.kt:181`:
     ```kotlin
     is WidgetTarget.Approval -> if (isDemo(t.computerId)) Android.openChats(context) else Approvals.openScreen(context, t.key, 13)
     ```
   - `ui/AppShell.kt:194-199`, the `approve` lambda's `else` branch:
     ```kotlin
     val i = Intent(context, ApproveActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
     // A tap that a window covered opens the screen without the fast path, so Approve there checks it again.
     if ((context as? MainActivity)?.touchObscured == false) i.putExtra(Approvals.EXTRA_TAP, requestKey(r))
     context.startActivity(i)
     ```
     with imports for `Approvals` and `requestKey` if missing.

   Verify: CI in step 5. Also:
   ```sh
   grep -rn "openScreen(" android/app/src/main/java
   ```
   shows exactly the two new calls and the definition.

   Traps:
   - Request code 10 stays the notification's, so a notification posted
     before the update still opens the screen.
   - `ApproveActivity` has no intent filter, so the data URI only makes
     each request's token distinct; the explicit component still starts
     it. The full-screen intent and the foreground start get no data.
   - `grep -rn "WidgetTarget.Approval" android/app/src` must show only
     the three places above.
   - `FLAG_IMMUTABLE` stays on every `PendingIntent`.
   - `== false`, not `!= true`: the cast must fail closed.

3. **The fast path** (`ui/ApproveActivity.kt`, and a new test)
   - Top-level, after the imports and before the class:
     ```kotlin
     /** The services whose approvals can take the fast path: the list in internal/approve/pam.go. */
     internal val FAST_SERVICES = setOf("sudo", "polkit-1", "hyprlock")

     /**
      * True when a tap on [r] opens the fingerprint prompt at once:
      * Fingerprint first is [on] for its computer, the tap was for this
      * request, the phone is not [locked], the request has not taken the
      * fast path ([done]), and the screen is [asking].
      */
     internal fun fastPath(r: ApproveRequest, tapFor: String?, on: Boolean, locked: Boolean, done: String?, asking: Boolean): Boolean {
         val key = requestKey(r)
         return r.kind == ApproveRequest.Kind.Approve && r.service in FAST_SERVICES &&
             tapFor == key && on && !locked && done != key && asking
     }
     ```
   - Fields, next to `touchObscured` (line 84):
     ```kotlin
     // The request key of the tap that started or brought this screen, until onResume takes it.
     private var tapFor: String? = null

     // True while the open prompt came from the fast path.
     private var fastPrompt = false
     ```
   - `onCreate` (lines 86-127), before `setContent`:
     ```kotlin
     // A recreated screen lost the request it signed, so it closes a prompt that the old one left open.
     if (savedInstanceState != null) prompt.cancelAuthentication()
     if (savedInstanceState == null) tapFor = takeTap(intent)
     // The prompt belongs to 1 request. It closes when that request ends or another takes its place.
     lifecycleScope.launch {
         Approvals.current.collect { c ->
             val s = signing ?: return@collect
             if (c == null || requestKey(c) != requestKey(s)) prompt.cancelAuthentication()
         }
     }
     ```
   - New members:
     ```kotlin
     override fun onNewIntent(intent: Intent) {
         super.onNewIntent(intent)
         setIntent(intent)
         tapFor = takeTap(intent)
     }

     override fun onResume() {
         super.onResume()
         val tap = tapFor
         // A tap counts once: a later resume, such as an unlock, does not take the fast path.
         tapFor = null
         val r = Approvals.current.value ?: return
         val locked = getSystemService(KeyguardManager::class.java)?.isKeyguardLocked != false
         if (fastPath(r, tap, FluxCore.settings.fingerprintFirst(r.computerId), locked, Approvals.fastFor, phase.value == Phase.Ask)) {
             Approvals.fastFor = requestKey(r)
             start(r, tapped = true)
         }
     }

     /** The tapped request key of [i], removed from it, so the intent counts once. */
     private fun takeTap(i: Intent): String? = i.getStringExtra(Approvals.EXTRA_TAP).also { i.removeExtra(Approvals.EXTRA_TAP) }
     ```
   - `start(r)` (line 291) becomes `start(r: ApproveRequest, tapped: Boolean = false)`.
     - The guard (lines 293-296): `if (!tapped && touchObscured) {`. The
       body stays.
     - Before `signing = r` (line 351): `fastPrompt = tapped`.
     - The `PromptInfo` (lines 343-350): the `.apply { ... setDescription(where) }`
       line becomes:
       ```kotlin
       .apply {
           val text = if (tapped) {
               val time = SimpleDateFormat("HH:mm:ss", Locale.getDefault()).format(Date(r.time * 1000))
               // The prompt covers the screen's warning, so the warning comes first.
               listOf("Approve only if you just typed the command.", where, "Asked at $time by ${r.computerName}")
                   .filter { it.isNotEmpty() }.joinToString("\n")
           } else {
               where
           }
           if (key == null && text.isNotEmpty()) setDescription(text)
       }
       ```
   - `onAuthenticationSucceeded` (lines 92-112): `fastPrompt = false` next
     to `signing = null`.
   - `onAuthenticationError` (lines 114-124) becomes:
     ```kotlin
     val r = signing
     val fast = fastPrompt
     onSigned = null
     signing = null
     fastPrompt = false
     discardPending()
     when (errorCode) {
         // A cancel keeps the request open, so the user can approve or deny it again.
         BiometricPrompt.ERROR_NEGATIVE_BUTTON, BiometricPrompt.ERROR_USER_CANCELED, BiometricPrompt.ERROR_CANCELED -> phase.value = Phase.Ask
         // A fast-path prompt that nobody answered, or a locked sensor, goes back to the screen. The request stays open.
         BiometricPrompt.ERROR_TIMEOUT, BiometricPrompt.ERROR_LOCKOUT, BiometricPrompt.ERROR_LOCKOUT_PERMANENT ->
             if (fast) phase.value = Phase.Ask else if (r != null) fail(r, errString.toString()) else phase.value = Phase.Failed(errString.toString())
         else -> if (r != null) fail(r, errString.toString()) else phase.value = Phase.Failed(errString.toString())
     }
     ```
   - Imports: `android.app.KeyguardManager`, `android.content.Intent`,
     `androidx.lifecycle.lifecycleScope`, `kotlinx.coroutines.launch`,
     `org.omarchy.flux.core.requestKey`.
   - New test `android/app/src/test/java/org/omarchy/flux/ui/ApproveFastPathTest.kt`:
     ```kotlin
     package org.omarchy.flux.ui

     import org.junit.Assert.assertFalse
     import org.junit.Assert.assertTrue
     import org.junit.Test
     import org.omarchy.flux.core.ApproveRequest

     class ApproveFastPathTest {
         private fun req(kind: ApproveRequest.Kind = ApproveRequest.Kind.Approve, service: String = "sudo") = ApproveRequest(
             computerId = "pc", computerName = "omarchy-xps", id = "r1", kind = kind,
             host = "omarchy-xps", user = "dev", service = if (kind == ApproveRequest.Kind.Enroll) "" else service,
             tty = "pts/3", rhost = "", time = 1, nonce = "", timeoutSeconds = 20,
         )

         private fun fast(
             r: ApproveRequest = req(), tapFor: String? = "pc/r1", on: Boolean = true,
             locked: Boolean = false, done: String? = null, asking: Boolean = true,
         ) = fastPath(r, tapFor, on, locked, done, asking)

         @Test
         fun aTapOnAnApprovalTakesTheFastPath() {
             assertTrue(fast())
             assertTrue(fast(req(service = "polkit-1")))
             assertTrue(fast(req(service = "hyprlock")))
         }

         @Test
         fun anyMissingConditionKeepsTheApproveScreen() {
             assertFalse("an enrollment", fast(req(ApproveRequest.Kind.Enroll)))
             assertFalse("another service", fast(req(service = "login")))
             assertFalse("no tap", fast(tapFor = null))
             assertFalse("a tap on another request", fast(tapFor = "pc/r0"))
             assertFalse("the switch is off", fast(on = false))
             assertFalse("the phone is locked", fast(locked = true))
             assertFalse("the request took it already", fast(done = "pc/r1"))
             assertFalse("the screen shows a result", fast(asking = false))
         }
     }
     ```

   Verify: CI in step 5, with the new test.

   Traps:
   - `ApproveActivity`'s companion is `private`, so `fastPath` and
     `FAST_SERVICES` are top-level `internal`, where the test can reach
     them (refinement R1).
   - `start` already has `val key: ApproveKeys.Pending?`. Don't add
     another `key`.
   - `start` (line 292) and `AskView` (line 178) each have a local
     `val requestKey` string. Inside them it hides the top-level
     `requestKey(r)`, and a call there doesn't compile. Call
     `requestKey(r)` only in `onResume`, `onCreate` and `fastPath`, which
     have no such local.
   - Leave `dispatchTouchEvent`, `Overlays.hide`, the
     `canAuthenticate` check, the key code and the enrollment path
     unchanged.
   - `onResume` must call `super.onResume()` first and start the prompt
     only after it. `onNewIntent` must not start the prompt.
   - `cancelAuthentication()` with no prompt open does nothing; the
     collector also fires once at start, while `signing` is null.
   - Don't touch `SshSignActivity.kt`.

4. **Docs** (`docs/approve.md`)
   - Android section, after line 132 (the last item, before the blank line
     and `### iPhone and Mac`), a new item:
     ```markdown
     - **Fingerprint first** is a switch for each paired computer, under
       the computer on the Computers screen, and off by default. With it
       on, a tap on a `sudo`, `polkit-1`, or `hyprlock` request in the
       notification, the widget, or the chat card opens `BiometricPrompt`
       at once, without the Approve screen first. It never opens without
       a tap, never for an enrollment, and not while the phone is locked.
       The prompt then also shows the computer, the time, and the
       warning. A cancel, a timeout, or a lockout goes back to the Approve
       screen.
     ```
   - Approval flow, step 9 (lines 298-300), a sentence at the end: "With
     Fingerprint first on Android, a tap on the request asks at once."
   - Failure modes (line 406): in the right-hand cell, after "stops",
     add: ". With Fingerprint first on Android, a timeout or a lockout of
     the prompt goes back to the Approve screen, and the request stays
     open". The section covers every platform, so it names Android.
   - Open risks, approval fatigue (lines 435-440), a sentence at the end:
     "Fingerprint first on Android skips the second look at the Approve
     screen, so turn it on only for a computer where you type `sudo`
     yourself."

   Verify:
   ```sh
   git diff --stat -- docs
   ```
   One file changes. Then `nix flake check path:.`.

   Trap: `docs/approve.md` wraps at about 72 columns and uses the Oxford
   comma. Keep the table row on one line.

5. **CI (the lead).**
   - `nix flake check path:.` passes before the push.
   - Push after an agent-bus announcement, then dispatch `build.yml`:
     ```sh
     gh workflow run build.yml -R olafkfreund/nixarchy-flux --ref feat/68-fingerprint-first
     ```
   - Poll every 5 minutes. The android job must pass: unit tests (with
     `ApproveFastPathTest`), `lintDebug`, the assembles and the ABI check.
   - A failure goes back to the step that caused it, with the same coder.

6. **Device checks (owner-run follow-ups, not merge gates).** The owner
   deferred them. They run later on the Pixel and the Galaxy Tab S8 Ultra
   with the #13 setup and the branch's `android-debug` APK. The results
   go in the PR, or in a follow-up issue if it is merged first.

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

   **Lock-screen probe (decision 5).** There is no local Android SDK, so
   the probe APK comes from CI: the lead pushes a temporary branch
   `probe/68-lock-screen` with the `locked` argument set to `false` in
   `onResume`, dispatches `build.yml` on it, and deletes the branch after.
   It never merges. On both devices:
   - lock the phone, start a request, tap the notification on the lock
     screen: the prompt shows, a fingerprint approves, and the phone stays
     locked;
   - restart the phone, and before the first unlock, start a request: no
     approval goes out without a fingerprint, and a failure goes back to
     the screen or fails the request with an error.

   Only if every check passes on both devices may a later change, with
   the owner's OK, remove the condition.

   Also: one SSH signature with the switch on (unchanged).

7. **PR (the lead).** Link the intent, spec and plan. Give the CI result,
   which steps each coder did, and that the device checks of step 6 are
   owner-run follow-ups.

## Tests

- CI `build.yml` android job: `ApproveFastPathTest` (step 3), the changed `WidgetModelTest` (step 2), the
  existing unit tests, `lintDebug` and the assembles.
- `nix flake check path:.`.
- The device checks of step 6, owner-run, after or alongside the PR.
- Not covered by a unit test: the lifecycle (`onNewIntent`, `onResume`),
  the `PendingIntent` routes and the cancel collector. They need a device
  (step 6), and the PR diff shows the routes.

## Rollback

- Revert the PR's squash commit. Nothing on the computer or in the
  protocol changes. The `fingerprintFirst` preference stays in
  `settings` and nothing reads it; it is harmless.
- Per user: turn the switch off for the computer.

## Refinements of the spec

| # | Step | What and why |
| --- | --- | --- |
| R1 | 3 | `fastPath` and `FAST_SERVICES` are top-level `internal` in `ApproveActivity.kt`, not in the companion: the companion is `private` (`ApproveActivity.kt:355`), and the test must reach them. |
| R2 | 2 | `EXTRA_TAP` is `Approvals.EXTRA_TAP`, not `ApproveActivity.EXTRA_TAP`: `Approvals` and `AppShell` set it, and the activity's companion is private. A shared `requestKey(r)` builds the key. |
| R3 | 2 | The widget row carries the key of the request it shows: `WidgetTarget.Approval` gets a `key` (`widget/WidgetModel.kt:44,136`). The spec only said "the key of `Approvals.current.value` when the widget draws", which a later request could replace. |
| R4 | 2 | The tap intent's data URI is the request key, so `FLAG_UPDATE_CURRENT` never moves an old token to a newer request. The spec named only the request codes. |
| R5 | 3 | A recreated `ApproveActivity` cancels a prompt left open by the old instance. `configChanges` (`AndroidManifest.xml:138`) already avoids recreation on rotation, resizing and dark mode; this covers the rest, such as a locale change. |
| R6 | 6 | The owner deferred the device checks (lead, 2026-11-15): they are owner-run follow-ups, not merge gates. The spec had the lock-screen probe during implementation and a cut description as a failure before the PR. Until they run, these are unverified: the lifecycle (`onNewIntent`, `onResume`), the routes, the cancel collector, the chat card's overlay guard, OEM prompts and the description at font scale 2.0. A cut description found later is fixed in a follow-up. |
| R7 | 2, 3 | Diff reviews (lead, 2026-11-15): `requestKey` adds the nonce, so a request that reuses an id after a cancel (an untrusted `fluxd`, agy partial review) no longer matches the tapped key, and the cancel collector closes its prompt. ApproveFastPathTest adds the same id on another computer (Opus) and the same id with a new nonce. approve.md no longer says "never opens without a tap": an app that holds the notification's or the widget's PendingIntent can open those routes, so it says the prompt opens only from the 3 Flux routes and still needs the fingerprint (Opus). |

## Review changes

| Finding | Change |
| --- | --- |
| Codex 1: `FLAG_UPDATE_CURRENT` moves an old token to a newer request | The tap intent's data URI is the request key (R4) |
| Codex 2: the widget took the key at click time, not the key of the row it shows | `WidgetTarget.Approval` carries the row's key (R3), with `WidgetModelTest` updated |
| Codex 3, agy 2: the deferral of the device checks was not recorded; "local build" with no SDK | R6; the probe APK comes from a CI run on a temporary branch that never merges |
| agy 1: a recreated activity loses `signing` while the prompt stays open | Mostly covered by `configChanges`; a one-line cancel on recreation for the rest (R5) |
| agy 3: the test was described, not given | `ApproveFastPathTest.kt` in full |
| agy 4: Codex and agy write steps and also review | Kept: the lead asked for this routing. Each reviews the other's step, and the Opus review reads both |
| agy 5: `docs/approve.md` line 132, not 133; "on Android" in Failure modes; one CI run | Fixed |
