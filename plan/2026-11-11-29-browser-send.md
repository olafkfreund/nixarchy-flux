---
status: approved
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

## Review corrections (Codex and agy spec and plan reviews — approve or strike)

These come from reviews of the spec and the plan draft and change spec
details; approving this plan approves them.

| # | Spec said | Plan does | Why |
| --- | --- | --- | --- |
| C1 | D5: with no profile `flux-native`, write the store path with a warning | refuse to write a manifest and print how to install Flux into a profile (`nix profile install`, the NixOS or Home Manager module) | an unrooted store path breaks after garbage collection, against the approved outcome |
| C2 | D5/D7: the copy lives in `~/.local/share/flux/browser-extension` | both the CLI copy and lookup and the Home Manager copy use `$XDG_DATA_HOME/flux/browser-extension` (default `~/.local/share/...`) | with a custom data folder the CLI and Home Manager would make 2 copies |
| C3 | D5: leave a symlinked extension alone | `browserInstall` also leaves a host manifest that is a symlink (Home Manager) alone: if its JSON matches, it prints "managed by Home Manager"; if not, it reports the difference and does not write; docs say to run `flux-cli browser remove` before enabling the Home Manager option | `os.WriteFile` through a store symlink fails with EROFS, and Home Manager refuses to replace an existing file |
| C4 | D2: refuse unless the scheme is http(s) and `Host` is not empty | refuse unless `url.Parse` succeeds, the scheme is http(s), and `Hostname()` is not empty; the tests also assert that no IPC request was made | `https://:443/` has a `Host` but no host name |
| C5 | D4: `--browser` runs `browserInstall` | `setup --browser` forwards `--dry-run`, and a failed browser step counts in master's failed-steps result (non-zero exit) | the branch prints browser errors and returns success |
| C6 | D7: stamp the manifests with `version` | stamp them with `version` cut to `N.N.N` (drop `+…` and `-…`), as the Makefile's `EXT_VERSION`; do the install and stamp in `postInstall`, not through `make` | Chromium and Firefox reject a manifest version with `+`; the Nix `version` is `0.19.1+<rev>` |
| C7 | D7: `xdg.dataFile."flux/browser-extension".source` | a `home.activation` step copies the package's extension into `${config.xdg.dataHome}/flux/browser-extension` as real files on every activation, removing stale files (`cp -rLT --no-preserve=mode`, `chmod -R u+w`), like the plugin step | Chromium saves the `realpath` of an unpacked extension, so a symlink saves a store path that garbage collection removes |
| C8 | D7: the option inside the module | `services.flux.browser` is not gated on `services.flux.enable`: when on, it adds the core package to `home.packages` (so `${profileDirectory}/bin/flux-native` exists) and writes the manifests | a NixOS-module user who enables only the browser option would get nothing |
| C9 | D7: `home.file` manifests, Chromium list = all 14 | Chromium manifests through `xdg.configFile`; Gecko through `home.file`; `services.flux.browser.chromium` defaults to `[ "chromium" "google-chrome" "BraveSoftware/Brave-Browser" ]`; `services.flux.browser.firefox` (default true) and `.zen` (default false) | Chromium reads `$XDG_CONFIG_HOME`; writing 16 folders creates config dirs for browsers that are not installed and fools detection |
| C10 | D5: 4 profile folders | add `$XDG_STATE_HOME/nix/profiles/profile/bin` (standalone Home Manager with XDG); use `os/user.Current()` when `$USER` is unset | the standalone Home Manager profile was missing |
| C11 | D5: copy the extension | under `--dry-run` print "would copy" and write nothing; `printExtensionHelp` never prints a `/nix/store` folder | a dry run must not change files, and a store folder is not safe to load |
| C12 | D7: manifests "the same content" | exact JSON: `name` `org.omarchy.flux`, the CLI's `description`, `path`, `type: "stdio"`; Chromium only `allowed_origins: ["chrome-extension://jgfibbdalckcbmdcngbalobbhcbepamm/"]`; Gecko only `allowed_extensions: ["flux@omarchy.org"]`; the HM check parses and compares them, and treats a different profile path spelling as equal | presence alone does not prove parity |
| C13 | tests use `t.Setenv` | the path lookups take their candidate list and executable path from package vars that tests replace, so tests build a fake package layout in `t.TempDir()` and never read `/run/current-system` or `/etc/profiles` | a test must not depend on what the host has installed |
| C14 | "host-only failures of master" | the allowed failures are exactly: `TestDesktopEndsAfterAnUnpair`, `TestDesktopEndsWhenThePhoneClosesTheStream`, `TestDesktopOverlappingStartsWaitForTheOldRecorder`, `TestDesktopStartFloodRunsOneRecorder`, `TestDesktopStartWaitsForTheOldRecorder`, `TestEnableDisable`, `TestExtendedScreen`, `TestKeyboardReleasesMods`, `TestRootInstall`, `TestSpeakerSession`; the docs check greps each required item separately | a gate that "leaves success to interpretation" is not a gate |
| C15 | step 4 names the files | `nix/checks.nix` HM stub (lines 113–137 on master) gains `home.file`, `xdg.configFile`, `xdg.dataHome`; the HM check runs twice, option on and off; edits anchored at `nix/home-manager.nix:13-50` and `nix/package.nix:22-40, 68-90` | the stub does not declare the new options |
| C16 | (agy plan review) step 1 fixes the test with `net.Listen` | use master's `ipc.Listen(path)` (`internal/ipc/ipc.go:59`) and pass its listener to `ipc.Serve`; step 1's check runs `go test ./cmd/flux-native/ ./internal/nativemsg/ -count=1` without a `-run` filter | the draft's `-run` pattern matched no test in `cmd/flux-native`, and `net.Listen` skips master's socket setup |
| C17 | (agy plan review) step order | steps 2 and 3 both edit `cmd/flux/browser.go`, so they run one after the other on the same Codex member; step 4 starts after step 3; step 5 may run beside steps 2–4 | parallel edits of one file conflict |
| C18 | (agy plan review) step 4 constants | the HM manifests use these literals: `name = "org.omarchy.flux"`, `description = "Flux host for the Flux browser extension"`, Chromium origin `chrome-extension://jgfibbdalckcbmdcngbalobbhcbepamm/`, Gecko ID `flux@omarchy.org` | the coder must not guess them; key order in JSON does not matter to browsers or to `browserRemove` |
| C19 | (implementation) the browser option adds the core to `home.packages` | only when `services.flux.enable` is off; with it on, `cfg.package` already holds the core. The module's `config` is an `lib.mkMerge` of 2 `mkIf` blocks | 2 copies of each file in the profile risk a `buildEnv` collision |

