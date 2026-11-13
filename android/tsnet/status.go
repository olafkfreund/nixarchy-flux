package main

import (
	"fmt"
	"os"
	"sync"
)

// The status lines for the app on stdout. One mutex keeps them whole and in
// order, and keeps "approve" from following "ready".
var (
	outMu    sync.Mutex
	up       bool
	approved bool
)

// line writes one status line.
func line(s string) {
	outMu.Lock()
	defer outMu.Unlock()
	writeLine(s)
}

// markReady writes "ready <ip>". After it, isUp is true and approve writes nothing.
func markReady(ip string) {
	outMu.Lock()
	defer outMu.Unlock()
	up = true
	writeLine("ready " + ip)
}

// approve writes "approve" once, and only before "ready".
func approve() {
	outMu.Lock()
	defer outMu.Unlock()
	if up || approved {
		return
	}
	approved = true
	writeLine("approve")
}

// isUp reports whether "ready" was written.
func isUp() bool {
	outMu.Lock()
	defer outMu.Unlock()
	return up
}

func writeLine(s string) {
	fmt.Fprintln(statusOut, s)
	_ = statusOut.Sync()
}

// statusOut is stdout; tests replace it with a file.
var statusOut = os.Stdout
