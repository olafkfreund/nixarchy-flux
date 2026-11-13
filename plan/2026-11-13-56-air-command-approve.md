---
status: draft
issue: 56
spec: spec/2026-11-13-56-air-command-approve.md
coders:
  1: coder   # a constant, a small composable, a KDoc line and one unit test
  2: coder   # the per-request state and scroll effect, given in full below; keeps codex an independent reviewer
  3: coder   # the same pattern in two sheets, given in full below
  4: coder   # three short doc edits
reviewers:
  - codex    # reads the branch diff with `codex review --base master`
  - agy      # independent read of the diff against this plan
---

# Plan: Explain a tap that an overlay blocked

## The approved decisions

- **The guard stays exactly as it is.** These don't change:
  - `Overlays.obscured` and its flags;
  - every `dispatchTouchEvent` and `motionEventSpy` check;
  - `Overlays.hide` and `HideOverlays`;
  - `MainActivity.touchObscured`;
  - the stream request's arming.

  There is no allow-list and no override. A covered tap is refused every
  time.
- **One shared message** replaces the four toasts. It shows on the screen
  above the buttons, or at the end of the scrolling request:

  > Another window covered Flux, so Flux ignored the tap. It can be an app's
  > bubble or floating button, or the S Pen Air Command button. Move it off
  > the button or close it, then tap again.
- **When it shows.** A refused tap shows it. It goes away only when:
  - a tap passes every check and the action goes on (the `else` branch of
    each guard); or
  - another request shows.

  Nothing writes the state during composition. A refused tap doesn't
  extend the timeout, and nothing retries by itself.
- **Accessibility:** TalkBack reads it once when it appears
  (`liveRegion = Polite`).
- **Layout:**
  - On Approve and SSH signing, the notice is the last item of the
    scrolling request column and scrolls into view when it appears.
  - The pair and stream sheets get `verticalScroll`.
  - The buttons stay reachable at a large font size.
- **Docs:**
  - a troubleshooting section;
  - `docs/android.md:211`;
  - the design text in `docs/approve.md:128-130`.

## Steps

Steps 1-4 run in order on the branch, and the lead commits each one before
the next starts, so steps 2 and 3 build on step 1's `BlockedNotice`. There
is no local Android SDK, so CI in step 5 is the first compile.

1. **`android/app/src/main/java/org/omarchy/flux/ui/Overlays.kt`**
   - In `object Overlays`, add:
     ```kotlin
     /** Shown in place of the action when [obscured] refused a tap. */
     const val BLOCKED =
         "Another window covered Flux, so Flux ignored the tap. " +
             "It can be an app's bubble or floating button, or the S Pen Air Command button. " +
             "Move it off the button or close it, then tap again."
     ```
   - In the KDoc of `obscured(flags, sdk)` (lines 20-26), replace the
     sentence "A window that the system keeps, such as the handle of an
     edge panel, then covers only a part and does not block a tap." with:
     "A window that the system or the phone maker keeps stays. It blocks a
     tap only where it covers the touch point, such as the S Pen Air
     Command button on Samsung devices."
   - After `HideOverlays` (line 50), add:
     ```kotlin
     /** The notice of a refused tap. It stays while [show] is true, and TalkBack reads it when it appears. */
     @Composable
     fun BlockedNotice(show: Boolean, align: TextAlign = TextAlign.Center) {
         if (!show) return
         Text(
             Overlays.BLOCKED,
             Modifier.fillMaxWidth().semantics { liveRegion = LiveRegionMode.Polite },
             style = MaterialTheme.typography.bodyMedium,
             color = MaterialTheme.colorScheme.error,
             textAlign = align,
         )
     }
     ```
     with the imports it needs: `Text`, `MaterialTheme`, `Modifier`,
     `fillMaxWidth`, `semantics`, `liveRegion`, `LiveRegionMode` and
     `TextAlign`.
   - `android/app/src/test/java/org/omarchy/flux/ui/OverlaysTest.kt`: add
     ```kotlin
     @Test
     fun theNoticeNamesAirCommandAndSaysToTapAgain() {
         assertTrue(Overlays.BLOCKED.contains("S Pen Air Command"))
         assertTrue(Overlays.BLOCKED.endsWith("then tap again."))
     }
     ```

   Verify: CI in step 5, because there's no local Android SDK.

   Traps:
   - `Overlays.kt` also holds `PairKey`; leave it.
   - Use Material 3 (`androidx.compose.material3`), as the activities do.

