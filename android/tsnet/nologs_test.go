package main

import (
	"os/exec"
	"strings"
	"testing"
)

// TestNoLogs runs testdata/nologs in its own process, with and without
// quiet.Apply. It must pass after each tailscale bump (docs/releasing.md).
func TestNoLogs(t *testing.T) {
	run := func(args ...string) string {
		t.Helper()
		out, err := exec.Command("go", append([]string{"run", "./testdata/nologs"}, args...)...).CombinedOutput()
		if err != nil {
			t.Fatalf("go run ./testdata/nologs %v: %v\n%s", args, err, out)
		}
		lines := strings.Split(strings.TrimSpace(string(out)), "\n")
		return lines[len(lines)-1]
	}
	if got := run(); got != "sent=0 logged=0 buffered=0" {
		t.Errorf("with quiet.Apply: %s, want sent=0 logged=0 buffered=0", got)
	}
	// The positive control: without Apply the transport sends, and the logger buffers and uploads.
	if got := run("-control"); !strings.HasPrefix(got, "sent=1 ") || strings.Contains(got, " logged=0 ") || strings.HasSuffix(got, " buffered=0") {
		t.Errorf("without quiet.Apply: %s, want sent=1, logged>0 and buffered>0", got)
	}
}
