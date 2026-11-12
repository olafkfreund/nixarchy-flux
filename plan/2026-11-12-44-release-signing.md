---
status: approved
issue: 44
spec: spec/2026-11-12-44-release-signing.md
coders:
  1: codex   # small test logic: a new key test, an upstream-refusal case, tests that must not rely on an empty key
  2: codex   # small control-flow change plus a test that must stop running yay
  3: coder   # two small YAML edits, no logic
  4: agy     # large mechanical link replacement across 11 files
  5: agy     # large mechanical rewrite of one long guide
  7: lead    # two public values from the owner; the lead types them in
reviewers:
  - codex    # diff review against this plan with `codex review --base master`
  - agy      # independent read of the docs and the release workflow
---

# Plan: Signed fork releases for the desktop and the Android app

Branch `fix/44-release-signing`. **One PR, merged only once the owner's keys
are in it, and squash-merged.** Use `gh ... -R olafkfreund/nixarchy-flux`
everywhere.

## Approved decisions (from the spec)

- The fork publishes the releases. `DefaultURL` and `downloadURL` point at
  `olafkfreund/nixarchy-flux`.
- The app keeps the ID `org.omarchy.flux`. Each device uninstalls upstream's
  app once.
- Signing is required:
  - `PublicKey` is the owner's Ed25519 key;
  - `CERT_SHA256` is the owner's Android certificate digest;
  - a new test, `TestPublicKey`, fails while the key is empty.
- There is no fork AUR package:
  - remove the `aur` job;
  - remove the `yay`/`paru` fallback;
  - `aur.yml` and the AUR tarball stay.
- The `release` environment has the owner as required reviewer. Rulesets
  protect `master` and `v*`.
- Links to the fork:
  - every `github.com/bjarneo/flux` link becomes
    `github.com/olafkfreund/nixarchy-flux`;
  - every `bjarneo.github.io/flux` link becomes
    `olafkfreund.github.io/nixarchy-flux`.
- These stay unchanged:
  - origin mentions;
  - upstream-refusal tests;
  - `cmd/flux-native/main_test.go:287`;
  - `docs/macos-plan.md` and `docs/macos-status.md`;
  - `site/`.
- The first release is v0.20.0. The owner tags it after #13, #50, #25 and #23.
- No agent sees a private key, a password or the keystore. The owner runs
  every key command.

## Planning corrections

