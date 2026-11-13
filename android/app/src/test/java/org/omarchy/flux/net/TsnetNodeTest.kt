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