2. **`ui/ApproveActivity.kt` and `ui/SshSignActivity.kt`**, the same
   change in each.
   - Add a field next to `phase`:
     ```kotlin
     // The request whose last tap a window covered. AskView shows the notice for it.
     private val blockedFor = mutableStateOf<String?>(null)
     ```
   - The request key, as a local `val requestKey` in both `start(r)` and
     `AskView(r)`:
     - in `ApproveActivity`, `"${r.computerId}/${r.id}"`;
     - in `SshSignActivity`, `"${r.computerId}/${r.id}"` too (`SshRequest`
       has `computerId`, `core/SshMessage.kt:28`).

     Never call it `key`: `ApproveActivity.start` already has
     `val key: ApproveKeys.Pending?` (`:289`), which stays unchanged.
   - **In `start(r)`:**
     - the `if (touchObscured)` branch (`ApproveActivity.kt:279-282`,
       `SshSignActivity.kt:206-209`) becomes:
       ```kotlin
       if (touchObscured) {
           blockedFor.value = requestKey
           return
       }
       blockedFor.value = null
       ```
     - the `FluxCore.toast(...)` line goes.
   - **In `AskView`:**
     - lift the scroll state: `val scroll = rememberScrollState()` before
       the outer `Column`, and `.verticalScroll(scroll)` on the inner
       column (`ApproveActivity.kt:177-178`, `SshSignActivity.kt:149-150`);
     - as the last child of that inner, scrolling column, add:
       ```kotlin
       val blocked = blockedFor.value == requestKey
       BlockedNotice(blocked)
       LaunchedEffect(blocked) {
           if (!blocked) return@LaunchedEffect
           // Wait one frame, so that maxValue includes the notice.
           withFrameNanos { }
           scroll.animateScrollTo(scroll.maxValue)
       }
       ```
       with the import `androidx.compose.runtime.withFrameNanos`. The
       spec's one-line effect read `maxValue` before the notice was laid
       out, so the frame wait is a correction (recorded as P1 when the step
       is done).
   - Leave `track()` unchanged; don't write any state from it.

   Verify: CI in step 5.

   Traps:
   - `blockedFor` is a `mutableStateOf` field, read in composition and
     written only from `start`, which a click calls. Never write it during
     composition.
   - Leave `dispatchTouchEvent`, `touchObscured`, `Overlays.hide`, the
     `BiometricManager` check and everything after it unchanged.
   - `SshSignActivity.kt` has its own `start` with the device-credential
     logic; only the `touchObscured` branch changes.

3. **`ui/TiledScreens.kt` (`TiledPairSheet`) and `ui/StreamRequestUi.kt`**
   - **Pair sheet:**
     - add `var blocked by remember(name) { mutableStateOf(false) }` at the
       top of `TiledPairSheet` (line 62);
     - the `pair` lambda (`:66-72`) becomes:
       ```kotlin
       val pair = {
           if (activity?.touchObscured == true) {
               blocked = true
           } else {
               blocked = false
               onPair()
           }
       }
       ```
     - add `.verticalScroll(rememberScrollState())` to the sheet's content
       `Column` modifier (`:86`), before `.navigationBarsPadding()`;
     - add `BlockedNotice(blocked, align = TextAlign.Start)` directly above
       the Cancel/Pair `Row` (`:121`).
   - **Stream request:**
     - add `var blocked by remember(r) { mutableStateOf(false) }` next to
       `obscured`;
     - in the Start `when` (`:114-120`):
       - `obscured -> blocked = true`;
       - `else -> { blocked = false; onStart() }`;
       - the `down < armedAt -> Unit` branch stays first and unchanged;
     - add `.verticalScroll(rememberScrollState())` to the column modifier
       after `.motionEventSpy { ... }` and before `.padding(...)` (`:87-99`);
     - add `BlockedNotice(blocked, align = TextAlign.Start)` directly above
       the Not now / Start `Row`.
   - The two `FluxCore.toast("Another app draws over Flux ...")` lines go.
   - Imports:
     - both files: `androidx.compose.ui.text.style.TextAlign`;
     - `StreamRequestUi.kt` also needs
       `androidx.compose.foundation.rememberScrollState` and
       `androidx.compose.foundation.verticalScroll`; `TiledScreens.kt`
       already has them.

   Verify: CI in step 5.

   Traps:
   - Keep `motionEventSpy` first in the stream column's modifier chain, so
     it still sees every touch.
   - `remember(name)`, not `remember(key)`: the key can arrive after a tap.
   - Leave `HideOverlays()` in `TiledPairSheet`.

