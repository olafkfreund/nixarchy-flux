---
status: draft
issue: 50
spec: spec/2026-11-13-50-tsnet-findings.md
coders:
  1: coder   # the helper's Go files, given in full and checked with go vet, go test and an android/arm64 build in a scratch copy
  2: coder   # TsnetNode.kt, Settings and the test, given in full and run on the JVM; codex and agy then review the concurrency independently
  3: coder   # wiring in FluxCore, Model, FluxService and SyncScreen, each edit given below
  4: coder   # the address helpers, 5 small edits and 2 tests, given in full
  5: coder   # three doc sections and one CI step, text given below
reviewers:
  - codex    # reads the branch diff with `codex review --base master`; strongest on the worker, the trial rollback and the Go status lines
  - agy      # independent read of the diff against this plan, including the Sync screen, the docs and the CI step
---

# Plan: Fix the remaining findings on the Android tailnet node

## The approved decisions

Paths are under `android/app/src/main/java/org/omarchy/flux/` unless they
start with `android/`, `docs/`, `.github/` or `tailscale.com/` (the pinned
module, v1.104.4, in `$(go env GOMODCACHE)/tailscale.com@v1.104.4`).

- **No logs to Tailscale, no setting.** The helper calls
  `envknob.SetNoLogsNoSupport()` and `logtail.Disable()` before tsnet
  starts (package `internal/quiet`), and deletes the
  `tailscaled.log*.txt` buffers that #48 builds left. The backend's
  verbose `Logf` is off unless the helper runs with `-verbose`, which the
  app never passes. `UserLogf` stays `log.Printf` (spec decision 1).
- **Stdout lines** from the helper, written by one mutex so they never
  interleave: `ready <ip>` (after `Up` and every `Listen`), `error
  <reason>`, `login` (the node needs a login it can't do; the helper
  stops), `approve` (`NeedsMachineAuth` before `ready`, at most once,
  never after `ready`).
- **Login watch.** The helper watches the IPN bus for the life of the
  process. `NeedsLogin` means `login` when the run has no key, or after
  `ready`. With a key before `ready` it is a normal step of enrollment.
- **Clean stop.** When the context ends during `Up` (stdin EOF, SIGTERM,
  `login`), `main` returns, so the deferred `srv.Close` runs. `Up` keeps
  its 2-minute context.
- **Network change.** The app writes `net` on the helper's stdin
  (flushed); the helper, once `ready`, calls `NetMon.InjectEvent`, then
  `MagicSock.Rebind` and `ReSTUN`, through `Sys()`. No restart.
- **One worker thread** in `TsnetNode` runs every start, stop, key
  change, forget and recheck, in the order asked. Callers return at once.
  Lines and exits of a helper reach the worker, which drops them when the
  helper is no longer the current run. A stop closes stdin, waits up to 8
  seconds, then kills and waits for the exit, so two helpers never share a
  dir.
- **Enrolled flag.** Settings keep `tailnetAuthKey`, `tailnetNode`
  (`None`, `Enrolled`, `NeedsLogin`) and `tailnetDir` (`tsnet` or
  `tsnet-2`), written together with `commit()` by `saveTailnet`. The first
  `ready` of a run with a key saves `Enrolled` and the run's dir, and
  clears the key unless a newer one was saved. An enrolled node starts
  without `TS_AUTHKEY`. `login` on a keyless run saves `NeedsLogin`, and
  the Sync screen says "Needs a new auth key."
- **Change key** always enrolls in the other dir (a trial). `ready`
  promotes it and deletes the old dir after the settings are committed. A
  trial that exits before `ready` deletes its dir, clears its key, toasts
  "The new key didn't work: <reason>.", and starts the old node again
  without a key when it is enrolled. A stop, forget or newer key during a
  trial doesn't roll back.
- **The #48 migration.** A node that #48 enrolled (a saved key, no flag,
  `tsnet/tailscaled.state`) starts in place with its key, so it keeps its
  identity (spec decision 2).
- **Forget node** on the Sync screen, with Remote access on or off, after
  a confirm: deletes both dirs and the tailnet settings.
- **Startup cleanup** deletes the dir that isn't `tailnetDir`, unless a
  key is saved.
