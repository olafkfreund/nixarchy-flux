package core

import (
	"regexp"
	"strings"
	"unicode/utf8"

	"flux/internal/herdr"
)

// opencode shows a sidebar at the right of a wide terminal. Each row then
// holds a row of the conversation and a row of the sidebar, and a phone
// shows the sidebar text inside the conversation. The sidebar also makes
// the history and the screen of the agent differ, so spliceScreen cannot
// join them. fluxd removes the sidebar before it joins them.

// sidebarText matches the rows of the Context and LSP sections of the
// opencode sidebar, for example "148,241 tokens", "30% used", and "$2.95
// spent".
var sidebarText = regexp.MustCompile(`^(?:\d[\d,.]*[KkM]? tokens|\d+% used|\$\d[\d,.]* spent|LSPs are disabled)$`)

const (
	// sidebarMinCol is the first column where the sidebar text can start.
	sidebarMinCol = 40
	// sidebarGap is the count of blank columns before the sidebar text: 2
	// between the panels and 2 of padding in the sidebar.
	sidebarGap = 4
	// sidebarShift is how many columns to the left a row can have its
	// sidebar. A wide character takes 2 columns of the terminal and 1
	// character of the row.
	sidebarShift = 3
	// sidebarMinRows is the least count of rows that show the sidebar text
	// at one column.
	sidebarMinRows = 2
)

// rowSource returns the source of an agent read. opencode draws each row
// of its screen to the last column, so the terminal marks the rows as
// wrapped, and herdr would join the rows of the conversation and the
// sidebar. Its rows stay apart.
func rowSource(kind string) string {
	if kind == "opencode" {
		return herdr.SourceRows
	}
	return herdr.SourceUnwrapped
}

// sidebarColumn returns the column where the text of the opencode sidebar
// starts, or -1 when the rows show no sidebar. The rows can have SGR
// sequences.
func sidebarColumn(rows ...[]string) int {
	count := map[int]int{}
	for _, part := range rows {
		for _, row := range part {
			p := plainRow(row)
			i := strings.LastIndex(p, strings.Repeat(" ", sidebarGap))
			if i < 0 {
				continue
			}
			text := strings.TrimLeft(p[i:], " ")
			col := utf8.RuneCountInString(p[:len(p)-len(text)])
			if col >= sidebarMinCol && sidebarText.MatchString(text) {
				count[col]++
			}
		}
	}
	best, most := -1, 0
	for col, n := range count {
		if n > most || n == most && col > best {
			best, most = col, n
		}
	}
	if most < sidebarMinRows {
		return -1
	}
	return best
}

// dropSidebar returns the rows without the sidebar whose text starts at
// col. A row keeps its sidebar part when it has no blank gap before the
// sidebar. It does not change rows.
func dropSidebar(rows []string, col int, ansi bool) []string {
	if col < 0 {
		return rows
	}
	out := make([]string, len(rows))
	for i, row := range rows {
		out[i] = row
		// The blank cells of the sidebar have its background, so they count.
		cut := sidebarCut([]rune(sgr.ReplaceAllString(row, "")), col)
		if cut < 0 {
			continue
		}
		row = cutColumns(row, cut)
		if ansi {
			out[i] = trimStyledEnd(row)
		} else {
			out[i] = strings.TrimRight(row, " \t")
		}
	}
	return out
}

// sidebarCut returns the column where the sidebar part of a row starts,
// with the gap before the sidebar text, or -1 when the row has no such
// gap.
func sidebarCut(p []rune, col int) int {
	gap := col - sidebarGap
	if len(p) <= gap {
		return -1
	}
	for shift := 0; shift <= sidebarShift && gap-shift >= 0; shift++ {
		from, to := gap-shift, min(col-shift, len(p))
		if strings.TrimSpace(string(p[from:to])) == "" {
			return from
		}
	}
	return -1
}

// cutColumns returns the row without its characters from column col. It
// keeps the SGR sequences before the cut, and it resets the style at the
// end when a style is on there. The row has only SGR sequences, as
// cleanANSI leaves it.
func cutColumns(row string, col int) string {
	n := 0
	styled := false
	for i := 0; i < len(row); {
		if row[i] == 0x1b {
			if end := strings.IndexByte(row[i:], 'm'); end > 0 {
				seq := row[i : i+end+1]
				styled = seq != "\x1b[0m" && seq != "\x1b[m"
				i += end + 1
				continue
			}
		}
		if n == col {
			if styled {
				return row[:i] + "\x1b[0m"
			}
			return row[:i]
		}
		_, size := utf8.DecodeRuneInString(row[i:])
		i += size
		n++
	}
	return row
}
