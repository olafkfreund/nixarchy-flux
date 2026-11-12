---
status: draft
issue: 44
spec: spec/2026-11-12-44-release-signing.md
coders:
  1: codex   # small test logic: a new key test and an upstream-refusal case
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
are in it.** Use `gh ... -R olafkfreund/nixarchy-flux` everywhere.

## Approved decisions (from the spec)

- The fork publishes the releases. `DefaultURL` and `downloadURL` point at
  `olafkfreund/nixarchy-flux`.
- The app keeps the ID `org.omarchy.flux`. Each device uninstalls upstream's
  app once.
- Signing is required. `PublicKey` is the owner's Ed25519 key, and
  `CERT_SHA256` is the owner's Android certificate digest. A new test,
  `TestPublicKey`, blocks a merge without the key.
- There is no fork AUR package:
  - remove the `aur` job;
  - remove the `yay`/`paru` fallback;
  - `aur.yml` and the AUR tarball stay.
- The `release` environment has the owner as required reviewer. Rulesets
  protect `master` and `v*`.
- Links to the fork: every `github.com/bjarneo/flux` link becomes
  `github.com/olafkfreund/nixarchy-flux`, and every `bjarneo.github.io/flux`
  becomes `olafkfreund.github.io/nixarchy-flux`. These stay unchanged:
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
| P1 | Verification 4 and 5: `nix flake check` and all four CI jobs pass before the keys are in | Before the keys are in, expect `nix flake check` and the CI `arch` job to fail, **only** on `TestPublicKey`. `android`, `ios` and `macos` must pass. Everything must be green at step 8. | `nix/package.nix:66` runs `go test ./internal/...`, and `build.yml:69` runs `make vet test`, so the deliberate failure shows in both. |
| P2 | §5 file list | Add `windows/Flux/Chat/ChatPage.cs:323` (install guide) and `gui/qml/tools/Snapshot.qml:260` (snapshot fixture URL). These stay unchanged: `windows/README.md:12` (upstream PR #92, history) and `.impeccable/surfaces/site-index-html.md` (describes `site/`). | §5's rule says "every link"; the spec's list missed these. |

## Steps

1. **`internal/release/`** (codex)
   - `release.go:23,27`: point `DefaultURL` at
     `https://api.github.com/repos/olafkfreund/nixarchy-flux/releases/latest`
     and `downloadURL` at
     `https://github.com/olafkfreund/nixarchy-flux/releases/download/`.
   - `release_test.go:101-117` (`TestOfficial`):
     - Move the 5 existing asset URLs to the fork prefix, keeping each one's
       defect (http, wrong host, wrong tag, wrong name).
     - Add a 6th asset, a valid-looking
       `https://github.com/bjarneo/flux/releases/download/v0.7.0/<name>`,
       and assert it lands in `Dropped`.
   - New `sign_test.go`, `TestPublicKey`:
     - `PublicKey != ""`.
     - `base64.StdEncoding.DecodeString` gives `ed25519.PublicKeySize` bytes.
     - Generate a key pair and sign a message. It verifies with that key and
       does not verify with `PublicKey`.
   - `sign.go:10-16`: change the comment to "the key of the fork's releases",
     and drop the "while empty" sentence. The value stays `""` until step 7.
   - Check:
     `go test ./internal/release/ -skip TestPublicKey` passes, and
     `go test ./internal/release/ -run TestPublicKey` fails with a message
     naming `PublicKey`.
   - Traps:
     - Other tests build URLs from `downloadURL`; grep `bjarneo` in
       `internal/release/` and leave only the new refusal case.
     - Don't touch `download.go`.

2. **`cmd/flux/`** (codex)
   - `update.go:103-121`:
     - Rewrite the comment so it no longer mentions an AUR helper.
     - Replace the `yay`/`paru` loop and its error with
       `fmt.Errorf("the release %s has no %s package, and Flux has no AUR package to build it from. See %s", r.Tag, packageArch(), r.Page)`.
   - `update_test.go`:
     - Move the line-120 `dir` constant to the fork prefix.
     - Replace the positive fake-`yay` case at lines 139-146
       (`TestInstallPackageNoAUR`) with an assertion: the error mentions the
       tag and the architecture, and the fake `yay` mark file does not exist.
   - `oldapp.go:6`: point `latestRelease` at the fork.
   - Check: `go vet ./cmd/flux/ && go test ./cmd/flux/ -run 'Install|Package|Old'`.
     The full package's `TestRootInstall` fails on this host, just as on
     master.
   - Traps:
     - `exec` stays imported: lines 88 and 211 still use it.
     - Keep the `Dropped` error at line 111 first.

3. **`.github/workflows/release.yml`** (coder)
   - Delete the `aur` job and its two comment lines (lines 472-484).
   - Above `CERT_SHA256` (line 135), add the comment "the fork's Android
     release certificate (docs/releasing.md)". The value stays until step 7.
   - Check:
     - `grep -n 'aur.yml\|AUR_' .github/workflows/release.yml` prints nothing;
     - no job has `needs:` naming `aur`;
     - `python -c 'import yaml,sys;yaml.safe_load(open(sys.argv[1]))' .github/workflows/release.yml`.
   - Traps: keep the `files` job's AUR tarball (line 328).

4. **Links** (agy)
   - Apply the §5 replacements in these files:
     - `android/.../ui/AboutScreen.kt:25-31,81-82`, including the visible
       labels `bjarneo.github.io/flux` and `github.com/bjarneo/flux`;
     - `android/.../ui/Onboarding.kt:30`;
     - `macos/App/Chat/PairGuide.swift:59`;
     - `windows/Flux/Chat/ChatPage.cs:323`;
     - `gui/qml/tools/Snapshot.qml:260`;
     - `README.md`;
     - `docs/install.md`, `docs/troubleshooting.md`, `docs/ios.md`,
       `docs/macos.md`, `docs/windows.md`.
   - Check:
     `grep -rn bjarneo --exclude-dir={intent,spec,plan,site,.git} .` prints
     only the allowed kinds, plus `docs/releasing.md`, which step 5 handles.
   - Traps:
     - Keep "a fork of bjarneo/flux"-style origin sentences.
     - Don't edit `site/`, `docs/macos-plan.md` or `docs/macos-status.md`.

5. **`docs/releasing.md`** (agy). Apply spec §6 in full:
   - Remove "AUR setup" (lines 159-197), step 6 of "First release setup"
     (line 30), and line 47 of "Release environment".
   - Rewrite lines 6-7, 15 and 20 to say that the fork does not publish to
     AUR. The `aur.yml` row stays and gets "not called by `release.yml`".
   - Rewrite lines 346 and 428-429 the same way.
   - Line 274: set `REPO=olafkfreund/nixarchy-flux`.
   - Lines 327 and 404: a fork release always has `SHA256SUMS.sig`, and the
     check is never skipped.
   - Rewrite "Release environment" (41), "Android release key" (78) and
     "Release signing key" (198). Use spec §6 steps 1-7 word for word for the
     commands:
     - the four secret names and the PKCS12 password note;
     - the `LC_ALL=C keytool … | sed | tr | tr` pipeline and its 64-hex
       check;
     - `signsums keygen "$HOME/.local/share/flux-release/release-signing.key"`;
     - the 1Password backup, in bold;
     - the two approvals per release;
     - the v0.20.0 first release;
     - the six first-install steps: mktemp, `gh release download`,
       `signsums verify` with `sha256sum --check`, `apksigner` against the
       kept digest, uninstall upstream, `adb install`, then pair again;
     - the NixOS and Arch desktop steps;
     - later releases through `update --phone`.
   - Keep the key-rotation (v3 lineage) text.
   - Check:
     - `grep -n 'AUR_PUBLISH\|AUR_SSH\|optional.*sig\|bjarneo' docs/releasing.md`
       prints nothing;
     - every command in the new sections matches spec §6;
     - the anchors `#release-environment` and `#release-signing-key` still
       resolve.
   - Traps: the TestFlight sections (431+) are untouched.

6. **Verify before the keys** (lead)
   - Run:
     - `go vet ./... && test -z "$(gofmt -l .)"`;
     - `go test ./internal/release/ ./cmd/flux/ ./scripts/signsums/ -skip TestPublicKey`;
     - `make vet test`, compared with master's failure set in a detached
       worktree. The only new failure may be `TestPublicKey`.
   - Repeat the `bjarneo` grep.
   - Dispatch `build.yml` on the branch. `android`, `ios` and `macos` must
     pass, and `arch` must fail only on `TestPublicKey` (P1).
   - Run the Codex and agy diff reviews and fix their findings. Open the PR
     as a **draft** that links all three files and says which steps codex,
     agy and coder did.
   - **Stop. Owner gate:** the owner does doc steps 1-3 (environment,
     rulesets, keystore, signing key, 1Password, secrets), then posts the
     Ed25519 public key and the 64-hex certificate digest. Both are public.

7. **Keys in** (lead)
   - `sign.go:16`: `const PublicKey = "<owner's key>"`.
   - `release.yml:135`: `CERT_SHA256: <owner's digest>`.
   - Check: `go run ./scripts/signsums key` prints exactly the owner's key, and
     the digest matches `^[0-9a-f]{64}$`.

8. **Verify with the keys** (lead)
   - Run the full step 6 again, without `-skip`:
     - `go test ./...`;
     - `make vet test`, against master's set;
     - `nix flake check path:.`;
     - `build.yml` dispatched again, with all four jobs green.
   - Mark the PR ready and wait for the owner's merge.
   - After the merge, the owner tags v0.20.0 once #13, #50, #25 and #23 are
     in. The owner follows doc steps 4-6 and checks that `apk-sign`, `sign`
     and `signsums verify` pass (spec verification 7).

## Tests

| Test | Expected |
| --- | --- |
| `TestOfficial` | the fork URL is kept, the 4 malformed cases and the upstream case are dropped |
| `TestPublicKey` | fails before step 7, passes after |
| `TestInstallPackageNoAUR` | returns an error naming the tag and the architecture, and never runs `yay` |
| `make vet test` | master's failure set (host-dependent tests), plus `TestPublicKey` before step 7 only |
| CI `build.yml` | step 6: android, ios and macos pass, and arch fails only on `TestPublicKey`; step 8: all four pass |

## Rollback

- Before the merge: close the PR. Nothing outside the branch changed except
  the owner's GitHub environment and secrets, which are harmless unused.
- After the merge, before any tag: revert the merge commit. Binaries built in
  between have the fork's address and key, so they are safe.
- After v0.20.0: don't revert the key, because that would make the installed
  apps accept unsigned releases. Fix forward. A lost keystore means each
  device reinstalls by hand (spec Risks).
