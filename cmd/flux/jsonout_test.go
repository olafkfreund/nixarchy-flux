package main

import (
	"encoding/json"
	"errors"
	"os"
	"os/exec"
	"path/filepath"
	"reflect"
	"strings"
	"testing"

	"flux/internal/ipc"
)

func TestMain(m *testing.M) {
	if os.Getenv("FLUXCLI_TEST_MAIN") == "1" {
		main()
		return
	}
	os.Exit(m.Run())
}

func TestSplitJSON(t *testing.T) {
	cases := []struct {
		in   string
		out  string
		json bool
	}{
		{"--json status", "status", true}, {"status --json", "status", true},
		{"--json --device P status", "--device P status", true},
		{"--device P --json status", "--device P status", true},
		{"webcam start --device P --json", "webcam start --device P", true},
		{"ssh key --json", "ssh key", true}, {"sms thread 12 --json", "sms thread 12", true},
		{"sms --json threads", "sms threads", true},
		{"sms --device P threads --json", "sms --device P threads", true},
		{"settings --json checkUpdates", "settings checkUpdates", true},
		{"settings checkUpdates --json", "settings checkUpdates", true},
		{"notifications --device P reply ID --json", "notifications --device P reply ID --json", false},
		{"--device=P clip --json", "--device=P clip --json", false},
		{"clip --json", "clip --json", false},
		{"ssh key -- --json", "ssh key -- --json", false},
		{"status -- --json", "status -- --json", false},
		{"status --json -- body", "status -- body", true},
		{"notify --json Title", "notify --json Title", false},
		{"--device=P status --json", "--device=P status", true},
	}
	for _, form := range []string{"ping", "clip", "text", "notify", "send", "sms NUMBER", "commands add", "notifications reply ID", "snippets search", "settings KEY VALUE"} {
		cases = append(cases, struct {
			in   string
			out  string
			json bool
		}{form + " --json", form + " --json", false})
	}
	for _, c := range cases {
		t.Run(c.in, func(t *testing.T) {
			got := splitJSON(strings.Fields(c.in))
			if !reflect.DeepEqual(got, strings.Fields(c.out)) || jsonMode != c.json {
				t.Fatalf("got %q, json=%v; want %q, json=%v", got, jsonMode, c.out, c.json)
			}
		})
	}
}

func cliProcess(t *testing.T, args ...string) (string, string, int) {
	t.Helper()
	cmd := exec.Command(os.Args[0], args...)
	root := t.TempDir()
	cmd.Env = append(os.Environ(), "FLUXCLI_TEST_MAIN=1", "HOME="+root, "XDG_CONFIG_HOME="+root)
	var stderr strings.Builder
	cmd.Stderr = &stderr
	out, err := cmd.Output()
	if err == nil {
		return string(out), stderr.String(), 0
	}
	var exit *exec.ExitError
	if !errors.As(err, &exit) {
		t.Fatal(err)
	}
	return string(out), stderr.String(), exit.ExitCode()
}

func errorCode(t *testing.T, out string) string {
	t.Helper()
	var v struct {
		Error struct {
			Code    string `json:"code"`
			Message string `json:"message"`
		} `json:"error"`
	}
	if err := json.Unmarshal([]byte(out), &v); err != nil {
		t.Fatal(err)
	}
	if v.Error.Code == "" || v.Error.Message == "" {
		t.Fatalf("bad error: %q", out)
	}
	return v.Error.Code
}

func TestJSONErrors(t *testing.T) {
	t.Setenv("FLUX_SOCKET", filepath.Join(t.TempDir(), "missing.sock"))
	for _, c := range []struct {
		args []string
		code string
		exit int
	}{
		{[]string{"--json", "bogus"}, "unknown_command", 1},
		{[]string{"bogus"}, "", 2},
		{[]string{"--json", "ring"}, "daemon_down", 1},
		{[]string{"--json", "sms"}, "bad_args", 1},
		{[]string{"--json", "run"}, "bad_args", 1},
		{[]string{"--json", "--help"}, "no_json", 1},
		{[]string{"--json", "--version"}, "no_json", 1},
		{[]string{"--json"}, "no_json", 1},
	} {
		out, stderr, code := cliProcess(t, c.args...)
		if code != c.exit {
			t.Errorf("%q: exit %d, want %d: %s", c.args, code, c.exit, stderr)
		}
		if c.code != "" && errorCode(t, out) != c.code {
			t.Errorf("%q: %s", c.args, out)
		}
		if stderr == "" {
			t.Errorf("%q: no stderr", c.args)
		}
	}
}

