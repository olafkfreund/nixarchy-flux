package main

import (
	"bytes"
	"context"
	"io"
	"net"
	"testing"
	"time"
)

func wait(t *testing.T, done <-chan struct{}) {
	t.Helper()
	select {
	case <-done:
	case <-time.After(3 * time.Second):
		t.Fatal("timed out waiting for forward")
	}
}

func testForward(t *testing.T) (net.Conn, net.Conn, context.CancelFunc, <-chan struct{}) {
	t.Helper()
	old := drainIdle
	drainIdle = 80 * time.Millisecond
	local, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	tail, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		local.Close()
		t.Fatal(err)
	}
	ctx, cancel := context.WithCancel(context.Background())
	done := make(chan struct{})
	go func() {
		defer close(done)
		conn, err := tail.Accept()
		if err == nil {
			forward(ctx, conn, local.Addr().String(), t.Logf)
		}
	}()
	client, err := net.Dial("tcp", tail.Addr().String())
	if err != nil {
		t.Fatal(err)
	}
	server, err := local.Accept()
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() {
		cancel()
		client.Close()
		server.Close()
		tail.Close()
		local.Close()
		wait(t, done)
		drainIdle = old
	})
	return client, server, cancel, done
}

func TestForwardDrain(t *testing.T) {
	t.Run("local closes while tailnet read is blocked", func(t *testing.T) {
		client, server, _, done := testForward(t)
		server.(*net.TCPConn).CloseWrite()
		_ = client.SetReadDeadline(time.Now().Add(time.Second))
		_, err := client.Read(make([]byte, 1))
		if err != io.EOF {
			t.Fatalf("read: %v, want EOF", err)
		}
		wait(t, done)
	})
	t.Run("tailnet closes while reply arrives in chunks", func(t *testing.T) {
		client, server, _, done := testForward(t)
		client.(*net.TCPConn).CloseWrite()
		_ = server.SetReadDeadline(time.Now().Add(time.Second))
		if _, err := server.Read(make([]byte, 1)); err != io.EOF {
			t.Fatalf("server read: %v, want EOF", err)
		}
		want := bytes.Repeat([]byte("chunk"), 8)
		for i := 0; i < 8; i++ {
			if _, err := server.Write([]byte("chunk")); err != nil {
				t.Fatal(err)
			}
			time.Sleep(drainIdle / 3)
		}
		server.(*net.TCPConn).CloseWrite()
		got := make([]byte, len(want))
		_ = client.SetReadDeadline(time.Now().Add(time.Second))
		if _, err := io.ReadFull(client, got); err != nil || !bytes.Equal(got, want) {
			t.Fatalf("reply %q, error %v", got, err)
		}
		wait(t, done)
	})
	t.Run("reply resumes after a quiet sample", func(t *testing.T) {
		// The first watchdog sample after the half-close sees no data; the
		// reply then runs for longer than drainIdle and must still arrive.
		client, server, _, done := testForward(t)
		client.(*net.TCPConn).CloseWrite()
		_ = server.SetReadDeadline(time.Now().Add(time.Second))
		if _, err := server.Read(make([]byte, 1)); err != io.EOF {
			t.Fatalf("server read: %v, want EOF", err)
		}
		time.Sleep(drainIdle * 7 / 10)
		const chunks = 9
		want := bytes.Repeat([]byte("chunk"), chunks)
		for i := 0; i < chunks; i++ {
			if _, err := server.Write([]byte("chunk")); err != nil {
				t.Fatal(err)
			}
			time.Sleep(drainIdle / 4)
		}
		server.(*net.TCPConn).CloseWrite()
		got := make([]byte, len(want))
		_ = client.SetReadDeadline(time.Now().Add(time.Second))
		if _, err := io.ReadFull(client, got); err != nil || !bytes.Equal(got, want) {
			t.Fatalf("reply %q, error %v", got, err)
		}
		wait(t, done)
	})
	t.Run("peer stops reading", func(t *testing.T) {
		client, server, _, done := testForward(t)
		_ = client.(*net.TCPConn).SetReadBuffer(1024)
		server.(*net.TCPConn).CloseRead()
		client.(*net.TCPConn).CloseWrite()
		writerDone := make(chan struct{})
		go func() {
			defer close(writerDone)
			buf := make([]byte, 32*1024)
			for {
				if _, err := server.Write(buf); err != nil {
					return
				}
			}
		}()
		wait(t, done)
		server.Close()
		wait(t, writerDone)
	})
	t.Run("silent drain gets initial deadline", func(t *testing.T) {
		client, server, _, done := testForward(t)
		server.(*net.TCPConn).CloseWrite()
		_ = client.SetReadDeadline(time.Now().Add(time.Second))
		_, _ = client.Read(make([]byte, 1))
		wait(t, done)
	})
	t.Run("cancellation closes both connections", func(t *testing.T) {
		client, server, cancel, done := testForward(t)
		cancel()
		wait(t, done)
		for _, c := range []net.Conn{client, server} {
			_ = c.SetReadDeadline(time.Now().Add(time.Second))
			if _, err := c.Read(make([]byte, 1)); err != io.EOF {
				t.Fatalf("read after cancel: %v, want EOF", err)
			}
		}
	})
}

