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

Revised after the Codex and agy reviews of the first draft (see "Review
changes" at the end).

## Design

### 1. Release source (`internal/release/release.go`)

- `DefaultURL` becomes
  `https://api.github.com/repos/olafkfreund/nixarchy-flux/releases/latest`.
- `downloadURL` becomes
  `https://github.com/olafkfreund/nixarchy-flux/releases/download/`.
- `URL()`, `allowedURL()` and `official()` are unchanged, so an asset outside
  `downloadURL` is still dropped.
- `release_test.go` and `cmd/flux/update_test.go` build their fixtures from
  `downloadURL`, or switch to the fork's address.
- `TestOfficial` (`release_test.go:101-117`) keeps its valid case and its 4
  malformed cases, all moved to the fork's prefix. It **adds** a 6th asset,
  an otherwise valid
  `https://github.com/bjarneo/flux/releases/download/v0.7.0/…` URL, which
  must be dropped. That shows upstream assets are refused for their
  repository alone.

### 2. Public key (`internal/release/sign.go`)

- `const PublicKey = "<owner's Ed25519 public key, base64>"`.
- The comment says it is the key of the fork's releases. The key comes from
  `go run ./scripts/signsums keygen` (`scripts/signsums/main.go:59`).
- This value has to come from the owner (see Order below). The code does not
  change otherwise:
  - `Signed()` becomes true, and `Fetch` refuses a release without a valid
    `SHA256SUMS.sig` (`download.go:188-199`);
  - `scripts/signsums` `key`, `sign` and the one-argument `verify` use it
    (`main.go:42-50`).
- A new test, `TestPublicKey` in `internal/release/`, checks that
  `PublicKey` is set, decodes from base64 to exactly 32 bytes, and verifies a
  signature only with the matching key. Today no test reads `PublicKey`; this
  test stops a typo, or an empty key, from merging.

### 3. No AUR fallback (`cmd/flux/update.go:113-121`)

- `installPackage` without a package for this architecture returns the
  error `the release %s has no %s package, and Flux has no AUR package to
  build it from. See %s`, with the tag, the architecture and `r.Page`, in
  the style of line 111.
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
- `README.md`;
- `docs/install.md`, `docs/troubleshooting.md`, `docs/ios.md`, `docs/macos.md`,
  `docs/windows.md`, `docs/releasing.md`.

Two kinds of reference stay:

- references in the text that name upstream as the origin of the fork, such
  as "a fork of bjarneo/flux";
- tests that check an upstream URL is refused (`cmd/flux-native/main_test.go:287`
  tests a URL list for allowed hosts, so it may keep `github.com/bjarneo/flux`
  as an ordinary GitHub URL).

Also staying:
- `docs/macos-plan.md` and `docs/macos-status.md`, which are history;
- **`site/`**, which is upstream's website. The fork has no GitHub Pages
  (`gh api repos/olafkfreund/nixarchy-flux/pages` answers 404). A change
  under `site/` would start `pages.yml`, which would fail on `master`. A fork
  website is out of scope.

### 6. `docs/releasing.md`

Make the whole guide match the two decisions, signing required and no AUR:

- Remove "AUR setup" (lines 159-197) and the AUR steps in "First release
  setup" (lines 23-31).
- Update the AUR mentions at lines 7, 15, 346 and 428-429, saying that the
  fork does not publish to AUR.
- Change the text that calls the signature optional (line 327) and the step
  that skips the check without a key (line 404): a fork release always has
  `SHA256SUMS.sig`.
- `REPO=bjarneo/flux` (line 274) becomes the fork.

Then rewrite "Release environment", "Android release key" and "Release
signing key" for the fork, in this order:

1. **Release environment.** Create `release` with the rules `v*` and
   `master`, add the owner as required reviewer, and add the rulesets. Note
   that each release asks for approval twice, once for `apk-sign` and once
   for `sign`.
2. **Android keystore.** Create it with the existing `keytool` command.
   - Back up the `.jks` file and both passwords in 1Password: one item, with
     the keystore attached.
   - Upload the four secrets: `KEYSTORE_BASE64`, `KEYSTORE_PASSWORD`,
     `KEY_ALIAS` and `KEY_PASSWORD`. With a PKCS12 keystore, the default,
     `KEY_PASSWORD` is the same as the keystore password.
   - Print the certificate digest with
     `LC_ALL=C keytool -list -v -keystore … -alias flux | sed -n 's/.*SHA256: //p' | tr -d : | tr A-F a-f`,
     and check that it is exactly 64 hex characters.
   - The digest is public. The owner keeps it with the 1Password item, and
     gives it for `CERT_SHA256`.
3. **Release signing key.** Run
   `go run ./scripts/signsums keygen "$HOME/.local/share/flux-release/release-signing.key"`,
   back up the key file in 1Password, then upload `RELEASE_SIGNING_KEY`. The command prints the
   public key, which is also public; the owner keeps it, and gives it for
   `PublicKey`.
4. **First release (v0.20.0).** Only after steps 1-3, and after `PublicKey`
   and `CERT_SHA256` are on `master`: tag `v0.20.0` on `master`. Approve both
   jobs, then check the release with `signsums verify`.
5. **First install on each phone and tablet.**
   1. From the checkout, `dir=$(mktemp -d)`, then
      `gh release download -R olafkfreund/nixarchy-flux v0.20.0 -D "$dir" -p 'SHA256SUMS*' -p '*.apk'`.
   2. Run `go run ./scripts/signsums verify "$dir/SHA256SUMS" <public key>`,
      then `(cd "$dir" && sha256sum --check --ignore-missing SHA256SUMS)`.
   3. Run `apksigner verify --print-certs "$dir/flux-android-0.20.0.apk"` and
      compare its `Signer #1 certificate SHA-256 digest` with the keystore
      digest the owner kept in step 2 (Android keystore). Do not use the
      `android-certificate.txt` from the same download.
   4. Only then uninstall upstream's Flux on the device.
   5. `adb install flux-android-0.20.0.apk`, or copy the file and open it.
   6. Pair the device again, and enroll `flux-approve` again.
6. **Desktops.**
   - A NixOS desktop rebuilds from a `master` that contains this change, so
     its fluxd has the key before it ever reads a fork release.
   - An Arch desktop on upstream's package still has upstream's addresses
     built in. It installs the fork's v0.20.0 package once by hand, after
     the same checks as in step 5, with `sudo pacman -U`.
7. **Later releases.** `flux-cli update --phone` sends them, and fluxd
   checks the signature first.

The old text about key rotation (v3 lineage) stays.

### Order (code versus owner)

**One PR, merged only with the keys in it.** A build without a key never
checks a signature, and setting the key later does not change binaries that
are already built. So no commit on `master` may have the fork's address
without the fork's key.

1. **Code.** Sections 1 and 3-6, and `TestPublicKey`, are written, tested and
   reviewed on the branch. `PublicKey` is `""` and `CERT_SHA256` is
   upstream's. `TestPublicKey` fails on purpose until the key is in, so the
   PR cannot merge without it.
2. **Owner.** Doc steps 1-3: the environment, the rulesets, the keys,
   1Password and the secrets. Then the owner gives the two public values,
   the Ed25519 public key and the certificate digest.
3. **Keys in.** The same PR sets `PublicKey` and `CERT_SHA256` and runs the
   full verification (below).
4. **Merge.** Only now. Every build from `master` after this point has the
   fork's address and key together.
5. **Owner.** Tags v0.20.0 once #13, #50, #25 and #23 are merged, then
   follows doc steps 5-6.

## Alternatives rejected

- **Keep upstream's releases.** Upstream does not sign them, and their APK
  carries upstream's key, which cannot update the fork's app.
- **A new app ID.** It would mean checking every place that uses
  `org.omarchy.flux`, and more conflicts in each upstream merge. The owner
  chose to uninstall upstream's app instead.
- **Two PRs: code first, keys later.** The first draft did this. A desktop
  built between the two would check nothing, even after the second PR.
- **A placeholder key.** It would turn the check on with a wrong key, and
  every update would be refused.
- **Changing the website links.** The fork has no Pages site, so the change
  would only make `pages.yml` fail.
- **Agents create the keys.** That breaks the rule that no agent handles a
  private key, and the key would exist outside 1Password.
- **Keep `aur.yml` callable behind the variable.** One wrong variable would
  push to upstream's AUR package.

## Risks

- **A lost keystore.** The installed apps cannot be updated without
  uninstalling them. 1Password is the backup, and the docs say so in bold.
- **A mistyped `CERT_SHA256` or `PublicKey`.** `TestPublicKey` catches a key
  that does not decode. A wrong key or digest makes `apk-sign` or `sign` fail
  in the release run, before anything is published, because `signsums sign`
  checks the secret against `PublicKey` (`release.yml:422`).
- **First release notes.** The fork has no earlier tag, so the notes step
  (`release.yml:352-361`) lists every commit on `master`: 368 today, about
  74 kB. That is below GitHub's 125,000-character limit, and below the rate
  limit for one call per commit. Long, but it works. Later releases list only
  the commits since the previous tag.
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
6. With the keys in (Order step 3), before the merge:
   - `go test ./...`;
   - `make vet test`, compared with `master`;
   - `build.yml` dispatched again;
   - `go run ./scripts/signsums key`, which prints the owner's public key.
7. The owner's v0.20.0 release run passes `apk-sign` and `sign`, and
   `signsums verify` of the published `SHA256SUMS` succeeds.

## Review changes

| Finding | Change |
| --- | --- |
| Codex 1, agy 4.1: a desktop built between two PRs would never check a signature | One PR, merged only once the keys are in it (Order). `TestPublicKey` blocks a merge without them. |
| agy 2: no test reads `PublicKey`; the post-key checks were too narrow | Added `TestPublicKey`. With the keys in, the full suite and CI run. |
| Codex 2: the secret names were shorthand | The four names are written out, plus the PKCS12 password note. |
| Codex 3, agy 5: other parts of the guide still describe AUR and optional signing | The whole guide is made consistent, with the lines listed. |
| agy 4.2: `site/` changes start `pages.yml`, which fails without Pages | `site/` is out of scope. |
| agy 4.3: the desktop switch was missing | Added doc step 6. |
| agy 6: `TestOfficial` lost its malformed cases | The cases are kept, and one upstream case is added. |
| Codex 4, agy 7: the commands were ambiguous | Added the `keygen FILE` path, a temporary download directory, the digest source, and `LC_ALL=C` with a 64-hex check. |
| agy 1: the first release notes walk all of history | Kept. 368 commits fit the limits (Risks). |
| agy 3: `prepare-aur.py` targets upstream | Refuted. `build.yml:57` passes `github.repository`, which is the fork. |

