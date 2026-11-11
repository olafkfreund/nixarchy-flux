package main

import (
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"os"
	"os/user"
	"path/filepath"
	"reflect"
	"regexp"
	"strings"

	"flux/internal/nativemsg"
)

// The browser extension asks fluxd for a share through flux-native, which
// every browser starts as a native messaging host. This file writes the
// manifest that tells a browser where that host is, and that the Flux
// extension is the one that may start it.

// hostBinary is the file that a browser starts.
const hostBinary = "flux-native"

// extensionID is the fixed ID of the Chromium build of the extension, from
// the key in browser/extension/manifest.json. A host manifest names the
// extensions that may start it, so a fixed ID keeps another extension from
// sending to the phone. TestExtensionMatchesID keeps it in step with the
// manifest.
const extensionID = "jgfibbdalckcbmdcngbalobbhcbepamm"

// geckoID is the add-on ID of the Firefox and Zen build, from
// browser/extension/manifest.firefox.json.
const geckoID = "flux@omarchy.org"

// installedExtension is where `make install` puts the unpacked extension.
// Tests replace it.
var installedExtension = "/usr/share/flux/browser-extension"

var browserExecutable = os.Executable
var nixHostCandidates = defaultNixHostCandidates

func defaultNixHostCandidates() []string {
	home, _ := os.UserHomeDir()
	username := os.Getenv("USER")
	if username == "" {
		if current, err := user.Current(); err == nil {
			username = current.Username
		}
	}
	state := os.Getenv("XDG_STATE_HOME")
	if state == "" {
		state = filepath.Join(home, ".local", "state")
	}
	return []string{
		filepath.Join("/etc/profiles/per-user", username, "bin"),
		filepath.Join(home, ".nix-profile", "bin"),
		filepath.Join(state, "nix", "profiles", "profile", "bin"),
		filepath.Join(state, "nix", "profile", "bin"),
		"/run/current-system/sw/bin",
	}
}

func userExtension() string {
	data := os.Getenv("XDG_DATA_HOME")
	if data == "" {
		home, _ := os.UserHomeDir()
		data = filepath.Join(home, ".local", "share")
	}
	return filepath.Join(data, "flux", "browser-extension")
}

// hostManifest is the file that a browser reads to find a native messaging
// host. The two browsers disagree on the key that names the callers: a
// Chromium browser reads allowed_origins, which holds the origin of the
// extension, and Firefox and Zen read allowed_extensions, which holds the
// add-on ID. A browser ignores the key it does not know, so each manifest
// carries only the one its browser reads.
type hostManifest struct {
	Name              string   `json:"name"`
	Description       string   `json:"description"`
	Path              string   `json:"path"`
	Type              string   `json:"type"`
	AllowedOrigins    []string `json:"allowed_origins,omitempty"`
	AllowedExtensions []string `json:"allowed_extensions,omitempty"`
}

// chromiumBrowsers are the profile roots of the Chromium browsers that
// Flux supports, below the config directory. Each of them reads its own
// NativeMessagingHosts directory.
var chromiumBrowsers = []string{
	"chromium",
	"chromium-browser",
	"google-chrome",
	"google-chrome-beta",
	"google-chrome-unstable",
	"BraveSoftware/Brave-Browser",
	"BraveSoftware/Brave-Browser-Beta",
	"BraveSoftware/Brave-Browser-Nightly",
	"BraveSoftware/Brave-Origin-Beta",
	"microsoft-edge",
	"microsoft-edge-beta",
	"microsoft-edge-dev",
	"vivaldi",
	"opera",
}

// geckoBrowsers are Firefox and Zen, below the home directory. Each keeps
// its profiles in one folder and reads the host manifests from another.
var geckoBrowsers = []struct {
	Home  string // the folder the browser keeps
	Hosts string // the directory with the host manifests
}{
	{".mozilla/firefox", ".mozilla/native-messaging-hosts"},
	{".zen", ".zen/native-messaging-hosts"},
}

// target is one host manifest to write.
type target struct {
	path       string
	host       string
	origins    []string
	extensions []string
}

func browser(args []string) error {
	switch first(args) {
	case "install":
		return browserInstall(args[1:])
	case "remove", "uninstall":
		return browserRemove(args[1:])
	}
	return errors.New("unknown browser action. Use install or remove")
}

// extensionIDPattern is the form of a Chromium extension ID.
var extensionIDPattern = regexp.MustCompile(`^[a-p]{32}$`)