- **Addresses.** A loopback link (the node's forward) keeps the stored
  LAN address, in `attach` and in pairing. Startup clears a stored
  loopback address. Wake drops loopback addresses. A loopback link shows
  no address, and "Connected · via tailnet" while the node is `Ready`. The
  handshake cap per source (`net/LanBackend.kt:48-49`) stays.
- **Docs**: a Remote access section in `docs/security.md`, the key, Change
  key, Forget node and no-logs text in `docs/tailscale.md`, and a
  tailscale-bump rule in `docs/releasing.md`.
- **CI** runs `go test ./...` in `android/tsnet`.
- **Unchanged:** `fluxd`, the protocol, the desktop, iOS, macOS, the
  handshake cap, `forward.go`'s `serve` and `forward`.

Refinements of the spec's sketches, each recorded in Corrections when its
step is done:

- **P1 (step 2).** `configure` also takes the `hostname` and a `toast`
  function. The spec moves the hostname into `configure` and asks for a
  toast, but its signature had neither.
- **P2 (step 2).** A stopped helper is recognised by its `Run` object
  (`run === r`) instead of a `gen` counter. `stopNow` sets `run = null`,
  so the effect is the same with one field less.
- **P3 (step 2).** The #48 migration uses the key saved at startup
  (`legacyKey`), and `setKey` clears it, so every typed key, even the
  same text, goes to a fresh dir. With the spec's `legacy(cur)`, a key
  typed after a #48 node failed to come up would also start in place, and
  tsnet ignores a key on a node with state (`tsnet.go:1008-1016`), so
  Change key would do nothing there. A #48 node runs with its key, so the
  helper treats `NeedsLogin` before `ready` as a step of enrollment: a #48
  node that can't log in fails after `Up`'s 2 minutes, not with `login`
  (the spec's risk said `login`). A typed key then goes to a fresh dir.
- **P4 (step 2).** `login` after `ready` on a run with a key also saves
  `NeedsLogin`: that run already cleared its key, so the node is keyless
  now.
- **P5 (step 1).** `readStdin` takes an `io.Reader`, so the test feeds it
  a string. The status lines live in `status.go`; `approve` and `ready`
  share one mutex, so `approve` can't follow `ready` even when the two
  goroutines race.
- **P6 (step 1).** The no-logs check points the upload transport at a
  local `httptest` server, not at `log.tailscale.com.invalid`. The real transport, after a lookup
  failure, falls back to Tailscale's bootstrap DNS on
  the DERP servers: a scratch run of the `.invalid` version dialled them
  for 14 seconds, so the control would itself contact Tailscale. The
  logger starts with 1 buffered entry, as a #48 state dir has, and the
  control must upload it, so the check covers an upload of old entries
  too.
- **P7 (step 4).** `isLoopbackText` also matches `0:0:0:0:0:0:0:1`, the
  form `InetAddress.hostAddress` gives for `::1`.
- **P8 (step 2).** `Store.save` returns whether `commit()` wrote the
  settings. A promotion deletes the old dir only after a successful save;
  otherwise both dirs stay and the old node is still the saved one.
- **P9 (step 2).** A trial that can't start (the launcher throws, or the
  other dir can't be cleared) rolls back like a trial that exited before
  `ready` (`trialFailed`). The spec rolled back only on an exit.
- **P10 (step 1).** The login watch decides on `NeedsLogin` by its own
  view of `Running`, not by `isUp()`: `Up` reads a second bus watcher, so
  `ready` can be written before this watcher reads an older `NeedsLogin`
  of a valid enrollment. And `main` doesn't write `ready` when the context
  ended during the listens.
- **P11 (step 2).** The exit waiter waits for the stdout reader to end
  (`out.join()`), not up to 1.5 seconds, so a late `login` or `ready`
  line is never dropped by the exit. The reader ends at EOF, which the
  exit brings.

## Steps

Steps 1-5 run in order, and the lead commits each one before the next.
Steps 2-4 change `TsnetNode`'s API and its callers, so the Android build
compiles only after step 4; there is no local Android SDK, so CI in step 6
is the first compile. Line numbers are from `e4fad62`; in a file that an
earlier step changed, find the place by its function name.

1. **The helper: `android/tsnet/`**

   - New `android/tsnet/internal/quiet/quiet.go`:
     ```go
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
     ```
   - New `android/tsnet/status.go`:
     ```go
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
     ```
   - `android/tsnet/main.go`: replace the whole file (today 97 lines) with:
     ```go
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
     ```
     What changed against today's file: `quiet.Apply()` first; the
     `-verbose` flag and `Logf` only with it, `UserLogf: log.Printf`; the
     key check (`main.go:42-45`) is gone; the old log buffers are deleted;
     `readStdin` replaces `exitOnStdinEOF` (`:61-63`); `LocalClient` and
     `watchLogin` before `Up`; a cancelled `Up` returns instead of
     `fatalf` (`:67-70`); `ready` moves after the listeners (`:74-75` to
     after `:88`) and goes through `markReady`; `fatalf` writes through
     `line`.
   - `android/tsnet/forward.go`: replace `exitOnStdinEOF` (`:107-117`)
     with:
     ```go
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
     ```
     and add `"bufio"` to its imports. `os` stays (`watchParent` uses it).
   - New `android/tsnet/stdin_test.go`:
     ```go
     package main

     import (
     	"os"
     	"strings"
     	"testing"
     )

     func TestReadStdin(t *testing.T) {
     	var rechecks, cancels int
     	readStdin(strings.NewReader("net\nnoise\nnet\n"), func() { cancels++ }, func() { rechecks++ })
     	if rechecks != 2 || cancels != 1 {
     		t.Fatalf("rechecks=%d cancels=%d, want 2 and 1", rechecks, cancels)
     	}
     }

     func TestStatusLines(t *testing.T) {
     	f, err := os.CreateTemp(t.TempDir(), "out")
     	if err != nil {
     		t.Fatal(err)
     	}
     	old := statusOut
     	statusOut = f
     	t.Cleanup(func() { statusOut = old; up = false; approved = false })

     	approve()
     	approve()
     	markReady("100.64.0.1")
     	approve()
     	line("login")
     	if !isUp() {
     		t.Fatal("isUp after ready")
     	}
     	got, _ := os.ReadFile(f.Name())
     	want := "approve\nready 100.64.0.1\nlogin\n"
     	if string(got) != want {
     		t.Fatalf("got %q, want %q", got, want)
     	}
     }
     ```
   - New `android/tsnet/testdata/nologs/main.go`:
     ```go
     // Command nologs checks what quiet.Apply turns off, in a process of its own:
     // inside a test binary the upload transport is a no-op anyway
     // (logpolicy.go:895). nologs_test.go runs it; with -control it skips Apply.
     // It prints "sent=<n> logged=<n> buffered=<n>": requests that reached a local
     // server through tsnet's upload transport, upload requests from a logger
     // that starts with 1 buffered entry, and the logger's buffer writes.
     package main

     import (
     	"context"
     	"flag"
     	"fmt"
     	"io"
     	"log"
     	"net/http"
     	"net/http/httptest"
     	"strings"
     	"sync/atomic"
     	"time"

     	"flux-android-tsnet/internal/quiet"
     	"tailscale.com/logpolicy"
     	"tailscale.com/logtail"
     	"tailscale.com/types/logid"
     )

     // countingBuffer is a memory buffer that counts the logger's writes.
     type countingBuffer struct {
     	logtail.Buffer
     	writes atomic.Int32
     }

     func (b *countingBuffer) Write(p []byte) (int, error) {
     	b.writes.Add(1)
     	return b.Buffer.Write(p)
     }

     func main() {
     	control := flag.Bool("control", false, "skip quiet.Apply")
     	flag.Parse()
     	if !*control {
     		quiet.Apply()
     	}

     	var sent, logged atomic.Int32
     	ts := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
     		if r.URL.Path == "/transport" {
     			sent.Add(1)
     		} else {
     			logged.Add(1)
     		}
     		_, _ = io.Copy(io.Discard, r.Body)
     	}))
     	defer ts.Close()

     	// The transport tsnet uploads with (tsnet.go:1132), to a local server, so
     	// nothing leaves the machine.
     	tr := logpolicy.NewLogtailTransport(logtail.DefaultHost, nil, nil, log.Printf)
     	req, err := http.NewRequest("POST", ts.URL+"/transport", strings.NewReader("x"))
     	if err != nil {
     		log.Fatal(err)
     	}
     	resp, err := tr.RoundTrip(req)
     	if err != nil {
     		log.Fatal(err)
     	}
     	if resp.Body != nil {
     		resp.Body.Close()
     	}

     	// A logger as tsnet makes one, with a counting buffer and the local server.
     	priv, err := logid.NewPrivateID()
     	if err != nil {
     		log.Fatal(err)
     	}
     	// One entry is already buffered, as a #48 state dir has: Disable must stop its upload too.
     	buf := &countingBuffer{Buffer: logtail.NewMemoryBuffer(100)}
     	if _, err := buf.Buffer.Write([]byte(`{"text":"from before"}` + "\n")); err != nil {
     		log.Fatal(err)
     	}
     	lg := logtail.NewLogger(logtail.Config{
     		Collection: "nologs.flux.test",
     		PrivateID:  priv,
     		BaseURL:    ts.URL,
     		HTTPC:      ts.Client(),
     		Buffer:     buf,
     		Stderr:     io.Discard,
     	}, log.Printf)
     	for i := range 10 {
     		lg.Logf("line %d", i)
     	}
     	ctx, cancel := context.WithTimeout(context.Background(), 10*time.Second)
     	defer cancel()
     	_ = lg.Shutdown(ctx) // uploads what is buffered, unless disabled
     	fmt.Printf("sent=%d logged=%d buffered=%d\n", sent.Load(), logged.Load(), buf.writes.Load())
     }
     ```
   - New `android/tsnet/nologs_test.go`:
     ```go
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
     ```

   Verify:
   ```sh
   cd android/tsnet
   gofmt -l .                      # prints nothing
   go vet ./...
   go test -count=1 ./...          # ok, about 5 s
   go run ./testdata/nologs        # sent=0 logged=0 buffered=0
   go run ./testdata/nologs -control   # sent=1 logged=1 buffered=11
   CGO_ENABLED=0 GOOS=android GOARCH=arm64 go build -o /dev/null .
   git diff --exit-code go.mod go.sum
   ```
   All of these passed on a scratch copy of `e4fad62` with this code.

   Traps:
   - `quiet.Apply()` is the first statement of `main`, before
     `flag.Parse`; nothing of tsnet may run before it.
   - Never print, log or pass the key on the command line. It stays in
     `TS_AUTHKEY`.
   - `go test ./...` skips `testdata/`; the test runs it with `go run`,
     because inside a test binary the transport is a no-op anyway
     (`logpolicy.go:895`). Don't move the program out of `testdata/`.
   - `testdata/nologs` imports `flux-android-tsnet/internal/quiet`; that
     is allowed because `testdata/` is under the module root.
   - `fatalf` still calls `os.Exit`, which skips `srv.Close`. Only the
     cancelled-context path returns; that is the spec's scope.
   - `go.mod` and `go.sum` don't change: every imported package is already
     a dependency of tsnet.

2. **The node's lifecycle: `net/TsnetNode.kt`, `core/Settings.kt`, a new test**

   - `core/Settings.kt`:
     - before `class Settings` (`:5`), add:
       ```kotlin
       /** Whether the tailnet node enrolled. See [org.omarchy.flux.net.TsnetNode]. */
       enum class NodeState { None, Enrolled, NeedsLogin }
       ```
     - replace `tailnetAuthKey` (`:31-34`) with:
       ```kotlin
       /** The Tailscale auth key, until the node it enrolled is up. Only [saveTailnet] writes it. */
       val tailnetAuthKey: String
           get() = prefs.getString("tailnetAuthKey", "").orEmpty()

       /** Whether the tailnet node enrolled. */
       val tailnetNode: NodeState
           get() = NodeState.entries.firstOrNull { it.name == prefs.getString("tailnetNode", null) } ?: NodeState.None

       /** The dir of the tailnet node in the app's files: "tsnet" or "tsnet-2". */
       val tailnetDir: String
           get() = prefs.getString("tailnetDir", null) ?: "tsnet"

       /** The tailnet node's settings, saved together and at once. Returns false when they weren't written. */
       fun saveTailnet(key: String, node: NodeState, dir: String): Boolean =
           prefs.edit().apply {
               if (key.isEmpty()) remove("tailnetAuthKey") else putString("tailnetAuthKey", key)
               putString("tailnetNode", node.name)
               putString("tailnetDir", dir)
           }.commit()
       ```
   - `net/TsnetNode.kt`: replace the whole file with:
     ```kotlin
     package org.omarchy.flux.net

     import java.io.File
     import java.util.concurrent.Executors
     import java.util.concurrent.ScheduledFuture
     import java.util.concurrent.TimeUnit
     import kotlinx.coroutines.flow.MutableStateFlow
     import kotlinx.coroutines.flow.StateFlow
     import kotlinx.coroutines.flow.asStateFlow
     import org.omarchy.flux.core.NodeState

     /**
      * Runs the userspace tailnet node. It joins the tailnet without a VPN
      * interface, so Google VPN and other VPN apps keep the VPN slot, and it
      * forwards the Flux ports to the listeners of this app on loopback.
      *
      * One worker thread does everything, in the order it was asked for: it
      * starts and stops the helper, reads its lines, and writes the tailnet
      * settings. A helper that was stopped changes nothing.
      */
     object TsnetNode {
         sealed interface State {
             data object Off : State
             data class Starting(val note: String? = null) : State
             data class Ready(val address: String) : State
             data class Failed(val reason: String) : State
         }

         /** The tailnet settings. The worker writes them, with commit(). */
         interface Store {
             val key: String
             val node: NodeState
             val dir: String

             /** Returns false when the settings couldn't be written. */
             fun save(key: String, node: NodeState, dir: String): Boolean
         }

         const val NEEDS_KEY = "Needs a new auth key."
         private val DIRS = listOf("tsnet", "tsnet-2")

         private val _state = MutableStateFlow<State>(State.Off)
         val state: StateFlow<State> = _state.asStateFlow()

         internal val worker = Executors.newSingleThreadScheduledExecutor { Thread(it, "flux-tsnet").apply { isDaemon = true } }

         // Seams that tests replace.
         internal var launch: (List<String>, Map<String, String>) -> Process = { cmd, env ->
             ProcessBuilder(cmd).apply { environment().putAll(env) }.start()
         }
         internal var debounceMs = 2_000L

         /** How long a helper gets to close after stdin ends. Above tsnet's 5-second shutdown, not a proven bound. */
         internal var stopGraceMs = 8_000L

         private lateinit var binary: File
         private lateinit var filesDir: File
         private lateinit var hostname: String
         private var pid = 0
         private lateinit var store: Store
         private var log: (String) -> Unit = {}
         private var toast: (String) -> Unit = {}

         // Worker only.
         private var wanted = false
         private var run: Run? = null
         private var pending: ScheduledFuture<*>? = null
         private var retry = false
         private var legacyKey: String? = null

         private class Run(val p: Process, val dir: File, val key: String, val from: File?) {
             var ready = false
             var result: String? = null
         }

         /** Sets the node up. FluxCore calls it once at startup; tests call it before each test. */
         internal fun configure(
             binary: File, filesDir: File, hostname: String, pid: Int, store: Store,
             log: (String) -> Unit, toast: (String) -> Unit,
         ) {
             this.binary = binary
             this.filesDir = filesDir
             this.hostname = hostname
             this.pid = pid
             this.store = store
             this.log = log
             this.toast = toast
             worker.execute {
                 stopNow()
                 wanted = false
                 // A node that #48 enrolled with this key, before the flag existed. It keeps its identity.
                 legacyKey = store.key.takeIf {
                     it.isNotBlank() && store.node == NodeState.None && File(dir(store.dir), "tailscaled.state").exists()
                 }
                 // Finish a promotion that a crash cut short. A saved key may still want the other dir.
                 if (store.key.isBlank()) DIRS.filter { it != store.dir }.forEach { dir(it).deleteRecursively() }
             }
         }

         /** Runs the node while [on]: Flux on and Remote access on. */
         fun apply(on: Boolean) = worker.execute {
             wanted = on
             reconcile()
         }

         /** Saves a new key. With the node wanted, it tries the key on a new node. */
         fun setKey(key: String) = worker.execute {
             store.save(key.trim(), store.node, store.dir)
             legacyKey = null // a typed key always goes to a fresh dir, even the #48 one
             reconcile()
         }

         /** Removes the node from this phone: its state and the key. */
         fun forget() = worker.execute {
             stopNow()
             DIRS.forEach { dir(it).deleteRecursively() }
             store.save("", NodeState.None, DIRS[0])
             legacyKey = null
             wanted = false
         }

         /** The phone changed networks. The node rechecks after [debounceMs]. */
         fun networkChanged() = worker.execute {
             pending?.cancel(false)
             pending = worker.schedule({ recheck() }, debounceMs, TimeUnit.MILLISECONDS)
         }

         private fun dir(name: String) = File(filesDir, name)

         private fun reconcile() {
             stopNow()
             if (!wanted) return
             val key = store.key
             val cur = dir(store.dir)
             when {
                 key.isNotBlank() && key == legacyKey -> start(cur, key, from = null)
                 key.isNotBlank() -> {
                     // A new key always gets a fresh dir: tsnet ignores a key on a node that has state.
                     val to = dir(DIRS.first { it != store.dir })
                     if (to.deleteRecursively()) start(to, key, from = cur)
                     else trialFailed(to, key, cur, "Could not clear the folder ${to.name}.")
                 }
                 store.node == NodeState.Enrolled -> start(cur, "", from = null)
                 store.node == NodeState.NeedsLogin -> fail(NEEDS_KEY)
                 else -> Unit // Off; the Sync screen asks for a key
             }
         }

         /** Stops the helper and waits for its exit, so two helpers never share a dir. */
         private fun stopNow() {
             pending?.cancel(false)
             pending = null
             retry = false
             val r = run
             run = null
             _state.value = State.Off
             if (r == null) return
             runCatching { r.p.outputStream.close() } // EOF: the helper cancels and closes
             if (!r.p.waitFor(stopGraceMs, TimeUnit.MILLISECONDS)) {
                 r.p.destroyForcibly()
                 r.p.waitFor()
             }
         }

         private fun start(dir: File, key: String, from: File?) {
             if (!binary.exists()) return fail("This build has no tailnet helper.")
             dir.mkdirs()
             val cmd = listOf(
                 binary.absolutePath, "-state-dir", dir.absolutePath,
                 "-hostname", hostname, "-parent-pid", pid.toString(),
             )
             // The key goes only in the environment, never on the command line or in a log.
             val env = if (key.isNotBlank()) mapOf("TS_AUTHKEY" to key) else emptyMap()
             val p = try {
                 launch(cmd, env)
             } catch (e: Exception) {
                 val reason = e.message ?: "Could not start the tailnet helper."
                 return if (from != null) trialFailed(dir, key, from, reason) else fail(reason)
             }
             val r = Run(p, dir, key, from)
             run = r
             _state.value = State.Starting()
             val out = Thread({
                 runCatching { p.inputStream.bufferedReader().forEachLine { line -> worker.execute { if (run === r) onLine(r, line) } } }
             }, "flux-tsnet-out").apply { isDaemon = true }
             out.start()
             Thread({ runCatching { p.errorStream.bufferedReader().forEachLine(log) } }, "flux-tsnet-log").apply { isDaemon = true }.start()
             Thread({
                 val code = runCatching { p.waitFor() }.getOrDefault(-1)
                 // The last stdout lines can arrive after the exit. The reader posts them first; it ends at EOF.
                 out.join()
                 worker.execute { if (run === r) onExit(r, code) }
             }, "flux-tsnet-wait").apply { isDaemon = true }.start()
         }

         private fun onLine(r: Run, line: String) {
             when {
                 line.startsWith("ready ") -> {
                     val address = line.removePrefix("ready ").trim()
                     if (address.isEmpty()) return
                     r.ready = true
                     if (r.key.isNotBlank()) {
                         // The old dir goes only after the settings point at the new one.
                         val saved = store.save(if (store.key == r.key) "" else store.key, NodeState.Enrolled, r.dir.name)
                         legacyKey = null
                         if (saved) r.from?.deleteRecursively() else log("could not save the tailnet settings; the old node stays")
                     }
                     _state.value = State.Ready(address)
                 }
                 line == "login" -> {
                     r.result = NEEDS_KEY
                     if (r.key.isBlank() || r.ready) store.save(store.key, NodeState.NeedsLogin, store.dir)
                     fail(NEEDS_KEY)
                 }
                 line == "approve" -> if (!r.ready) _state.value = State.Starting("Approve this phone in the Tailscale admin console.")
                 line.startsWith("error ") -> line.removePrefix("error ").trim().takeIf { it.isNotEmpty() }?.let { r.result = it }
             }
         }

         private fun onExit(r: Run, code: Int) {
             run = null
             val reason = r.result ?: "The tailnet helper stopped ($code)."
             if (r.ready || r.from == null) return fail(reason, retry = reason != NEEDS_KEY)
             trialFailed(r.dir, r.key, r.from, reason)
         }

         /** A new key that failed: its dir goes, the key is cleared, and an enrolled old node starts again. */
         private fun trialFailed(dir: File, key: String, from: File, reason: String) {
             dir.deleteRecursively()
             store.save(if (store.key == key) "" else store.key, store.node, store.dir)
             val why = reason.removeSuffix(".")
             if (store.node == NodeState.Enrolled) {
                 toast("The new key didn't work: $why. Flux kept the old node.")
                 start(from, "", from = null)
             } else {
                 toast("The new key didn't work: $why.")
                 fail(reason)
             }
         }

         private fun recheck() {
             pending = null
             val r = run
             when {
                 // Process.getOutputStream is buffered: without flush the line never arrives.
                 r != null && r.ready -> runCatching { r.p.outputStream.apply { write("net\n".toByteArray()); flush() } }
                 r == null && retry && wanted -> reconcile()
             }
         }

         private fun fail(reason: String, retry: Boolean = false) {
             _state.value = State.Failed(reason)
             this.retry = retry
         }
     }
     ```
   - New `android/app/src/test/java/org/omarchy/flux/net/TsnetNodeTest.kt`:
     ```kotlin
     package org.omarchy.flux.net

     import java.io.BufferedOutputStream
     import java.io.ByteArrayOutputStream
     import java.io.File
     import java.io.InputStream
     import java.io.OutputStream
     import java.io.PipedInputStream
     import java.io.PipedOutputStream
     import java.util.concurrent.CopyOnWriteArrayList
     import java.util.concurrent.CountDownLatch
     import java.util.concurrent.TimeUnit
     import org.junit.After
     import org.junit.Assert.assertEquals
     import org.junit.Assert.assertFalse
     import org.junit.Assert.assertTrue
     import org.junit.Before
     import org.junit.Rule
     import org.junit.Test
     import org.junit.rules.TemporaryFolder
     import org.omarchy.flux.core.NodeState

     class TsnetNodeTest {
         @get:Rule val tmp = TemporaryFolder()

         /** A helper process. [say] writes a stdout line. It exits at stdin EOF unless [slow]. */
         private class FakeProcess(val slow: Boolean) : Process() {
             private val stdoutIn = PipedInputStream(1 shl 16)
             private val stdout = PipedOutputStream(stdoutIn)
             private val stderrIn = PipedInputStream()
             private val stderr = PipedOutputStream(stderrIn)
             private val exited = CountDownLatch(1)
             @Volatile private var code = -1
             private val received = ByteArrayOutputStream()
             @Volatile var stdinClosed = false

             // Buffered like the real Process stdin, so a missing flush loses the line.
             private val stdin = BufferedOutputStream(object : OutputStream() {
                 override fun write(b: Int) = synchronized(received) { received.write(b) }
                 override fun write(b: ByteArray, off: Int, len: Int) = synchronized(received) { received.write(b, off, len) }
                 override fun close() {
                     stdinClosed = true
                     if (!slow) exit(0)
                 }
             })

             val stdinText: String get() = synchronized(received) { received.toString() }
             val alive: Boolean get() = exited.count > 0

             fun say(line: String) {
                 stdout.write("$line\n".toByteArray())
                 stdout.flush()
             }

             @Synchronized fun exit(c: Int) {
                 if (!alive) return
                 code = c
                 runCatching { stdout.close() }
                 runCatching { stderr.close() }
                 exited.countDown()
             }

             override fun getOutputStream(): OutputStream = stdin
             override fun getInputStream(): InputStream = stdoutIn
             override fun getErrorStream(): InputStream = stderrIn
             override fun waitFor(): Int { exited.await(); return code }
             override fun waitFor(timeout: Long, unit: TimeUnit) = exited.await(timeout, unit)
             override fun exitValue(): Int = if (alive) throw IllegalThreadStateException() else code
             override fun destroy() = exit(143)
             override fun destroyForcibly(): Process = apply { exit(137) }
         }

         private class Launch(val cmd: List<String>, val env: Map<String, String>, val p: FakeProcess, val othersExited: Boolean) {
             val dir: String get() = File(cmd[cmd.indexOf("-state-dir") + 1]).name
         }

         private class FakeStore(key: String = "", node: NodeState = NodeState.None, dir: String = "tsnet") : TsnetNode.Store {
             @Volatile override var key = key
             @Volatile override var node = node
             @Volatile override var dir = dir
             @Volatile var broken = false
             override fun save(key: String, node: NodeState, dir: String): Boolean {
                 if (broken) return false
                 this.key = key
                 this.node = node
                 this.dir = dir
                 return true
             }
         }

         private val launches = CopyOnWriteArrayList<Launch>()
         private val toasts = CopyOnWriteArrayList<String>()
         @Volatile private var slow = false
         private lateinit var files: File
         private lateinit var binary: File
         private lateinit var store: FakeStore

         private fun setUp(store: FakeStore) {
             this.store = store
             TsnetNode.configure(binary, files, "flux-test", 1, store, {}, { toasts += it })
             idle()
         }

         @Before
         fun before() {
             files = tmp.newFolder("files")
             binary = tmp.newFile("libflux_tsnet.so")
             TsnetNode.debounceMs = 0
             TsnetNode.stopGraceMs = 300
             TsnetNode.launch = { cmd, env ->
                 val others = launches.none { it.p.alive }
                 FakeProcess(slow).also { launches += Launch(cmd, env, it, others) }
             }
         }

         @After
         fun after() {
             TsnetNode.apply(false)
             idle()
         }

         /** Waits until the worker has run everything queued so far. */
         private fun idle() = TsnetNode.worker.submit {}.get(5, TimeUnit.SECONDS)

         /** Waits until [cond] holds; lines and exits reach the worker from other threads. */
         private fun await(what: String, cond: () -> Boolean) {
             val end = System.nanoTime() + 3_000_000_000
             while (System.nanoTime() < end) {
                 idle()
                 if (cond()) return
                 Thread.sleep(10)
             }
             throw AssertionError("timed out: $what; state ${TsnetNode.state.value}")
         }

         private fun state() = TsnetNode.state.value
         private fun last() = launches.last()
         private fun dir(name: String) = File(files, name)

         @Test
         fun theLastCallWins() {
             setUp(FakeStore(node = NodeState.Enrolled))
             TsnetNode.apply(true)
             TsnetNode.apply(false)
             TsnetNode.apply(true)
             idle()
             assertEquals(1, launches.count { it.p.alive })
             assertEquals(TsnetNode.State.Starting(), state())
             TsnetNode.apply(true)
             TsnetNode.apply(false)
             idle()
             assertTrue(launches.none { it.p.alive })
             assertEquals(TsnetNode.State.Off, state())
         }

         @Test
         fun aStoppedHelperChangesNothing() {
             setUp(FakeStore(node = NodeState.Enrolled))
             slow = true
             TsnetNode.apply(true)
             idle()
             val p = last().p
             TsnetNode.apply(false)
             // Not await: the worker is busy in the stop until the grace ends.
             val end = System.nanoTime() + 3_000_000_000
             while (!p.stdinClosed && System.nanoTime() < end) Thread.sleep(5)
             assertTrue(p.alive)
             p.say("ready 100.64.0.9")
             p.say("error tailnet up: context canceled")
             p.say("login")
             idle()
             await("helper killed") { !p.alive }
             Thread.sleep(100)
             idle()
             assertEquals(TsnetNode.State.Off, state())
             assertEquals(NodeState.Enrolled, store.node)
         }

         @Test
         fun stopClearsAFailure() {
             setUp(FakeStore(node = NodeState.Enrolled))
             binary.delete()
             TsnetNode.apply(true)
             idle()
             assertEquals(TsnetNode.State.Failed("This build has no tailnet helper."), state())
             TsnetNode.apply(false)
             idle()
             assertEquals(TsnetNode.State.Off, state())
         }

         @Test
         fun aNewHelperStartsAfterTheOldOneExited() {
             setUp(FakeStore(node = NodeState.Enrolled))
             slow = true
             TsnetNode.apply(true)
             TsnetNode.setKey("tskey-2")
             idle()
             assertEquals(2, launches.size)
             assertTrue(launches.all { it.othersExited })
         }

         @Test
         fun readyWithAKeyEnrollsAndClearsTheKey() {
             setUp(FakeStore(key = "tskey-1"))
             File(dir("tsnet"), "junk").apply { parentFile.mkdirs(); writeText("x") }
             TsnetNode.apply(true)
             idle()
             assertEquals("tsnet-2", last().dir)
             assertEquals("tskey-1", last().env["TS_AUTHKEY"])
             assertFalse("the key is never an argument", last().cmd.any { "tskey" in it })
             last().p.say("ready 100.64.0.5")
             await("ready") { state() == TsnetNode.State.Ready("100.64.0.5") }
             assertEquals("", store.key)
             assertEquals(NodeState.Enrolled, store.node)
             assertEquals("tsnet-2", store.dir)
             assertFalse(dir("tsnet").exists())
         }

         @Test
         fun aNewerKeyStays() {
             setUp(FakeStore(key = "tskey-1"))
             TsnetNode.apply(true)
             idle()
             store.key = "tskey-2"
             last().p.say("ready 100.64.0.5")
             await("ready") { state() is TsnetNode.State.Ready }
             assertEquals("tskey-2", store.key)
         }

         @Test
         fun loginNeedsANewKeyAndTheExitKeepsTheReason() {
             setUp(FakeStore(node = NodeState.Enrolled))
             TsnetNode.apply(true)
             idle()
             assertEquals(null, last().env["TS_AUTHKEY"])
             last().p.say("login")
             await("login") { state() == TsnetNode.State.Failed(TsnetNode.NEEDS_KEY) }
             assertEquals(NodeState.NeedsLogin, store.node)
             last().p.exit(0)
             await("exit") { !last().p.alive }
             Thread.sleep(100)
             idle()
             assertEquals(TsnetNode.State.Failed(TsnetNode.NEEDS_KEY), state())
             TsnetNode.apply(true)
             idle()
             assertEquals(1, launches.size)
             assertEquals(TsnetNode.State.Failed(TsnetNode.NEEDS_KEY), state())
         }

         @Test
         fun approveThenReady() {
             setUp(FakeStore(node = NodeState.Enrolled))
             TsnetNode.apply(true)
             idle()
             last().p.say("approve")
             await("approve") { (state() as? TsnetNode.State.Starting)?.note != null }
             last().p.say("ready 100.64.0.7")
             last().p.say("approve")
             await("ready") { state() is TsnetNode.State.Ready }
             idle()
             assertEquals(TsnetNode.State.Ready("100.64.0.7"), state())
         }

         @Test
         fun aFailedNewKeyKeepsTheOldNode() {
             setUp(FakeStore(node = NodeState.Enrolled))
             TsnetNode.apply(true)
             TsnetNode.setKey("tskey-bad")
             idle()
             val trial = last()
             assertEquals("tsnet-2", trial.dir)
             trial.p.say("error tailnet up: invalid key")
             trial.p.exit(1)
             await("relaunch") { launches.size == 3 }
             assertEquals("tsnet", last().dir)
             assertEquals(null, last().env["TS_AUTHKEY"])
             assertFalse(dir("tsnet-2").exists())
             assertEquals("", store.key)
             assertEquals("tsnet", store.dir)
             assertTrue(toasts.single().startsWith("The new key didn't work: tailnet up: invalid key"))
         }

         @Test
         fun aGoodNewKeyReplacesTheOldNode() {
             setUp(FakeStore(node = NodeState.Enrolled))
             File(dir("tsnet"), "tailscaled.state").apply { parentFile.mkdirs(); writeText("old") }
             TsnetNode.apply(true)
             TsnetNode.setKey("tskey-good")
             idle()
             last().p.say("ready 100.64.0.8")
             await("ready") { state() is TsnetNode.State.Ready }
             assertFalse(dir("tsnet").exists())
             assertEquals("tsnet-2", store.dir)
             assertEquals("", store.key)
         }

         @Test
         fun aFailedSaveKeepsTheOldNode() {
             setUp(FakeStore(node = NodeState.Enrolled))
             File(dir("tsnet"), "tailscaled.state").apply { parentFile.mkdirs(); writeText("old") }
             TsnetNode.apply(true)
             TsnetNode.setKey("tskey-good")
             idle()
             store.broken = true
             last().p.say("ready 100.64.0.8")
             await("ready") { state() is TsnetNode.State.Ready }
             assertTrue(File(dir("tsnet"), "tailscaled.state").exists())
             assertEquals("tsnet", store.dir)
         }

         @Test
         fun aTrialThatCannotLaunchKeepsTheOldNode() {
             setUp(FakeStore(node = NodeState.Enrolled))
             TsnetNode.apply(true)
             idle()
             val good = TsnetNode.launch
             TsnetNode.launch = { cmd, env -> if ("TS_AUTHKEY" in env) throw java.io.IOException("no exec") else good(cmd, env) }
             TsnetNode.setKey("tskey-2")
             idle()
             assertEquals(2, launches.size)
             assertEquals("tsnet", last().dir)
             assertEquals(null, last().env["TS_AUTHKEY"])
             assertEquals("", store.key)
             assertTrue(toasts.single().startsWith("The new key didn't work: no exec"))
         }

         @Test
         fun aStoppedTrialDoesNotRollBack() {
             setUp(FakeStore(node = NodeState.Enrolled))
             TsnetNode.apply(true)
             TsnetNode.setKey("tskey-2")
             TsnetNode.apply(false)
             idle()
             Thread.sleep(100)
             idle()
             assertEquals(2, launches.size)
             assertEquals(TsnetNode.State.Off, state())
             assertEquals("tskey-2", store.key)
         }

         @Test
         fun forgetDuringATrialRemovesEverything() {
             setUp(FakeStore(node = NodeState.Enrolled))
             TsnetNode.apply(true)
             TsnetNode.setKey("tskey-2")
             TsnetNode.forget()
             idle()
             Thread.sleep(100)
             idle()
             assertEquals(2, launches.size)
             assertFalse(dir("tsnet").exists() || dir("tsnet-2").exists())
             assertEquals("", store.key)
             assertEquals(NodeState.None, store.node)
             assertEquals("tsnet", store.dir)
         }

         @Test
         fun aSecondKeyStartsItsOwnTrial() {
             setUp(FakeStore(node = NodeState.Enrolled))
             TsnetNode.apply(true)
             TsnetNode.setKey("tskey-2")
             TsnetNode.setKey("tskey-3")
             idle()
             assertEquals(3, launches.size)
             assertEquals("tsnet-2", last().dir)
             assertEquals("tskey-3", last().env["TS_AUTHKEY"])
             assertTrue(toasts.isEmpty())
         }

         @Test
         fun aNodeFrom48KeepsItsDir() {
             File(dir("tsnet"), "tailscaled.state").apply { parentFile.mkdirs(); writeText("old") }
             setUp(FakeStore(key = "tskey-48"))
             TsnetNode.apply(true)
             idle()
             assertEquals("tsnet", last().dir)
             assertEquals("tskey-48", last().env["TS_AUTHKEY"])
             TsnetNode.setKey("tskey-48")
             idle()
             assertEquals("a typed key goes to a fresh dir, even the same one", "tsnet-2", last().dir)
         }

         @Test
         fun aNetworkChangeRechecksOnce() {
             setUp(FakeStore(node = NodeState.Enrolled))
             TsnetNode.apply(true)
             idle()
             TsnetNode.networkChanged()
             idle()
             idle()
             assertEquals("nothing while starting", "", last().p.stdinText)
             TsnetNode.debounceMs = 200
             last().p.say("ready 100.64.0.5")
             await("ready") { state() is TsnetNode.State.Ready }
             TsnetNode.networkChanged()
             TsnetNode.networkChanged()
             await("net line") { last().p.stdinText.isNotEmpty() }
             Thread.sleep(300)
             assertEquals("net\n", last().p.stdinText)
         }

         @Test
         fun aNetworkChangeRestartsAFailedNode() {
             setUp(FakeStore(node = NodeState.Enrolled))
             TsnetNode.apply(true)
             idle()
             last().p.say("error tailnet up: no network")
             last().p.exit(1)
             await("failed") { state() == TsnetNode.State.Failed("tailnet up: no network") }
             TsnetNode.networkChanged()
             await("relaunch") { launches.size == 2 }
             assertEquals(TsnetNode.State.Starting(), state())
         }

         @Test
         fun startupRemovesTheOtherDir() {
             File(dir("tsnet-2"), "junk").apply { parentFile.mkdirs(); writeText("x") }
             File(dir("tsnet"), "tailscaled.state").apply { parentFile.mkdirs(); writeText("x") }
             setUp(FakeStore(node = NodeState.Enrolled))
             assertFalse(dir("tsnet-2").exists())
             assertTrue(dir("tsnet").exists())
         }
     }
     ```

   Verify: CI in step 6. `TsnetNode.kt` and this test, with a stub of
   `NodeState`, compiled with kotlinc 2.4 and passed 5 runs in a row on
   the JVM (19 tests, about 2 s). The first 17 failed, as they should,
   with the `flush()` removed, the `run === r` check removed, the stop
   grace skipped, and the spec's `legacy(cur)`.

   Traps:
   - `TsnetNode` must not use `Context`, `android.os.Process` or
     `android.util.Log`: the unit tests run on the JVM without Android
     stubs (no `testOptions` in `android/app/build.gradle.kts`).
   - Only the worker writes `wanted`, `run`, `pending`, `retry`,
     `legacyKey` and the store. Reader and waiter threads only post to
     the worker.
   - `stopNow` sets `run = null` before it waits, so lines that the old
     helper prints during its `Close` are dropped.
   - `destroyForcibly()` doesn't wait; the `waitFor()` after it is what
     keeps two helpers off one dir. The fake process exits at once, so
     the tests can't catch its removal; keep it.
   - `save` (commit) runs before `r.from?.deleteRecursively()` in the
     `ready` branch, and the delete runs only when `save` returned true.
   - A trial failure clears its key, also when the cause was no network
     (see decision 1 for the owner). Such a `Failed` is not retried on a
     network change: there is no key and, on a fresh phone, no node.
   - `Process.getOutputStream` is buffered: the `net` write needs its
     `flush()`.
   - `TsnetNodeTest` uses the `object` across tests; `configure` queues a
     reset (stop, `wanted = false`), and `@After` turns the node off.
   - The old `start`, `stop`, `stopLocked`, `readReady`, `readLog` and
     `awaitExit` go; step 3 changes their callers.

3. **Wiring: `core/FluxCore.kt`, `core/Model.kt`, `service/FluxService.kt`, `ui/SyncScreen.kt`**

   - `core/FluxCore.kt`:
     - imports: add `kotlinx.coroutines.CoroutineScope`,
       `kotlinx.coroutines.Dispatchers`, `kotlinx.coroutines.SupervisorJob`,
       `kotlinx.coroutines.launch` and `org.omarchy.flux.net.TsnetNode`.
     - after `val io = ...` (`:50`), add:
       ```kotlin
       /** The tailnet settings, as [TsnetNode] reads and writes them. */
       private val tailnetStore = object : TsnetNode.Store {
           override val key get() = settings.tailnetAuthKey
           override val node get() = settings.tailnetNode
           override val dir get() = settings.tailnetDir
           override fun save(key: String, node: NodeState, dir: String) =
               settings.saveTailnet(key, node, dir).also { publish() }
       }
       ```
     - in `init`, after `settings = Settings(app)` (`:128`), add:
       ```kotlin
       val tailnetId = local.deviceId.lowercase().filter { it.isLetterOrDigit() || it == '-' }.take(20)
       TsnetNode.configure(
           File(app.applicationInfo.nativeLibraryDir, "libflux_tsnet.so"), app.filesDir,
           "flux-" + tailnetId.ifBlank { "phone" }, android.os.Process.myPid(), tailnetStore,
           { Log.i("FluxTsnet", it) }, { toast(it) },
       )
       ```
     - at the end of `init`, after `publish()` (`:149`), add:
       ```kotlin
       // The computer rows show "via tailnet" only while the node is up.
       CoroutineScope(SupervisorJob() + Dispatchers.Default).launch { TsnetNode.state.collect { publish() } }
       ```
     - in `publish` (`:476`), after `tailnetKeySet = ...`, add
       `tailnetNode = settings.tailnetNode,`.
     - replace `applyTsnet`, `setTailnetAuthKey` and `stopTsnet`
       (`:607-637`, keep `setRemoteAccess` at `:623-627` as it is) with:
       ```kotlin
       /**
        * Runs the userspace tailnet node while Flux and Remote access are on.
        * The node joins the tailnet without a VPN interface, so another VPN
        * app keeps the VPN slot.
        */
       fun applyTsnet() = TsnetNode.apply(settings.enabled && settings.remoteAccess)

       /** Saves a new tailnet key. The node tries it on a new node while Remote access is on. */
       fun setTailnetAuthKey(key: String) = TsnetNode.setKey(key)

       /** Removes the tailnet node from this phone and turns Remote access off. */
       fun forgetTailnet() {
           settings.remoteAccess = false
           publish()
           TsnetNode.forget()
       }

       fun stopTsnet() = TsnetNode.apply(false)
       ```
   - `core/Model.kt`, `UiState`, after `tailnetKeySet` (`:224`), add:
     ```kotlin
     /** Whether the tailnet node enrolled. */
     val tailnetNode: NodeState = NodeState.None,
     ```
   - `service/FluxService.kt`:
     - import `org.omarchy.flux.net.TsnetNode`;
     - `networkCallback.onAvailable` and `onLost` (`:58-66`): add
       `TsnetNode.networkChanged()` as the last line of each;
     - `screenReceiver` (`:89-91`) becomes:
       ```kotlin
       private val screenReceiver = object : BroadcastReceiver() {
           override fun onReceive(context: Context, intent: Intent) {
               search()
               // After Doze the node's paths can be old.
               TsnetNode.networkChanged()
           }
       }
       ```
     - `:243` and `:334` stay as they are.
   - `ui/SyncScreen.kt`, `RemoteAccessSection` (`:254-325`):
     - import `org.omarchy.flux.core.NodeState`;
     - add `var showForget by rememberSaveable { mutableStateOf(false) }`
       after `keyText` (`:259`), and
       `val enrolled = state.tailnetNode == NodeState.Enrolled`;
     - `toggle()` (`:265-268`): only the condition at `:266` changes, to
       `if (!state.remoteAccess && !state.tailnetKeySet && !enrolled) askKey()`;
       the `else` at `:267` stays;
     - the detail (`:270-275`):
       ```kotlin
       val detail = when (val n = nodeState) {
           is TsnetNode.State.Ready -> "Address ${n.address}. Add it as an extra address on the computer."
           is TsnetNode.State.Starting -> n.note ?: "Connecting to the tailnet."
           is TsnetNode.State.Failed -> n.reason
           else -> if (state.tailnetKeySet || enrolled) "Reach this phone away from home without a VPN app." else "Needs a tailnet auth key."
       }
       ```
     - after the `if (state.remoteAccess) { ... }` block (ends `:296`),
       inside the `Column`:
       ```kotlin
       if (state.tailnetKeySet || state.tailnetNode != NodeState.None) {
           TextButton(onClick = { showForget = true }) { Text("Forget node") }
       }
       ```
     - the key dialog's `text` (`:303-311`) becomes:
       ```kotlin
       text = {
           Column(verticalArrangement = Arrangement.spacedBy(12.dp)) {
               if (enrolled) {
                   Text(
                       "A new key makes a new node with a new address. Add the new address on " +
                           "each computer. Remove the old node in the admin console.",
                   )
               }
               OutlinedTextField(
                   value = keyText,
                   onValueChange = { keyText = it },
                   label = { Text("tskey-auth-...") },
                   singleLine = false,
                   minLines = 3,
               )
           }
       },
       ```
     - after the key dialog (`:324`), add:
       ```kotlin
       if (showForget) {
           AlertDialog(
               onDismissRequest = { showForget = false },
               title = { Text("Forget node") },
               text = { Text("Remove the tailnet node from this phone? Remove it in the Tailscale admin console too.") },
               confirmButton = {
                   TextButton(onClick = {
                       showForget = false
                       FluxCore.forgetTailnet()
                   }) { Text("Forget") }
               },
               dismissButton = { TextButton(onClick = { showForget = false }) { Text("Cancel") } },
           )
       }
       ```

   Verify: CI in step 6, and
   ```sh
   grep -rn "TsnetNode\.\(start\|stop\)(\|tailnetAuthKey =" android/app/src/main/java
   ```
   prints nothing.

   Traps:
   - `configure` needs `local` (for the id) and `settings`; keep it
     after both (`:122-128`).
   - `NodeState` is in `org.omarchy.flux.core`, so `FluxCore.kt` and
     `Model.kt` need no import for it.
   - Don't call `TsnetNode` from `io` any more; every call returns at once.
   - `tailnetStore.save` runs on the worker; `publish` takes the core
     lock, and the worker never holds it, so there is no lock order issue.
   - The key dialog's Save (`:316-317`) stays: `setTailnetAuthKey` then
     `setRemoteAccess(true)`. Both queue on the worker in that order.
   - `Arrangement`, `dp`, `Text`, `TextButton`, `AlertDialog`,
     `rememberSaveable` and `mutableStateOf` are already imported in
     `SyncScreen.kt`; check with
     `grep -n "^import.*\.\(Arrangement\|dp\|AlertDialog\)$" android/app/src/main/java/org/omarchy/flux/ui/SyncScreen.kt`.

4. **Addresses: `core/Device.kt`, `core/FluxCore.kt`, `core/Wake.kt`, `core/Model.kt`, `ui/ComputersScreen.kt`, tests**

   - `core/Device.kt`:
     - imports: add `java.net.InetAddress` and
       `org.omarchy.flux.net.TsnetNode`;
     - at the end of the file, after `refusesUnpaired` (`:387-388`):
       ```kotlin
       /** The address to keep for a computer after a link from [link]: a loopback link is the tailnet node's forward, so the stored one stays. */
       internal fun nextLastIp(stored: String, link: InetAddress?): String =
           link?.takeUnless { it.isLoopbackAddress }?.hostAddress ?: stored

       /** True for a stored loopback address, without a name lookup. */
       internal fun isLoopbackText(ip: String) = ip.startsWith("127.") || ip == "::1" || ip == "0:0:0:0:0:0:0:1"
       ```
     - `snapshot` (`:153-198`): `ip` (`:157`) becomes
       ```kotlin
       ip = link?.address?.let { if (it.isLoopbackAddress) "" else it.hostAddress.orEmpty() } ?: lastIp,
       ```
       and add as the last argument of the `DeviceUi(...)` call (after
       `:197`):
       ```kotlin
       viaTailnet = link?.address?.isLoopbackAddress == true && TsnetNode.state.value is TsnetNode.State.Ready,
       ```
     - `pairingDone` (`:328`): `lastIp = nextLastIp(lastIp, link?.address),`.
   - `core/Model.kt`, `DeviceUi`, as the last field (before `)` at `:162`):
     ```kotlin
     /** True while the link comes through this phone's tailnet node. */
     val viaTailnet: Boolean = false,
     ```
   - `core/FluxCore.kt`:
     - `init` (`:135`): replace `d.lastIp = t.lastIp` with:
       ```kotlin
       // Builds before #50 stored the tailnet node's 127.0.0.1.
       if (isLoopbackText(t.lastIp)) trust.update(t.id) { it.copy(lastIp = "") }
       d.lastIp = t.lastIp.takeUnless { isLoopbackText(it) }.orEmpty()
       ```
     - `attach` (`:336`): `d.lastIp = nextLastIp(d.lastIp, link.address)`.
       The trust update at `:339` then stores the kept address.
   - `core/Wake.kt`, `destinations` (`:64-71`): the KDoc adds "Loopback
     addresses are left out: a send there always succeeds."; the filter
     becomes `.filter { IPV4.matches(it) && !isLoopbackText(it) }.distinct()`.
   - `ui/ComputersScreen.kt`, `linkLine` (`:179-190`):
     ```kotlin
     /** The link of a computer in words: "Connected · Wi-Fi", "Connected · Tailscale", "Connected · via tailnet", or "Offline". */
     private fun linkLine(d: DeviceUi, onWifi: Boolean): String {
         if (!d.online) return "Offline"
         // Only an address with digits, dots, colons, and hex letters goes to the parser, so that it never looks up a name.
         val tailscale = d.ip.isNotBlank() && d.ip.none { it.isLetter() && it !in 'a'..'f' && it !in 'A'..'F' } &&
             runCatching { isTailscale(InetAddress.getByName(d.ip)) }.getOrDefault(false)
         return when {
             d.viaTailnet -> "Connected · via tailnet"
             // A loopback link while the node isn't up: from the node or from an app on this phone.
             d.ip.isBlank() -> "Connected"
             tailscale -> "Connected · Tailscale"
             onWifi -> "Connected · Wi-Fi"
             else -> "Connected"
         }
     }
     ```
   - New `android/app/src/test/java/org/omarchy/flux/core/DeviceTest.kt`:
     ```kotlin
     package org.omarchy.flux.core

     import java.net.InetAddress
     import org.junit.Assert.assertEquals
     import org.junit.Assert.assertFalse
     import org.junit.Assert.assertTrue
     import org.junit.Test

     class DeviceTest {
         private fun ip(s: String) = InetAddress.getByName(s) // literals only: no lookup

         @Test
         fun aLoopbackLinkKeepsTheStoredAddress() {
             assertEquals("192.168.1.20", nextLastIp("192.168.1.20", ip("127.0.0.1")))
             assertEquals("192.168.1.20", nextLastIp("192.168.1.20", ip("::1")))
             assertEquals("", nextLastIp("", ip("127.0.0.1")))
             assertEquals("192.168.1.30", nextLastIp("192.168.1.20", ip("192.168.1.30")))
             assertEquals("100.64.0.3", nextLastIp("192.168.1.20", ip("100.64.0.3")))
             assertEquals("192.168.1.20", nextLastIp("192.168.1.20", null))
         }

         @Test
         fun loopbackText() {
             for (s in listOf("127.0.0.1", "127.1.2.3", "::1", "0:0:0:0:0:0:0:1", ip("::1").hostAddress)) assertTrue(s, isLoopbackText(s))
             for (s in listOf("", "192.168.1.20", "100.64.0.1", "fe80::1", "10.127.0.1")) assertFalse(s, isLoopbackText(s))
         }
     }
     ```
   - `android/app/src/test/java/org/omarchy/flux/core/WakeTest.kt`, at the
     end of `destinationsAreDistinctIpv4` (`:44`):
     ```kotlin
     assertEquals(
         "a send to loopback always succeeds",
         listOf("192.168.1.255", "255.255.255.255"),
         Wake.destinations(targets.take(1), emptyList(), "127.0.0.1"),
     )
     ```

   Verify: CI in step 6, and
   ```sh
   grep -rn "lastIp = \|hostAddress" android/app/src/main/java/org/omarchy/flux/core/{FluxCore,Device}.kt
   ```
   shows no `lastIp = link...hostAddress` left.

   Traps:
   - `isLoopbackText` works on text; never call `InetAddress.getByName`
     on a stored address on the main thread.
   - `TsnetNode.state.value` in `snapshot` is a plain read, under the core
     lock; it never blocks.
   - The handshake cap in `LanBackend` doesn't change.
   - `DeviceUi` is built in other places too (demo devices); the new field
     has a default, so they need no change. Check with
     `grep -rn "DeviceUi(" android/app/src`.

5. **Docs and CI: `docs/security.md`, `docs/tailscale.md`, `docs/releasing.md`, `.github/workflows/build.yml`**

   - `docs/security.md`: before `## Album art` (`:184`), add:
     ```markdown
     ## Remote access

     With **Remote access** on, Flux for Android runs a userspace Tailscale node.
     It connects to Tailscale's control servers, to Tailscale's DERP relays, and to the peers in the tailnet.
     It uploads no logs to Tailscale and keeps no log buffer, so Tailscale support has no logs of it.
     This was checked against tailscale v1.104.4, and the check runs again with each update (see the [release guide](releasing.md#pinned-tools)).
     The node's state is in the app's files, and app backup is off.
     The auth key is kept only until the node is up.
     ```
     and `:186` becomes "Besides the paired computers and, with Remote
     access, Tailscale, the apps connect to 1 other kind of host."
   - `docs/tailscale.md`:
     - step 3 (`:30-32`) becomes:
       ```markdown
       3. Enter a tailnet auth key from the Tailscale admin console.
          Use a key with an expiry, and remove the phone from the tailnet when you
          stop using it. The phone forgets the key once the node is up.
       ```
     - `:46` ("The node keeps its key and identity in the app storage.")
       becomes:
       ```markdown
       The node keeps its identity in the app storage. It uploads no logs to Tailscale.
       **Change key** makes a new node with a new address: add the new address on
       each computer, and remove the old node in the admin console. If the new key
       fails, the phone keeps the old node.
       **Forget node** removes the node from the phone. Remove it in the admin
       console too.
       ```
   - `docs/releasing.md`, in "Pinned tools", after the Gradle wrapper item
     (ends `:365`), add:
     ```markdown
     - The tailnet node pins `tailscale.com` in `android/tsnet/go.mod`.
       After a bump, `go test ./...` in `android/tsnet` must pass; it checks that no logs are uploaded or buffered.
       Also read the diff of `logpolicy`, `logtail`, `tsnet.startLogger`, `tsd`, and the `NetMon` and `MagicSock` methods that `recheck` in `main.go` calls: `Sys()` is not a stable API.
     ```
   - `.github/workflows/build.yml`, in the `android` job, after the
     `actions/setup-go` step (ends `:117`), add:
     ```yaml
           - name: Test the tailnet helper
             working-directory: android/tsnet
             run: go test ./...
     ```

   Verify: `nix flake check path:.` in step 6; read the rendered
   Markdown on GitHub after the push.

   Traps:
   - A step-level `working-directory` is relative to the workspace, not
     to the job's default `android`; so it is `android/tsnet`.
   - `release.yml:67` calls `build.yml`, so the release runs the test too;
     don't add it there.
   - Don't claim more than v1.104.4 does: no "never", no other versions.

6. **CI (the lead).**
   - `cd android/tsnet && go test -count=1 ./...` and `nix flake check path:.`
     pass locally.
   - Push, after an agent-bus announcement.
   - Dispatch `build.yml` on the branch:
     ```sh
     gh workflow run build.yml -R olafkfreund/nixarchy-flux --ref fix/50-tsnet-findings
     ```
   - Poll every 5 minutes. The android job must pass: "Test the tailnet
     helper", unit tests (with `TsnetNodeTest`, `DeviceTest` and
     `WakeTest`), `lintDebug`, both assembles, and the ABI check.

7. **Device tests (the lead and the owner)**, in a booked slot, with the
   #13 setup (`fluxd` on p620, paired) and the branch's `android-debug`
   APK.

   `$b` is `/tmp/claude-1000/-mnt-code-Source-home-GitHub/9f0892e4-8d93-4a6d-a1da-d5bcdbb3eba1/scratchpad/r13/flux-core`,
   the build that runs `fluxd` now. Use `$b/bin/flux-cli`, never a
   `flux-cli` from `PATH`. `<phone>` is the device name from
   `$b/bin/flux-cli devices`.

   **Setup:**
   1. Read the serials with `adb devices`; wireless ports change. The
      Pixel 11 Pro XL runs every check; the Galaxy Tab S8 Ultra repeats
      checks 1 and 4 if time allows.
   2. Before the install, on a phone that runs a #48 build with Remote
      access on: note the tailnet address on the Sync screen.
   3. A new debug APK from CI can't install over the installed one (a
      different debug key). Ask the owner, then
      `adb -s <serial> uninstall org.omarchy.flux`,
      `adb -s <serial> install app-debug.apk`, and pair again with
      `$b/bin/flux-cli pair` and `accept`. The uninstall removes the #48
      node, so check 1's upgrade part runs only if the signatures match
      (`adb -s <serial> shell pm dump org.omarchy.flux | grep -A1 signatures`
      against `keytool -printcert -jarfile app-debug.apk`). Whether a
      mismatch may skip it is decision 2 for the owner.

   Before each check, note the Sync screen and `$b/bin/flux-cli doctor`.

   | # | Check | How |
   | --- | --- | --- |
   | 1 | Enroll, key gone | Remote access on, enter a key: the node is `Ready`. `adb -s <serial> shell run-as org.omarchy.flux cat shared_prefs/settings.xml` shows `tailnetNode` `Enrolled`, `tailnetDir`, and no `tailnetAuthKey`. Turn Flux off and on: the node comes back with the same address, without a key. (Upgrade from #48: same address after the install, when the signatures match.) |
   | 2 | Change key | A wrong key: toast "The new key didn't work: ...", the old address comes back. A right key: a new address; the old node is offline in the admin console. Add the new address on p620 (`$b/bin/flux-cli --device <phone> addresses add <ip>`), remove the old one. |
   | 3 | Needs a new key | Remove the node in the admin console while it runs: the Sync screen shows "Needs a new auth key." Turn Flux off and on: the same, within 30 seconds. Enter a new key: a new node comes up. Note the time an invalid key takes on a fresh node. |
   | 4 | Order | Toggle Remote access 6 times fast: it ends as the switch shows, and `adb -s <serial> shell ps -A \| grep tsnet` shows 1 helper or none. Turn Flux off during a Change key trial: no helper runs. |
   | 5 | Network change | With p620 reaching the phone over the tailnet only (phone on mobile data): Wi-Fi to mobile and back, airplane mode on and off, both again with another VPN on. Note the time until `$b/bin/flux-cli doctor` shows the phone, and whether an open `$b/bin/flux-cli` terminal survives. If it fails with another VPN on, stop and report (spec risk). |
   | 6 | Address | Use p620 over the tailnet: its row says "Connected · via tailnet" and shows no `127.0.0.1`. Join the LAN, disconnect p620, Wake it from the phone: it wakes. |
   | 7 | Forget node | With Remote access off: Forget node, confirm. `adb -s <serial> shell run-as org.omarchy.flux ls files` shows no `tsnet` dirs; the settings have no `tailnetAuthKey`, `tailnetNode` is `None` and `tailnetDir` is `tsnet`. |
   | 8 | Logcat | `adb -s <serial> logcat -s FluxTsnet` over 10 minutes of use: no backend lines and no `log.tailscale.com`. |

   Afterwards, with the owner's OK: remove the test nodes in the admin
   console and the test addresses on p620. Record the results in the PR.

8. **No upload, measured (the lead, with the owner's key)**, on p620.
   The owner supplies, at test time, either one reusable ephemeral key or
   two single-use ephemeral keys (each run enrolls a fresh node). A key
   goes only into the environment of its run, never on a command line, in
   a file in the repository, or in a log. Both runs must reach `ready` on
   stdout, or the result doesn't count.
   - Announce on the agent bus: a test node joins the tailnet from p620
     for about 25 minutes.
   - Build both helpers for Linux:
     ```sh
     o=/tmp/claude-1000/-mnt-code-Source-home-GitHub/9f0892e4-8d93-4a6d-a1da-d5bcdbb3eba1/scratchpad/i50
     mkdir -p "$o"
     (cd android/tsnet && go build -o "$o/tsnet-branch" .)
     git worktree add "$o/master" master && (cd "$o/master/android/tsnet" && go build -o "$o/tsnet-master" .)
     ```
   - Run each for 10 minutes, with `TS_AUTHKEY` read from the owner
     (`read -rs TS_AUTHKEY; export TS_AUTHKEY`; read it again before the
     second run when there are two keys), a fresh state dir and ports
     that nothing uses:
     ```sh
     timeout 600 strace -f -e trace=connect -o "$o/branch.trace" \
       "$o/tsnet-branch" -state-dir "$(mktemp -d)" -hostname flux-strace-50 -watch-stdin=false -ports 19070
     timeout 600 strace -f -e trace=connect -o "$o/master.trace" \
       "$o/tsnet-master" -state-dir "$(mktemp -d)" -hostname flux-strace-50m -watch-stdin=false -ports 19071
     unset TS_AUTHKEY
     ```
   - `getent ahosts log.tailscale.com | awk '{print $1}' | sort -u` gives
     the addresses; `grep -F` each in both traces. The master trace must
     have at least one, the branch trace none. Also check the branch
     state dir has no `tailscaled.log*.txt` content.
   - Afterwards: `git worktree remove "$o/master"`, remove both test nodes
     in the admin console (ephemeral nodes go by themselves), delete the
     state dirs. Record the result in the PR.

9. **PR (the lead).** Open the PR linking the intent, spec and plan, with
   the CI, device and strace results, and which steps each implementer
   did.

## Tests

- `go test ./...` in `android/tsnet` (locally and in CI):
  `TestReadStdin`, `TestStatusLines`, `TestNoLogs` (with its positive
  control), and the existing `forward_test.go` tests.
- CI `build.yml` android job: `TsnetNodeTest` (19 tests: call order,
  stale lines, stop during `Starting`, stop after a failure, a new helper
  after the old one's exit, enrollment and a newer key, `login`,
  `approve`, trial rollback and promotion, stop, forget and a second key
  during a trial, the #48 node, network rechecks and a restart after a
  failure, startup cleanup, a failed settings save, a trial that can't
  launch), `DeviceTest`, `WakeTest`.
- Not unit-tested: the login watch's order against `Up` (it needs a fake
  IPN bus); steps 7.1 and 7.3 cover it on the phone.
- The device tests of step 7 and the strace check of step 8.
- `nix flake check path:.`.

## Rollback

- Revert the PR's squash commit, then build and install the reverted APK.
  The reverted app keeps the key again, uploads logs to Tailscale, and
  writes the backend's verbose log to logcat, as before #50.
- After a revert, the app reads `tailnetAuthKey` again; an enrolled
  phone has none, so the old code doesn't start the node, and its node is
  in `tsnet` or `tsnet-2`. To use the node with the old app: Forget node
  before the revert, then enter a new key after it. The `tailnetNode` and
  `tailnetDir` entries stay unused in the settings.
- The CI step and the docs revert with the commit.
- A revert can't bring back a key the app cleared, or a node that Forget
  node or a promotion deleted.

## Decisions for the owner

1. **A failed first enrollment clears the key.** The spec clears a
   trial's key when it exits before `ready`. On a phone with no node, a
   first try without network (the 2-minute `Up` timeout) also clears it,
   and the user enters the key again. Proposed: keep, as approved; the
   Sync screen shows the reason. The alternative is to keep the key after
   a timeout, which the helper can't tell apart from other errors without
   a new status line.
2. **The #48 upgrade check (spec 3.1)** needs a branch APK signed like
   the installed #48 build. CI debug APKs may have a different key; then
   the uninstall removes the #48 node. Proposed: run it if the signatures
   match, else record "not tested" in the PR and rely on
   `aNodeFrom48KeepsItsDir`. The alternative is to build both APKs with
   one debug key before the test.
3. **The strace check** needs a reusable ephemeral key, or two
   single-use ones (step 8).

## Review changes

Codex and agy reviewed the first draft, read only. agy stopped at its
15-minute limit: `print timeout after 15m0s with the turn still in
progress; the response is partial` (`DEADLINE_EXCEEDED`); its partial
output is used. Each finding was checked against the code, the tailscale
source, and by running the changed code in the scratch copies.

| Finding | Source | Verdict | Change |
| --- | --- | --- | --- |
| A failed `commit()` still deletes the old node on promotion | Codex (high) | Confirmed: `commit()`'s result was dropped | P8: `save` returns it; delete only on success; test `aFailedSaveKeepsTheOldNode` |
| A trial that can't launch never restores the old node | Codex | Confirmed: `start`'s catch went to `fail` | P9: `trialFailed`; test `aTrialThatCannotLaunchKeepsTheOldNode` |
| Login watch reads `isUp()`, so a late `NeedsLogin` after `ready` prints `login`; `ready` after a cancel | Codex | Confirmed: `Up` uses its own bus watcher (`tsnet.go:579`) | P10: the watcher's own `Running`; no `ready` after the context ended. No unit test (needs a fake bus) |
| `out.join(1500)` can drop the last line | Codex | Confirmed, low: carried over from `TsnetNode.kt:116` | P11: `out.join()` |
| Re-entering the #48 key starts in place | Codex | Confirmed | P3: `setKey` clears `legacyKey`; test extended |
| A partial delete of the trial dir keeps an identity | Codex | Confirmed: `deleteRecursively`'s result was dropped | A failed delete rolls back (P9); `mkdirs` left as is: the launch then fails, and that rolls back too |
| No-logs fixture can't show a suppressed upload of old entries | Codex | Confirmed: control gave `logged=0` | P6: a memory buffer with 1 old entry; control must upload (`logged=1`) |
| A single-use key can't enroll two nodes | Codex | Confirmed | Step 8: one reusable or two single-use keys; both runs must reach `ready` |
| The #48 upgrade check may be skipped, against spec 3.1 | Codex | Confirmed | Owner decision 2 |
| An offline first enrollment fails for good and clears the key | Codex, agy | Confirmed: the spec's trial rule | Owner decision 1; trap in step 2 |
| A #48 node with its key never prints `login` | agy | Confirmed: `hasKey` before `Running` | P3 states it; the spec's risk text was wrong |
| Forget check expected no `tailnet*` keys | Codex | Confirmed: `forget` saves `None` and `tsnet` | Check 7 fixed |
| Rollback omits the log upload and key coming back | Codex | Confirmed | Rollback |
| `toggle()` snippet drops `:267`; grep path relative | agy | Confirmed | Wording and full path |
| `error` sets `Failed` only at the exit | agy | Agreed as intended: a trial rolls back without a flash of `Failed` | None |
| Anchors, Go and Kotlin compile | both | Agreed; Go and `TsnetNode` run in scratch, the rest compiles first in CI | None |

## Corrections

| # | Step | What changed and why |
| --- | --- | --- |
