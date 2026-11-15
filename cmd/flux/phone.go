package main

import (
	"encoding/json"
	"fmt"
	"math"
	"sort"
	"strconv"
	"strings"
	"time"

	"flux/internal/config"
)

// sortedDevices lists the keys of devs by device name, then ID.
func sortedDevices(devs map[string]map[string]json.RawMessage) []string {
	ids := make([]string, 0, len(devs))
	for id := range devs {
		ids = append(ids, id)
	}
	name := func(id string) string {
		var n string
		_ = json.Unmarshal(devs[id]["name"], &n)
		return n
	}
	sort.Slice(ids, func(i, j int) bool {
		if a, b := name(ids[i]), name(ids[j]); a != b {
			return a < b
		}
		return ids[i] < ids[j]
	})
	return ids
}

// deviceField is the raw field of each paired device, or raw null/[] fill
// for a missing one, keyed by device ID.
func deviceField(devs map[string]map[string]json.RawMessage, field, fill string) json.RawMessage {
	out := make(map[string]json.RawMessage, len(devs))
	for id, d := range devs {
		v := d[field]
		if len(v) == 0 || string(v) == "null" {
			v = json.RawMessage(fill)
		}
		out[id] = v
	}
	b, _ := json.Marshal(out)
	return b
}

func notificationActions(device string, args []string) error {
	sub := args[0]
	if len(args) < 2 || args[1] == "" {
		return usageErr("Usage: flux-cli notifications %s ID%s", sub, map[string]string{"dismiss": "", "reply": " TEXT...", "action": " ACTION"}[sub])
	}
	if sub == "dismiss" && len(args) != 2 {
		return usageErr("Usage: flux-cli notifications dismiss ID")
	}
	params := map[string]any{"device": device, "id": args[1]}
	switch sub {
	case "reply":
		text := strings.Join(args[2:], " ")
		if text == "" {
			return usageErr("Usage: flux-cli notifications reply ID TEXT...")
		}
		params["message"] = text
	case "action":
		if len(args) != 3 || args[2] == "" {
			return usageErr("Usage: flux-cli notifications action ID ACTION")
		}
		params["action"] = args[2]
	}
	return act("notification."+sub, params)
}

func smsCommand(device string, args []string) error {
	switch args[0] {
	case "refresh":
		if len(args) != 1 {
			return usageErr("Usage: flux-cli sms refresh")
		}
		return act("sms.refresh", map[string]any{"device": device})
	case "thread":
		if len(args) != 2 {
			return usageErr("Usage: flux-cli sms thread N")
		}
		n, err := strconv.ParseInt(args[1], 10, 64)
		if err != nil || n <= 0 {
			return usageErr("give a thread number above 0")
		}
		if jsonMode {
			return act("sms.thread", map[string]any{"device": device, "thread": n})
		}
		var res struct {
			Messages []struct {
				Name     string
				Body     string
				Time     int64
				Outgoing bool
			}
		}
		if err := callInto("sms.thread", map[string]any{"device": device, "thread": n}, &res); err != nil {
			return err
		}
		for _, m := range res.Messages {
			who := m.Name
			if m.Outgoing {
				who = "me"
			}
			fmt.Printf("%s  %s  %s\n", time.Unix(m.Time, 0).Format("2006-01-02 15:04"), who, m.Body)
		}
		return nil
	}
	if len(args) != 1 {
		return usageErr("Usage: flux-cli sms threads")
	}
	devs, err := devicesRaw(device)
	if err != nil {
		return err
	}
	if jsonMode {
		emitJSON(deviceField(devs, "conversations", "[]"))
		return nil
	}
	type conv struct {
		Thread int64
		Name   string
		Last   string
		Time   int64
		Device string `json:"-"`
	}
	var all []conv
	for _, id := range sortedDevices(devs) {
		var cs []conv
		if v := devs[id]["conversations"]; len(v) > 0 {
			if err := json.Unmarshal(v, &cs); err != nil {
				return err
			}
		}
		var name string
		_ = json.Unmarshal(devs[id]["name"], &name)
		for i := range cs {
			cs[i].Device = name
		}
		all = append(all, cs...)
	}
	sort.SliceStable(all, func(i, j int) bool { return all[i].Time > all[j].Time })
	for _, c := range all {
		// With more than 1 phone, name the phone, since a thread number
		// belongs to 1 phone: flux-cli --device NAME sms thread N.
		if len(devs) > 1 {
			fmt.Printf("%s  ", safe(c.Device))
		}
		fmt.Printf("%d  %s  %s\n", c.Thread, safe(c.Name), safe(c.Last))
	}
	return nil
}

