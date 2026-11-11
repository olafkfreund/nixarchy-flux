---
status: approved
issue: 29
author: olafkfreund
---

# Intent: Land "send a link to the phone from the browser" (PR #29)

## Problem

PR #29 (upstream `bjarneo/flux#54`, by `haripako`) adds a browser extension
that sends the current link or page to a paired phone, through a native
messaging host `flux-native` that talks to fluxd over its Unix socket. The
feature is useful and was tested on real hardware by its author, but it
cannot be merged as it is:

- **It does not build on our master.** `ipc.Serve` now takes a
  `net.Listener` (`cmd/flux-native/main_test.go:71`), and the branch
  conflicts in `Makefile`, `README.md`, `dist/arch/PKGBUILD`,
  `docs/README.md` and `skills/omarchy-flux/SKILL.md`.
- **It does not work on Nix.** The flake does not build `flux-native` or
  ship the extension files (`origin/master:nix/package.nix:22-40`).
  `extensionDir` (`cmd/flux/browser.go:260`) never looks in a package's
  `share/flux/browser-extension`, so even shipped files would show "The Flux
  extension is missing". `hostPath` (`browser.go:296`) writes the resolved
  `/nix/store/...` path of the binary into each host manifest, and Chromium
  remembers the folder of an unpacked extension, so both break once that
  store path is no longer kept alive by a profile and is garbage-collected.
- **`flux-cli browser remove --dry-run` deletes the manifests:**
  `browserRemove` takes no arguments (`browser.go:93-99, 177`).
- **The host trusts the extension for validation.** Only the extension
  checks that a link is `http(s)` (`popup.js:29`, `host.js:104`);
  `flux-native` sends any string up to 8 KiB to the phone
  (`cmd/flux-native/main.go:124-136`). The host is the security boundary, so
  it must check.
- **`flux-cli setup` installs host manifests by default** for every browser
  it detects (`cmd/flux/setup.go:24, 64-75`; opt-out with `--no-browser`),
  which writes into browser config folders the user did not ask about.
- **Firefox and Zen are assumptions.** Install is gated on exactly
  `~/.mozilla/firefox` or `~/.zen` and writes to two fixed host folders
  (`browser.go:75, 144`); other layouts are skipped without a word. An
  unpacked add-on in Firefox is temporary and is gone after a restart unless
  it is signed.

## Proposed outcome

- PR #29's feature is on master, rebased on current code, building and
  passing tests. Conflicts keep master's current behavior and text **and**
  add the PR's `flux-native`, extension, install and docs additions.
- `flux-cli browser install` and `remove` work, and `remove --dry-run`
  changes nothing.
- `flux-native` refuses any link that is not `http` or `https` with a host,
  whatever the extension sends.
- On Nix: the flake builds and installs `flux-native` and the extension
  files, `flux-cli` finds the installed extension, and the host manifests and
  the folder the user loads the extension from keep working across upgrades
  and garbage collection. How the paths are chosen is for the spec.
- Host manifests are **opt-in**: `flux-cli setup` no longer writes them
  unless asked.
- Firefox and Zen host lookup is checked against what those browsers read,
  and the docs say that an unpacked Firefox add-on lasts until restart.
- The Arch package and `make install` install `flux-native` and the
  extension as the PR intended.

## Affected users and systems

- Desktop users who want to send links from Chromium, Firefox or Zen.
- New code: `browser/`, `cmd/flux-native/`, `internal/nativemsg/`,
  `cmd/flux/browser.go`; changes to `cmd/flux/setup.go`, `cmd/flux/main.go`,
  `Makefile`, `dist/arch/PKGBUILD`, `nix/package.nix`, the Nix modules, docs.
- Phones: no change; the link goes through the existing `share.url`.

## Constraints

- No change to fluxd's IPC or to the phone protocol; reuse `share.url`.
- Keep the author's design (one fixed extension ID per browser, the host as
  the only bridge, two commands only) and credit the author in the commit.
- Must not break the existing Arch install or `make install`.
- Tests pass in the Nix sandbox.

## Decisions (approved 2026-11-11)

The owner approved the recommended answer to each question below.


1. **Setup default.** Should `flux-cli setup` stop installing host manifests
   by default (opt-in through `flux-cli browser install` or
   `setup --browser`)? Recommended: yes, opt-in, since it writes into
   browser config folders.
2. **Nix configuration.** Should the Home Manager module get a
   `services.flux.browser.enable` option (default off) that writes the host
   manifests and a stable copy of the extension? Recommended: yes. Without
   Home Manager, `flux-cli browser install` on a Nix build writes paths that
   outlive the store path; the spec picks how.
3. **Extension key.** The PR's `manifest.json` carries a public `key` that
   fixes the Chromium extension ID (`jgfibbdalckcbmdcngbalobbhcbepamm`). It is
   public by design, not a secret, so another unpacked extension can reuse it
   and get the same ID. The host would then let that extension list the
   paired devices (IDs, names, online state) and send links to them without a
   prompt. Keep it? Recommended: keep it, keep the two fixed commands and the
   size bounds, and document exactly what the host allows any extension with
   that ID to do.
4. **Credit and upstream.** Land it as a new PR on our fork that supersedes
   #29, with the author credited (`Co-authored-by`), and close #29 pointing
   to it? Recommended: yes.