func browserInstall(args []string) error {
	id, dry := extensionID, false
	for i := 0; i < len(args); i++ {
		switch a := args[i]; {
		case a == "--dry-run" || a == "-n":
			dry = true
		case a == "--id" && i+1 < len(args):
			id, i = args[i+1], i+1
		case strings.HasPrefix(a, "--id="):
			id = strings.TrimPrefix(a, "--id=")
		default:
			return fmt.Errorf("unknown option %q. Use --id EXTENSION_ID or --dry-run", a)
		}
	}
	if !extensionIDPattern.MatchString(id) {
		return fmt.Errorf("%q is not a Chromium extension ID: 32 letters from a to p", id)
	}
	path, err := hostPath()
	if err != nil {
		return err
	}
	home, err := os.UserHomeDir()
	if err != nil {
		return err
	}
	var targets []target
	var chromium, gecko bool
	for _, config := range configHomes(home) {
		for _, rel := range chromiumBrowsers {
			dir := filepath.Join(config, rel)
			if !isDir(dir) {
				fmt.Println("  - skipped", rel, "(profile directory not found at", dir+")")
				continue
			}
			chromium = true
			targets = append(targets, target{
				path:    filepath.Join(dir, "NativeMessagingHosts", nativemsg.Host+".json"),
				host:    path,
				origins: []string{"chrome-extension://" + id + "/"},
			})
		}
	}
	for _, b := range geckoBrowsers {
		profile := filepath.Join(home, b.Home)
		if b.Home == ".mozilla/firefox" && !isDir(profile) {
			profile = filepath.Join(configHome(home), "mozilla", "firefox")
		}
		if !isDir(profile) {
			fmt.Println("  - skipped", b.Home, "(profile directory not found)")
			continue
		}
		gecko = true
		// Firefox and Zen name the caller by its add-on ID, which
		// manifest.firefox.json fixes. The UUID a profile hands the
		// add-on is not in the host manifest and is not needed: the ID
		// is the same in every profile, so the manifest is written the
		// first time, whether or not the add-on has been loaded.
		targets = append(targets, target{
			path:       filepath.Join(home, b.Hosts, nativemsg.Host+".json"),
			host:       path,
			extensions: []string{geckoID},
		})
	}
	if len(targets) == 0 {
		fmt.Println("No browser found in your home directory. Flux writes the host manifest when a browser is there.")
	}
	if nixBuild() {
		if err := copyNixExtension(dry); err != nil {
			return err
		}
	}
	for _, t := range targets {
		managed, err := managedHost(t)
		if err != nil {
			fmt.Println("  ✗", err)
			continue
		}
		if managed {
			fmt.Println("  -", t.path, "managed by Home Manager")
			continue
		}
		if dry {
			fmt.Println("  would write:", t.path)
			continue
		}
		if err := writeHost(t); err != nil {
			return err
		}
		fmt.Println("  ✓", t.path)
	}
	printExtensionHelp(chromium, gecko)
	return nil
}

func browserRemove(args []string) error {
	dry := false
	for _, a := range args {
		switch a {
		case "--dry-run", "-n":
			dry = true
		default:
			return fmt.Errorf("unknown option %q. Use --dry-run", a)
		}
	}
	home, err := os.UserHomeDir()
	if err != nil {
		return err
	}
	var dirs []string
	for _, config := range configHomes(home) {
		for _, rel := range chromiumBrowsers {
			dirs = append(dirs, filepath.Join(config, rel, "NativeMessagingHosts"))
		}
	}
	for _, b := range geckoBrowsers {
		dirs = append(dirs, filepath.Join(home, b.Hosts))
	}
	removed := 0
	for _, dir := range dirs {
		path := filepath.Join(dir, nativemsg.Host+".json")
		if info, err := os.Lstat(path); err == nil && info.Mode()&os.ModeSymlink != 0 {
			fmt.Println("  -", path, "managed by Home Manager")
			continue
		}
		body, err := os.ReadFile(path)
		if err != nil {
			continue
		}
		// Leave a file of another program alone, even with the same name.
		var m hostManifest
		if json.Unmarshal(body, &m) != nil || m.Name != nativemsg.Host {
			continue
		}
		if dry {
			fmt.Println("  would remove", path)
			removed++
			continue
		}
		if err := os.Remove(path); err != nil {
			return err
		}
		fmt.Println("  ✓ removed", path)
		removed++
	}
	if removed == 0 {
		fmt.Println("No Flux host manifest found.")
	}
	return nil
}

