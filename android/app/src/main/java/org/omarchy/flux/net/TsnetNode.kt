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
