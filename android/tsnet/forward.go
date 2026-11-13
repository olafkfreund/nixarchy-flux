package main

import (
	"bufio"
	"context"
	"io"
	"net"
	"os"
	"sync/atomic"
	"syscall"
	"time"
)

const maxForwards = 64

var (
	drainIdle   = 30 * time.Second
	slots       = make(chan struct{}, maxForwards)
	forwardDone func()
)

// serve accepts connections on ln and copies each one to target. It returns
// when ctx ends or the listener closes.
func serve(ctx context.Context, ln net.Listener, target string, logf func(string, ...any)) {
	stop := context.AfterFunc(ctx, func() { _ = ln.Close() })
	defer stop()
	for {
		conn, err := ln.Accept()
		if err != nil {
			return
		}
		select {
		case slots <- struct{}{}:
			go func() {
				forward(ctx, conn, target, logf)
				<-slots
				if forwardDone != nil {
					forwardDone()
				}
			}()
		default:
			_ = conn.Close()
			logf("too many connections")
		}
	}
}

// forward copies between a tailnet connection and the local listener. The
// local listener must see a clean TCP stream, so this is a plain byte copy.
func forward(ctx context.Context, conn net.Conn, target string, logf func(string, ...any)) {
	defer conn.Close()
	stopConn := context.AfterFunc(ctx, func() { _ = conn.Close() })
	defer stopConn()
	d := net.Dialer{Timeout: 5 * time.Second}
	up, err := d.DialContext(ctx, "tcp", target)
	if err != nil {
		logf("forward %s: %v", target, err)
		return
	}
	defer up.Close()
	stopUp := context.AfterFunc(ctx, func() { _ = up.Close() })
	defer stopUp()
	// After one direction ends, every write extends a drain deadline on both
	// connections, so the other direction ends after drainIdle without
	// data, and a reply that keeps sending is never cut. SetDeadline is safe
	// to call from both copies at once.
	var draining atomic.Bool
	extend := func() {
		if draining.Load() {
			deadline := time.Now().Add(drainIdle)
			_ = conn.SetDeadline(deadline)
			_ = up.SetDeadline(deadline)
		}
	}
	done := make(chan net.Conn, 2)
	go func() {
		_, _ = io.Copy(drainWriter{up, extend}, conn)
		done <- up
	}()
	go func() {
		_, _ = io.Copy(drainWriter{conn, extend}, up)
		done <- conn
	}()
	first := <-done
	if c, ok := first.(interface{ CloseWrite() error }); ok {
		_ = c.CloseWrite()
	}
	// The first deadline also ends a drain in which no byte ever moves.
	draining.Store(true)
	extend()
	<-done
}

// drainWriter calls extend after each write that moved data.
type drainWriter struct {
	io.Writer
	extend func()
}

func (w drainWriter) Write(p []byte) (int, error) {
	n, err := w.Writer.Write(p)
	if n > 0 {
		w.extend()
	}
	return n, err
}

// readStdin cancels cancel when stdin closes, and calls recheck on each
// "net" line. The app holds the write end, so the node exits when the app
// process does.
func readStdin(r io.Reader, cancel func(), recheck func()) {
	s := bufio.NewScanner(r)
	for s.Scan() {
		if s.Text() == "net" {
			recheck()
		}
	}
	cancel()
}

// watchParent cancels cancel when the process pid no longer exists, so that
// the node does not outlive the app that started it.
func watchParent(pid int, cancel func()) {
	for {
		time.Sleep(5 * time.Second)
		p, err := os.FindProcess(pid)
		if err != nil {
			cancel()
			return
		}
		if err := p.Signal(os.Signal(syscall.Signal(0))); err != nil {
			cancel()
			return
		}
	}
}
