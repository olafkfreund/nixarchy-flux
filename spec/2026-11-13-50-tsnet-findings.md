---
status: draft
issue: 50
intent: intent/2026-11-13-50-tsnet-findings.md
---

# Spec: Fix the remaining findings on the Android tailnet node

The owner approved the intent with its proposed answers:
- logs off, with no setting;
- an "enrolled" flag, and the key cleared after the first `ready`;
- Change key enrolls in a fresh state dir and keeps the old one until the
  new node is up;
- fail fast on reported errors, and on login without a key;
- no stored loopback address, and the handshake cap kept;
- `InjectEvent` on a network change, with no restart;
- one branch.

Paths are under `android/app/src/main/java/org/omarchy/flux/` unless they
start with `android/`, `docs/`, `.github/` or `tailscale.com/`
(v1.104.4, the pinned module).

## Design

### 1. The helper (`android/tsnet/`)

**No logs** (`internal/quiet/quiet.go`, new; called first in `main`):

```go
// Package quiet turns off Tailscale's log upload and its local log buffer.
// Checked against tailscale.com v1.104.4; testdata/nologs proves it.
package quiet

func Apply() {
	envknob.SetNoLogsNoSupport() // upload transport is a no-op (logpolicy.go:894-897)
	logtail.Disable()            // nothing is buffered or uploaded (logtail.go:532,768)
}
```

`main` also deletes the log buffers that #48 builds left in the state
dir, before tsnet starts: `filepath.Glob(filepath.Join(dir, "tailscaled.log*.txt"))`
(the names come from `filch.New`, `logtail/filch/filch.go:437-438`).
tsnet recreates them empty (`tsnet.go:1120`); with `logtail.Disable` they
stay empty.

**Logs on stderr.** A new flag `-verbose` (default false). Without it,
`Logf` stays nil, so the backend log is discarded (`tsnet.go:255-258`).
`UserLogf` stays `log.Printf`, as approved (intent Q1); see decision 1.
The app never passes `-verbose`; it is for running the helper by hand.

**No key needed.** `main.go:42-45` goes. `AuthKey` is
`os.Getenv("TS_AUTHKEY")`, possibly empty.

**Stdout lines.** One writer function with a mutex, so lines never
interleave:

| Line | When | App |
| --- | --- | --- |
| `ready <ip>` | after `Up` **and** every `Listen` succeeded (moved after `main.go:81-87`) | `Ready` |
| `error <reason>` | fatal, as today (`main.go:93-97`) | `Failed(reason)` |
| `login` | the node needs login and can't use a key (below); the helper then stops | `Failed("Needs a new auth key.")` |
| `approve` | `NeedsMachineAuth` before `ready`; at most once | `Starting("Approve this phone in the Tailscale admin console.")` |

After `ready`, the helper prints no `approve`.

**Login watch.** Before `srv.Up`, the helper starts the server and
watches the IPN bus for the life of the process (`ctx`, not `upCtx`):

```go
lc, err := srv.LocalClient() // starts the server, as Up does (tsnet.go:574)
if err != nil { fatalf("tailnet start: %v", err) }
go watchLogin(ctx, cancel, lc, authKey != "")

func watchLogin(ctx context.Context, cancel func(), lc *local.Client, hasKey bool) {
	w, err := lc.WatchIPNBus(ctx, ipn.NotifyInitialState)
	if err != nil { line("error login watch: " + err.Error()); cancel(); return }
	defer w.Close()
	for {
		n, err := w.Next()
		if err != nil { return } // ctx ended
		if n.State == nil { continue }
		switch *n.State {
		case ipn.NeedsLogin:
			// With a key, NeedsLogin is brief during enrollment (tsnet.go:628-633).
			// Without one, or once up, it means the node key expired or was removed.
			if !hasKey || isReady.Load() { line("login"); cancel(); return }
		case ipn.NeedsMachineAuth:
			if !isReady.Load() { lineOnce("approve") }
		}
	}
}
```

An enrolled node starts in `Starting`, not `NeedsLogin`
(`tsnet.go:1007-1016`), so a node that control later rejects reaches
`NeedsLogin` through the bus. This doesn't depend on a login URL.

