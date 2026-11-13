---
status: draft
issue: 13
author: olafkfreund
---

# Intent: Fingerprint enrollment fails with "Key user not authenticated"

## Problem

On some Android phones, `sudo flux-cli approve enroll` always fails. The
phone shows the fingerprint prompt and the user touches the sensor, then
the command prints:

```
flux-cli: the phone could not make the key: The phone could not sign the request: android.security.KeyStoreException: Key user not authenticated (internal Keystore code: -26 message: system/security/keystore2/src/operation.rs:853: KeystoreOpera
```

This is upstream `bjarneo/flux#136`, still open there. Clearing the app's
data doesn't help. Without enrollment, the phone cannot approve `sudo` or
logins, which is one of the features the owner wants on the phones and the
tablet.

Facts from the code:

- **The key** (`android/app/src/main/java/org/omarchy/flux/core/ApproveKeys.kt:87-109`):
  an EC P-256 signing key with `setUserAuthenticationRequired(true)`, a
  strong biometric for each use (`setUserAuthenticationParameters(0,
  AUTH_BIOMETRIC_STRONG)` on Android 11+), and
  `setInvalidatedByBiometricEnrollment(true)`. It's made in **StrongBox**
  when the phone has it, else in the TEE (`ApproveKeys.kt:63-67`).
- **The flow** (`ui/ApproveActivity.kt:277-338`):
  1. make the key and `initSign` a `Signature`;
  2. `BiometricPrompt` with a `CryptoObject` for that `Signature`,
     `BIOMETRIC_STRONG` only;
  3. after `onAuthenticationSucceeded`, `update` and `sign`.

  Error -26 (`KEY_USER_NOT_AUTHENTICATED`) comes from step 3. So the
  prompt succeeded, but Keystore did not accept its authentication for this
  key.
- **The SSH key** (`core/SshKeys.kt:118-135`) uses the same pattern, also in
  StrongBox when available, but it also allows the device credential and
  isn't invalidated by a new biometric. No failure is reported for it.
- **The error text is cut** at 200 characters (`core/ApproveMessage.kt:169-176`),
  so the report lost the end of the Keystore message. The phone logs the
  full exception (`ApproveActivity.kt:105`), but the user doesn't see it,
  and it doesn't say whether the key was in StrongBox or the TEE.
- **The likely cause, unverified:** on some phones the strong-biometric
  authentication token is not accepted by the StrongBox chip for a key that
  needs authentication for each use, while the TEE accepts it. The common
  fix in other apps is to fall back to a TEE key when a StrongBox key fails
  this way. The reporter's phone model is unknown.
- **The owner's Pixel 11 Pro XL** (API 37) has StrongBox
  (`android.hardware.strongbox_keystore=400`), a strong fingerprint and a
  strong face sensor. Whether enrollment works on it is not yet tested.
- The earlier symptom in the issue, `flux-cli` waiting forever, was fixed
  upstream before the fork (upstream#138): the phone now sends the error at
  once.

## Proposed outcome

- On a phone where the StrongBox key cannot be used this way, enrollment
  still succeeds: the phone falls back to a TEE key, with the same
  rules (a strong biometric for each use, invalidated by a new biometric).
- Approvals with that key keep working afterwards.
- When signing fails anyway, the error tells the cause. It names where the
  key lives (StrongBox or TEE), and the Keystore reason is not cut off
  before the useful part.
- `docs/approve.md` says when the phone uses StrongBox and when it falls
  back.
- On the owner's Pixel, enrollment and an approval work end to end.

## Affected users and systems

- Every Android phone and tablet that enrolls for approvals.
- `android/app/.../core/ApproveKeys.kt`, `ui/ApproveActivity.kt`,
  `core/ApproveMessage.kt` and their unit tests.
- `docs/approve.md`.
- Not affected: `fluxd`, the approval protocol, the PAM module, iOS,
  macOS, Windows, and the SSH keys.

## Constraints

- No weaker security: still a strong biometric for each use, no device
  PIN, no time window, and still invalidated by a new fingerprint. A TEE
  key is hardware-backed, so the fallback keeps the key off the app's
  memory.
- No protocol change, so a computer with an earlier `fluxd` still accepts
  the enrollment.
- An enrollment that fails keeps the current key (`ApproveKeys.create`
  and `commit`).
- The test needs the desktop side: `fluxd` and `flux-cli approve enroll`
  on a computer. p620 has no Flux installed now.

## Open questions

1. **Fallback only after the failure, or never StrongBox for approval
   keys?**
   - Proposed: fall back after the -26 failure. The phone deletes the
     StrongBox key, makes a TEE key and asks for the fingerprint once more.
     Phones where StrongBox works keep it.
   - The alternative, TEE only, is simpler: one prompt and no retry path.
     But it gives up StrongBox on every phone.
2. **How to test on the Pixel?** Enrollment needs a real computer side:
   `fluxd`, then `sudo flux-cli approve enroll`, which writes
   `/etc/flux/approve/<user>` as root (`internal/approve/keyfile.go:21`).
   - Proposed: on p620, run `fluxd` as the user with `nix run` from this
     repo and pair the Pixel. Enroll with today's code first, then with
     the fix. Afterwards, delete `/etc/flux/approve/` and unpair. The NixOS
     config and `/etc/pam.d` don't change, so this tests the enrollment,
     not a `sudo` approval through PAM. That needs `services.flux` in the
     NixOS config, which is the owner's call.
   - If the Pixel fails with -26 today, that directly tests the fix. If
     it works, a debug-only switch forces the fallback for the test.
   - `tools/test_peer.py` has no approval requests, so it can't do this
     without new code.
3. **Also the SSH key?** Proposed: no, out of scope, since nothing is
   reported for it. Same pattern, so a follow-up issue if it shows up.