func dndCommand(device string, args []string) error {
	if len(args) > 1 || (len(args) == 1 && args[0] != "on" && args[0] != "off") {
		return usageErr("Usage: flux-cli dnd [on|off]")
	}
	if len(args) == 1 {
		return act("dnd.set", map[string]any{"device": device, "on": args[0] == "on"})
	}
	devs, err := devicesRaw(device)
	if err != nil {
		return err
	}
	type dnd struct {
		On *bool `json:"on"`
	}
	view := make(map[string]json.RawMessage, len(devs))
	word := map[string]string{}
	for id, d := range devs {
		var online bool
		_ = json.Unmarshal(d["online"], &online)
		var v dnd
		raw := d["dnd"]
		if !online || len(raw) == 0 || json.Unmarshal(raw, &v) != nil {
			view[id], word[id] = json.RawMessage("null"), "unknown"
			continue
		}
		view[id], word[id] = raw, "unknown"
		if v.On != nil {
			word[id] = map[bool]string{true: "on", false: "off"}[*v.On]
		}
	}
	if jsonMode {
		b, _ := json.Marshal(view)
		emitJSON(b)
		return nil
	}
	for _, id := range sortedDevices(devs) {
		var name string
		_ = json.Unmarshal(devs[id]["name"], &name)
		fmt.Printf("Do Not Disturb is %s on %s\n", word[id], safe(name))
	}
	return nil
}

func textCommand(device string, args []string) error {
	text := strings.Join(args, " ")
	if text == "" {
		return usageErr("Usage: flux-cli text TEXT...")
	}
	return act("share.text", map[string]any{"device": device, "text": text})
}

var boolSettings = []string{"autoClipboard", "notifications", "shareHome", "pauseMediaOnCall", "syncDnd", "checkUpdates", "herdr", "herdrControl", "herdrTerminals", "remoteInput", "remoteDesktop", "motionCues", "motionAuto"}

func settingKnown(key string) bool {
	switch key {
	case "clipboardLimit", "motionIdle", "motionSensitivity", "name", "downloadDir", "motionSize":
		return true
	}
	for _, k := range boolSettings {
		if k == key {
			return true
		}
	}
	return false
}

func settingsCommand(args []string) error {
	if len(args) > 2 {
		return usageErr("Usage: flux-cli settings [KEY [VALUE]]")
	}
	if len(args) > 0 && !settingKnown(args[0]) {
		return usageErr("unknown setting %q", args[0])
	}
	if len(args) == 2 {
		value, err := settingValue(args[0], args[1])
		if err != nil {
			return err
		}
		return act("settings.set", map[string]any{"key": args[0], "value": value})
	}
	state, err := stateRaw()
	if err != nil {
		return err
	}
	settings := map[string]json.RawMessage{}
	if len(state["settings"]) > 0 {
		if err := json.Unmarshal(state["settings"], &settings); err != nil {
			return err
		}
	}
	var self struct{ Name json.RawMessage }
	_ = json.Unmarshal(state["self"], &self)
	if len(self.Name) > 0 {
		settings["name"] = self.Name
	}
	if len(args) == 1 {
		v := settings[args[0]]
		if len(v) == 0 {
			v = json.RawMessage("null")
		}
		if jsonMode {
			b, _ := json.Marshal(map[string]any{"key": args[0], "value": v})
			emitJSON(b)
		} else {
			fmt.Println(settingText(v))
		}
		return nil
	}
	if jsonMode {
		b, _ := json.Marshal(settings)
		emitJSON(b)
		return nil
	}
	keys := make([]string, 0, len(settings))
	for k := range settings {
		if !strings.HasSuffix(k, "Rules") {
			keys = append(keys, k)
		}
	}
	sort.Strings(keys)
	for _, k := range keys {
		fmt.Printf("%s = %s\n", k, settingText(settings[k]))
	}
	return nil
}

// settingText prints a string unquoted, and anything else as compact JSON.
func settingText(v json.RawMessage) string {
	var s string
	if json.Unmarshal(v, &s) == nil {
		return safe(s)
	}
	return safe(string(v))
}

func settingValue(key, s string) (any, error) {
	switch key {
	case "name", "downloadDir", "motionSize":
		return s, nil
	case "clipboardLimit":
		n, err := strconv.Atoi(s)
		if err != nil || n < 1 || n > config.MaxClipboardLimit {
			return nil, usageErr("give a limit between 1 and %d", config.MaxClipboardLimit)
		}
		return n, nil
	case "motionIdle":
		n, err := strconv.Atoi(s)
		if err != nil || n < config.MinMotionIdle || n > config.MaxMotionIdle {
			return nil, usageErr("give a whole number of seconds from %d to %d", config.MinMotionIdle, config.MaxMotionIdle)
		}
		return n, nil
	case "motionSensitivity":
		f, err := strconv.ParseFloat(s, 64)
		if err != nil || math.IsNaN(f) || math.IsInf(f, 0) {
			return nil, usageErr("give a number")
		}
		return f, nil
	}
	switch s {
	case "true", "on":
		return true, nil
	case "false", "off":
		return false, nil
	}
	return nil, usageErr("%s takes true, false, on, or off", key)
}