**Clean stop.** When `ctx` ends during `Up` (stdin EOF, SIGTERM, `login`),
`main` returns instead of calling `fatalf`, so the deferred `srv.Close`
runs. Only a real `Up` error prints `error`. `Up` keeps its 2-minute
context (`main.go:65`); `Up` already fails at once on a backend error
(`tsnet.go:590-592`).

**Network changes on stdin.** `exitOnStdinEOF` (`forward.go:107-117`)
becomes:

```go
// readStdin cancels on EOF, and rechecks the network on each "net" line.
func readStdin(cancel func(), recheck func()) {
	s := bufio.NewScanner(os.Stdin)
	for s.Scan() {
		if s.Text() == "net" { recheck() }
	}
	cancel()
}
```

`recheck` does nothing until `isReady` is set (after `ready`), then:

```go
sys := srv.Sys()
if nm, ok := sys.NetMon.GetOK(); ok { nm.InjectEvent() }
if ms, ok := sys.MagicSock.GetOK(); ok { ms.Rebind(); ms.ReSTUN("flux-net-change") }
```

`InjectEvent` alone doesn't rebind: the engine rebinds only on a major
change (`wgengine/userspace.go:1218-1222`), and the synthetic interface
can look the same after airplane mode or a same-address network. So the
helper rebinds and re-STUNs itself. Both subsystems are registered by the
engine (`tsnet.go:902`, `userspace.go:598,602`; `tsd.go:56-57`). `Sys()` is
not a stable API (`tsnet.go:2331-2336`).

### 2. The node's lifecycle (`net/TsnetNode.kt`)

`TsnetNode` owns the node: the process, its state, and the tailnet
settings. Everything runs on one worker thread, in the order it was
asked for. Callers return at once.

```kotlin
private val worker = Executors.newSingleThreadScheduledExecutor { Thread(it, "flux-tsnet").apply { isDaemon = true } }
```

**Seams**, set once by `FluxCore` at startup and replaced in tests:

```kotlin
internal fun configure(binary: File, filesDir: File, pid: Int, store: Store, log: (String) -> Unit)
internal var launch: (List<String>, Map<String, String>) -> Process = { cmd, env ->
    ProcessBuilder(cmd).apply { environment().putAll(env) }.start()
}
internal var debounceMs = 2_000L

/** The tailnet settings. Written only on the worker, with commit(). */
interface Store {
    val key: String
    val node: NodeState   // None, Enrolled, NeedsLogin
    val dir: String       // "tsnet" or "tsnet-2"
    fun save(key: String, node: NodeState, dir: String)
}
```

No `Context`, no `android.os.Process`, no `android.util.Log` inside, so
`TsnetNodeTest` runs on the JVM.

**Worker-only state:**

```kotlin
private var wanted = false          // Flux on and Remote access on
private var run: Run? = null
private var gen = 0L                // bumped on every stop
private class Run(val p: Process, val gen: Long, val dir: File, val key: String, val from: File?) {
    var result: String? = null      // reason from "error" or "login"
}
```

**Lines from a helper** are read on a reader thread and handed to the
worker: `worker.execute { if (g == gen) onLine(run, line) }`. The exit
waiter does the same with `onExit`. So only the worker changes `_state`
and the settings, and nothing from a stopped helper gets through. This
fixes the `error` and `ready` races (`TsnetNode.kt:86-104`) and the exit
overwriting a `login` reason (`TsnetNode.kt:120`): `onExit` reports
`run.result` when there is one.

**Stopping**, on the worker:

```kotlin
private fun stopNow() {
    gen++
    pending?.cancel(false)
    val r = run
    run = null
    _state.value = State.Off
    if (r == null) return
    runCatching { r.p.outputStream.close() }   // EOF: the helper cancels and closes
    if (!r.p.waitFor(8, TimeUnit.SECONDS)) { r.p.destroyForcibly(); r.p.waitFor() }
}
```