// writeHost writes one host manifest for a browser to read.
func writeHost(t target) error {
	body, err := json.MarshalIndent(manifestFor(t), "", "  ")
	if err != nil {
		return err
	}
	if err := os.MkdirAll(filepath.Dir(t.path), 0o755); err != nil {
		return err
	}
	return os.WriteFile(t.path, append(body, '\n'), 0o644)
}

func manifestFor(t target) hostManifest {
	return hostManifest{
		Name:              nativemsg.Host,
		Description:       "Flux host for the Flux browser extension",
		Path:              t.host,
		Type:              "stdio",
		AllowedOrigins:    t.origins,
		AllowedExtensions: t.extensions,
	}
}

func managedHost(t target) (bool, error) {
	info, err := os.Lstat(t.path)
	if errors.Is(err, os.ErrNotExist) {
		return false, nil
	}
	if err != nil {
		return false, err
	}
	if info.Mode()&os.ModeSymlink == 0 {
		return false, nil
	}
	body, err := os.ReadFile(t.path)
	if errors.Is(err, os.ErrNotExist) {
		return false, fmt.Errorf("%s is managed by Home Manager but its target is missing", t.path)
	}
	if err != nil {
		return false, err
	}
	var got hostManifest
	if err := json.Unmarshal(body, &got); err != nil {
		return false, fmt.Errorf("%s is managed by Home Manager but has invalid JSON: %w", t.path, err)
	}
	want := manifestFor(t)
	// 2 spellings of the same profile file are the same host.
	if a, err := filepath.EvalSymlinks(got.Path); err == nil {
		if b, err := filepath.EvalSymlinks(want.Path); err == nil && a == b {
			got.Path = want.Path
		}
	}
	if !reflect.DeepEqual(got, want) {
		return false, fmt.Errorf("%s is managed by Home Manager and differs from the requested manifest: got %+v, want %+v", t.path, got, want)
	}
	return true, nil
}

func copyNixExtension(dry bool) error {
	exe, err := browserExecutable()
	if err != nil {
		return err
	}
	src := filepath.Clean(filepath.Join(filepath.Dir(exe), "..", "share", "flux", "browser-extension"))
	if !isFile(filepath.Join(src, "manifest.json")) {
		return fmt.Errorf("Flux extension is missing from %s", src)
	}
	// The Chromium folder, then the Firefox one, whose manifest.json is the
	// Gecko manifest.
	for _, pair := range [][2]string{{src, userExtension()}, {firefoxDir(src), firefoxDir(userExtension())}} {
		if err := copyExtensionDir(pair[0], pair[1], dry); err != nil {
			return err
		}
	}
	return nil
}

// copyExtensionDir copies one unpacked extension folder as real files.
func copyExtensionDir(src, dst string, dry bool) error {
	if !isFile(filepath.Join(src, "manifest.json")) {
		return fmt.Errorf("Flux extension is missing from %s", src)
	}
	if info, err := os.Lstat(dst); err == nil && info.Mode()&os.ModeSymlink != 0 {
		// Chromium saves the real path of an unpacked extension, so a
		// link into /nix/store breaks after garbage collection.
		fmt.Println("  !", dst, "is a symlink, and a browser would save its store path. Remove it, then run flux-cli browser install again, or enable services.flux.browser in Home Manager")
		return nil
	} else if err != nil && !errors.Is(err, os.ErrNotExist) {
		return err
	}
	if dry {
		fmt.Println("  would copy", src, "to", dst)
		return nil
	}
	if err := os.MkdirAll(filepath.Dir(dst), 0o755); err != nil {
		return err
	}
	tmp, err := os.MkdirTemp(filepath.Dir(dst), ".browser-extension-")
	if err != nil {
		return err
	}
	defer os.RemoveAll(tmp)
	err = filepath.WalkDir(src, func(path string, entry os.DirEntry, walkErr error) error {
		if walkErr != nil {
			return walkErr
		}
		rel, err := filepath.Rel(src, path)
		if err != nil {
			return err
		}
		out := filepath.Join(tmp, rel)
		if entry.IsDir() {
			return os.MkdirAll(out, 0o755)
		}
		if !entry.Type().IsRegular() {
			return fmt.Errorf("unsupported extension file %s", path)
		}
		in, err := os.Open(path)
		if err != nil {
			return err
		}
		file, err := os.OpenFile(out, os.O_CREATE|os.O_EXCL|os.O_WRONLY, 0o644)
		if err != nil {
			in.Close()
			return err
		}
		_, copyErr := io.Copy(file, in)
		closeErr := file.Close()
		in.Close()
		if copyErr != nil {
			return copyErr
		}
		return closeErr
	})
	if err != nil {
		return err
	}
	if err := os.RemoveAll(dst); err != nil {
		return err
	}
	if err := os.Rename(tmp, dst); err != nil {
		return err
	}
	fmt.Println("  ✓ copied", dst)
	return nil
}