4. **Docs**
   - `docs/troubleshooting.md`: a new section after line 450:
     ```markdown
     ## Approve, Enroll, Pair, or Start does nothing

     The phone says that another window covered Flux, and nothing else happens.
     Flux refuses a tap while a window of another app lies over it, so that an app cannot draw a false request over Flux and make you tap it.
     Bubbles and floating buttons of other apps do this, and on Samsung devices the S Pen Air Command button.
     Move the window off the button, or close it, then tap **Approve**, **Enroll**, **Pair**, **Start**, or **Play on phone** again before the request times out.
     ```
   - `docs/android.md:211`: "- The prompt refuses a tap while another
     window covers Flux, and says so on the screen."
   - `docs/approve.md:128-130` becomes:
     ```markdown
     - On Android 12 and later, the approval screen hides the overlay
       windows of other apps. Windows that the system or the phone maker
       keeps, such as the S Pen Air Command button, can stay. The phone
       refuses **Approve** when another window covered the touch point,
       and says so on the screen.
     ```

   Verify:
   ```sh
   git diff --stat -- docs
   ```
   Three files change.

   Trap: one sentence per line in `docs/troubleshooting.md` and
   `docs/android.md`, as those files are written. `docs/approve.md` wraps
   at about 72 columns.

5. **CI (the lead).**
   - Push, after an agent-bus announcement.
   - Dispatch `build.yml` on the branch:
     ```sh
     gh workflow run build.yml -R olafkfreund/nixarchy-flux --ref fix/56-air-command-approve
     ```
   - Poll every 5 minutes. The android job must pass: unit tests (with the
     new `OverlaysTest` test), `lintDebug`, both assembles, and the ABI
     check.
   - Before the push, `nix flake check path:.` must pass.