`Off` is set even with no process, so an old `Failed` clears. 8 seconds
is a grace period chosen above tsnet's 5-second shutdown contexts
(`tsnet.go:665`, `userspace.go:1122`); it is not a proven bound. The next
helper starts only after this returns, so two helpers never share a dir.

**Deciding what runs.** Every public call ends in `reconcile()`:

```kotlin
private fun reconcile() {
    stopNow()
    if (!wanted) return
    val key = store.key
    val cur = File(filesDir, store.dir)
    when {
        key.isNotBlank() && legacy(cur) -> launch(cur, key, from = null)
        key.isNotBlank() -> trial(key, from = cur)
        store.node == NodeState.Enrolled -> launch(cur, "", from = null)
        store.node == NodeState.NeedsLogin -> _state.value = State.Failed("Needs a new auth key.")
        else -> Unit // Off; the Sync screen asks for a key
    }
}

/** A #48 node: enrolled with this key before the flag existed. It keeps its identity. */
private fun legacy(cur: File) = store.node == NodeState.None && File(cur, "tailscaled.state").exists()
```

- `launch` reports `Failed("This build has no tailnet helper.")` when the
  binary is missing, else `Starting`, then starts the process with
  `-state-dir`, `-hostname`, `-parent-pid`, and `TS_AUTHKEY` only when the
  key isn't blank.
- `trial(key, from)`: `to` is the other dir name; delete `to`; launch on
  `to` with the key.

**Public calls**, each `worker.execute { ...; reconcile() }`:

| Call | From | Before `reconcile` |
| --- | --- | --- |
| `apply(on: Boolean)` | `FluxCore.applyTsnet`, `stopTsnet` | `wanted = on` |
| `setKey(key)` | the key dialog | `store.save(key.trim(), store.node, store.dir)` |
| `forget()` | Forget node | `stopNow()`; delete `tsnet` and `tsnet-2`; `store.save("", None, "tsnet")`; `wanted = false` |
| `networkChanged()` | `FluxService` | see section 6 (no `reconcile` when `Ready`) |

**Events from the current run** (`onLine`, `onExit`):

- `ready`: report `Ready`. If the run had a key:
  `store.save(if (store.key == run.key) "" else store.key, Enrolled, run.dir.name)`;
  then, if `run.from != null`, delete `run.from`. `save` commits before the
  delete, so a crash never leaves the settings pointing at a deleted dir.
  A newer key saved during the run stays, and the next `reconcile` tries
  it.
- `login`: `run.result = "Needs a new auth key."`; report it;
  `store.save(store.key, NeedsLogin, store.dir)` for a keyless run.
- `approve`: report `Starting(note)`.
- exit before `ready`:
  - a trial (`run.from != null`): delete `run.dir`;
    `store.save(if (store.key == run.key) "" else store.key, store.node, store.dir)`;
    toast "The new key didn't work: <reason>." Then, if the old node is
    enrolled, launch `from` without a key ("Flux kept the old node.").
    Otherwise report `Failed(reason)`.
  - otherwise: report `Failed(run.result ?: "The tailnet helper stopped (<code>).")`.
- exit after `ready`: report `Failed(...)` as today.

A stop, forget or new key bumps `gen` first, so a trial that the user
interrupts doesn't roll back and doesn't relaunch anything. Its partial
`to` dir is deleted by the next trial, or by the startup cleanup below.

**Startup cleanup.** `configure` queues one task: delete whichever of
`tsnet` and `tsnet-2` isn't `store.dir`, unless a key is saved and a
trial may want it (it deletes `to` itself then). This finishes a
promotion that a crash interrupted.

**States.** `Starting` becomes `data class Starting(val note: String? = null)`.

### 3. Settings (`core/Settings.kt`)

```kotlin
enum class NodeState { None, Enrolled, NeedsLogin }

/** The tailnet node's settings, as [TsnetNode.Store]. Saved together and at once. */
fun saveTailnet(key: String, node: NodeState, dir: String) {
    prefs.edit().apply {
        if (key.isEmpty()) remove("tailnetAuthKey") else putString("tailnetAuthKey", key)
        putString("tailnetNode", node.name)
        putString("tailnetDir", dir)
    }.commit()
}
```

