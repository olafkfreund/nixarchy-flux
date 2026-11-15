package main

import (
	"encoding/json"
	"fmt"
	"strconv"
	"strings"
	"time"

	"flux/internal/config"
)

func transferCommand(args []string) error {
	if len(args) == 2 && (args[0] == "retry" || args[0] == "cancel") {
		return act("transfer."+args[0], map[string]any{"id": args[1]})
	}
	if len(args) == 1 && (args[0] == "open" || args[0] == "copy") {
		return usageErr("Usage: flux-cli transfers %s ID", args[0])
	}
	if len(args) == 2 && (args[0] == "open" || args[0] == "copy") {
		if args[1] == "" {
			return usageErr("Usage: flux-cli transfers %s ID", args[0])
		}
		return act("transfer."+args[0], map[string]any{"id": args[1]})
	}
	if len(args) != 0 {
		return fmt.Errorf("use: flux-cli transfers [retry ID|cancel ID|open ID|copy ID]")
	}
	if jsonMode {
		return emitStateKey("transfers")
	}
	var state struct {
		Transfers []struct {
			ID, Name, State, Error string
			Done, Size             int64
		}
	}
	if err := callInto("state", nil, &state); err != nil {
		return err
	}
	for _, t := range state.Transfers {
		fmt.Printf("%s  %-9s %d/%d  %s  %s\n", t.ID, t.State, t.Done, t.Size, t.Name, t.Error)
	}
	return nil
}

func printCall(method string, params any) error {
	var result json.RawMessage
	if err := callInto(method, params, &result); err != nil {
		return err
	}
	if jsonMode {
		if len(result) == 0 || string(result) == "null" {
			result = json.RawMessage(`{}`)
		}
		emitJSON(result)
		return nil
	}
	fmt.Println(string(result))
	return nil
}

func emitSettingObject(key, fill string) error {
	state, err := stateRaw()
	if err != nil {
		return err
	}
	var settings map[string]json.RawMessage
	if len(state["settings"]) > 0 {
		if err := json.Unmarshal(state["settings"], &settings); err != nil {
			return err
		}
	}
	v := settings[key]
	if len(v) == 0 {
		v = json.RawMessage(fill)
	}
	b, _ := json.Marshal(map[string]json.RawMessage{key: v})
	emitJSON(b)
	return nil
}

func clipboardCommand(args []string) error {
	if len(args) == 0 {
		return printCall("clipboard.search", nil)
	}
	switch args[0] {
	case "pause", "resume", "status":
		if len(args) != 1 {
			return fmt.Errorf("clipboard %s takes no arguments", args[0])
		}
		switch args[0] {
		case "pause":
			return act("clipboard.pause", nil)
		case "resume":
			return act("clipboard.resume", nil)
		}
		return clipboardStatus()
	case "clear":
		all := len(args) > 1 && args[1] == "--all"
		return act("clipboard.clear", map[string]any{"all": all})
	case "delete", "remove":
		if len(args) < 2 {
			return fmt.Errorf("give a clipboard entry ID")
		}
		return act("clipboard.delete", map[string]any{"id": args[1]})
	case "limit":
		if len(args) == 1 {
			if jsonMode {
				return emitSettingObject("clipboardLimit", "null")
			}
			return printSetting("clipboardLimit")
		}
		limit, err := strconv.Atoi(args[1])
		if err != nil || limit < 1 || limit > config.MaxClipboardLimit {
			return fmt.Errorf("give a limit between 1 and %d", config.MaxClipboardLimit)
		}
		return act("settings.set", map[string]any{"key": "clipboardLimit", "value": limit})
	}
	return fmt.Errorf("use: flux-cli clipboard [clear [--all]|delete ID|limit [N]|pause|resume|status]")
}

// clipboardStatus prints whether local copies go to the devices.
func clipboardStatus() error {
	if jsonMode {
		state, err := stateRaw()
		if err != nil {
			return err
		}
		var settings map[string]json.RawMessage
		if len(state["settings"]) > 0 {
			if err := json.Unmarshal(state["settings"], &settings); err != nil {
				return err
			}
		}
		paused := state["clipboardPaused"]
		if len(paused) == 0 {
			paused = json.RawMessage("false")
		}
		auto := settings["autoClipboard"]
		if len(auto) == 0 {
			auto = json.RawMessage("false")
		}
		b, _ := json.Marshal(struct {
			Paused        json.RawMessage `json:"paused"`
			AutoClipboard json.RawMessage `json:"autoClipboard"`
		}{paused, auto})
		emitJSON(b)
		return nil
	}
	var state struct {
		ClipboardPaused bool `json:"clipboardPaused"`
		Settings        struct {
			AutoClipboard bool `json:"autoClipboard"`
		} `json:"settings"`
	}
	if err := callInto("state", nil, &state); err != nil {
		return err
	}
	fmt.Println(clipboardStatusWord(state.ClipboardPaused, state.Settings.AutoClipboard))
	return nil
}

