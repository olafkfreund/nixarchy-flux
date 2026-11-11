---
status: draft
issue: 29
spec: spec/2026-11-11-29-browser-send.md
coders:
  2: codex   # small, security-relevant: host URL check and remove --dry-run, with tests
  3: codex   # tricky path logic: opt-in setup, Nix profile paths, extension copy, detection
  4: coder   # Nix packaging, the Home Manager option and its check: ordinary Nix
  5: agy     # docs: mechanical prose in three files
reviewers:
  - codex    # diff review against this plan before the PR
  - agy      # second, independent diff review
---

# Plan: Land "send a link to the phone from the browser" (PR #29)

Branch `fix/29-browser-send` (the PR's commits by `haripako` plus our
artifacts). Step 1 (the merge) and step 6 (verification, reviews, PR) are the
session model's. The final PR supersedes #29 and credits the author.

## Approved decisions (from the spec)

- D1 merge `origin/master`; conflicts in `Makefile`, `README.md`,
  `dist/arch/PKGBUILD`, `docs/README.md`, `skills/omarchy-flux/SKILL.md` keep
  master's content and add the PR's `flux-native`, extension, `make browser`
  and docs lines. `cmd/flux-native/main_test.go:71` passes a
  `net.Listener` to `ipc.Serve`. `setup.go` keeps master's `nixBuild()`
  branch.