`tailnetNode` defaults to `None`, `tailnetDir` to `"tsnet"` (today's dir,
`TsnetNode.kt:49`). The `tailnetAuthKey` setter (`Settings.kt:32-34`)
goes; only `saveTailnet` writes it.

### 4. `FluxCore` and the UI

- `FluxCore` init calls `TsnetNode.configure(File(app.applicationInfo.nativeLibraryDir, "libflux_tsnet.so"), app.filesDir, Process.myPid(), settingsStore, { Log.i("FluxTsnet", it) })`.
- `applyTsnet()` (`FluxCore.kt:612-621`) becomes
  `TsnetNode.apply(settings.enabled && settings.remoteAccess)`; the
  hostname moves to `configure`. `stopTsnet()` (`:635-637`) is
  `TsnetNode.apply(false)`. Neither uses `io`. Turning Flux off therefore
  stops a trial too, and Save in the dialog while Flux is off only saves
  the key.
- `setTailnetAuthKey(key)` (`:629-633`) is `TsnetNode.setKey(key)`.
- `forgetTailnet()` is `settings.remoteAccess = false; TsnetNode.forget()`.
- `FluxCore` collects `TsnetNode.state` and calls `publish()` on each
  change, so `UiState` and the computer rows follow the node.
- `UiState` (`core/Model.kt:221-224`) gets `tailnetNode: NodeState`,
  set at `FluxCore.kt:476`.

**Sync screen** (`ui/SyncScreen.kt:250-325`):
- `toggle()` asks for a key when `!tailnetKeySet && tailnetNode != Enrolled`.
- The detail line shows `Starting.note` when set. Its fallback (`:274`)
  says "Reach this phone away from home without a VPN app." when a key is
  saved or the node is enrolled.
- When `tailnetNode == Enrolled`, the key dialog shows above the field:
  "A new key makes a new node with a new address. Add the new address on
  each computer. Remove the old node in the admin console."
- **Forget node**: a `TextButton` shown when `tailnetKeySet || tailnetNode != None`,
  with Remote access on or off. It opens a confirm dialog: "Remove the
  tailnet node from this phone? Remove it in the Tailscale admin console
  too." Forget and Cancel.

### 5. The address of a link (`core/Device.kt`, `core/FluxCore.kt`, `core/Wake.kt`, `ui/ComputersScreen.kt`)

Two pure helpers in `core/Device.kt`:

```kotlin
/** The address to keep for a computer after a link from [link]: a loopback link is the tailnet node's forward, so the stored one stays. */
internal fun nextLastIp(stored: String, link: InetAddress?): String =
    link?.takeUnless { it.isLoopbackAddress }?.hostAddress ?: stored

/** True for a stored loopback address, without a name lookup. */
internal fun isLoopbackText(ip: String) = ip.startsWith("127.") || ip == "::1"
```

- `attach` (`FluxCore.kt:336`): `d.lastIp = nextLastIp(d.lastIp, link.address)`.
  The trust update at `:339` then writes the kept address.
- Pairing (`Device.kt:328`): `lastIp = nextLastIp(lastIp, link?.address)`.
- Startup (`FluxCore.kt:135`): a loopback `t.lastIp` loads as `""`, and
  that trust entry is updated once. No `InetAddress` call on the main
  thread.
- `Wake.destinations` (`Wake.kt:69-71`) drops `isLoopbackText` entries,
  so a send to `127.0.0.1` no longer counts as sent (`Wake.kt:86-102`).
- `snapshot` (`Device.kt:157`): `ip` is the link's address when it isn't
  loopback, `""` for a loopback link, and `lastIp` with no link. A new
  `viaTailnet` is true for a loopback link while `TsnetNode.state` is
  `Ready`.
- `linkLine` (`ComputersScreen.kt:179-190`): "Connected · via tailnet"
  when `viaTailnet`. A loopback link without a ready node shows
  "Connected".

`LanBackend`'s cap of 4 handshakes per source (`net/LanBackend.kt:48-49`)
doesn't change.

### 6. Network changes (`service/FluxService.kt`, `TsnetNode.networkChanged`)

