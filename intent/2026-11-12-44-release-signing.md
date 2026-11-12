---
status: approved
issue: 44
author: olafkfreund
---

# Intent: Signed fork releases for the desktop and the Android app

Revised after the Codex and agy reviews of the first draft.

## Problem

- **No signature check.** `internal/release/sign.go:16` has
  `const PublicKey = ""`. So `release.Fetch` checks a download only against
  `SHA256SUMS`, never against `SHA256SUMS.sig`
  (`internal/release/download.go:188-199`). Anyone who can replace a release
  file and its checksum gets it past `Fetch`:
  - on Arch, `flux-cli update` installs it as root with `sudo pacman -U`
    (`cmd/flux/update.go:139-162`);
  - `flux-cli update --phone` and fluxd *send* the APK to the phones. Android
    installs it only if it is signed with the installed app's key, so for the
    APK the risk is a downgrade or an upstream build, not someone else's
    code.
  - If the release has no package for the machine, `flux-cli update` falls
    back to `yay`/`paru -S omarchy-flux` (`update.go:113-121`). That builds
    upstream's AUR package and skips the signature check entirely.
- **The releases come from upstream, not from this fork.**
  `internal/release/release.go:23,27` reads
  `https://api.github.com/repos/bjarneo/flux/releases/latest` and downloads
  from `github.com/bjarneo/flux/releases/download/`. Upstream's v0.19.1 has
  no `SHA256SUMS.sig` (checked with `gh release view -R bjarneo/flux`). So
  this fork cannot sign what its own binaries install, and its updater
  installs upstream builds that do not contain this fork's changes.
- **The fork has never released.** On GitHub it has no release and no tags
  (`git ls-remote --tags origin` lists none), so a fork release cannot work
  yet. Whether a `release` environment or any repository secrets exist could
  not be checked from the code; the owner confirms it in the settings.
  `release.yml` needs:
  - `KEYSTORE_BASE64`, `KEYSTORE_PASSWORD`, `KEY_ALIAS`, `KEY_PASSWORD` for
    the APK. Without them `apk-sign` fails (`release.yml:150`).
  - `RELEASE_SIGNING_KEY` for `SHA256SUMS`. While `PublicKey` is empty, a
    missing key only gives a warning (`release.yml:411-418`).
- **`release.yml` pins upstream's APK certificate.** `CERT_SHA256` at
  `release.yml:135` is upstream's certificate digest (`a9af3fb3…0884`), and
  `apk-sign` fails when the APK's signer is a different one (lines 178-184).
  With the fork's keystore, every release fails until this value is the
  fork's certificate digest.
- **Other upstream links.** These still point at `bjarneo/flux`:
  - `cmd/flux/oldapp.go:6`, the latest-release link;
  - the Android app's About and onboarding links (`AboutScreen.kt:25-31`,
    `Onboarding.kt:30`);
  - the macOS pairing guide (`macos/App/Chat/PairGuide.swift:59`);
  - `site/index.html`;
  - `docs/releasing.md:274` (`REPO=bjarneo/flux`).
- **The Android signing key is the app's lasting identity.** The app keeps
  upstream's ID, `org.omarchy.flux` (`android/app/build.gradle.kts:38`).
  Android installs an update only when it is signed with the same key as the
  installed app. So the key that signs the first APK on the owner's phones
  and tablets has to sign every later release, and must never be lost.
  - It can be replaced on purpose later with a v3 signing lineage
    (`docs/releasing.md:126`), but only while the old key still exists.
  - `apk-sign` today accepts only one signer (`release.yml:181`).
- **The first install cannot go through `update --phone`.** fluxd sends an
  update only to a phone that is paired, runs the Android app, and has an
  older version (`internal/core/update.go:208-246`). A phone that has just
  uninstalled upstream's app is none of these, so the first fork APK has to
  be installed by hand.

## Proposed outcome

- Every release of this fork is built and signed by `release.yml`. A release
  is a stable `vMAJOR.MINOR.PATCH` tag on a commit of `master`
  (`release.yml:33,60`) on `olafkfreund/nixarchy-flux`:
  - the APK with the fork's Android keystore;
  - `SHA256SUMS` with the fork's Ed25519 key.
- `PublicKey` in `internal/release/sign.go` holds the fork's Ed25519 public
  key. `flux-cli update`, fluxd and `update --phone` refuse a release whose
  `SHA256SUMS.sig` is missing or wrong.
  - `flux-cli update` no longer falls back to upstream's AUR package once a
    key is set.
  - `PublicKey` is set **before** the first tag. That first release is then
    signed, and it checks the signature of every later one, so nothing is
    left unchecked in between. The fork has no earlier release that the key
    could break.
- `CERT_SHA256` in `release.yml` holds the fork keystore's certificate
  digest. The owner gives the digest, which is public; `docs/releasing.md`
  says how to read it from the keystore.