6. **Device tests (the lead and the owner).**

   In the commands, `$b` is `<scratchpad>/r13/flux-core`, the build that
   runs `fluxd` now. Use `$b/bin/flux-cli` and `$b/lib/flux/flux-approve`,
   never a `flux-cli` from `PATH`.

   **Setup:**
   1. Read the serials with `adb devices`, because wireless ports change.
   2. **Install.** Compare the signing certificate of the installed app
      with the branch's `android-debug` APK. No debug keystore is
      committed, so each CI run may sign with a new key.
      - Use `adb -s <serial> shell pm dump org.omarchy.flux | grep -A1 signatures`
        for the installed app, against `keytool -printcert -jarfile app-debug.apk`.
      - The same: `adb -s <serial> install -r`.
      - Different: ask the owner, then `adb uninstall` and install. That
        drops the pairing and keys on the device, so pair again with
        `$b/bin/flux-cli pair` and `accept`, and enroll the tablet again.
   3. **Enrollment** uses the method that worked in #13: `sudo -n` with
      stdin from a FIFO. The tablet shows the key code after the
      fingerprint; the owner sends it, and the lead writes it to the
      FIFO.

   **First check:** with the Air Command button over Approve, tap next to
   its icon and check that the notice shows. If no tap shows it, stop and
   report: the fix doesn't help that case.

   **Then, in this order** (pairing goes last, because an unpair deletes the
   tablet's approval key). For each flow, three covered taps: no action,
   and the notice shows and stays. Then move the button and tap once: the
   action goes on and the notice is gone.

   | # | Flow on the tablet | Start it with |
   | --- | --- | --- |
   | 1 | Approve | `PAM_TYPE=auth PAM_USER=olafkfreund PAM_SERVICE=sudo PAM_TTY=/dev/pts/9 $b/lib/flux/flux-approve`. `PAM_TTY` is only shown on the phone, so any value works. |
   | 2 | Approve, left covered | the same: it times out at 20 s, as before |
   | 3 | Approve, the next request | the same: it starts with no notice |
   | 4 | Large font | font scale 2.0, split screen, portrait and landscape, notice on Approve: all buttons reachable (see below) |
   | 5 | SSH signing | turn on the SSH key in Flux, then `SSH_AUTH_SOCK=/run/user/1000/flux/ssh-agent.sock ssh-keygen -Y sign -n test -f <the tablet's public key file> <a file>` |
   | 6 | Stream request | `$b/bin/flux-cli webcam start --device "olafs Tab S8 Ultra"` |
   | 7 | Enroll | `sudo -n $b/bin/flux-cli approve enroll`, with the FIFO |
   | 8 | Pair, and the pair sheet at font scale 2.0 | `$b/bin/flux-cli unpair "olafs Tab S8 Ultra"`, Flux open on the tablet, `$b/bin/flux-cli discover`, then `$b/bin/flux-cli pair "olafs Tab S8 Ultra"` and `accept` |

   **Also:** TalkBack reads the notice once, and Deny, Cancel and Not now
   work (during flow 1).

   **Large font:**
   - Read `adb -s <tablet> shell settings get system font_scale` first.
   - Set `2.0`.
   - Afterwards, restore the old value. If it was `null`, run
     `settings delete system font_scale` instead.

   **Pixel regression:** enroll the Pixel (`sudo -n ... approve enroll` with
   the Pixel the only connected device, as in #13), then one approval with
   nothing over the button. The key file holds one device, so this
   replaces the tablet's enrollment.

   Record the results in the PR.

7. **PR (the lead).** Open the PR linking the intent, spec and plan, with
   the CI and device results and which steps each implementer did.

8. **Clean up the test setup (the lead), after the device tests, with the
   owner's OK for each delete.**

   The state before the #13 test, checked on 2026-11-13:
   - `/etc/flux` did not exist;
   - `~/.config/flux` did not exist;
   - `~/.local/share/flux` held only `browser-extension/`;
   - no `fluxd.service` ran.

   So everything else there was made by the test.
   1. `sudo $b/bin/flux-cli approve remove`.
   2. `sudo rmdir /etc/flux/approve /etc/flux`. It fails, and stops, if a
      folder isn't empty.
   3. `$b/bin/flux-cli unpair` for each device.
   4. Stop the scratchpad `fluxd`.
   5. List `~/.config/flux` and `~/.local/share/flux`. With the owner's
      OK, delete `~/.config/flux` and every entry of `~/.local/share/flux`
      except `browser-extension`.
   6. With the owner's OK, `adb -s <serial> uninstall org.omarchy.flux` on
      both devices.

## Tests

- CI `build.yml` android job, with `OverlaysTest.theNoticeNamesAirCommandAndSaysToTapAgain`.
- The device tests of step 6.
- `nix flake check path:.`.
- Not testable on these devices:
  - a cover only at down or only at up;
  - partial cover before Android 12;
  - a stream tap before arming.

  Their code doesn't change, and the existing `OverlaysTest` covers the
  flag logic.

## Rollback

- Revert the PR's squash commit. The guard never changed, so a revert
  only brings back the toasts.

## Review changes

| Finding | Change |
| --- | --- |
| Codex 2, agy 1: `key` clashes with `val key: ApproveKeys.Pending?` | `requestKey`, never `key`. |
| Codex 3, agy 2: missing imports | Listed in steps 2 and 3. |
| agy 3: `maxValue` is read before the notice is laid out | Wait one frame with `withFrameNanos`. A refinement of the spec's snippet, to be recorded as P1. |
| Codex 5, agy 4: an unpair deletes the approval key; the Pixel isn't enrolled; `--device`; bare `flux-cli` | Pairing goes last, the Pixel is enrolled before its check, `--device` on `webcam start`, and `$b/bin/flux-cli` everywhere. |
| Codex 4, agy 4: enrollment reads `/dev/tty`, so a pipe can't work | Refuted by #13: both piped enrollments (`sudo -n`, no terminal) read the code from the FIFO and enrolled. The code is written after the phone shows it. |
| Codex 1, agy 5: the cleanup can delete real Flux data | The state before #13 is written down, and each delete needs the owner's OK. |
| agy 6: `font_scale` may be `null`; serials change; the APK may not install over the old one | `settings delete` for `null`; serials read first; certificates compared, with an uninstall only on the owner's OK. |
| agy 7: step order without a compiler; Codex reviews its own step | Steps run in order with a commit each. Step 2 goes to `coder`, so Codex stays an independent reviewer. `master` is this repo's base. |
| agy 8: SSH request IDs can repeat across computers | `"${r.computerId}/${r.id}"`, as for approvals. A refinement of the spec. |

## Corrections

| # | Step | What changed and why |
| --- | --- | --- |
