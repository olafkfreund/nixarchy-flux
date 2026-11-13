// Package quiet turns off Tailscale's log upload and its local log buffer.
// Checked against tailscale.com v1.104.4; testdata/nologs proves it.
package quiet

import (
	"tailscale.com/envknob"
	"tailscale.com/logtail"
)

// Apply must run before tsnet starts. It can't be undone.
func Apply() {
	envknob.SetNoLogsNoSupport() // upload transport is a no-op (logpolicy.go:894-897)
	logtail.Disable()            // nothing is buffered or uploaded (logtail.go:532,768)
}
