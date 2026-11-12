---
status: draft
issue: 44
intent: intent/2026-11-12-44-release-signing.md
---

# Spec: Signed fork releases for the desktop and the Android app

The owner approved the intent with the proposed answers:
- releases come from the fork;
- the app keeps the ID `org.omarchy.flux`, and upstream's app is uninstalled
  once on each device;
- there is no fork AUR package, so the `aur` job and the `yay`/`paru`
  fallback go;
- the owner is a required reviewer on the `release` environment, and
  rulesets protect `master` and `v*`;
- the first release is v0.20.0.

The private keys are created by the owner, backed up in 1Password and stored
as secrets of the `release` environment. No agent sees them.

## Design

### 1. Release source (`internal/release/release.go`)

- `DefaultURL` becomes
  `https://api.github.com/repos/olafkfreund/nixarchy-flux/releases/latest`.
- `downloadURL` becomes
  `https://github.com/olafkfreund/nixarchy-flux/releases/download/`.
- `URL()`, `allowedURL()` and `official()` are unchanged, so an asset outside
  `downloadURL` is still dropped.
- `release_test.go` and `cmd/flux/update_test.go` build their fixtures from
  `downloadURL`, or switch to the fork's address. The test of `official()`
  keeps one upstream URL, which must now be **dropped**. That proves upstream
  assets are refused.

### 2. Public key (`internal/release/sign.go`)

- `const PublicKey = "<owner's Ed25519 public key, base64>"`.
- The comment says it is the key of the fork's releases. The key comes from
  `go run ./scripts/signsums keygen` (`scripts/signsums/main.go:59`).
- This value has to come from the owner (see Order below). The code does not
  change otherwise: `Signed()` becomes true, and `Fetch` refuses a release
  without a valid `SHA256SUMS.sig` (`download.go:188-199`).

### 3. No AUR fallback (`cmd/flux/update.go:113-121`)

- `installPackage` without a package for this architecture returns the
  error `the release %s has no %s package, and Flux has no AUR package to
  build it from` with the release page.
- It no longer runs `yay` or `paru`.
- The comment above the function (lines 103-107) and its test in
  `update_test.go` change to match.

### 4. `release.yml`

- **Certificate.** `CERT_SHA256` (line 135) is the owner's certificate
  digest: lowercase hex, without colons, as `apksigner` prints it after
  `Signer #1 certificate SHA-256 digest:`. The comment says it is the fork's
  certificate.
- **No AUR job.** Remove the `aur` job (lines 472-484).
  - `aur.yml` stays in the tree for a possible upstream merge, but no
    workflow calls it.
  - The release still publishes `omarchy-flux-VERSION-aur.tar.gz` (the
    PKGBUILD), because `files` hashes it (line 328). Removing it is out of
    scope.
- Everything else is unchanged: the `apk` and `apk-sign` split, `sign`, and
  `publish`.

### 5. Links to the fork

Every link to `github.com/bjarneo/flux` becomes
`github.com/olafkfreund/nixarchy-flux`, and every `bjarneo.github.io/flux`
becomes `olafkfreund.github.io/nixarchy-flux`. They are in:

- `cmd/flux/oldapp.go:6`;
- `android/app/src/main/java/org/omarchy/flux/ui/AboutScreen.kt:25-31,82`;
- `android/app/src/main/java/org/omarchy/flux/ui/Onboarding.kt:30`;
- `macos/App/Chat/PairGuide.swift:59`;
- `site/index.html`;
- `README.md`;
- `docs/install.md`, `docs/troubleshooting.md`, `docs/ios.md`, `docs/macos.md`,
  `docs/windows.md`, `docs/releasing.md`.

Two kinds of reference stay:

- references in the text that name upstream as the origin of the fork, such
  as "a fork of bjarneo/flux";
- tests that check an upstream URL is refused (`cmd/flux-native/main_test.go:287`
  tests a URL list for allowed hosts, so it may keep `github.com/bjarneo/flux`
  as an ordinary GitHub URL).

`docs/macos-plan.md` and `docs/macos-status.md` are history and stay as
they are.

### 6. `docs/releasing.md`

Rewrite "Release environment", "Android release key" and "Release signing
key" for the fork, in this order:

1. **Release environment.** Create `release` with the rules `v*` and
   `master`, add the owner as required reviewer, and add the rulesets. Note
   that each release asks for approval twice, once for `apk-sign` and once
   for `sign`.
2. **Android keystore.** Create it with the existing `keytool` command.
   - Back up the `.jks` file and both passwords in 1Password: one item, with
     the keystore attached.
   - Upload the four `KEYSTORE_*` secrets.
   - Print the certificate digest with
     `keytool -list -v -keystore … -alias flux | sed -n 's/.*SHA256: //p' | tr -d : | tr A-F a-f`.
     It is public. The owner keeps it, and gives it for `CERT_SHA256`.