func TestJSONSocket(t *testing.T) {
	h := &recordHandler{
		state:   map[string]any{"self": map[string]any{"name": "Desk"}, "devices": []any{}},
		results: map[string]any{"ssh.keys": map[string]any{"socket": "/tmp/ssh", "keys": []any{map[string]any{"device": "p", "name": "Phone", "key": "ssh-ed25519 x", "fingerprint": "fp"}, map[string]any{"device": "q", "name": "Bad\x1bName", "key": "ssh-ed25519 y", "fingerprint": "fp"}}}},
		errs:    map[string]error{"ring": &ipc.Error{Code: "offline", Message: "Phone is offline"}},
	}
	serveRecord(t, h)
	for _, c := range []struct {
		args []string
		want string
		code string
	}{
		{[]string{"status", "--json"}, `"Desk"`, ""},
		{[]string{"ssh", "--json"}, `"socket": "/tmp/ssh"`, ""},
		{[]string{"ssh", "key", "--device", "p", "--json"}, `"ssh-ed25519 x"`, ""},
		{[]string{"ssh", "--json"}, `Bad�Name`, ""},
		{[]string{"--json", "ring"}, "", "offline"},
	} {
		out, _, exit := cliProcess(t, c.args...)
		if c.code != "" {
			if exit != 1 || errorCode(t, out) != c.code {
				t.Errorf("%q: %q, exit %d", c.args, out, exit)
			}
		} else if exit != 0 || !strings.Contains(out, c.want) {
			t.Errorf("%q: %q, exit %d", c.args, out, exit)
		}
	}
	out, _, exit := cliProcess(t, "ssh", "key", "--", "--json")
	if exit != 0 || strings.HasPrefix(out, "{") || !strings.Contains(out, "ssh-ed25519 x") {
		t.Fatalf("ssh key text after --: %q, exit %d", out, exit)
	}
	h.mu.Lock()
	h.results["ssh.keys"] = map[string]any{"socket": "/tmp/ssh", "keys": []any{}}
	h.mu.Unlock()
	out, _, exit = cliProcess(t, "ssh", "--json")
	if exit != 0 || !strings.Contains(out, `"keys": []`) {
		t.Fatalf("ssh without keys: %q, exit %d", out, exit)
	}
	out, _, exit = cliProcess(t, "--json", "notify", "Title", "--run")
	if exit != 0 || strings.TrimSpace(out) != "{}" {
		t.Fatalf("notify text: %q, exit %d", out, exit)
	}
	h.mu.Lock()
	calls := append([]string(nil), h.calls...)
	h.mu.Unlock()
	if !strings.Contains(strings.Join(calls, "\n"), `"body":"--run"`) {
		t.Fatalf("notify text calls: %q", calls)
	}
	flag := filepath.Join(t.TempDir(), "must-not-exist")
	h.mu.Lock()
	count := len(h.calls)
	h.mu.Unlock()
	out, _, exit = cliProcess(t, "--json", "notify", "--run", "--", "touch", flag)
	if exit != 1 || errorCode(t, out) != "no_json" {
		t.Fatalf("refusal: %q, exit %d", out, exit)
	}
	if _, err := os.Stat(flag); !os.IsNotExist(err) {
		t.Fatalf("child ran: %v", err)
	}
	h.mu.Lock()
	defer h.mu.Unlock()
	if len(h.calls) != count {
		t.Fatalf("refusal called socket: %q", h.calls[count:])
	}
}

func TestRawHelpers(t *testing.T) {
	root := t.TempDir()
	t.Setenv("HOME", root)
	t.Setenv("XDG_CONFIG_HOME", root)
	h := &recordHandler{state: map[string]any{"devices": []any{
		map[string]any{"id": "p1", "name": "Phone", "paired": true, "extra": "kept"},
		map[string]any{"id": "p2", "name": "phone", "paired": true},
		map[string]any{"id": "x", "name": "Guest", "paired": false},
	}}, results: map[string]any{"empty": nil}}
	serveRecord(t, h)
	var result struct{ Value string }
	if err := callInto("empty", nil, &result); err != nil {
		t.Fatal(err)
	}
	for _, c := range []struct {
		device, code string
		count        int
	}{
		{"", "", 2}, {"p1", "", 1}, {"PHONE", "ambiguous", 0}, {"missing", "not_found", 0},
	} {
		got, err := devicesRaw(c.device)
		var e *ipc.Error
		if c.code != "" {
			if !errors.As(err, &e) || e.Code != c.code {
				t.Errorf("%q: %v", c.device, err)
			}
		} else if err != nil || len(got) != c.count {
			t.Errorf("%q: %d devices, %v", c.device, len(got), err)
		}
		if c.device == "p1" && string(got["p1"]["extra"]) != `"kept"` {
			t.Errorf("raw field lost: %v", got)
		}
	}
	if string(safeJSON(json.RawMessage(`{"message":"x‮y"}`))) != `{"message":"x\u202ey"}` {
		t.Fatal("bidi control was not escaped")
	}
}