| # | Spec text | Correction | Why |
| --- | --- | --- | --- |
| P1 | Verification 4 and 5: `nix flake check` and all four CI jobs pass before the keys are in | Before the keys are in, `nix flake check` and the CI `arch` job are expected to fail, **only** on `TestPublicKey`. `android`, `ios` and `macos` must pass. At step 8 everything must be green. | `nix/package.nix:66` runs `go test ./internal/...`, and `build.yml:69` runs `make vet test`. Both run the deliberate failure. |
| P2 | §5 file list | Add `windows/Flux/Chat/ChatPage.cs:323` (install guide) and `gui/qml/tools/Snapshot.qml:260` (snapshot fixture URL). Leave `windows/README.md:12` (upstream PR #92, history) and `.impeccable/surfaces/site-index-html.md` (describes `site/`). Because `build.yml` ignores `windows/**`, steps 6 and 8 also dispatch `windows.yml`. | §5's rule says "every link", and the spec's list missed these two. |
| P3 | §6 step 5.5: `adb install flux-android-0.20.0.apk` | `adb install "$dir/flux-android-0.20.0.apk"` | The download goes to `$dir`. The `cd` in step 5.2 runs in a subshell, so the bare filename would fail after upstream's app is already uninstalled. |
| P4 | Order step 1 says `TestPublicKey` keeps the PR from merging | `build.yml` does not run on pull requests (`build.yml:3`). So the merge gate is the lead's step 8, with the PR kept a draft until then, not CI. Squash-merge, so that no commit on `master` has the fork's address without the key. | Codex and agy both found this. |

## Implementation corrections

| # | Step | Change | Why |
| --- | --- | --- | --- |
| R1 | 1 | The dummy-key check restores `sign.go` from a backup, not with `git checkout`. | `git checkout` also reverted the step's own comment edit. |
| R2 | 4 | After the clone, `README.md:69` and `docs/install.md:31` run `cd nixarchy-flux` instead of `cd flux`. | The clone URL now creates a `nixarchy-flux` folder. |
| R3 | 4 | `docs/install.md`: the update text drops the `yay` fallback and the line about skipping the signature check. "Install from AUR" is replaced by a short note that the AUR package is upstream's, and "Update an AUR install" is removed. | Steps 1-2 and the no-AUR decision make that text wrong. The spec listed only the links in this file. |
| R4 | 5 | `docs/releasing.md`: cross-references use the guide's own section links instead of the spec's step numbers. The leftover "AUR job fails" paragraph is removed. | agy copied the spec's step numbers, which the guide does not have. |

## Steps

1. **`internal/release/`** (codex)
   - `release.go:23,27`: point `DefaultURL` at
     `https://api.github.com/repos/olafkfreund/nixarchy-flux/releases/latest`,
     and `downloadURL` at
     `https://github.com/olafkfreund/nixarchy-flux/releases/download/`.
   - `release_test.go:101-117` (`TestOfficial`):
     - Move the 5 existing asset URLs to the fork prefix. Each keeps its
       defect: http, wrong host, wrong tag, or wrong name.
     - Add a 6th asset, a valid-looking
       `https://github.com/bjarneo/flux/releases/download/v0.7.0/<name>`, and
       assert that it lands in `Dropped`.
   - New `sign_test.go`, `TestPublicKey`:
     - `PublicKey != ""`;
     - `base64.StdEncoding.DecodeString` gives `ed25519.PublicKeySize` bytes;
     - a signature from a freshly generated key pair verifies with that key
       and not with `PublicKey`.
   - `sign.go:10-15`, the comment: make it "the key of the fork's releases",
     and drop the "While PublicKey is empty" sentence. The value stays `""`
     until step 7.
   - Tests must not depend on the key being empty. Each test that fetches
     without a signature sets `releaseKey = ""` and restores it with
     `t.Cleanup`, as `download_test.go:101-103` does. Two known cases:
     - `download_test.go:149` (`TestFetch`, sig `""`);
     - `internal/core/update_test.go:212`, which branches on
       `release.Signed()`. Check both, and keep each test's meaning.
   - Check:
     - `go test ./internal/release/ -skip '^TestPublicKey$'` passes;
     - `go test ./internal/release/ -run '^TestPublicKey$'` fails, with a
       message naming `PublicKey`;
     - with a **temporary, uncommitted** dummy key in `sign.go`, generated by
       `go run ./scripts/signsums keygen "$(mktemp -u)"` and its file deleted
       after, `go test ./internal/release/ ./internal/core/ -run 'Fetch|Update|Official|PublicKey'`
       passes. Then `git checkout internal/release/sign.go`. The lead runs
       this again.
   - Traps:
     - grep `bjarneo` in `internal/release/`; only the new refusal case may
       be left;
     - don't change `download.go`.

2. **`cmd/flux/`** (codex)
   - `update.go:103-121`:
     - Rewrite the comment (lines 103-107) without the AUR helper.
     - Replace the `yay`/`paru` loop and its error with
       `fmt.Errorf("the release %s has no %s package, and Flux has no AUR package to build it from. See %s", r.Tag, packageArch(), r.Page)`.
   - `update_test.go`:
     - Move the `dir` const at line 120 to the fork prefix.
     - Update the stale comment at lines 110-111.
     - Replace the positive fake-`yay` case at lines 139-146
       (`TestInstallPackageNoAUR`) with a `Release` that has a `Page`. Assert
       that the error contains the tag, the architecture, `no AUR package`
       and the page, and that the fake `yay` mark file does not exist.
   - `oldapp.go:6`: point `latestRelease` at the fork.
   - Check:
     `go vet ./cmd/flux/ && go test ./cmd/flux/ -run '^(TestInstallPackageNoAUR|TestPackageAsset|TestOldAppHint)$'`.
   - Traps:
     - `exec` stays imported (lines 88, 211);
     - the `Dropped` error at line 111 stays first;
     - `TestRootInstall` fails on this host, as on master; leave it out of
       the check.

3. **`.github/workflows/release.yml`** (coder)
   - Delete the `aur` job and its two comment lines (lines 472-484).
   - Reword the existing comment above `CERT_SHA256` (lines 132-134) to say
     "the fork's Android release certificate (see docs/releasing.md)". Don't
     add a second comment. The value stays until step 7.
   - Check:
     - `! grep -nE 'aur\.yml|AUR_|needs:.*\baur\b' .github/workflows/release.yml`;
     - `nix run nixpkgs#yq-go -- e '.jobs | keys' .github/workflows/release.yml`
       parses, and lists no `aur`.
   - Trap: keep the `files` job's AUR tarball (lines 327-328).

4. **Links** (agy). Apply the §5 replacements in:
   - `android/app/src/main/java/org/omarchy/flux/ui/AboutScreen.kt:25-31,81-82`,
     including the visible labels `bjarneo.github.io/flux` and
     `github.com/bjarneo/flux`;
   - `android/app/src/main/java/org/omarchy/flux/ui/Onboarding.kt:30`;
   - `macos/App/Chat/PairGuide.swift:59`;
   - `windows/Flux/Chat/ChatPage.cs:323`;
   - `gui/qml/tools/Snapshot.qml:260`;
   - `README.md`;
   - `docs/install.md`, `docs/troubleshooting.md`, `docs/ios.md`,
     `docs/macos.md` and `docs/windows.md`.

   Check: `grep -rn bjarneo --exclude-dir={intent,spec,plan,site,.git} .`
   prints only the kinds that stay, plus `docs/releasing.md`, which step 5
   handles.

   Traps:
   - keep "a fork of bjarneo/flux"-style origin sentences;
   - don't edit `site/`, `docs/macos-plan.md`, `docs/macos-status.md`,
     `windows/README.md` or `.impeccable/`.

5. **`docs/releasing.md`** (agy). Self-contained; no need to open the spec.

   Remove:
   - "AUR setup" (lines 159-197);
   - step 6 of "First release setup" (line 30);
   - the `aur.yml` bullet in "Release environment" (line 47).

   Rewrite:
   - Lines 6-7, 15 and 20: the fork does not publish to AUR. The `aur.yml`
     row stays, with "not called by `release.yml`".
   - Lines 346 and 428-429: the same.
   - Line 274: `REPO=olafkfreund/nixarchy-flux`.
   - Line 327: `SHA256SUMS.sig` is always present.
   - Lines 404-405: drop "While `PublicKey` is empty …" and "… so skip the
     `verify` command". The check is never skipped.
   - Line 55: the required reviewer is mandatory, not "Optional".
   - Lines 116-122: replace upstream's digest literal with "the value of
     `CERT_SHA256` in `.github/workflows/release.yml`", so the digest lives
     in one place.
   - Add `-R olafkfreund/nixarchy-flux` to every `gh secret`, `gh workflow`,
     `gh run` and `gh release` command (lines 60, 67, 105-108, 221, 228, 338,
     391-392, 399), and to every new one.

   Keep:
   - the `apk`/`apk-sign` split;
   - the `FLUX_VERSION_CODE` text;
   - the local signed Gradle build (lines 111-115, 130-158);
   - the key-rotation (v3 lineage) text;
   - the TestFlight sections (431+).

   Write these sections, in this order:
   1. **Release environment** (line 41). Create `release` with the deployment
      ref rules `v*` and `master`. Add the owner as required reviewer. Add
      rulesets protecting `master` and `v*` tags. Each release asks for two
      approvals: one for `apk-sign` and one for `sign`.
   2. **Android release key** (line 78). Use the existing `keytool -genkeypair`
      command (lines 84-91).
      - **Back up the `.jks` file and both passwords in 1Password: one item,
        with the keystore attached.**
      - Upload the four secrets with the existing commands (lines 105-108):
        `KEYSTORE_BASE64`, `KEYSTORE_PASSWORD`, `KEY_ALIAS` and
        `KEY_PASSWORD`. With PKCS12, the default, `KEY_PASSWORD` equals the
        keystore password.
      - Print the digest with
        `LC_ALL=C keytool -list -v -keystore "$HOME/.local/share/flux-release/flux-release.jks" -alias flux | sed -n 's/.*SHA256: //p' | tr -d : | tr A-F a-f`.
        Check that it is exactly 64 hex characters.
      - The digest is public. Keep it in the 1Password item, and give it for
        `CERT_SHA256`.
   3. **Release signing key** (line 198).
      - Run
        `go run ./scripts/signsums keygen "$HOME/.local/share/flux-release/release-signing.key"`.
      - **Back it up in 1Password.**
      - Then run
        `gh secret set RELEASE_SIGNING_KEY -R olafkfreund/nixarchy-flux --env release < "$HOME/.local/share/flux-release/release-signing.key"`.
      - The command prints the public key, which is public. Keep it in
        1Password, and give it for `PublicKey`.
   4. **First release (v0.20.0).** Do this only after steps 1-3, and after
      `PublicKey` and `CERT_SHA256` are on `master`.
      - Tag `v0.20.0` on `master`.
      - Approve both jobs.
      - Check the release with `signsums verify`.
   5. **First install on each phone and tablet.**
      1. From the checkout, run `dir=$(mktemp -d)`, then
         `gh release download -R olafkfreund/nixarchy-flux v0.20.0 -D "$dir" -p 'SHA256SUMS*' -p '*.apk'`.
      2. Run `go run ./scripts/signsums verify "$dir/SHA256SUMS" <public key>`,
         then `(cd "$dir" && sha256sum --check --ignore-missing SHA256SUMS)`.
      3. Run `apksigner verify --print-certs "$dir/flux-android-0.20.0.apk"`.
         Compare `Signer #1 certificate SHA-256 digest` with the digest kept
         in 1Password (step 2). **Don't use the `android-certificate.txt` from
         the same download.**
      4. Only then uninstall upstream's Flux on the device.
      5. Run `adb install "$dir/flux-android-0.20.0.apk"`, or copy the file
         over and open it.
      6. Pair the device again, and enroll `flux-approve` again.
   6. **Desktops.**
      - A NixOS desktop rebuilds from a `master` that contains this change.
        Its fluxd has the key before it reads a fork release.
      - An Arch desktop on upstream's package installs the fork's v0.20.0
        package once by hand, after the checks in step 5.1-5.2, with
        `sudo pacman -U`.
   7. **Later releases.** `flux-cli update --phone` sends them, and fluxd
      checks the signature first.

   Check:
   - `! grep -nE 'AUR_PUBLISH|AUR_SSH|PublicKey. is empty|when the \[release signing key\]|skip the .verify.|Optional: add a required|a9af3fb3|bjarneo' docs/releasing.md`;
   - `grep -nE '^## (Release environment|Release signing key|Android release key)$' docs/releasing.md`
     prints 3 lines, so the anchors still resolve;
   - `grep -n 'gh \(secret\|workflow\|run\|release\)' docs/releasing.md | grep -v -- '-R olafkfreund/nixarchy-flux'`
     prints nothing.

6. **Verify before the keys** (lead)
   - `go vet ./... && test -z "$(gofmt -l .)"`.
   - `go test ./internal/release/ ./cmd/flux/ ./scripts/signsums/ -skip '^(TestPublicKey|TestRootInstall)$'`.
   - Run the step 1 dummy-key test again.
   - `make vet test`. Compare it with master's failure set in a detached
     worktree; the only new failure may be `TestPublicKey`.
   - Run the `bjarneo` grep again.
   - Dispatch `build.yml` and `windows.yml` on the branch:
     - `android`, `ios`, `macos` and `windows` pass;
     - `arch` fails only on `TestPublicKey` (P1).
   - Get the Codex and agy diff reviews, and fix their findings.
   - Open the PR as a **draft**:
     - it links all three files;
     - it names the steps codex, agy and coder did.
   - **Stop. Owner gate.** The owner does doc steps 1-3: environment,
     rulesets, keystore, signing key, 1Password and secrets. Then the owner
     posts the Ed25519 public key and the 64-hex certificate digest. Both are
     public values.

7. **Keys in** (lead)
   - In `sign.go`, `const PublicKey = "<owner's key>"`. Find it by name, not
     line, because step 1 moves it.
   - In `release.yml`, `CERT_SHA256: <owner's digest>`. Also by name.
   - Check:
     - `go run ./scripts/signsums key` prints exactly the owner's key;
     - `grep -c 'CERT_SHA256: <owner digest>' .github/workflows/release.yml`
       is 1;
     - `! grep -rn a9af3fb3 .github docs`;
     - `go test ./internal/release/ -run '^TestPublicKey$'` passes.

8. **Verify with the keys** (lead)
   - `go test ./internal/... ./cmd/flux/ ./scripts/... -skip '^TestRootInstall$'`.
     Any failure must be in master's host failure set.
   - `make vet test`, compared with master's set: no new failures.
   - `nix flake check path:.`.
   - Dispatch `build.yml` and `windows.yml` again; all 5 jobs pass.
   - Mark the PR ready, and wait for the owner to merge it, **squashed**.
   - After the merge, the owner tags v0.20.0 once #13, #50, #25 and #23 are
     in. The owner then follows doc steps 4-6, checking that `apk-sign`,
     `sign` and `signsums verify` pass (spec verification 7).

## Tests

| Test | Expected |
| --- | --- |
| `TestOfficial` | Keeps the fork URL. Drops the 4 malformed cases and the upstream case. |
| `TestPublicKey` | Fails before step 7 and passes after. |
| `TestFetch`, `internal/core` update tests | Pass with the key empty, and with a dummy key. |
| `TestInstallPackageNoAUR` | The error names the tag, the architecture, "no AUR package" and the page, and `yay` never runs. |
| `make vet test` | Master's failure set (host-dependent tests), plus `TestPublicKey` before step 7 only. |
| CI `build.yml` + `windows.yml` | Step 6: android, ios, macos and windows pass, and arch fails only on `TestPublicKey`. Step 8: all 5 pass. |

## Rollback

- **Before the merge:** close the PR. Outside the branch, only the owner's
  GitHub setup changed: the environment, the secrets and the rulesets. The
  rulesets are an active policy that the owner keeps or removes.
- **After the merge, before any tag:** revert the squash commit through a PR,
  because the ruleset protects `master`. Binaries built in between have the
  fork's address and key, so they are safe.
- **After v0.20.0:** don't revert the key. Reverting doesn't change the
  installed binaries, but new builds would accept unsigned releases. Fix
  forward instead.
  - A lost keystore means each device reinstalls by hand (spec Risks).
  - A lost `release-signing.key` means a new key pair, a new `PublicKey`,
    and one manual update on each device. Android needs no reinstall while
    the keystore is intact.