- D2 `flux-native` `send`: `net/url.Parse` of the trimmed link; refuse unless
  scheme is `http`/`https` and `Host` is not empty ("only web links can be
  sent"); 8 KiB bound on the trimmed value; commands stay `devices` and
  `send`.
- D3 `browser remove` gets `args[1:]`, accepts `--dry-run`/`-n` ("would remove
  PATH"), refuses other flags.
- D4 `flux-cli setup` writes no host manifest unless `--browser`;
  `--no-browser` is accepted and does nothing; without `--browser` it prints
  "To send links from a browser, run: flux-cli browser install".
- D5 on `nixBuild()`: `hostPath` prefers the first existing `flux-native` in
  `/etc/profiles/per-user/$USER/bin`, `$HOME/.nix-profile/bin`,
  `$XDG_STATE_HOME/nix/profile/bin` (default `~/.local/state/nix/profile/bin`),
  `/run/current-system/sw/bin`; else the store path with a warning.
  `browserInstall` copies `../share/flux/browser-extension` (next to the
  executable) into `~/.local/share/flux/browser-extension` as real files,
  unless that folder is a symlink (Home Manager). `extensionDir` also
  searches `../share/flux/browser-extension`, after the user copy.
- D6 Firefox detected by `~/.mozilla/firefox` or
  `$XDG_CONFIG_HOME/mozilla/firefox`; its manifest stays in
  `~/.mozilla/native-messaging-hosts`. Zen keeps `~/.zen` and
  `~/.zen/native-messaging-hosts`. Each skipped browser is printed with the
  reason.
- D7 Arch/`make install` as in the PR. `nix/package.nix`: `cmd/flux-native` in
  `subPackages`, `browser` in the fileset, extension installed to
  `$out/share/flux/browser-extension` with stamped manifests. Home Manager:
  `services.flux.browser.enable` (default false) →
  `xdg.dataFile."flux/browser-extension".source` and `home.file` host
  manifests with `path = "${config.home.profileDirectory}/bin/flux-native"`;
  `services.flux.browser.chromium` lists the Chromium folders (default: the
  PR's `chromiumBrowsers`). NixOS module unchanged.
- D8 docs: what an extension with the fixed ID can do; http(s) only; Firefox
  add-on lasts until restart; Zen tested on 1.22.3b; opt-in setup;
  `docs/nix.md` for the HM option.

## Steps

1. **Session model — merge.** `git merge origin/master` on
   `fix/29-browser-send`; resolve the 5 conflicts by D1; fix
   `cmd/flux-native/main_test.go:71` (`ln, _ := net.Listen("unix", path)`,
   pass `ln`); keep the PR's `setup.go` browser step and master's Nix branch
   together (D4 applies in step 3).
   → verify `go build ./... && go vet ./cmd/... ./internal/... && go test ./cmd/flux-native/ ./internal/nativemsg/ ./cmd/flux/ -run 'Browser|Extension|Gecko|Native|Setup' -count=1`.
   Traps: do not drop master's `nixBuild()` guards in `setup.go`, or the
   flake, `nix/` and docs/nix.md from master.

2. **`cmd/flux-native/main.go`, `cmd/flux/browser.go`** — D2 in `send`
   (124–136) with tests in `cmd/flux-native/main_test.go` (refuse
   `javascript:alert(1)`, `file:///etc/passwd`, `intent://x`, `https://` with
   no host, over 8 KiB; accept `https://example.com/a?b`); D3 in `browser`
   (93–101) and `browserRemove` (177) with tests in `cmd/flux/browser_test.go`
   next to `TestBrowserRemove` (219): `--dry-run` keeps the files, an unknown
   flag is refused.
   → verify `go test -race ./cmd/flux-native/ ./cmd/flux/ -run 'Send|Browser|Native' -count=1`.
   Traps: keep `handle`'s fixed routing; the check runs before `call`.

3. **`cmd/flux/setup.go`, `cmd/flux/browser.go`** — D4, D5, D6, with tests in
   `cmd/flux/browser_test.go` and `cmd/flux/setup_test.go`: setup without
   `--browser` writes no manifest and prints the hint; `--no-browser` is
   accepted; with `packager = "nix"` (t.Cleanup reset) `hostPath` prefers a
   profile link over the store path, falls back with a warning, and
   `browserInstall` copies the extension into `~/.local/share/...` and leaves a
   symlinked folder alone; Firefox is detected through the XDG profile folder;
   a skipped browser is printed.
   → verify `go test -race ./cmd/flux/ -count=1` (no failure beyond
   `TestRootInstall`, the host-only one).
   Traps: `$HOME` and `$XDG_*` from `t.Setenv`; `$USER` for the per-user
   profile; never touch the real home in a test.

4. **`nix/package.nix`, `nix/home-manager.nix`, `nix/checks.nix`** — D7 for
   Nix: `subPackages` gains `cmd/flux-native`; the core fileset gains
   `../browser`; `postInstall` installs the extension like the Makefile's
   `install-extension` (stamped manifests with `version`); add
   `TestExtensionDirFindsUserCopy` and any new test that needs a real browser
   profile or git to `checkFlags` only if it fails in the sandbox, each named.
   Home Manager option and manifests per D7; the HM check (structural) asserts
   the data file and a Chromium and a Gecko manifest when on, and none when
   off; the `flux-core` check asserts `bin/flux-native` and
   `share/flux/browser-extension/manifest.json`.
   → verify `nix flake check path:. -L` and `nix run nixpkgs#nixfmt -- --check flake.nix nix/*.nix`.
   Traps: every verify uses `path:.` (untracked files); the HM manifests must
   match `flux-cli browser install`'s JSON (`name`, `description`, `path`,
   `type: stdio`, `allowed_origins` / `allowed_extensions`).

5. **`docs/browser.md`, `docs/nix.md`, `docs/cli.md`** — D8.
   → verify `grep -n "only web links\|until.*restart\|1.22.3b\|browser.enable" docs/browser.md docs/nix.md`.
   Traps: short declarative sentences, one per line (`AGENTS.md`).

6. **Session model** — `go vet`, `gofmt`, the full suite against the master
   baseline, `make build`, `make browser`, `nix flake check`; Opus, Codex and
   agy diff reviews; a new PR that supersedes #29 with `Co-authored-by:
   haripako` on the merge commit, naming who did each step; close #29 with a
   link.

## Tests

| Command | Expected |
| --- | --- |
| `go test -race ./cmd/... ./internal/...` | new tests pass; only the host-only failures of master |
| `make build && make browser` | `bin/flux-native`, `browser/dist/flux-{chromium,firefox}.zip` |
| `nix flake check` | passes; package has `flux-native` and the extension |
| Owner, manual | the extension in a Chromium browser and in Firefox sends a page to the phone; `flux-cli browser remove --dry-run` changes nothing |

## Rollback

Revert the merge of the new PR. The feature is opt-in: no host manifest is
written unless the user runs `flux-cli browser install`, `setup --browser`,
or enables the Home Manager option. `flux-cli browser remove` deletes written
manifests.
