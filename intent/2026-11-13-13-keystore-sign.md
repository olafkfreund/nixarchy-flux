---
status: approved
issue: 13
author: olafkfreund
---

# Intent: Approval enrollment fails with "Key user not authenticated"

## Problem

On some Android phones, `sudo flux-cli approve enroll` always fails. The
phone shows the biometric prompt and the user passes it, then the command
prints:

```
flux-cli: the phone could not make the key: The phone could not sign the request: android.security.KeyStoreException: Key user not authenticated (internal Keystore code: -26 message: system/security/keystore2/src/operation.rs:853: KeystoreOpera
```

This is upstream `bjarneo/flux#136`, still open there. Clearing the app's
data doesn't help. Without enrollment, the phone cannot approve `sudo` or
logins, which the owner wants on the phones and the tablet.

### Facts from the code

- **The key** (`android/app/src/main/java/org/omarchy/flux/core/ApproveKeys.kt:83-104`):
  - EC P-256 for signing;
  - a strong biometric for each use: `setUserAuthenticationParameters(0,
    AUTH_BIOMETRIC_STRONG)` on Android 11+, a validity of -1 s on Android 10;
  - `setInvalidatedByBiometricEnrollment(true)`, so any new biometric,
    fingerprint or face, invalidates it.

  It's made in **StrongBox** when the phone has it, else without StrongBox
  (`ApproveKeys.kt:62-66`). The code never checks where the key really
  lives (`KeyInfo`), and `Pending` doesn't record it. `SshKeys.kt:72-86`
  already reads the security level for SSH keys.
- **The flow:**
  1. `start()` (`ui/ApproveActivity.kt:278-338`) makes the key, runs
     `initSign` and opens `BiometricPrompt` with a `CryptoObject`. The
     prompt allows `BIOMETRIC_STRONG` only, so a strong face also counts.
  2. `onAuthenticationSucceeded` (`ApproveActivity.kt:89-109`) runs `update`
     and `sign`, and that's where -26 (`KEY_USER_NOT_AUTHENTICATED`) is
     thrown.

  So the prompt succeeded, but Keystore did not accept an authentication
  for this operation.
- **The error text is cut.** `ApproveMessage.failure` (`core/ApproveMessage.kt:169-176`)
  and `failed` (`:162`) both cut it to 200 characters. The report lost the
  rest of the Keystore message and any `Caused by`. A wire field must stay
  at most 256 bytes of valid UTF-8 without control characters
  (`ApproveMessage.kt:38,47-55`), or `fluxd` drops it.
- **Phone logs:** the phone logs the full exception
  (`ApproveActivity.kt:105`), without the key's security level, the
  Android version or the build.
- **Timeouts:** an enrollment waits 120 s (`internal/approve/enroll.go:15`).
  An approval waits 20 s by default (`approve_timeout`).

### Possible causes

All unverified. -26 at `update` or `sign` after a successful prompt can
mean any of these:

1. **The authentication isn't accepted** for a StrongBox key while the TEE
   accepts it. The token signing shared between the biometric hardware and
   StrongBox is broken on some OEM builds. If this is the cause, a TEE key
   works.
2. **The authentication doesn't reach the operation:** `keystore2` finds no
   matching token before it calls the hardware. This affects TEE and
   StrongBox keys alike.
3. **The authenticator IDs don't match.** `setInvalidatedByBiometricEnrollment(true)`
   binds the key to the authenticator IDs of the enrolled biometrics. A
   vendor biometric that reports a different or zero ID, for example face
   against fingerprint, fails even with a TEE key. The SSH key is not bound
   to them (`SshKeys.kt:124-129`).
4. **The operation is mishandled:** a hardware implementation that handles
   a per-use token badly across `update` and then `finish`, or an
   operation pruned while the prompt waits. Pruning usually gives -28, not
   -26.

The reporter's phone model, Android version and whether it has StrongBox
are unknown.

### The owner's Pixel 11 Pro XL

- API 37, with StrongBox (`android.hardware.strongbox_keystore=400`).
- A strong fingerprint sensor and a strong face sensor.
- Flux is not installed now, and p620 has no `/etc/flux`.
- Whether enrollment fails on it is not yet tested.

## Proposed outcome

1. **The cause is known before the fix.**
   - Reproduce on the Pixel with today's code, with fingerprint and with
     face.
   - The phone logs the full exception chain, the key's security level,
     the Android version and the build.
   - If the Pixel doesn't fail, the fix rests on the cause these logs
     support. It is then tested by forcing the failure in a debug build,
     and stated as unverified on affected hardware.
2. **Diagnostics.** When signing fails, the message to the computer is a
   short, structured line within the 256-byte field limit:
   - what failed (make, init, sign);
   - where the key was (StrongBox, TEE, software, unknown);
   - the exception class and the numeric Keystore code.

   The phone keeps the full chain in its log.