// printExtensionHelp prints how to load the extension, which no browser
// does on its own.
func printExtensionHelp(chromium, gecko bool) {
	fmt.Println()
	dir, ok := extensionDir()
	if !ok || strings.HasPrefix(dir, "/nix/store/") {
		if nixBuild() {
			fmt.Println("Run flux-cli browser install (without --dry-run) to copy the Flux extension to", userExtension())
		} else {
			fmt.Println("The Flux extension is missing. Run make install, or make browser")
		}
		return
	}
	fmt.Println("Load the Flux extension from", dir)
	if chromium {
		fmt.Println("  Chromium and friends: the extensions page, then Developer mode, then Load unpacked")
	}
	if gecko {
		// Firefox reads manifest.json from the folder of the file that is
		// picked, so Firefox and Zen get their own folder, whose
		// manifest.json is the Gecko manifest.
		fmt.Println("  Firefox and Zen: about:debugging, then Load temporary add-on, then")
		if ff := firefoxDir(dir); isFile(filepath.Join(ff, "manifest.json")) {
			fmt.Println("   ", filepath.Join(ff, "manifest.json"))
		} else if dist := filepath.Join(dir, "..", "dist", "firefox", "manifest.json"); isFile(dist) {
			fmt.Println("   ", filepath.Clean(dist))
		} else {
			fmt.Println("    run make browser, then pick browser/dist/firefox/manifest.json")
		}
	}
}

// firefoxDir is the Firefox and Zen folder next to the Chromium folder dir.
func firefoxDir(dir string) string { return dir + "-firefox" }

// extensionDir prefers the user copy, then the package share folder,
// system install, or checkout.
func extensionDir() (string, bool) {
	if dir := userExtension(); isFile(filepath.Join(dir, "manifest.json")) {
		return dir, true
	}
	exe, err := browserExecutable()
	if err == nil {
		share := filepath.Join(filepath.Dir(exe), "..", "share", "flux", "browser-extension")
		if isFile(filepath.Join(share, "manifest.json")) {
			return filepath.Clean(share), true
		}
	}
	if isFile(filepath.Join(installedExtension, "manifest.json")) {
		return installedExtension, true
	}
	if err != nil {
		return "", false
	}
	dir := filepath.Join(filepath.Dir(exe), "..", "browser", "extension")
	if isFile(filepath.Join(dir, "manifest.json")) {
		return filepath.Clean(dir), true
	}
	return "", false
}

// configHomes returns the directories where the browsers keep their
// profiles. A Chromium browser follows XDG_CONFIG_HOME, so Flux writes
// there as well as in the default place.
func configHomes(home string) []string {
	dirs := []string{filepath.Join(home, ".config")}
	if xdg := os.Getenv("XDG_CONFIG_HOME"); xdg != "" && xdg != dirs[0] {
		dirs = append(dirs, xdg)
	}
	return dirs
}

func configHome(home string) string {
	if xdg := os.Getenv("XDG_CONFIG_HOME"); xdg != "" {
		return xdg
	}
	return filepath.Join(home, ".config")
}

// hostPath returns the flux-native that the browser must start. A browser
// does not read the PATH of the session that installed Flux, so the
// manifest needs the whole path.
func hostPath() (string, error) {
	home, _ := os.UserHomeDir()
	if nixBuild() {
		for _, dir := range nixHostCandidates() {
			path := filepath.Join(dir, hostBinary)
			if isFile(path) {
				return path, nil
			}
		}
		return "", errors.New("flux-native is not in a stable Nix profile. Install Flux with nix profile install, or enable its NixOS or Home Manager module")
	}
	if exe, err := browserExecutable(); err == nil {
		next := filepath.Join(filepath.Dir(exe), hostBinary)
		if isFile(next) {
			return next, nil
		}
	}
	for _, dir := range []string{"/usr/bin", "/usr/lib/flux/bin", filepath.Join(home, ".local", "bin")} {
		if isFile(filepath.Join(dir, hostBinary)) {
			return filepath.Join(dir, hostBinary), nil
		}
	}
	return "", fmt.Errorf("%s is not installed. Run make build, or install Flux", hostBinary)
}

func isDir(path string) bool {
	fi, err := os.Stat(path)
	return err == nil && fi.IsDir()
}

func isFile(path string) bool {
	fi, err := os.Stat(path)
	return err == nil && fi.Mode().IsRegular()
}
