package main

import (
	"encoding/json"
	"errors"
	"fmt"
	"os"
	"sort"
	"strings"

	"flux/internal/ipc"
)

var jsonMode bool

func callRaw(method string, params any) (json.RawMessage, error) {
	c, err := dial()
	if err != nil {
		return nil, err
	}
	defer c.Close()
	var raw json.RawMessage
	if err := c.Call(method, params, &raw); err != nil {
		return nil, cleanErr(err)
	}
	return raw, nil
}

func splitJSON(in []string) (out []string) {
	jsonMode = false
	command := false
	for i := 0; i < len(in); i++ {
		a := in[i]
		switch {
		case a == "--":
			return stripCommandJSON(append(out, in[i:]...))
		case (a == "--device" || a == "-d") && i+1 < len(in):
			out = append(out, a, in[i+1])
			i++
		case strings.HasPrefix(a, "--device="):
			out = append(out, a)
		case a == "--json" && !command:
			jsonMode = true
		case !command:
			out = append(out, a)
			command = true
		case strings.HasPrefix(a, "-"):
			out = append(out, a)
		default:
			out = append(out, in[i:]...)
			return stripCommandJSON(out)
		}
	}
	return stripCommandJSON(out)
}

func stripCommandJSON(out []string) []string {
	var cmd string
	var rest []string
	for i := 0; i < len(out); i++ {
		a := out[i]
		if a == "--" {
			break
		}
		if (a == "--device" || a == "-d") && i+1 < len(out) {
			i++
		} else if strings.HasPrefix(a, "--device=") || a == "--json" {
			continue
		} else if cmd == "" {
			cmd = a
		} else {
			rest = append(rest, a)
		}
	}
	if takesText(cmd, rest) {
		return out
	}
	command, flagRegion := false, true
	for i := 0; i < len(out); i++ {
		a := out[i]
		switch {
		case a == "--":
			return out
		case (a == "--device" || a == "-d") && i+1 < len(out):
			i++
		case strings.HasPrefix(a, "--device="):
		case !command:
			command = true
		case a == "--json" && (flagRegion || i == len(out)-1):
			jsonMode = true
			out = append(out[:i], out[i+1:]...)
			i--
		case !strings.HasPrefix(a, "-"):
			flagRegion = false
		}
	}
	return out
}

func takesText(cmd string, rest []string) bool {
	switch cmd {
	case "ping", "clip", "text", "notify", "send":
		return true
	case "sms":
		return len(rest) == 0 || (rest[0] != "threads" && rest[0] != "thread" && rest[0] != "refresh")
	case "commands":
		return first(rest) == "add"
	case "notifications":
		return first(rest) == "reply"
	case "snippets":
		return first(rest) == "search"
	case "settings":
		return len(rest) >= 2
	}
	return false
}

func act(method string, params any) error {
	if !jsonMode {
		return call(method, params)
	}
	raw, err := callRaw(method, params)
	if err != nil {
		return err
	}
	if len(raw) == 0 || string(raw) == "null" {
		raw = json.RawMessage(`{}`)
	}
	emitJSON(raw)
	return nil
}

func emitJSON(raw json.RawMessage) {
	if len(raw) == 0 {
		raw = json.RawMessage(`null`)
	}
	fmt.Println(string(safeJSON(raw)))
}

func stateRaw() (map[string]json.RawMessage, error) {
	raw, err := callRaw("state", nil)
	if err != nil {
		return nil, err
	}
	var state map[string]json.RawMessage
	if err := json.Unmarshal(raw, &state); err != nil {
		return nil, err
	}
	return state, nil
}

func devicesRaw(device string) (map[string]map[string]json.RawMessage, error) {
	state, err := stateRaw()
	if err != nil {
		return nil, err
	}
	var devices []map[string]json.RawMessage
	if err := json.Unmarshal(state["devices"], &devices); err != nil {
		return nil, err
	}
	out := make(map[string]map[string]json.RawMessage)
	var matches []string
	for _, d := range devices {
		var id, name string
		var paired bool
		if err := json.Unmarshal(d["id"], &id); err != nil {
			return nil, err
		}
		if err := json.Unmarshal(d["name"], &name); err != nil {
			return nil, err
		}
		if err := json.Unmarshal(d["paired"], &paired); err != nil {
			return nil, err
		}
		if !paired {
			continue
		}
		out[id] = d
		if strings.EqualFold(name, device) {
			matches = append(matches, id)
		}
	}
	if device == "" {
		return out, nil
	}
	if d, ok := out[device]; ok {
		return map[string]map[string]json.RawMessage{device: d}, nil
	}
	sort.Strings(matches)
	switch len(matches) {
	case 0:
		return nil, &ipc.Error{Code: "not_found", Message: fmt.Sprintf("No device named %q", device)}
	case 1:
		return map[string]map[string]json.RawMessage{matches[0]: out[matches[0]]}, nil
	default:
		return nil, &ipc.Error{Code: "ambiguous", Message: fmt.Sprintf("%d devices are named %q: %s. Give the device ID", len(matches), device, strings.Join(matches, ", "))}
	}
}

func usageErr(format string, args ...any) error {
	return &ipc.Error{Code: "bad_args", Message: fmt.Sprintf(format, args...)}
}

func jsonError(err error, exit int) {
	code := "error"
	var e *ipc.Error
	switch {
	case errors.As(err, &e):
		code = e.Code
	case errors.Is(err, errDaemonOff):
		code = "daemon_off"
	case errors.Is(err, errDaemonDown):
		code = "daemon_down"
	case errors.Is(err, errDaemonUnreachable):
		code = "daemon_unreachable"
	}
	message := err.Error()
	lines := strings.Split(message, "\n")
	for i := range lines {
		lines[i] = safe(lines[i])
	}
	message = strings.Join(lines, "\n")
	data, _ := json.Marshal(map[string]any{"error": map[string]string{"code": code, "message": message}})
	fmt.Println(string(safeJSON(data)))
	fmt.Fprintln(os.Stderr, message)
	os.Exit(exit)
}

func refusesJSON(cmd string, args []string) bool {
	switch cmd {
	case "open", "pair", "accept", "reject", "unpair", "setup", "doctor", "update", "approve", "browser", "on", "off", "version", "--version", "help", "-h", "--help":
		return true
	case "notify":
		return first(args) == "--run"
	}
	return false
}