3. **Fallback, only if cause 1 holds** (or can't be ruled out, and the
   owner agrees). During an enrollment, a -26 from a key that the phone
   verified as StrongBox-backed triggers one retry:
   - a new key, verified as TEE-backed (`KeyInfo`), with the same rules;
   - a new `Signature`, and a second prompt with the same settings;
   - on a TEE key that fails as well, the enrollment ends with the
     diagnostic line.
4. **Approvals keep working** with the key that was enrolled, including
   after the app restarts.
5. **`docs/approve.md`** says when the phone uses StrongBox and when it
   falls back.

## Affected users and systems

- Every Android phone and tablet that enrolls for approvals.
- `android/app/.../core/ApproveKeys.kt`, `ui/ApproveActivity.kt`,
  `core/ApproveMessage.kt` and their unit tests.
- `docs/approve.md`.
- Not affected: `fluxd`, the approval protocol, the PAM module, iOS,
  macOS, Windows and the SSH keys.

## Constraints

- **The authentication rules stay the same:** a strong biometric for each
  use, no device PIN, no time window, and invalidated by a new biometric.
  - A TEE key gives up StrongBox's extra isolation from the main
    processor. That is the trade-off, and it applies only to phones where
    StrongBox fails.
  - A key that isn't verified as hardware-backed (software or unknown) is
    refused, as on iOS and macOS.
- **The fallback runs only:**
  - during an enrollment, never during an approval, because the computer
    pins the enrolled public key;
  - once per enrollment;
  - on -26 from a verified StrongBox key.

  Cancel, error, timeout, an invalidated key or any other failure ends the
  enrollment as today.
- The second prompt keeps `BIOMETRIC_STRONG`, `setConfirmationRequired(true)`
  and the overlay checks.
- A failed or cancelled enrollment keeps the current key (`ApproveKeys.create`
  and `commit`). Today's limit stays: the phone commits once it sent the
  key, so a wrong key code on the computer leaves no working key until the
  next enrollment (`docs/approve.md:117-125`).
- No protocol change: every field stays a valid field, so a computer with
  an earlier `fluxd` still gets the error.
- **The SSH key stays out of scope.** It is a different case: its key is
  published before any signature (`SshKeys.kt:99-109`), so replacing it on
  failure would change a public key that servers already trust.

## Open questions

1. **Fallback only after the failure, or never StrongBox for approval
   keys?**
   - Proposed: only after the failure, within the constraints above, so
     phones where StrongBox works keep it.
   - TEE only is simpler (one prompt, no retry), but gives up StrongBox on
     every phone.
   - Either way, only if the logs from outcome 1 support cause 1.
2. **The test setup on p620 and the Pixel.** Proposed:
   - Run `fluxd` as the owner's user with `nix run` from this repo, and
     pair the Pixel.
   - Enroll with `sudo flux-cli approve enroll`. That writes
     `/etc/flux/approve/olafkfreund.pub` and creates `/etc/flux`, which
     doesn't exist today.
   - Test an approval without PAM: run the `flux-approve` helper directly
     as the user, with `PAM_USER`, `PAM_SERVICE=sudo` and `PAM_TTY` set
     (`docs/approve.md:270-290`).
   - Test enroll and approve with fingerprint and with face, after an app
     restart, and a cancel of the second prompt in the forced-fallback
     debug build.
   - Clean up:
     - `sudo flux-cli approve remove`;
     - `rmdir` of `/etc/flux/approve` and `/etc/flux` if they are empty;
     - unpair, and remove the `fluxd` state the test made in the owner's
       home, keeping `~/.local/share/flux/browser-extension`, which exists
       today;
     - uninstall the debug app.

     The NixOS config and `/etc/pam.d` don't change.
3. **The SSH key:** out of scope, for the reason in Constraints.

## Result

Tested on 2026-11-13 with the master debug build (`build.yml` run
43329559309), `fluxd` from this repo on p620, and the approval helper run
directly with the `PAM_*` variables.

| Device | StrongBox | Enrollment | Approval |
| --- | --- | --- | --- |
| Pixel 11 Pro XL, Android 17 | KeyMint on Titan M (`keymint-service.citadel` in the log) | fingerprint and face: signed | fingerprint, after an app restart: approved |
| Galaxy Tab S8 Ultra (SM-X900), Android 16 | Keymaster 4.0 (`keymaster@4.0-strongbox-service-qti`) | fingerprint: signed | fingerprint: approved |

Neither device gives -26, so cause 1 is not confirmed on the owner's
devices, and nothing here points to another cause. The owner decided to
close #13 as not reproducible, with no code change. No spec or plan
follows.

The tablet test found a separate bug: the floating S Pen Air Command
button over the Approve button makes Flux refuse the tap as an overlay
(`ApproveActivity.kt:279-282`). That is tracked in its own issue.