- `networkCallback.onAvailable` and `onLost` (`FluxService.kt:57-67`) and
  `screenReceiver` (`:89-91`, screen on after Doze) also call
  `TsnetNode.networkChanged()`.
- `networkChanged` posts to the worker, which cancels the pending recheck
  and schedules one `debounceMs` later. The recheck:
  - `Ready`: writes `net\n` to the helper's stdin and **flushes** (the
    process stdin is buffered);
  - `Failed` from an `error` (not `login`), with `wanted`: `reconcile()`,
    once per recheck, so a node that failed while offline starts again
    when the network is back;
  - otherwise nothing. The callback at registration (`FluxService.kt:232`)
    lands while the node is `Off` or `Starting`, so it does nothing.

### 7. Docs

- `docs/security.md`: a new section "Remote access" before "Album art"
  (`:184`):
  - the node talks to Tailscale's control servers and DERP relays, and to
    peers in the tailnet;
  - it uploads no logs to Tailscale and keeps no log buffer (checked
    against tailscale v1.104.4), so Tailscale support has no logs of it;
  - the node state is in the app's files, and app backup is off
    (`AndroidManifest.xml:64`); the auth key is kept only until the node
    is up.

  `:186` becomes "Besides the paired computers and, with Remote access,
  Tailscale, the apps connect to 1 other kind of host."
- `docs/tailscale.md:22-52`: the phone forgets the key once the node is
  up; Change key makes a new node with a new address; Forget node; no
  logs to Tailscale.
- `docs/releasing.md`, "Pinned tools" (`:337`): a tailscale bump in
  `android/tsnet/go.mod` must keep `go test ./...` in `android/tsnet`
  green, and needs a read of the diff of `logpolicy`, `logtail`,
  `tsnet.startLogger` and `tsd`.

### 8. CI (`.github/workflows/build.yml`)

The helper's Go tests run nowhere today (`nix/package.nix:66` tests only
the desktop module). The Android job gets a step before Gradle, with the
Go it already sets up (`build.yml:114-117`):

```yaml
- name: Test the tailnet helper
  working-directory: android/tsnet
  run: go test ./...
```

## Alternatives rejected

- **`TS_NO_LOGS_NO_SUPPORT` in the app's env only.** Easy to lose, and it
  leaves the local buffer.
- **Detect enrollment from files in the state dir.** tsnet writes files
  before enrollment (`tsnet.go:1104-1122`). Only the #48 migration uses a
  file, `tailscaled.state`, together with a saved key.
- **Reuse the state dir for a new key after `NeedsLogin`.** tsnet can
  ignore the key on existing state (`tsnet.go:264-269,1014-1016`); a new
  key always goes to a fresh dir.
- **`TSNET_FORCE_LOGIN=1`.** Rejected in the intent.
- **Old and new node at once during Change key.** Works (each helper only
  dials loopback), but needs two runs for a rare action.
- **Restart the helper on a network change.** Ends every open link
  (`forward.go:50-61`).
- **`InjectEvent` alone.** Doesn't rebind on a minor change
  (`userspace.go:1218-1222`).
- **The local API's `debug?action=rebind`.** Same effect as `Rebind`
  through HTTP; no more stable.
- **Fail on `NeedsLogin` with a key before `ready`.** It shows briefly in
  a valid enrollment (`tsnet.go:628-633`).
- **Pass the real tailnet peer to the app.** Rejected in the intent.
- **Keep the `io` pool and add locks.** A single worker gives the order
  and one owner for the settings.

## Risks

- **`Sys()` is not a stable API.** A tailscale bump can break `NetMon` or
  `MagicSock`. Compile errors show a removal; behaviour only the phone
  test shows. Read with each bump (releasing.md).
- **Rebind with another VPN on** may still not help if the synthetic
  interface sees only the VPN (`androidbin.go`). Verification 3.5
  measures it; if it fails, stop and report.
- **The login watch** relies on the IPN bus `NeedsLogin` state.
  Verification 3.3 checks a node removed in the admin console. If tsnet
  stays in `Starting`, the helper fails after 2 minutes as today.
