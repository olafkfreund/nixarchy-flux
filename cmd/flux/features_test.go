package main

import (
	"encoding/json"
	"strings"
	"testing"
	"time"
)

func newPhoneHandler() *recordHandler {
	return &recordHandler{
		state: map[string]any{
			"self": map[string]any{"name": "Desk"},
			"settings": map[string]any{
				"checkUpdates": true, "name": "old", "downloadDir": "/d", "motionSensitivity": 1.0,
				"notificationRules": []any{}, "automationRules": []any{}, "deviceRules": map[string]any{},
			},
			"devices": []any{
				map[string]any{"id": "p1", "name": "Phone", "paired": true, "online": true,
					"dnd": map[string]any{"on": true, "by": "phone", "at": 1},
					"conversations": []any{
						map[string]any{"thread": 1, "name": "Ann", "last": "old", "time": 10},
						map[string]any{"thread": 2, "name": "Bob", "last": "new", "time": 20},
					}},
				map[string]any{"id": "p2", "name": "Tablet", "paired": true, "online": false,
					"dnd": map[string]any{"on": false, "by": "phone", "at": 1}},
				map[string]any{"id": "x", "name": "Guest", "paired": false},
			},
		},
		results: map[string]any{
			"sms.thread": map[string]any{"messages": []any{
				map[string]any{"name": "Ann", "body": "hi", "time": 1700000000, "outgoing": false},
				map[string]any{"name": "Ann", "body": "yo", "time": 1700000060, "outgoing": true},
			}},
			"notification.dismiss": map[string]any{"m": "dismiss"},
		},
	}
}

func (h *recordHandler) lastCall() string {
	h.mu.Lock()
	defer h.mu.Unlock()
	return h.calls[len(h.calls)-1]
}

func TestNewCommandsCalls(t *testing.T) {
	h := newPhoneHandler()
	serveRecord(t, h)
	for _, c := range []struct {
		args []string
		want string
	}{
		{[]string{"notifications", "--device", "p1", "dismiss", "n1"}, `notification.dismiss {"device":"p1","id":"n1"}`},
		{[]string{"notifications", "reply", "n1", "on", "my", "way"}, `notification.reply {"device":"","id":"n1","message":"on my way"}`},
		{[]string{"notifications", "action", "n1", "Mute"}, `notification.action {"action":"Mute","device":"","id":"n1"}`},
		{[]string{"sms", "-d", "p1", "refresh"}, `sms.refresh {"device":"p1"}`},
		{[]string{"sms", "thread", "12"}, `sms.thread {"device":"","thread":12}`},
		{[]string{"dnd", "off"}, `dnd.set {"device":"","on":false}`},
		{[]string{"dnd", "on"}, `dnd.set {"device":"","on":true}`},
		{[]string{"transfers", "open", "t1"}, `transfer.open {"id":"t1"}`},
		{[]string{"transfers", "copy", "t1"}, `transfer.copy {"id":"t1"}`},
		{[]string{"text", "hello", "there"}, `share.text {"device":"","text":"hello there"}`},
		{[]string{"settings", "checkUpdates", "off"}, `settings.set {"key":"checkUpdates","value":false}`},
		{[]string{"settings", "clipboardLimit", "50"}, `settings.set {"key":"clipboardLimit","value":50}`},
		{[]string{"settings", "motionIdle", "60"}, `settings.set {"key":"motionIdle","value":60}`},
		{[]string{"settings", "motionSensitivity", "1.6"}, `settings.set {"key":"motionSensitivity","value":1.6}`},
		{[]string{"settings", "name", "Box"}, `settings.set {"key":"name","value":"Box"}`},
	} {
		if _, stderr, exit := cliProcess(t, c.args...); exit != 0 {
			t.Errorf("%q: exit %d: %s", c.args, exit, stderr)
			continue
		}
		if got := h.lastCall(); got != c.want {
			t.Errorf("%q: sent %s, want %s", c.args, got, c.want)
		}
	}
	out, _, _ := cliProcess(t, "--json", "notifications", "dismiss", "n1")
	if strings.TrimSpace(out) != `{"m":"dismiss"}` {
		t.Errorf("dismiss JSON: %q", out)
	}
}

