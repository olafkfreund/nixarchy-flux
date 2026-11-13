---
status: draft
issue: 56
spec: spec/2026-11-13-56-air-command-approve.md
coders:
  1: coder   # a constant, a small composable, a KDoc line and one unit test
  2: codex   # the per-request blocked state and the scroll effect in two activities: small but easy to get wrong
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
   - The request key:
     - in `ApproveActivity`, `"${r.computerId}/${r.id}"`;
     - in `SshSignActivity`, `r.id`.
   - **In `start(r)`:**
     - the `if (touchObscured)` branch (`ApproveActivity.kt:279-282`,
       `SshSignActivity.kt:206-209`) becomes:
       ```kotlin
       if (touchObscured) {
           blockedFor.value = key
           return
       }
       blockedFor.value = null
       ```
       with `key` computed as above;
     - the `FluxCore.toast(...)` line goes.
   - **In `AskView`:**
     - lift the scroll state: `val scroll = rememberScrollState()` before
       the outer `Column`, and `.verticalScroll(scroll)` on the inner
       column (`ApproveActivity.kt:177-178`, `SshSignActivity.kt:149-150`);
     - as the last child of that inner, scrolling column, add:
       ```kotlin
       val blocked = blockedFor.value == key
       BlockedNotice(blocked)
       LaunchedEffect(blocked) { if (blocked) scroll.animateScrollTo(scroll.maxValue) }
       ```
       with `key` the shown request's key.
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
   - Install the branch's `android-debug` APK on the tablet
     (`192.168.1.97:37937`) and the Pixel (`192.168.1.81:44549`) with
     `adb -s <serial> install -r`.
   - Use the running #13 setup: `fluxd` from the scratchpad build, both
     paired, the tablet enrolled.
   - **First:** with the Air Command button over Approve, tap next to its
     icon and check that the notice shows. If no tap shows it, stop and
     report: the fix doesn't help that case.
   - **Then, for each flow:** three covered taps (no action, the notice
     shows and stays), then move the button and tap once (the action goes
     on and the notice is gone).

     | Flow | Start it with |
     | --- | --- |
     | Approve | `PAM_TYPE=auth PAM_USER=olafkfreund PAM_SERVICE=sudo PAM_TTY=/dev/pts/9 <scratchpad>/flux-core/lib/flux/flux-approve` |
     | Enroll | `sudo <scratchpad>/flux-core/bin/flux-cli approve enroll`, with the key code through a pipe |
     | Pair | `flux-cli unpair "olafs Tab S8 Ultra"`, then `flux-cli pair "olafs Tab S8 Ultra"` |
     | SSH signing | turn on the SSH key in Flux on the tablet, then `SSH_AUTH_SOCK=/run/user/1000/flux/ssh-agent.sock ssh-keygen -Y sign -n test -f <the tablet's public key file> <a file>` |
     | Stream request | `flux-cli webcam start` |

   - **Also:**
     - an approval left covered times out at 20 s as before;
     - a new request starts with no notice;
     - TalkBack reads the notice once, and Deny, Cancel and Not now work.
   - **Large font:**
     - read `adb -s <tablet> shell settings get system font_scale`, then
       set `2.0`;
     - with Flux in split screen, in portrait and landscape, show the
       notice on Approve and on the pair sheet;
     - check that the notice and all buttons are reachable;
     - put the value back.
   - **Pixel:** one approval with nothing over the button.
   - Record the results in the PR.

7. **PR (the lead).** Open the PR linking the intent, spec and plan, with
   the CI and device results and which steps each implementer did.

8. **Clean up the test setup (the lead), after the device tests.**
   - `sudo <scratchpad>/flux-core/bin/flux-cli approve remove`.
   - `sudo rmdir /etc/flux/approve /etc/flux`, only if empty.
   - Unpair both devices with `flux-cli unpair`.
   - Stop the scratchpad `fluxd`.
   - Delete the state it made: `~/.config/flux`, and every entry under
     `~/.local/share/flux` except `browser-extension`. List them first.
   - `adb -s <serial> uninstall org.omarchy.flux` on both devices, after
     the owner confirms, since that removes the pairing and keys on the
     device.

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

## Corrections

| # | Step | What changed and why |
| --- | --- | --- |
