---
status: draft
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
  ship the extension files, and `hostPath` (`cmd/flux/browser.go:296`) writes
  the resolved `/nix/store/...` path of the binary into each browser's host
  manifest, which breaks after the next update and garbage collection.
- **`flux-cli browser remove --dry-run` deletes the manifests:**
  `browserRemove` takes no arguments (`browser.go:93-99, 177`).
- **The host trusts the extension for validation.** Only the extension
  checks that a link is `http(s)` (`popup.js:29`, `host.js:104`);
  `flux-native` sends any string up to 8 KiB to the phone
  (`cmd/flux-native/main.go:124-136`). The host is the security boundary, so
  it must check.
- **`flux-cli setup` installs host manifests for every browser by
  default** (`cmd/flux/setup.go:64-75`), which writes into browser config
  folders the user did not ask about.

## Proposed outcome

- PR #29's feature is on master, rebased on current code, building and
  passing tests, with the conflicts resolved in favor of master's text.
- `flux-cli browser install` and `remove` work, and `remove --dry-run`
  changes nothing.
- `flux-native` refuses any link that is not `http` or `https` with a host,
  whatever the extension sends.
- On Nix: the flake builds and installs `flux-native` and the extension
  files, and host manifests point to a stable path that survives updates.
- Setting up host manifests is the user's choice.
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

## Open questions

1. **Setup default.** Should `flux-cli setup` stop installing host manifests
   by default (opt-in through `flux-cli browser install` or
   `setup --browser`)? Recommended: yes, opt-in, since it writes into
   browser config folders.
2. **Nix configuration.** Should the Home Manager module get a
   `services.flux.browser.enable` option that writes the host manifests
   declaratively? Recommended: yes, small, and it gives the stable path for
   free; NixOS users without Home Manager use `flux-cli browser install`,
   which on a Nix build writes the profile path instead of the store path.
3. **Extension key.** The PR's `manifest.json` carries a public `key` that
   fixes the Chromium extension ID. It is public by design, not a secret, and
   anyone could reuse it for another unpacked extension with the same ID;
   the host only answers `devices` and `send`. Keep it? Recommended: keep it,
   and document that the host trusts any extension with that ID.
4. **Credit and upstream.** Land it as a new PR on our fork that supersedes
   #29, with the author credited (`Co-authored-by`), and close #29 pointing
   to it? Recommended: yes.