func TestNewCommandsBadArgs(t *testing.T) {
	h := newPhoneHandler()
	serveRecord(t, h)
	for _, args := range [][]string{
		{"sms", "thread", "0"}, {"sms", "thread", "-1"}, {"sms", "thread", "9223372036854775808"}, {"sms", "thread", "abc"},
		{"settings", "motionSensitivity", "NaN"}, {"settings", "motionSensitivity", "Inf"},
		{"settings", "bogus"}, {"settings", "bogus", "1"}, {"settings", "checkUpdates", "maybe"},
		{"settings", "clipboardLimit", "0"}, {"settings", "motionIdle", "5"},
		{"transfers", "open"}, {"transfers", "copy"},
		{"text"}, {"notifications", "reply", "n1"}, {"notifications", "action", "n1"}, {"notifications", "dismiss"},
		{"dnd", "maybe"},
	} {
		out, _, exit := cliProcess(t, append([]string{"--json"}, args...)...)
		if exit != 1 || errorCode(t, out) != "bad_args" {
			t.Errorf("%q: %q, exit %d", args, out, exit)
		}
	}
	h.mu.Lock()
	defer h.mu.Unlock()
	for _, c := range h.calls {
		if !strings.HasPrefix(c, "state") {
			t.Errorf("bad args reached the socket: %s", c)
		}
	}
}

func TestNewCommandsReads(t *testing.T) {
	h := newPhoneHandler()
	serveRecord(t, h)
	when := time.Unix(1700000000, 0).Format("2006-01-02 15:04")
	when2 := time.Unix(1700000060, 0).Format("2006-01-02 15:04")
	for _, c := range []struct {
		args []string
		want string
	}{
		{[]string{"sms", "threads"}, "Phone  2  Bob  new\nPhone  1  Ann  old\n"},
		{[]string{"sms", "thread", "1"}, when + "  Ann  hi\n" + when2 + "  me  yo\n"},
		{[]string{"dnd"}, "Do Not Disturb is on on Phone\nDo Not Disturb is unknown on Tablet\n"},
		{[]string{"--device", "p2", "dnd"}, "Do Not Disturb is unknown on Tablet\n"},
		{[]string{"settings", "checkUpdates"}, "true\n"},
		{[]string{"settings", "name"}, "Desk\n"},
		{[]string{"settings"}, "checkUpdates = true\ndownloadDir = /d\nmotionSensitivity = 1\nname = Desk\n"},
	} {
		out, stderr, exit := cliProcess(t, c.args...)
		if exit != 0 || out != c.want {
			t.Errorf("%q: %q, exit %d, %s", c.args, out, exit, stderr)
		}
	}
	for _, c := range []struct {
		args []string
		want string
	}{
		{[]string{"--json", "sms", "--device", "Tablet", "threads"}, `{"p2":[]}`},
		{[]string{"--json", "dnd"}, `{"p1":{"at":1,"by":"phone","on":true},"p2":null}`},
		{[]string{"--json", "--device", "p2", "dnd"}, `{"p2":null}`},
		{[]string{"--json", "settings", "name"}, `{"key":"name","value":"Desk"}`},
		{[]string{"--json", "settings", "bogus"}, ""},
	} {
		out, _, _ := cliProcess(t, c.args...)
		if c.want != "" && strings.TrimSpace(out) != c.want {
			t.Errorf("%q: %q, want %s", c.args, out, c.want)
		}
	}
	out, _, _ := cliProcess(t, "--json", "sms", "threads")
	var got map[string][]map[string]any
	if err := json.Unmarshal([]byte(out), &got); err != nil || len(got["p1"]) != 2 || len(got["p2"]) != 0 || len(got) != 2 {
		t.Errorf("sms threads JSON: %q, %v", out, err)
	}
	out, _, _ = cliProcess(t, "--json", "settings")
	var set map[string]json.RawMessage
	if err := json.Unmarshal([]byte(out), &set); err != nil || string(set["name"]) != `"Desk"` || set["notificationRules"] == nil || set["deviceRules"] == nil {
		t.Errorf("settings JSON: %q, %v", out, err)
	}
	if out, _, exit := cliProcess(t, "--json", "--device", "missing", "dnd"); exit != 1 || errorCode(t, out) != "not_found" {
		t.Errorf("missing device: %q, exit %d", out, exit)
	}
}

func TestNewCommandsDeviceEdges(t *testing.T) {
	h := &recordHandler{state: map[string]any{"devices": []any{}}}
	serveRecord(t, h)
	for _, args := range [][]string{{"sms", "threads"}, {"dnd"}} {
		if out, _, exit := cliProcess(t, append([]string{"--json"}, args...)...); exit != 0 || strings.TrimSpace(out) != "{}" {
			t.Errorf("%q: %q, exit %d", args, out, exit)
		}
	}
	// An exact ID wins over a name.
	h.mu.Lock()
	h.state = map[string]any{"devices": []any{
		map[string]any{"id": "Tablet", "name": "A", "paired": true, "online": true, "dnd": map[string]any{"on": true}},
		map[string]any{"id": "t2", "name": "Tablet", "paired": true, "online": true, "dnd": map[string]any{"on": false}},
	}}
	h.mu.Unlock()
	if out, _, exit := cliProcess(t, "--json", "--device", "Tablet", "dnd"); exit != 0 || strings.TrimSpace(out) != `{"Tablet":{"on":true}}` {
		t.Errorf("exact ID: %q, exit %d", out, exit)
	}
}
