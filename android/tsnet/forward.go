package main

import (
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
	var copied atomic.Int64
	done := make(chan net.Conn, 2)
	go func() {
		_, _ = io.Copy(countWriter{up, &copied}, conn)
		done <- up
	}()
	go func() {
		_, _ = io.Copy(countWriter{conn, &copied}, up)
		done <- conn
	}()
	first := <-done
	if c, ok := first.(interface{ CloseWrite() error }); ok {
		_ = c.CloseWrite()
	}
	watchStop := make(chan struct{})
	watchDone := make(chan struct{})
	go func() {
		defer close(watchDone)
		last := copied.Load()
		deadline := time.Now().Add(drainIdle)
		_ = conn.SetDeadline(deadline)
		_ = up.SetDeadline(deadline)
		ticker := time.NewTicker(drainIdle / 2)
		defer ticker.Stop()
		for {
			select {
			case <-watchStop:
				return
			case <-ticker.C:
				now := copied.Load()
				if now == last {
					return
				}
				last = now
				deadline = time.Now().Add(drainIdle)
				_ = conn.SetDeadline(deadline)
				_ = up.SetDeadline(deadline)
			}
		}
	}()
	<-done
	close(watchStop)
	<-watchDone
}

type countWriter struct {
	io.Writer
	copied *atomic.Int64
}

func (w countWriter) Write(p []byte) (int, error) {
	n, err := w.Writer.Write(p)
	w.copied.Add(int64(n))
	return n, err
}

// exitOnStdinEOF cancels cancel when stdin closes. The app holds the write
// end, so the node exits when the app process does.
func exitOnStdinEOF(cancel func()) {
	var b [64]byte
	for {
		if _, err := os.Stdin.Read(b[:]); err != nil {
			cancel()
			return
		}
	}
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
