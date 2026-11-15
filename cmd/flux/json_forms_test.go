package main

import (
	"bytes"
	"encoding/json"
	"io"
	"os"
	"strings"
	"testing"
)

func runCLI(t *testing.T, args ...string) string {
	t.Helper()
	oldArgs, oldOut := os.Args, os.Stdout
	r, w, err := os.Pipe()
	if err != nil {
		t.Fatal(err)
	}
	os.Args = append([]string{"flux-cli"}, args...)
	os.Stdout = w
	defer func() {
		os.Args, os.Stdout = oldArgs, oldOut
	}()
	main()
	w.Close()
	var buf bytes.Buffer
	if _, err := io.Copy(&buf, r); err != nil {
		t.Fatal(err)
	}
	r.Close()
	return buf.String()
}

func newJSONFormsHandler() *recordHandler {
	methods := []string{
		"discover", "ring", "ping", "clipboard.send", "share.url", "commands.run",
		"notify.send", "sms.send", "addresses.add", "addresses.remove", "share.files",
		"transfer.retry", "transfer.cancel", "clipboard.pause", "clipboard.resume",
		"clipboard.clear", "clipboard.delete", "clipboard.pin", "clipboard.unpin",
		"clipboard.copy", "settings.set", "notification.dismissAll",
		"notification.rule.add", "notification.rule.remove",
		"automation.add", "automation.remove",
		"device.settings", "device.settings.set",
		"commands.add", "commands.remove",
		"webcam.start", "webcam.stop", "webcam.config",
		"mic.start", "mic.stop",
		"speaker.start", "speaker.stop",
		"screen.stop", "desktop.stop", "browse.stop",
		"motion.start", "motion.stop",
	}
	results := make(map[string]any, len(methods)+1)
	for _, m := range methods {
		results[m] = map[string]any{"m": m}
	}
	results["notification.dismissAll"] = map[string]any{"m": "notification.dismissAll", "dismissed": 3}
	results["clipboard.search"] = []any{
		map[string]any{"id": "c1", "text": "clip\nbody\u202e\x7f", "pinned": true},
	}
	results["clipboard.pin"] = map[string]any{"m": "clipboard.pin", "note": "pin\u202e"}
	results["device.settings"] = map[string]any{"m": "device.settings", "device": "p1", "note": "dev\u202e"}

	return &recordHandler{
		state: map[string]any{
			"self":            map[string]any{"id": "s1", "name": "Desk", "type": "desktop", "tcpPort": 12100},
			"clipboardPaused": true,
			"settings": map[string]any{
				"autoClipboard":     false,
				"clipboardLimit":    50,
				"remoteInput":       true,
				"remoteDesktop":     false,
				"motionCues":        true,
				"motionAuto":        false,
				"motionSensitivity": 1.0,
				"motionSize":        "medium",
				"motionIdle":        60,
				"notificationRules": []any{map[string]any{"id": "r1", "app": "Chat\u202e\x7f", "mode": "silent"}},
				"automationRules":   []any{map[string]any{"id": "a1", "event": "battery.low", "command": "cmd1", "label": "low\u202e\x7f"}},
			},
			"devices": []any{
				map[string]any{
					"id":        "p1",
					"name":      "Pixel 8",
					"paired":    true,
					"online":    true,
					"addresses": []any{"10.0.0.1", "pixel.tail"},
					"notifications": []any{
						map[string]any{
							"id":          "n1",
							"app":         "Signal",
							"title":       "Alice",
							"text":        "line1\nline2\u202e\x7f",
							"time":        1700000000,
							"replyId":     "rep-1",
							"actions":     []any{"Mark read"},
							"dismissable": true,
						},
					},
				},
				map[string]any{
					"id":            "p2",
					"name":          "Tab S9",
					"paired":        true,
					"online":        false,
					"addresses":     []any{},
					"notifications": []any{},
				},
				map[string]any{
					"id":     "u1",
					"name":   "Unpaired",
					"paired": false,
				},
			},
			"transfers": []any{
				map[string]any{"id": "t1", "name": "photo\u202e.jpg", "state": "done", "done": 100, "size": 100, "extra": "kept"},
			},
			"commands": []any{
				map[string]any{"id": "cmd1", "name": "Lock", "command": "loginctl lock-session", "extra": "kept"},
			},
			"webcam":  map[string]any{"active": true, "device": "/dev/video0", "fromName": "Pixel 8", "width": 1920, "height": 1080, "fps": 30},
			"mic":     map[string]any{"active": true, "source": "flux-mic", "fromName": "Pixel 8", "rate": 48000, "channels": 1},
			"speaker": map[string]any{"active": true, "toName": "Pixel 8", "rate": 48000, "channels": 2},
			"screen":  map[string]any{"active": true, "fromName": "Pixel 8", "width": 1080, "height": 2400, "player": "mpv"},
			"desktop": map[string]any{"active": true, "toName": "Pixel 8", "monitor": "DP-1", "width": 1920, "height": 1080, "extend": false},
			"browse":  []any{map[string]any{"device": "p1", "name": "Pixel 8", "since": 1700000000}},
			"motion":  map[string]any{"running": true, "deviceName": "Pixel 8", "auto": false, "active": true},
		},
		results: results,
	}
}