func TestServeSharedSlots(t *testing.T) {
	local, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	defer local.Close()
	accepted := make(chan net.Conn, maxForwards+1)
	acceptDone := make(chan struct{})
	go func() {
		defer close(acceptDone)
		for {
			c, err := local.Accept()
			if err != nil {
				return
			}
			accepted <- c
		}
	}()
	ctx, cancel := context.WithCancel(context.Background())
	defer cancel()
	proxies := make([]net.Listener, 2)
	served := make([]chan struct{}, 2)
	finished := make(chan struct{}, maxForwards+1)
	forwardDone = func() { finished <- struct{}{} }
	defer func() { forwardDone = nil }()
	for i := range proxies {
		proxies[i], err = net.Listen("tcp", "127.0.0.1:0")
		if err != nil {
			t.Fatal(err)
		}
		defer proxies[i].Close()
		served[i] = make(chan struct{})
		go func(ln net.Listener, done chan struct{}) {
			serve(ctx, ln, local.Addr().String(), t.Logf)
			close(done)
		}(proxies[i], served[i])
	}
	clients := make([]net.Conn, 0, maxForwards+1)
	servers := make([]net.Conn, 0, maxForwards+1)
	completed := 0
	defer func() {
		cancel()
		local.Close()
		for _, c := range clients {
			c.Close()
		}
		for _, c := range servers {
			c.Close()
		}
		for _, done := range served {
			wait(t, done)
		}
		wait(t, acceptDone)
		for ; completed < len(clients); completed++ {
			wait(t, finished)
		}
	}()
	for i := 0; i < maxForwards; i++ {
		c, err := net.Dial("tcp", proxies[i%2].Addr().String())
		if err != nil {
			t.Fatal(err)
		}
		clients = append(clients, c)
		select {
		case server := <-accepted:
			servers = append(servers, server)
		case <-time.After(3 * time.Second):
			t.Fatal("forward did not reach local listener")
		}
	}
	for _, ln := range proxies {
		c, err := net.Dial("tcp", ln.Addr().String())
		if err != nil {
			t.Fatal(err)
		}
		_ = c.SetReadDeadline(time.Now().Add(time.Second))
		if _, err := c.Read(make([]byte, 1)); err != io.EOF {
			t.Fatalf("overflow read: %v, want EOF", err)
		}
		c.Close()
	}
	clients[0].Close()
	servers[0].Close()
	wait(t, finished)
	completed++
	c, err := net.Dial("tcp", proxies[1].Addr().String())
	if err != nil {
		t.Fatal(err)
	}
	clients = append(clients, c)
	select {
	case server := <-accepted:
		servers = append(servers, server)
	case <-time.After(3 * time.Second):
		t.Fatal("released slot was not reused")
	}
}

type failedListener struct{ closed chan struct{} }

func (l failedListener) Accept() (net.Conn, error) { return nil, net.ErrClosed }
func (l failedListener) Close() error {
	close(l.closed)
	return nil
}
func (l failedListener) Addr() net.Addr { return &net.TCPAddr{} }

func TestServeAcceptErrorStopsCancelWatcher(t *testing.T) {
	ctx, cancel := context.WithCancel(context.Background())
	l := failedListener{closed: make(chan struct{})}
	serve(ctx, l, "127.0.0.1:1", t.Logf)
	cancel()
	select {
	case <-l.closed:
		t.Fatal("cancel watcher remained after Accept failed")
	case <-time.After(20 * time.Millisecond):
	}
}

func TestForwardCopiesBothWays(t *testing.T) {
	echo, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	defer echo.Close()
	echoDone := make(chan struct{})
	go func() {
		defer close(echoDone)
		c, err := echo.Accept()
		if err != nil {
			return
		}
		defer c.Close()
		_, _ = io.Copy(c, c)
	}()

	proxy, err := net.Listen("tcp", "127.0.0.1:0")
	if err != nil {
		t.Fatal(err)
	}
	defer proxy.Close()

	ctx, cancel := contextWithTimeout(t, 5*time.Second)
	defer cancel()
	served := make(chan struct{})
	finished := make(chan struct{}, 1)
	forwardDone = func() { finished <- struct{}{} }
	go func() {
		serve(ctx, proxy, echo.Addr().String(), t.Logf)
		close(served)
	}()

	c, err := net.Dial("tcp", proxy.Addr().String())
	if err != nil {
		t.Fatal(err)
	}
	t.Cleanup(func() {
		c.Close()
		wait(t, finished)
		cancel()
		wait(t, served)
		wait(t, echoDone)
		forwardDone = nil
	})
	want := []byte("flux tsnet forwarding")
	if _, err := c.Write(want); err != nil {
		t.Fatal(err)
	}
	got := make([]byte, len(want))
	if _, err := io.ReadFull(c, got); err != nil {
		t.Fatal(err)
	}
	if !bytes.Equal(got, want) {
		t.Fatalf("got %q, want %q", got, want)
	}
}

func TestParsePorts(t *testing.T) {
	got, err := parsePorts("")
	if err != nil {
		t.Fatal(err)
	}
	if len(got) != len(fluxPorts) {
		t.Fatalf("got %d ports, want %d", len(got), len(fluxPorts))
	}
	if got[0] != 12070 || got[len(got)-1] != 12108 {
		t.Fatalf("range is %d to %d", got[0], got[len(got)-1])
	}
	got, err = parsePorts("12100, 12070")
	if err != nil {
		t.Fatal(err)
	}
	if len(got) != 2 || got[0] != 12100 || got[1] != 12070 {
		t.Fatalf("got %v", got)
	}
	if _, err := parsePorts("0"); err == nil {
		t.Fatal("port 0 accepted")
	}
}
