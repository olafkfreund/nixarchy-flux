package core

import (
	"context"
	"encoding/json"
	"strings"
	"testing"
)

// sideRow returns a row of a wide opencode screen: the conversation part
// in 46 columns, the gap of 4 blanks, and the sidebar text at column 50.
func sideRow(main, side string) string {
	if side == "" {
		return main
	}
	return main + strings.Repeat(" ", 50-len([]rune(main))) + side
}

func TestSidebarColumn(t *testing.T) {
	rows := []string{
		sideRow("  ┃  Fix the parser", "Making places clickable"),
		sideRow("", "Context"),
		sideRow("     The tests pass now.", "148,241 tokens"),
		sideRow("", "30% used"),
		sideRow("     → Read src/a.ts", "$2.95 spent"),
	}
	if col := sidebarColumn(rows); col != 50 {
		t.Errorf("column %d, want 50", col)
	}
	// 1 row with sidebar text is not enough, and a narrow terminal has no sidebar.
	if col := sidebarColumn(rows[:3]); col != -1 {
		t.Errorf("1 row: column %d", col)
	}
	if col := sidebarColumn([]string{"     a    12 tokens", "     b    30% used"}); col != -1 {
		t.Errorf("narrow: column %d", col)
	}
	// A conversation without a sidebar has none.
	if col := sidebarColumn([]string{"● Done.", "  Ran 2 shell commands", "❯ "}); col != -1 {
		t.Errorf("claude: column %d", col)
	}
}

func TestDropSidebar(t *testing.T) {
	rows := []string{
		sideRow("  ┃  Fix the parser", "Making places clickable"),
		sideRow("", "Context"),
		// A wide character takes 2 columns, so the sidebar text starts 1
		// character to the left in its row.
		"     界 The tests pass now." + strings.Repeat(" ", 22) + "148,241 tokens",
		"     A short row",
		// A row without a gap before the sidebar keeps its text.
		strings.Repeat("x", 60),
	}
	got := dropSidebar(rows, 50, false)
	want := []string{"  ┃  Fix the parser", "", "     界 The tests pass now.", "     A short row", strings.Repeat("x", 60)}
	if strings.Join(got, "\n") != strings.Join(want, "\n") {
		t.Errorf("rows\n%q\nwant\n%q", got, want)
	}
	if rows[0] == got[0] {
		t.Error("dropSidebar changed its input")
	}
	if got := dropSidebar(rows, -1, false); &got[0] != &rows[0] {
		t.Error("no sidebar must keep the rows")
	}
}

func TestCutColumns(t *testing.T) {
	row := "\x1b[48;2;20;42;26m  ┃\x1b[0m\x1b[1m ok\x1b[0m   \x1b[48;2;1;2;3m  side\x1b[0m"
	if got := cutColumns(row, 6); got != "\x1b[48;2;20;42;26m  ┃\x1b[0m\x1b[1m ok\x1b[0m" {
		t.Errorf("cut %q", got)
	}
	if got := cutColumns(row, 4); got != "\x1b[48;2;20;42;26m  ┃\x1b[0m\x1b[1m \x1b[0m" {
		t.Errorf("cut %q", got)
	}
	if got := cutColumns("plain row", 5); got != "plain" {
		t.Errorf("plain cut %q", got)
	}
	if got := cutColumns("short", 10); got != "short" {
		t.Errorf("short row %q", got)
	}
}

func TestReadHerdrOpencodeSidebar(t *testing.T) {
	f := newFakeHerdr(t)
	d := herdrDaemon(context.Background(), f.path)
	d.herdrAgents = []HerdrAgent{{Pane: "w1:p1", Agent: "opencode", Status: "idle"}}
	history := []string{
		sideRow("  ┃  Fix the parser", "Fix the parser"),
		sideRow("", "Context"),
		sideRow("     I fixed the parser.", "148,241 tokens"),
		sideRow("", "30% used"),
		sideRow("     ▣  Build · Big Pickle · 21.2s", "$2.95 spent"),
		"",
		"   ~/Code/flux    ctrl+p commands",
	}
	// The screen has the title of the session at the top of the sidebar,
	// where the history has older sidebar rows, and it has colors.
	bg := "\x1b[48;2;20;42;26m"
	screen := []string{
		bg + "     I fixed the parser." + strings.Repeat(" ", 22) + "\x1b[0m    " + bg + "Fix the parser\x1b[0m",
		strings.Repeat(" ", 50) + bg + "Context\x1b[0m",
		"     ▣  Build · Big Pickle · 21.2s" + strings.Repeat(" ", 16) + bg + "148,241 tokens\x1b[0m",
		// A row of the conversation has the blank cells of the sidebar with its background.
		"     → Read src/a.ts" + strings.Repeat(" ", 31) + bg + strings.Repeat(" ", 20) + "\x1b[0m",
		"",
		"   ~/Code/flux    ctrl+p commands",
	}
	text := func(rows []string) string {
		b, _ := json.Marshal(strings.Join(rows, "\n"))
		return string(b)
	}
	f.read = `"result":{"type":"pane_read","read":{"pane_id":"w1:p1","format":"ansi","text":` + text(screen) + `,"truncated":false}}`
	f.readText = `"result":{"type":"pane_read","read":{"pane_id":"w1:p1","text":` + text(history) + `,"truncated":false}}`
	body := outputBody(t, d.readHerdr("w1:p1", 1000, true))
	want := strings.Join([]string{
		"  ┃  Fix the parser",
		"",
		bg + "     I fixed the parser." + strings.Repeat(" ", 22) + "\x1b[0m",
		"",
		"     ▣  Build · Big Pickle · 21.2s",
		"     → Read src/a.ts",
		"",
		"   ~/Code/flux    ctrl+p commands",
	}, "\n")
	if body["text"] != want {
		t.Errorf("text\n%q\nwant\n%q", body["text"], want)
	}
	for _, c := range f.takeCalls() {
		if !strings.Contains(c, `"source":"recent"`) {
			t.Errorf("opencode must keep its rows apart: %s", c)
		}
	}

	// Claude Code keeps the joined rows.
	d.herdrAgents = []HerdrAgent{{Pane: "w1:p1", Agent: "claude", Status: "idle"}}
	d.herdrHistory = nil
	outputBody(t, d.readHerdr("w1:p1", 1000, true))
	for _, c := range f.takeCalls() {
		if !strings.Contains(c, `"source":"recent_unwrapped"`) {
			t.Errorf("claude must read joined rows: %s", c)
		}
	}
}
