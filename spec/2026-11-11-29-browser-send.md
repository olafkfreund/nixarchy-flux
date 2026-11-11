---
status: approved
issue: 29
intent: intent/2026-11-11-29-browser-send.md
---

# Spec: Land "send a link to the phone from the browser" (PR #29)

Approved decisions from the intent: host manifests are opt-in; a Home
Manager option `services.flux.browser.enable` (default off) writes the host
manifests and a stable copy of the extension; keep the public extension key,
the two fixed host commands and the size bounds, and document what any
extension with that ID can do; land as a new PR that credits the author
(`haripako`) and supersedes #29.

## Design

### D1. Rebase onto master

- Merge `origin/master` into `fix/29-browser-send` (the branch already holds
  the PR's commits). Resolve the conflicts in `Makefile`, `README.md`,
  `dist/arch/PKGBUILD`, `docs/README.md` and `skills/omarchy-flux/SKILL.md` by
  keeping master's current content and adding the PR's lines for
  `flux-native`, the extension, `make browser`, and the browser docs.
- `cmd/flux-native/main_test.go:71`: `ipc.Serve` now takes a `net.Listener`
  (`origin/master:internal/ipc/ipc.go:93`); the test listens itself
  (`net.Listen("unix", path)`) and passes the listener.
- `cmd/flux/setup.go` keeps master's Nix branch (`nixBuild()`, from #40) and
  gains the PR's browser step under D4's opt-in rule.

### D2. The host validates every link (`cmd/flux-native/main.go`)

`send` (`main.go:124-136`) parses the trimmed link with `net/url.Parse` and
refuses it unless the scheme is `http` or `https` and `Host` is not empty,
with the reply "only web links can be sent". The 8 KiB bound stays, checked
on the trimmed value. The command set stays `devices` and `send`, and the
host never takes a fluxd method from the browser (`handle`, `main.go:84-100`).

### D3. `browser remove` honors its flags (`cmd/flux/browser.go`)

`browser()` (`browser.go:93-101`) passes `args[1:]` to `browserRemove`, which
accepts `--dry-run`/`-n` and refuses other flags like `browserInstall`
(`browser.go:103-116`). With `--dry-run` it prints "would remove PATH" for
each Flux manifest and removes nothing.

### D4. Opt-in setup (`cmd/flux/setup.go`)

`flux-cli setup` no longer writes host manifests by default. `--browser`
runs `browserInstall` as its last step; `--no-browser` is still accepted and
does nothing, so existing scripts keep working. Without `--browser`, setup
prints one line: "To send links from a browser, run: flux-cli browser
install".

### D5. Stable paths on a Nix build (`cmd/flux/browser.go`)

When `nixBuild()` (`cmd/flux/main.go`, #40) is true:

- **Host path.** `hostPath` (`browser.go:295-309`) uses the first existing
  `flux-native` of: `/etc/profiles/per-user/$USER/bin`, `$HOME/.nix-profile/bin`,
  `$XDG_STATE_HOME/nix/profile/bin` (default `~/.local/state/nix/profile/bin`),
  `/run/current-system/sw/bin`. Those are profile links that a rebuild moves
  to the new store path, and each generation keeps its closure alive. When
  none exists (for example under `nix run`), it writes the store path and
  warns that the manifest breaks after garbage collection.
- **Extension folder.** `browserInstall` copies the package's
  `share/flux/browser-extension` (found next to the executable,
  `../share/flux/browser-extension`) into
  `~/.local/share/flux/browser-extension` as real files, replacing an earlier
  copy, and prints that folder as the one to load. `extensionDir`
  (`browser.go:255-278`) already searches that folder. A folder that is a
  Home Manager symlink is left alone.
- Not on a Nix build, both keep the PR's behavior.

`extensionDir` also searches `../share/flux/browser-extension` next to the
executable, after the user copy, so a Nix `flux-cli` can find the package's
files even before the copy.

### D6. Firefox and Zen lookup (`cmd/flux/browser.go`)

- Firefox reads user host manifests from `~/.mozilla/native-messaging-hosts`
  only; MDN documents it, and Firefox 147 does not read
  `$XDG_CONFIG_HOME/mozilla/native-messaging-hosts` (Mozilla bug 2012310; the
  XDG location is still a proposal, bug 2005167). The host folder stays.
- Firefox is detected when `~/.mozilla/firefox` **or**
  `$XDG_CONFIG_HOME/mozilla/firefox` (default `~/.config/mozilla/firefox`, the
  newer profile layout) exists; the manifest still goes to
  `~/.mozilla/native-messaging-hosts`.
- Zen keeps `~/.zen` and `~/.zen/native-messaging-hosts`, which the PR's
  author verified end to end on Zen 1.22.3b. No public Zen documentation
  confirms it, so the docs say it was tested on that version.
- `browserInstall` prints each browser it skipped and why ("no profile
  folder found"), instead of skipping silently.

### D7. Packaging

- **Arch / `make install`:** the PR's `Makefile` and `PKGBUILD` changes
  (`flux-native` in `bin/`, the unpacked extension in
  `share/flux/browser-extension`, `VERSION` for the extension manifest).
- **Nix (`nix/package.nix`):** `flux-core` adds `cmd/flux-native` to
  `subPackages` and `browser` to the source fileset, and installs the
  extension into `$out/share/flux/browser-extension` with the same
  `install-extension` steps (manifests stamped with the version). The
  sandbox test skip list gains any new test that needs a browser profile or
  git, each named.
- **Home Manager (`nix/home-manager.nix`):** `services.flux.browser.enable`
  (default `false`). When on, `xdg.dataFile."flux/browser-extension".source`
  points to the package's extension folder (Home Manager keeps it current
  and alive), and `home.file` writes the Chromium and Gecko host manifests
  with `path = "${config.home.profileDirectory}/bin/flux-native"`, the same
  content `flux-cli browser install` writes. The option list
  `services.flux.browser.chromium` (default: the PR's `chromiumBrowsers`)
  picks which Chromium folders get one.
- **NixOS module:** no change; browser manifests are per user.

### D8. Docs

`docs/browser.md`: what the host lets any extension with the fixed ID do
(list the paired devices with their IDs, names and online state, and send a
web link to one without a prompt), that only `http`/`https` links are sent,
that an unpacked Firefox add-on lasts until Firefox restarts unless it is
signed, that Zen was tested on 1.22.3b, and the opt-in setup. `docs/nix.md`:
the `services.flux.browser.enable` option and `flux-cli browser install` on
Nix.

### Tests

- `cmd/flux-native`: `send` refuses `javascript:alert(1)`, `file:///etc/passwd`,
  `intent://…`, `https://` with no host, and a link over 8 KiB; accepts
  `https://example.com/a?b`. The existing tests pass with the `ipc.Serve`
  change.
- `cmd/flux`: `browser remove --dry-run` leaves the manifests; an unknown
  flag is refused; setup without `--browser` writes no manifest; on a Nix
  build `hostPath` prefers a profile link over the store path, and
  `browserInstall` copies the extension into the user folder.
- `nix flake check`: `flux-core` contains `bin/flux-native` and
  `share/flux/browser-extension/manifest.json`; the HM check (structural)
  shows the manifests and the data file when the option is on and nothing
  when it is off.

## Alternatives rejected

- **Validate links only in the extension.** The host is the boundary; a
  second unpacked extension with the same ID skips the extension's checks.
- **Write store paths and rely on the user to rerun install.** Breaks
  silently after garbage collection.
- **Keep setup's default-on step.** Writes into browser folders the user did
  not ask about.
- **Drop the fixed key.** Without it the Chromium ID changes per machine and
  the host manifest cannot name it.
- **Write Firefox manifests to `$XDG_CONFIG_HOME/mozilla`.** Firefox does not
  read it yet (bug 2012310).

## Risks

- **Zen path:** confirmed only by the author's test on one version. If a later
  Zen reads `~/.mozilla/native-messaging-hosts`, the Firefox manifest covers
  it only when Firefox is also detected.
- **Firefox add-on is temporary:** users must reload it after a restart, or
  sign `flux-firefox.zip`. Documented.
- **Conflict resolution** could drop an upstream line; the review compares the
  merged files with master and the PR.
- **Upstream drift:** the PR is about 170 commits behind upstream; later
  upstream changes to `setup.go` or `browser.go` may conflict again.

## Verification

1. `go vet`, `gofmt`, and `go test -race ./cmd/... ./internal/...` with no
   failure beyond the host-only baseline.
2. `make build` builds `bin/flux-native`; `make browser` builds both zips.
3. `nix flake check` passes; the package contains `flux-native` and the
   extension.
4. Owner, manual: load the extension in a Chromium browser and in Firefox,
   send a page to the phone, and check that `flux-cli browser remove
   --dry-run` changes nothing.