- **Change key** takes the node offline for up to 2 minutes; a failed key
  brings back the old node.
- **The key is cleared on the first `ready`.** A node that later needs
  login needs a new key, and gets a new address.
- **The #48 migration** trusts `tailscaled.state` plus a saved key. If
  that node can't log in, the first start fails with `login` and the next
  key goes to a fresh dir.
- **A helper that ignores stdin EOF** is killed after 8 seconds. Later
  actions wait on the worker; nothing else blocks.
- **Forget node deletes the node state.** It asks first.
- **Shared handshake cap.** Four stalled loopback connections from a
  tailnet peer or a local app can block tailnet links for 10 seconds at a
  time (`LanBackend.kt:43,48-49`). Accepted in the intent; tailnet access
  controls (`docs/tailscale.md:50-51`) limit the peers.
- **Phones storing `127.0.0.1`** get it cleared at the first start; until
  the computer connects from the LAN, Wake has only the broadcasts.
- **`UserLogf`** stays on (approved answer 1), so a login URL can reach
  logcat once before the helper prints `login`. See decision 1.

## Verification

1. **Unit tests** in CI (`./gradlew :app:testDebugUnitTest :app:lintDebug :app:assembleDebug`):
   - `net/TsnetNodeTest.kt`, with `configure` (temp binary file, fake
     `Store`, a list as log) and a fake `Process`:
     - the fake has piped stdout and stderr whose write ends close on
       exit; `exitValue()` throws `IllegalThreadStateException` until
       exit; `waitFor(timeout)` waits on a latch; its stdin is a
       `BufferedOutputStream` over a pipe, so a missing `flush` fails;
     - each test waits for the worker with `worker.submit {}.get()`, and
       sets `debounceMs = 0`.

     Cases:
     - apply off then on, and on then off: the last call wins;
     - an old helper's `error`, `ready`, `login` and exit after a stop
       change nothing;
     - stop during `Starting` ends `Off`; stop with no process clears a
       `Failed`;
     - a second launch waits for the first helper's exit;
     - `ready` with a key saves `Enrolled`, the run's dir and an empty
       key; a newer key saved during the run is kept;
     - `login` reports "Needs a new auth key." and the exit doesn't
       overwrite it;
     - `approve` then `ready` ends `Ready`;
     - a trial that fails deletes `to`, clears its key, relaunches `from`
       without a key; one that succeeds deletes `from`;
     - stop, forget, or a second key during a trial: no relaunch of
       `from`, and the second key starts its own trial;
     - legacy: no flag, a saved key and `tailscaled.state` launch in place;
     - `networkChanged` twice: one `net` line, flushed; none while
       `Starting`; a `Failed` from `error` relaunches;
     - startup cleanup deletes the dir that isn't `store.dir`.
   - `core/DeviceTest.kt` (new): `nextLastIp` keeps the stored address for
     `127.0.0.1` and `::1` and takes a LAN address; `isLoopbackText`.
   - `core/WakeTest.kt`: `destinations` drops `127.0.0.1`.
