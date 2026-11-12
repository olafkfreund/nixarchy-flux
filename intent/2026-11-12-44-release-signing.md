---
status: draft
issue: 44
author: olafkfreund
---

# Intent: Signed fork releases for the desktop and the Android app

## Problem

- **No signature check.** `internal/release/sign.go:16` has
  `const PublicKey = ""`. So `release.Fetch` checks a download only against
  `SHA256SUMS`, never against `SHA256SUMS.sig`
  (`internal/release/download.go:188-196`). Anyone who can replace a release
  file and its checksum gets it installed:
  - on Arch, `flux-cli update` installs the package as root
    (`cmd/flux/update.go:134-136`);
  - `flux-cli update --phone` and fluxd send the Android app from a release
    to the phones.
- **The releases come from upstream, not from this fork.**
  `internal/release/release.go:23,27` reads
  `https://api.github.com/repos/bjarneo/flux/releases/latest` and downloads
  from `github.com/bjarneo/flux/releases/download/`. Upstream's v0.19.1 has
  no `SHA256SUMS.sig` (checked with `gh release view -R bjarneo/flux`). So
  this fork cannot sign what its own binaries install, and its updater
  installs upstream builds that do not contain this fork's changes.
- **The fork has never released.** It has no release and no `release`
  environment, so none of the secrets that `release.yml` expects exist:
  - `KEYSTORE_BASE64`, `KEYSTORE_PASSWORD`, `KEY_ALIAS`, `KEY_PASSWORD` for
    the APK;
  - `RELEASE_SIGNING_KEY` for `SHA256SUMS`.
- **The Android signing key is permanent.** The app keeps upstream's ID,
  `org.omarchy.flux` (`android/app/build.gradle.kts:38`). Android installs an
  update only when it is signed with the same key as the installed app. So
  the key that signs the first APK installed on the owner's phones and
  tablets must sign every later release, and must never be lost.

## Proposed outcome

- Every tagged release of this fork, `v*` on `olafkfreund/nixarchy-flux`,
  is built and signed by `release.yml`:
  - the APK with the fork's Android keystore;
  - `SHA256SUMS` with the fork's Ed25519 key.
- `PublicKey` in `internal/release/sign.go` holds the fork's Ed25519 public
  key. `flux-cli update`, fluxd and `update --phone` refuse a release whose
  `SHA256SUMS.sig` is missing or wrong.
- Releases are fetched from the fork. The two URLs in `release.go` point at
  `olafkfreund/nixarchy-flux`, and `FLUX_RELEASES_URL` still overrides them.
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
  releases. NixOS installs through the flake and is not affected (#40).

## Constraints

- No private key, password or keystore in the repo, in an agent's prompt, or
  in this conversation. Agents may write the instructions; the owner runs
  the commands that create and upload keys.
- `release.yml` keeps its split: the `apk` job builds with no secrets, and
  `apk-sign` signs with no Gradle (`docs/releasing.md` "Android release
  key").
- The fork must not publish to upstream's AUR package. `aur.yml` and its
  `publish` job have to stay off, or have no secret, in the fork.
- A build with a `PublicKey` refuses an unsigned release. So `PublicKey` is
  set only once a signed release exists, or in the same release that the
  `sign` job signs. `docs/releasing.md` gives this order (steps 1-5).

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
3. **AUR.** Proposed: turn off the fork's `aur.yml` publish job, since the
   fork has no AUR package. Do you want a fork AUR package instead?
4. **Release reviewer.** Add yourself as a required reviewer on the
   `release` environment, so that each signing job waits for your approval?
   Proposed: yes.