- **First version.** The first fork release is newer than every upstream
  version on the devices, that is newer than v0.19.1. Proposed: v0.20.0.
  Otherwise `release.Newer` (`release.go:144-156`) never offers it.
- Releases are fetched from the fork. `DefaultURL` and `downloadURL` in
  `release.go` point at `olafkfreund/nixarchy-flux`. `FLUX_RELEASES_URL`
  still overrides the metadata address, and `official()` (line 118) keeps
  accepting only assets under `downloadURL`. The other upstream links above
  point at the fork too.
- **Keys** (owner's decision):
  - The owner creates both private keys on the desktop, with commands from
    `docs/releasing.md`, and backs them up in 1Password.
  - The owner uploads them as secrets of the GitHub Actions environment
    `release`, scoped to `v*` tags and `master`.
  - No private key is committed, pasted into a chat, or read by an agent.
    Only the public key goes into the repo.
- `docs/releasing.md` describes the fork's setup: the environment, the
  secrets, the 1Password backup, and the first release.
- The first signed release, built from `master` once the bugs before the
  Android rollout are fixed, is the APK that goes on the phones and tablets.
  `docs/releasing.md` gets the steps for the first install, on each device:
  1. Download the APK, `SHA256SUMS` and `SHA256SUMS.sig` from the fork's
     release.
  2. Check `SHA256SUMS.sig` with the public key that the owner kept, then the
     APK against `SHA256SUMS`.
  3. Compare the APK's certificate (`apksigner verify --print-certs`) with
     the digest that the owner wrote down when creating the keystore.
  4. Only then uninstall upstream's app.
  5. Install the fork's APK (`adb install` or a file transfer), and pair
     again.

  From then on, `update --phone` delivers updates.

## Affected users and systems

- `internal/release/release.go`, `internal/release/sign.go`, their tests,
  `docs/releasing.md`, and possibly `.github/workflows/release.yml` and
  `aur.yml`.
- The owner's GitHub repo settings: the `release` environment, its secrets
  and the ref rules. Only the owner can change these.
- **Phones and tablets that have upstream's Flux installed.** It is signed
  with upstream's key, so the fork's APK cannot be installed over it. Each
  device must uninstall upstream's app once, which loses its pairing, and
  then install the fork's.
- Arch desktops that run `flux-cli update`. They switch to the fork's
  releases.
- NixOS desktops. They install fluxd through the flake (#40), but
  `update --phone` runs before that guard (`cmd/flux/update.go:27`), so their
  fluxd's release address and public key decide what the phones get.

## Constraints

- No private key, password or keystore in the repo, in an agent's prompt, or
  in this conversation. Agents may write the instructions; the owner runs
  the commands that create and upload keys.
- `release.yml` keeps its split: the `apk` job builds with no secrets, and
  `apk-sign` signs with no Gradle (`docs/releasing.md` "Android release
  key").
- The fork must not publish to upstream's AUR package. Today the `aur` job
  runs only when `vars.AUR_PUBLISH == 'true'` (`release.yml:476`), and
  `aur.yml:66` clones upstream's `omarchy-flux.git`.
- A release run needs every platform job to pass: `files` waits for
  `build`, `apk-sign`, `ipa` and `windows` (`release.yml:280`). None of them
  needs a secret the fork lacks (iOS unsigned, macOS ad hoc, Windows without
  Authenticode), but any failure blocks the Android release.
- With a required reviewer, each release asks for approval twice: once for
  `apk-sign` and once for `sign`.
- A build with a `PublicKey` refuses an unsigned release. So
  `RELEASE_SIGNING_KEY` is in the `release` environment before the first tag
  that has `PublicKey` set.

## Open questions

1. **Where updates come from.** Proposed: point `DefaultURL` and
   `downloadURL` at `olafkfreund/nixarchy-flux`. The alternative is to keep
   upstream and set no key. Then the check stays off, and the phones get
   upstream's APK with upstream's key, which cannot update the fork's app.
2. **App ID.** Keep `org.omarchy.flux` (proposed), so the
   companion-protocol, Keystore and notification code stays as it is, and
   uninstall upstream's app once on each device? Or use a new ID such as
   `org.nixarchy.flux`? A new ID can sit next to upstream's app, but the
   code that uses the ID needs checking.
3. **AUR.** Proposed: remove the `aur` job from the fork's `release.yml`, so
   that setting the variable by mistake cannot push to upstream's package.
   Also stop the `yay`/`paru` fallback in `flux-cli update`. Or do you want a
   fork AUR package instead?
4. **Release reviewer.** Add yourself as a required reviewer on the
   `release` environment, so that each signing job waits for your approval
   (twice per release)? Proposed: yes, together with rulesets on `master`
   and `v*` tags.
5. **First version.** v0.20.0 (proposed), or another number above v0.19.1?