2. **Helper tests** (`go test ./...` in `android/tsnet`, now in CI):
   - `nologs_test.go` runs `go run ./testdata/nologs` as a subprocess.
     The program calls `quiet.Apply()` (the same function `main` calls),
     then:
     - sends a POST with a body through
       `logpolicy.NewLogtailTransport(logtail.DefaultHost, nil, nil, t.Logf)`,
       the transport tsnet uses (`tsnet.go:1132`), to
       `https://log.tailscale.com.invalid/`: it must get 200 and no error
       (a real transport can't resolve `.invalid`);
     - creates a `logtail.Logger` with a counting `Buffer` and an
       `HTTPC` pointing at a local `httptest` server, logs 10 lines,
       shuts down: 0 buffer writes and 0 requests.

     A subprocess is needed because `testenv.InTest()` makes the
     transport a no-op in any test binary (`logpolicy.go:895`,
     `util/testenv/testenv.go:18-22`). As a positive control, the test
     runs the program once more with `-control`, which skips `Apply`:
     the transport must then fail on `.invalid`, and the logger must
     write to the buffer.
   - `stdin_test.go`: `readStdin` calls `recheck` once per `net` line and
     `cancel` at EOF.
   - `forward_test.go` stays green.
   - A keyless start can't be tested without a control server; the phone
     test covers it (3.1).
3. **On the phone** (Pixel), with the #13 setup (`fluxd` on p620,
   paired), the branch's debug APK, in a booked slot. Before each step,
   note the Sync screen and `flux-cli doctor` on p620.
   1. Upgrade over the #48 build with an enrolled node: same address
      after the upgrade; then `adb shell run-as org.omarchy.flux cat shared_prefs/settings.xml`
      shows `tailnetNode` `Enrolled` and no `tailnetAuthKey`. Restart the
      service: the node comes back, keyless, with the same address.
   2. Change key with a wrong key: toast, old address back. With a right
      key: new address; old node offline in the admin console. Add the new
      address on p620 (`flux-cli --device ... addresses add`), remove the
      old one.
   3. Remove the node in the admin console while it runs: the Sync screen
      shows "Needs a new auth key." Restart the service: the same, within
      30 seconds. Enter a new key: a new node comes up. Note the time for
      an invalid key on a fresh node.
   4. Toggle Remote access 6 times fast: it ends as the switch shows.
      Turn Flux off during a Change key trial: no helper runs
      (`adb shell ps -A | grep tsnet`).
   5. With p620 reaching the phone over the tailnet only (phone on mobile
      data): Wi-Fi to mobile and back, airplane mode on and off, both again
      with another VPN on. Note the time until `flux-cli doctor` shows the
      phone, and whether an open `flux-cli` terminal survives.
   6. Use p620 over the tailnet: its row says "Connected · via tailnet"
      and shows no `127.0.0.1`. Join the LAN, disconnect, Wake p620: it
      wakes.
   7. Forget node with Remote access off: `run-as ... ls files` shows no
      `tsnet` dirs; the settings have no tailnet keys.
   8. Logcat for `FluxTsnet` over 10 minutes of use has no backend lines
      and no `log.tailscale.com`.
4. **No upload, measured** (p620, with the owner's OK to run a test node
   there): build the helper for `linux/amd64`, run it with an ephemeral
   test key and `strace -f -e trace=connect` for 10 minutes; resolve
   `log.tailscale.com` and check no `connect` goes to its addresses. Run
   the `master` build the same way as the positive control: it must
   connect there. An unrooted phone can't attribute connections to the
   helper, so this runs on Linux, with the same `quiet` code.
5. `nix flake check path:.`.

The Pixel is in use by another task; steps 3.x wait for a slot. Don't
touch `fluxd`, adb devices or `/etc/flux` before then.

## Decisions for the owner

1. **`UserLogf` in release builds.** Approved answer 1 keeps it. It can
   print a login URL to logcat once before `login`. Proposed: keep, as
   approved. The alternative is to discard it too; then the helper's own
   lines are the only stderr.
2. **The #48 migration** (`legacy` above) keeps the node and address of
   phones that enrolled under #48. Proposed: yes. Without it, the update
   makes each such phone a new node with a new address.
3. **The p620 strace check** (Verification 4) needs an ephemeral key and
   a test node in the tailnet for 10 minutes. Proposed: yes.

## Review changes

Codex and agy reviewed the first draft, read only. Each finding was
checked against the code and the tailscale source.