// clipboardStatusWord is "paused" while clipboard.pause holds, also with
// automatic sync off, else "off" without automatic sync, else "syncing".
func clipboardStatusWord(paused, auto bool) string {
	switch {
	case paused:
		return "paused"
	case !auto:
		return "off"
	}
	return "syncing"
}

func snippetCommand(args []string) error {
	if len(args) == 0 {
		return printCall("clipboard.search", nil)
	}
	if args[0] == "search" {
		return printCall("clipboard.search", map[string]any{"text": strings.Join(args[1:], " ")})
	}
	if len(args) < 2 {
		return fmt.Errorf("give a clipboard entry ID")
	}
	p := map[string]any{"id": args[1]}
	switch args[0] {
	case "save":
		if len(args) > 2 {
			duration, err := time.ParseDuration(args[2])
			if err != nil || duration <= 0 {
				return fmt.Errorf("give a positive duration, such as 24h")
			}
			p["expires"] = time.Now().Add(duration).Unix()
		}
		return printCall("clipboard.pin", p)
	case "remove":
		return act("clipboard.unpin", p)
	case "copy":
		return act("clipboard.copy", p)
	}
	return fmt.Errorf("use snippets search, save, remove, or copy")
}

func printSetting(key string) error {
	var state struct{ Settings map[string]json.RawMessage }
	if err := callInto("state", nil, &state); err != nil {
		return err
	}
	value := state.Settings[key]
	if len(value) == 0 {
		value = json.RawMessage("[]")
	}
	if jsonMode {
		emitJSON(value)
		return nil
	}
	fmt.Println(string(value))
	return nil
}

func automationCommand(device string, args []string) error {
	if len(args) == 0 {
		return printSetting("automationRules")
	}
	if len(args) == 2 && args[0] == "remove" {
		return act("automation.remove", map[string]any{"id": args[1]})
	}
	if len(args) < 3 || len(args) > 4 || args[0] != "add" {
		return fmt.Errorf("use: flux-cli automation add EVENT COMMAND_ID [NUMBER]")
	}
	r := map[string]any{"event": args[1], "command": args[2]}
	if device != "" {
		var s State
		if err := callInto("state", nil, &s); err != nil {
			return err
		}
		for _, d := range s.Devices {
			if d.Paired && (d.ID == device || strings.EqualFold(d.Name, device)) {
				r["device"] = d.ID
				break
			}
		}
		if r["device"] == nil {
			return fmt.Errorf("no paired device named %q", device)
		}
	}
	if len(args) == 4 {
		n, err := strconv.Atoi(args[3])
		if err != nil {
			return err
		}
		// The number is the charge in percent for battery.low, and the wait
		// in seconds for device.disconnected.
		switch args[1] {
		case "battery.low":
			r["below"] = n
		case "device.disconnected":
			r["after"] = n
		default:
			return fmt.Errorf("%s takes no number. Only battery.low and device.disconnected take one", args[1])
		}
	}
	return printCall("automation.add", map[string]any{"config": r})
}

func deviceSettingsCommand(device string, args []string) error {
	if len(args) == 0 {
		return printCall("device.settings", map[string]any{"device": device})
	}
	if len(args) != 2 {
		return fmt.Errorf("use: flux-cli --device NAME device-settings KEY true|false|inherit")
	}
	var value any
	if args[1] != "inherit" {
		b, err := strconv.ParseBool(args[1])
		if err != nil {
			return err
		}
		value = b
	}
	return act("device.settings.set", map[string]any{"device": device, "key": args[0], "value": value})
}

func notificationRulesCommand(device string, args []string) error {
	if len(args) == 0 {
		return printSetting("notificationRules")
	}
	if args[0] == "remove" && len(args) == 2 {
		return act("notification.rule.remove", map[string]any{"id": args[1]})
	}
	rule := map[string]any{"device": device}
	if device != "" {
		var s State
		if err := callInto("state", nil, &s); err != nil {
			return err
		}
		found := false
		for _, d := range s.Devices {
			if d.Paired && (d.ID == device || strings.EqualFold(d.Name, device)) {
				rule["device"], found = d.ID, true
				break
			}
		}
		if !found {
			return fmt.Errorf("no paired device named %q", device)
		}
	}
	switch {
	case args[0] == "add" && (len(args) == 3 || len(args) == 4):
		rule["app"], rule["mode"] = args[1], args[2]
		if len(args) == 4 {
			duration, err := time.ParseDuration(args[3])
			if err != nil || duration <= 0 {
				return fmt.Errorf("give a positive duration, such as 1h")
			}
			rule["until"] = time.Now().Add(duration).Unix()
		}
	case args[0] == "quiet" && len(args) == 3:
		rule["app"], rule["mode"], rule["start"], rule["end"] = "*", "silent", args[1], args[2]
	default:
		return fmt.Errorf("use notification-rules add APP MODE [DURATION], quiet START END, or remove ID")
	}
	return printCall("notification.rule.add", map[string]any{"config": rule})
}