## Steps

1. **Session model — merge.** `git merge origin/master` on
   `fix/29-browser-send`; resolve the 5 conflicts by D1; fix
   `cmd/flux-native/main_test.go:71` (`ln, _ := net.Listen("unix", path)`,
   pass `ln`); keep the PR's `setup.go` browser step and master's Nix branch
   together (D4 applies in step 3).
   → verify `go build ./... && go vet ./cmd/... ./internal/... && go test ./cmd/flux-native/ ./internal/nativemsg/ -count=1 && go test ./cmd/flux/ -run 'Browser|Extension|Gecko|Setup' -count=1` (C16).
   Traps: do not drop master's `nixBuild()` guards in `setup.go`, or the
   flake, `nix/` and docs/nix.md from master.

2. **`cmd/flux-native/main.go`, `cmd/flux/browser.go`** — D2 in `send`
   (124–136) with tests in `cmd/flux-native/main_test.go` (refuse
   `javascript:alert(1)`, `file:///etc/passwd`, `intent://x`, `https://` with
   no host, over 8 KiB; accept `https://example.com/a?b`); D3 in `browser`
   (93–101) and `browserRemove` (177) with tests in `cmd/flux/browser_test.go`
   next to `TestBrowserRemove` (219): `--dry-run` keeps the files, an unknown
   flag is refused.
   Apply C4.
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
   Apply C1, C2, C3, C5, C10, C11, C13.
   → verify `go test -race ./cmd/flux/ -count=1 2>&1 | grep -E '^--- FAIL' | grep -v TestRootInstall` prints nothing.
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
   Apply C6, C7, C8, C9, C12, C15.
   → verify `nix flake check path:. -L` and `nix run nixpkgs#nixfmt -- --check flake.nix nix/*.nix`.
   Traps: every verify uses `path:.` (untracked files); the HM manifests must
   match `flux-cli browser install`'s JSON (`name`, `description`, `path`,
   `type: stdio`, `allowed_origins` / `allowed_extensions`).

5. **`docs/browser.md`, `docs/nix.md`, `docs/cli.md`** — D8.
   → verify each separately: `grep -q "only web links" docs/browser.md && grep -q "restart" docs/browser.md && grep -q "1.22.3b" docs/browser.md && grep -q "browser.enable" docs/nix.md && grep -q "browser remove" docs/nix.md`.
   Traps: short declarative sentences, one per line (`AGENTS.md`).

6. **Session model** — `go vet ./cmd/... ./internal/... ./scripts/...`,
   `test -z "$(gofmt -l cmd internal scripts)"`, the full suite whose
   failures must be a subset of C14's list, `make build-go`, `make browser`,
   `nix flake check path:.`; Opus, Codex and
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
