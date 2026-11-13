// Command flux-android-tsnet runs a userspace tailnet node for Flux for
// Android. It joins the tailnet without a VPN interface and forwards the
// Flux ports to the listeners of the app on loopback.
package main

import (
	"context"
	"flag"
	"fmt"
	"log"
	"net"
	"os"
	"os/signal"
	"path/filepath"
	"syscall"
	"time"

	// Android denies app UIDs the NETLINK_ROUTE socket, so netmon cannot
	// list interfaces. This registers the synthetic fallback, and it also
	// brings the Android DNS and CA roots that a standalone binary needs.
	_ "tailscale.com/feature/androidbin"

	"flux-android-tsnet/internal/quiet"
	"tailscale.com/client/local"
	"tailscale.com/ipn"
	"tailscale.com/tsnet"
)

func main() {
	// Before anything of tsnet runs, so no log line is buffered or uploaded.
	quiet.Apply()

	var (
		stateDir   = flag.String("state-dir", "", "directory for the node state")
		hostname   = flag.String("hostname", "flux-phone", "tailnet hostname")
		host       = flag.String("host", "127.0.0.1", "forward target host")
		parentPID  = flag.Int("parent-pid", 0, "exit when this process is gone")
		watchStdin = flag.Bool("watch-stdin", true, "exit when stdin closes, and recheck the network on a \"net\" line")
		ports      = flag.String("ports", "", "comma separated ports, or empty for the Flux ports")
		verbose    = flag.Bool("verbose", false, "write the backend's debug log to stderr")
	)
	flag.Parse()

	log.SetOutput(os.Stderr)
	log.SetFlags(log.LstdFlags | log.Lmicroseconds)

	if *stateDir == "" {
		fatalf("state-dir is required")
	}
	list, err := parsePorts(*ports)
	if err != nil {
		fatalf("ports: %v", err)
	}
	// Builds before #50 buffered the backend log here.
	if old, _ := filepath.Glob(filepath.Join(*stateDir, "tailscaled.log*.txt")); len(old) > 0 {
		for _, f := range old {
			_ = os.Remove(f)
		}
	}

	// An enrolled node needs no key; tsnet ignores one on a node that is logged in.
	authKey := os.Getenv("TS_AUTHKEY")
	srv := &tsnet.Server{
		Dir:      *stateDir,
		Hostname: *hostname,
		AuthKey:  authKey,
		UserLogf: log.Printf,
	}
	if *verbose {
		srv.Logf = log.Printf
	}
	defer srv.Close()

	ctx, cancel := signal.NotifyContext(context.Background(), syscall.SIGINT, syscall.SIGTERM)
	defer cancel()
	if *watchStdin {
		go readStdin(os.Stdin, cancel, func() { recheck(srv) })
	}

	lc, err := srv.LocalClient() // starts the server, as Up does
	if err != nil {
		fatalf("tailnet start: %v", err)
	}
	go watchLogin(ctx, cancel, lc, authKey != "")

	upCtx, cancelUp := context.WithTimeout(ctx, 2*time.Minute)
	defer cancelUp()
	status, err := srv.Up(upCtx)
	if err != nil {
		if ctx.Err() != nil {
			return // stopped, or "login": the deferred Close runs
		}
		fatalf("tailnet up: %v", err)
	}
	if len(status.TailscaleIPs) == 0 {
		fatalf("tailnet up without an address")
	}

	if *parentPID > 0 {
		go watchParent(*parentPID, cancel)
	}

	for _, port := range list {
		ln, err := srv.Listen("tcp", fmt.Sprintf(":%d", port))
		if err != nil {
			fatalf("listen %d: %v", port, err)
		}
		go serve(ctx, ln, net.JoinHostPort(*host, fmt.Sprint(port)), log.Printf)
	}
	log.Printf("forwarding %d ports to %s", len(list), *host)
	if ctx.Err() != nil {
		return // "login" or a stop came during the listens
	}
	markReady(status.TailscaleIPs[0].String())
	<-ctx.Done()
}

// watchLogin prints "login" and stops the node when it needs a login that it
// can't do by itself, and "approve" while it waits for approval in the admin
// console. It runs for the life of the process.
func watchLogin(ctx context.Context, cancel func(), lc *local.Client, hasKey bool) {
	w, err := lc.WatchIPNBus(ctx, ipn.NotifyInitialState)
	if err != nil {
		if ctx.Err() == nil {
			line("error login watch: " + err.Error())
			cancel()
		}
		return
	}
	defer w.Close()
	// running is this watcher's own view: Up reads another bus watcher, so
	// isUp can turn true before this one has read an older NeedsLogin.
	running := false
	for {
		n, err := w.Next()
		if err != nil {
			return // ctx ended
		}
		if n.State == nil {
			continue
		}
		switch *n.State {
		case ipn.Running:
			running = true
		case ipn.NeedsLogin:
			// With a key, NeedsLogin is brief during enrollment (tsnet.go:628-633).
			// Without one, or after Running, the node key expired or was removed.
			if !hasKey || running {
				line("login")
				cancel()
				return
			}
		case ipn.NeedsMachineAuth:
			approve()
		}
	}
}

// recheck makes the node look at the network again after the phone changed
// networks. Before "ready" it does nothing. Sys is not a stable API
// (tsnet.go:2331-2336); check it on each tailscale bump.
func recheck(srv *tsnet.Server) {
	if !isUp() {
		return
	}
	sys := srv.Sys()
	if nm, ok := sys.NetMon.GetOK(); ok {
		nm.InjectEvent()
	}
	// InjectEvent rebinds only on a major change (wgengine/userspace.go:1218-1222).
	if ms, ok := sys.MagicSock.GetOK(); ok {
		ms.Rebind()
		ms.ReSTUN("flux-net-change")
	}
}

// fatalf reports a startup error on stdout, which the app reads, and exits.
func fatalf(format string, args ...any) {
	line("error " + fmt.Sprintf(format, args...))
	os.Exit(1)
}