3. **Release signing key.** Run `signsums keygen` and back up the key file in
   1Password, then upload `RELEASE_SIGNING_KEY`. The command prints the
   public key, which is also public; the owner keeps it, and gives it for
   `PublicKey`.
4. **First release (v0.20.0).** Only after steps 1-3, and after `PublicKey`
   and `CERT_SHA256` are on `master`: tag `v0.20.0` on `master`. Approve both
   jobs, then check the release with `signsums verify`.
5. **First install on each phone and tablet.**
   1. Download `flux-android-0.20.0.apk`, `SHA256SUMS` and
      `SHA256SUMS.sig` with `gh release download -R olafkfreund/nixarchy-flux v0.20.0`.
   2. Run `go run ./scripts/signsums verify SHA256SUMS <public key>`, then
      `sha256sum --check --ignore-missing SHA256SUMS`.
   3. Run `apksigner verify --print-certs flux-android-0.20.0.apk` and
      compare the digest with the one from step 2.
   4. Only then uninstall upstream's Flux on the device.
   5. `adb install flux-android-0.20.0.apk`, or copy the file and open it.
   6. Pair the device again, and enroll `flux-approve` again.
6. **Later releases.** `flux-cli update --phone` sends them, and fluxd
   checks the signature first.

The old text about key rotation (v3 lineage) stays.

### Order (code versus owner)

The secrets and the two public values do not exist yet, so the work is split
in two:

1. **The PR, without keys.** The changes in sections 1 and 3-6, with
   `PublicKey` still `""` and `CERT_SHA256` still upstream's.
   - It builds and its tests pass.
   - Merging it installs nothing until a fork release exists. Until then the
     update check gets 404 from the fork (`release.go:108` returns an error
     for any status other than 200). fluxd and `flux-cli update` show that
     as a failed check, and nothing is downloaded. This lasts only until
     v0.20.0.
2. **Owner.** Doc steps 1-3: the environment, the keys, 1Password, and the
   secrets. Then the owner gives the public key and the certificate digest.
3. **A second, small PR.** It sets `PublicKey` and `CERT_SHA256` and runs the
   tests.
4. **Owner.** Tags v0.20.0 once the bugs before the rollout (#13, #50, #25,
   #23) are merged.

## Alternatives rejected

- **Keep upstream's releases.** Upstream does not sign them, and their APK
  carries upstream's key, which cannot update the fork's app.
- **A new app ID.** It would mean checking every place that uses
  `org.omarchy.flux`, and more conflicts in each upstream merge. The owner
  chose to uninstall upstream's app instead.
- **Set `PublicKey` in the first PR.** The value does not exist yet. A
  placeholder would turn the check on with a wrong key, and every update
  would be refused.
- **Agents create the keys.** That breaks the rule that no agent handles a
  private key, and the key would exist outside 1Password.
- **Keep `aur.yml` callable behind the variable.** One wrong variable would
  push to upstream's AUR package.

## Risks

- **A lost keystore.** The installed apps cannot be updated without
  uninstalling them. 1Password is the backup, and the docs say so in bold.
- **A mistyped `CERT_SHA256` or `PublicKey`.** `apk-sign` or `sign` fails in
  the release run, before anything is published, because `signsums sign`
  checks the secret against `PublicKey` (`release.yml:421`). The tests check
  that `PublicKey` decodes to 32 bytes, through the existing `CheckSignature`
  path.
- **Missing a link.** A grep for `bjarneo/flux` in the verification step
  lists every reference that is left, and each must be one of the kinds that
  stay (section 5).
- **Devices on upstream v0.19.1.** They are offered nothing until they are
  reinstalled by hand. That is the intended first install.
- **The release run fails on another platform** (iOS, macOS, Windows). The
  Android release is blocked until it passes. It is checked on the first run
  and fixed then; no secret is involved.

## Verification

1. `go vet ./... && test -z "$(gofmt -l .)"`, and
   `go test ./internal/release/ ./cmd/flux/ ./scripts/signsums/`, all pass.
2. `make vet test`: the same failures as `master` (the tests that depend on
   this host), and no new ones.
3. `grep -rn "bjarneo" --exclude-dir={intent,spec,plan} .`: every match is
   one of the kinds allowed in section 5.
4. `nix flake check path:.`.
5. CI `build.yml`, dispatched on the branch: all four jobs pass, including
   Android, which builds the changed `AboutScreen.kt` and `Onboarding.kt`.
6. After the second PR: `go test ./internal/release/` with the real
   `PublicKey`. The owner's v0.20.0 release run passes `apk-sign` and `sign`,
   and `signsums verify` of the published `SHA256SUMS` succeeds.