// parseOneJSON verifies that out contains exactly one valid JSON value and no
// unescaped control or bidi characters.
func parseOneJSON(t *testing.T, out string) any {
	t.Helper()
	if strings.ContainsRune(out, '\u202e') || strings.ContainsRune(out, '\x7f') {
		t.Fatalf("unescaped control character in JSON output: %q", out)
	}
	dec := json.NewDecoder(strings.NewReader(out))
	dec.UseNumber()
	var val any
	if err := dec.Decode(&val); err != nil {
		t.Fatalf("invalid JSON %q: %v", out, err)
	}
	var extra any
	if err := dec.Decode(&extra); err != io.EOF {
		t.Fatalf("more than one JSON value in %q: extra=%v err=%v", out, extra, err)
	}
	return val
}

func compactJSON(t *testing.T, s string) string {
	t.Helper()
	var buf bytes.Buffer
	if err := json.Compact(&buf, []byte(s)); err != nil {
		t.Fatalf("compact %q: %v", s, err)
	}
	return buf.String()
}

func TestJSONExistingForms(t *testing.T) {
	h := newJSONFormsHandler()
	serveRecord(t, h)

	cases := []struct {
		args     []string
		wantCall string
		wantJSON string
		mustHave string
	}{
		// Action forms through act
		{[]string{"--json", "discover"}, `discover `, `{"m":"discover"}`, ""},
		{[]string{"--json", "ring"}, `ring {"device":""}`, `{"m":"ring"}`, ""},
		{[]string{"--json", "ping", "hello", "world"}, `ping {"device":"","message":"hello world"}`, `{"m":"ping"}`, ""},
		{[]string{"--json", "clip", "copied", "text"}, `clipboard.send {"device":"","text":"copied text"}`, `{"m":"clipboard.send"}`, ""},
		{[]string{"--json", "url", "https://example.com"}, `share.url {"device":"","url":"https://example.com"}`, `{"m":"share.url"}`, ""},
		{[]string{"--json", "run", "cmd1"}, `commands.run {"id":"cmd1"}`, `{"m":"commands.run"}`, ""},
		{[]string{"--json", "notify", "Build", "done", "now"}, `notify.send {"body":"done now","device":"","title":"Build"}`, `{"m":"notify.send"}`, ""},
		{[]string{"--json", "sms", "+1555000", "hi", "there"}, `sms.send {"addresses":["+1555000"],"body":"hi there","device":""}`, `{"m":"sms.send"}`, ""},

		// Addresses
		{[]string{"--json", "addresses"}, `state `, `{"p1":["10.0.0.1","pixel.tail"],"p2":[]}`, ""},
		{[]string{"--json", "addresses", "--device", "Tab S9"}, `state `, `{"p2":[]}`, ""},
		{[]string{"--json", "addresses", "add", "10.0.0.2"}, `addresses.add {"address":"10.0.0.2","device":""}`, `{"m":"addresses.add"}`, ""},
		{[]string{"--json", "addresses", "remove", "10.0.0.2"}, `addresses.remove {"address":"10.0.0.2","device":""}`, `{"m":"addresses.remove"}`, ""},

		// Send & transfers
		{[]string{"--json", "send", "/tmp/file.txt"}, `share.files {"device":"","paths":["/tmp/file.txt"]}`, `{"m":"share.files"}`, ""},
		{[]string{"--json", "transfers"}, `state `, `[{"done":100,"extra":"kept","id":"t1","name":"photo\u202e.jpg","size":100,"state":"done"}]`, `\u202e`},
		{[]string{"--json", "transfers", "retry", "t1"}, `transfer.retry {"id":"t1"}`, `{"m":"transfer.retry"}`, ""},
		{[]string{"--json", "transfers", "cancel", "t1"}, `transfer.cancel {"id":"t1"}`, `{"m":"transfer.cancel"}`, ""},

		// Clipboard & snippets
		{[]string{"--json", "clipboard"}, `clipboard.search `, `[{"id":"c1","pinned":true,"text":"clip\nbody\u202e\u007f"}]`, `\u202e`},
		{[]string{"--json", "snippets"}, `clipboard.search `, `[{"id":"c1","pinned":true,"text":"clip\nbody\u202e\u007f"}]`, `\u202e`},
		{[]string{"--json", "snippets", "search", "needle"}, `clipboard.search {"text":"needle"}`, `[{"id":"c1","pinned":true,"text":"clip\nbody\u202e\u007f"}]`, `\u202e`},
		{[]string{"--json", "clipboard", "status"}, `state `, `{"paused":true,"autoClipboard":false}`, ""},
		{[]string{"--json", "clipboard", "pause"}, `clipboard.pause `, `{"m":"clipboard.pause"}`, ""},
		{[]string{"--json", "clipboard", "resume"}, `clipboard.resume `, `{"m":"clipboard.resume"}`, ""},
		{[]string{"--json", "clipboard", "clear"}, `clipboard.clear {"all":false}`, `{"m":"clipboard.clear"}`, ""},
		{[]string{"--json", "clipboard", "clear", "--all"}, `clipboard.clear {"all":true}`, `{"m":"clipboard.clear"}`, ""},
		{[]string{"--json", "clipboard", "delete", "c1"}, `clipboard.delete {"id":"c1"}`, `{"m":"clipboard.delete"}`, ""},
		{[]string{"--json", "snippets", "save", "c1"}, `clipboard.pin {"id":"c1"}`, `{"m":"clipboard.pin","note":"pin\u202e"}`, `\u202e`},
		{[]string{"--json", "snippets", "remove", "c1"}, `clipboard.unpin {"id":"c1"}`, `{"m":"clipboard.unpin"}`, ""},
		{[]string{"--json", "snippets", "copy", "c1"}, `clipboard.copy {"id":"c1"}`, `{"m":"clipboard.copy"}`, ""},
		{[]string{"--json", "clipboard", "limit"}, `state `, `{"clipboardLimit":50}`, ""},
		{[]string{"--json", "clipboard", "limit", "100"}, `settings.set {"key":"clipboardLimit","value":100}`, `{"m":"settings.set"}`, ""},

		// Notifications
		{[]string{"--json", "notifications"}, `state `, `{"p1":[{"actions":["Mark read"],"app":"Signal","dismissable":true,"id":"n1","replyId":"rep-1","text":"line1\nline2\u202e\u007f","time":1700000000,"title":"Alice"}],"p2":[]}`, `\u202e`},
		{[]string{"--json", "--device", "p1", "notifications"}, `state `, `{"p1":[{"actions":["Mark read"],"app":"Signal","dismissable":true,"id":"n1","replyId":"rep-1","text":"line1\nline2\u202e\u007f","time":1700000000,"title":"Alice"}]}`, `\u007f`},
		{[]string{"--json", "notifications", "clear"}, `notification.dismissAll {"device":""}`, `{"dismissed":3,"m":"notification.dismissAll"}`, ""},

		// Rules, automation, device-settings
		{[]string{"--json", "notification-rules"}, `state `, `[{"app":"Chat\u202e\u007f","id":"r1","mode":"silent"}]`, `\u202e`},
		{[]string{"--json", "automation"}, `state `, `[{"command":"cmd1","event":"battery.low","id":"a1","label":"low\u202e\u007f"}]`, `\u202e`},
		{[]string{"--json", "device-settings"}, `device.settings {"device":""}`, `{"device":"p1","m":"device.settings","note":"dev\u202e"}`, `\u202e`},
		{[]string{"--json", "notification-rules", "add", "Chat", "silent"}, `notification.rule.add {"config":{"app":"Chat","device":"","mode":"silent"}}`, `{"m":"notification.rule.add"}`, ""},
		{[]string{"--json", "notification-rules", "quiet", "22:00", "07:00"}, `notification.rule.add {"config":{"app":"*","device":"","end":"07:00","mode":"silent","start":"22:00"}}`, `{"m":"notification.rule.add"}`, ""},
		{[]string{"--json", "notification-rules", "remove", "r1"}, `notification.rule.remove {"id":"r1"}`, `{"m":"notification.rule.remove"}`, ""},
		{[]string{"--json", "automation", "add", "battery.low", "cmd1", "20"}, `automation.add {"config":{"below":20,"command":"cmd1","event":"battery.low"}}`, `{"m":"automation.add"}`, ""},
		{[]string{"--json", "automation", "remove", "a1"}, `automation.remove {"id":"a1"}`, `{"m":"automation.remove"}`, ""},
		{[]string{"--json", "--device", "p1", "device-settings", "remoteInput", "true"}, `device.settings.set {"device":"p1","key":"remoteInput","value":true}`, `{"m":"device.settings.set"}`, ""},

		// Commands
		{[]string{"--json", "commands"}, `state `, `[{"command":"loginctl lock-session","extra":"kept","id":"cmd1","name":"Lock"}]`, ""},
		{[]string{"--json", "commands", "add", "Lock", "loginctl", "lock-session"}, `commands.add {"command":"loginctl lock-session","name":"Lock"}`, `{"m":"commands.add"}`, ""},
		{[]string{"--json", "commands", "remove", "cmd1"}, `commands.remove {"id":"cmd1"}`, `{"m":"commands.remove"}`, ""},

		// Streams, desktop, input, motion status forms
		{[]string{"--json", "webcam"}, `state `, `{"active":true,"device":"/dev/video0","fps":30,"fromName":"Pixel 8","height":1080,"width":1920}`, ""},
		{[]string{"--json", "mic"}, `state `, `{"active":true,"channels":1,"fromName":"Pixel 8","rate":48000,"source":"flux-mic"}`, ""},
		{[]string{"--json", "speaker"}, `state `, `{"active":true,"channels":2,"rate":48000,"toName":"Pixel 8"}`, ""},
		{[]string{"--json", "screen"}, `state `, `{"active":true,"fromName":"Pixel 8","height":2400,"player":"mpv","width":1080}`, ""},
		{[]string{"--json", "desktop"}, `state `, `{"active":true,"extend":false,"height":1080,"monitor":"DP-1","toName":"Pixel 8","width":1920}`, ""},
		{[]string{"--json", "browse"}, `state `, `[{"device":"p1","name":"Pixel 8","since":1700000000}]`, ""},
		{[]string{"--json", "motion"}, `state `, `{"active":true,"auto":false,"deviceName":"Pixel 8","running":true}`, ""},
		{[]string{"--json", "motion", "status"}, `state `, `{"active":true,"auto":false,"deviceName":"Pixel 8","running":true}`, ""},
		{[]string{"--json", "input"}, `state `, `{"remoteInput":true}`, ""},

		// Streams, desktop, input, motion actions
		{[]string{"--json", "webcam", "start", "--device", "p1"}, `webcam.start {"device":"p1"}`, `{"m":"webcam.start"}`, ""},
		{[]string{"--json", "webcam", "stop"}, `webcam.stop `, `{"m":"webcam.stop"}`, ""},
		{[]string{"--json", "webcam", "set", "aspect=16:9"}, `webcam.config {"config":{"aspect":"16:9"}}`, `{"m":"webcam.config"}`, ""},
		{[]string{"--json", "webcam", "reset"}, `webcam.config {"reset":true}`, `{"m":"webcam.config"}`, ""},
		{[]string{"--json", "mic", "start", "--device", "p1"}, `mic.start {"device":"p1"}`, `{"m":"mic.start"}`, ""},
		{[]string{"--json", "mic", "stop"}, `mic.stop `, `{"m":"mic.stop"}`, ""},
		{[]string{"--json", "speaker", "start", "--device", "p1"}, `speaker.start {"device":"p1"}`, `{"m":"speaker.start"}`, ""},
		{[]string{"--json", "speaker", "stop"}, `speaker.stop `, `{"m":"speaker.stop"}`, ""},
		{[]string{"--json", "screen", "stop"}, `screen.stop `, `{"m":"screen.stop"}`, ""},
		{[]string{"--json", "desktop", "stop"}, `desktop.stop `, `{"m":"desktop.stop"}`, ""},
		{[]string{"--json", "--device", "p1", "browse", "stop"}, `browse.stop {"device":"p1"}`, `{"m":"browse.stop"}`, ""},
		{[]string{"--json", "--device", "p1", "motion", "start"}, `motion.start {"device":"p1"}`, `{"m":"motion.start"}`, ""},
		{[]string{"--json", "motion", "stop"}, `motion.stop `, `{"m":"motion.stop"}`, ""},
		{[]string{"--json", "input", "on"}, `settings.set {"key":"remoteInput","value":true}`, `{"m":"settings.set"}`, ""},
		{[]string{"--json", "input", "off"}, `settings.set {"key":"remoteInput","value":false}`, `{"m":"settings.set"}`, ""},
		{[]string{"--json", "desktop", "on"}, `settings.set {"key":"remoteDesktop","value":true}`, `{"m":"settings.set"}`, ""},
		{[]string{"--json", "desktop", "off"}, `settings.set {"key":"remoteDesktop","value":false}`, `{"m":"settings.set"}`, ""},
		{[]string{"--json", "motion", "on"}, `settings.set {"key":"motionCues","value":true}`, `{"m":"settings.set"}`, ""},
		{[]string{"--json", "motion", "off"}, `settings.set {"key":"motionCues","value":false}`, `{"m":"settings.set"}`, ""},
		{[]string{"--json", "motion", "auto", "on"}, `settings.set {"key":"motionAuto","value":true}`, `{"m":"settings.set"}`, ""},
		{[]string{"--json", "motion", "auto", "off"}, `settings.set {"key":"motionAuto","value":false}`, `{"m":"settings.set"}`, ""},
		{[]string{"--json", "motion", "sensitivity", "1.6"}, `settings.set {"key":"motionSensitivity","value":1.6}`, `{"m":"settings.set"}`, ""},
		{[]string{"--json", "motion", "size", "Large"}, `settings.set {"key":"motionSize","value":"large"}`, `{"m":"settings.set"}`, ""},
		{[]string{"--json", "motion", "idle", "120"}, `settings.set {"key":"motionIdle","value":120}`, `{"m":"settings.set"}`, ""},
	}

	for _, c := range cases {
		t.Run(strings.Join(c.args, "_"), func(t *testing.T) {
			h.mu.Lock()
			h.calls = nil
			h.mu.Unlock()

			out := runCLI(t, c.args...)
			parseOneJSON(t, out)
			if c.mustHave != "" && !strings.Contains(out, c.mustHave) {
				t.Fatalf("stdout %q missing %q", out, c.mustHave)
			}
			if got := compactJSON(t, out); got != c.wantJSON {
				t.Fatalf("got JSON %s, want %s", got, c.wantJSON)
			}

			h.mu.Lock()
			calls := append([]string(nil), h.calls...)
			h.mu.Unlock()
			if len(calls) != 1 || calls[0] != c.wantCall {
				t.Fatalf("calls = %q, want [%q]", calls, c.wantCall)
			}
		})
	}

	// Verify notification newline and control characters round-trip intact when decoded.
	out := runCLI(t, "--json", "--device", "p1", "notifications")
	var byDev map[string][]map[string]any
	if err := json.Unmarshal([]byte(out), &byDev); err != nil {
		t.Fatal(err)
	}
	if got := byDev["p1"][0]["text"]; got != "line1\nline2\u202e\x7f" {
		t.Fatalf("notification text = %q, want line1\\nline2\\u202e\\x7f", got)
	}
	if got := byDev["p1"][0]["replyId"]; got != "rep-1" {
		t.Fatalf("notification replyId = %v, want rep-1", got)
	}

	// devices --json returns the full state with control characters escaped.
	h.mu.Lock()
	h.calls = nil
	h.mu.Unlock()
	out = runCLI(t, "devices", "--json")
	stateVal, ok := parseOneJSON(t, out).(map[string]any)
	if !ok || stateVal["self"] == nil || stateVal["devices"] == nil {
		t.Fatalf("devices --json did not return full state: %q", out)
	}
	if !strings.Contains(out, `\u202e`) || !strings.Contains(out, `\u007f`) {
		t.Fatalf("devices --json did not escape control characters: %q", out)
	}
	h.mu.Lock()
	if len(h.calls) != 1 || h.calls[0] != "state " {
		t.Fatalf("devices --json calls = %q", h.calls)
	}
	h.mu.Unlock()

	// Device-scoped reads: not_found on unknown device, and {} when no paired devices exist.
	for _, cmd := range []string{"addresses", "notifications"} {
		out, _, exit := cliProcess(t, "--json", "--device", "missing", cmd)
		if exit != 1 || errorCode(t, out) != "not_found" {
			t.Fatalf("%s missing device: %q, exit %d", cmd, out, exit)
		}
	}

	h.mu.Lock()
	h.state["devices"] = []any{map[string]any{"id": "u1", "name": "Unpaired", "paired": false}}
	h.mu.Unlock()
	for _, cmd := range []string{"addresses", "notifications"} {
		out := runCLI(t, "--json", cmd)
		if strings.TrimSpace(out) != "{}" {
			t.Fatalf("%s with no paired devices: %q", cmd, out)
		}
	}
}