| Finding | Source | Verdict | Change |
| --- | --- | --- | --- |
| Queued Change key requests capture stale dirs and delete the new node | Codex (high) | Confirmed | Dirs chosen on the worker in `trial`; `reconcile` model |
| Promotion not crash-safe (`apply()` then delete) | Codex (high), agy | Confirmed, `Settings.kt:26-34` | `saveTailnet` commits before the delete; startup cleanup |
| `ready` before the listeners; a later `error` bypasses rollback | Codex (high) | Confirmed, `main.go:74-85` | `ready` after every `Listen` |
| Callbacks and settings writes not guarded by `gen`; forget vs `ready` | Codex (high), agy (high) | Confirmed | All lines and exits handled on the worker with `gen`; settings written only there |
| Stop, forget or a second key during a trial triggers rollback or wipes the new key | agy (high) | Confirmed | `gen` bump drops the trial's exit; the key is compared before clearing |
| stdin is buffered; `net` never arrives without `flush` | agy (high) | Confirmed: `Process.getOutputStream` is buffered | Flush; the fake stdin is buffered too |
| Login watch on `BrowseToURL` and `upCtx` misses rejected and expiring nodes | Codex, agy (high) | Confirmed, `tsnet.go:1007-1016`, `main.go:65-66` | Watch `NeedsLogin` on `ctx`; after `ready` too |
| `login` exit overwritten by "helper stopped (1)" | agy (high) | Confirmed, `TsnetNode.kt:120` | `run.result` |
| A new key on the same dir after `NeedsLogin` can be ignored | agy (high) | Confirmed, `tsnet.go:1014-1016` | Every new key goes to a fresh dir |
| `start` without `Context`; `myPid`, `Log` break JVM tests; missing binary | Codex, agy | Confirmed, `TsnetNode.kt:44-59,108` | `configure` seam; missing binary reported before `Starting` |
| Change key bypasses Flux off | Codex, agy | Confirmed, `FluxCore.kt:612-632` | `wanted` checked in `reconcile` |
| `approve` after `ready` regresses the state | Codex | Confirmed | Mutex writer; no `approve` after `ready` |
| Recovery after starting offline | Codex | Confirmed, `androidbin.go:77-78` | A `Failed` from `error` relaunches on a network change |
| `InjectEvent` doesn't rebind on a minor change; Doze | agy, Codex | Confirmed, `userspace.go:1218-1222` | `Rebind` + `ReSTUN`; screen-on also rechecks |
| No-logs test duplicates `quiet`, misses `Disable`, GET without body panics | Codex, agy | Confirmed, `logpolicy.go:962-964` | `internal/quiet`; POST with body; logger buffer check; positive control |
| Phone log check can't attribute connections; release vs debug APK | Codex, agy | Confirmed (unrooted phone) | Strace check on p620 with a positive control; logcat only on the phone |
| `tailnetAuthKey` stays as an empty entry | Codex, agy | Confirmed, `Settings.kt:34` | `remove` when empty |
| Pairing over loopback drops a known LAN address | Codex | Confirmed | `nextLastIp(stored, link)` |
| UI shows the LAN address on a tailnet link; `viaTailnet` stale | agy | Confirmed | `ip` empty for loopback; publish on node state |
| Integration tests missing (attach, startup, enrollment) | Codex | Partly: `FluxCore` has no JVM seam | Pure helpers tested; enrollment in `TsnetNodeTest`; phone 3.1, 3.6 |
| Stop during `Up` skips `srv.Close` | Codex, agy | Confirmed, `main.go:67-69,93-96` | Return instead of `fatalf` when `ctx` ended |
| `stopNow` with no run left `Failed` | Codex | Confirmed | `Off` always |
| 5 s doesn't bound all of `Close` | Codex, agy | Confirmed | Worded as a grace period |
| Old log files from #48 stay | agy | Confirmed | Deleted at helper start |
| Main-thread lookup for a stored address | agy | Agreed | `isLoopbackText`, no `InetAddress` |
| `change` didn't report `Starting` | agy | Confirmed | `launch` reports it |
| `UserLogf` silenced against approved answer 1 | Codex, agy | Confirmed | Kept; decision 1 |
| Alternatives misnamed the rejected loopback option | Codex | Confirmed | Fixed; shared-cap risk restored |
| Citations `forward.go:108-118`, `SyncScreen.kt:300-325`, `ComputersScreen.kt:179-189`, `tsnet.go:1008-1012` | agy | Confirmed off by one or misleading | Fixed |
| Forget hidden after `NeedsLogin` | agy | Confirmed | Shown when `tailnetNode != None` |
| `release.yml` needs the Go test step | agy (unverified) | Not needed: `release.yml:67` calls `build.yml`, so the step runs there too | None |
