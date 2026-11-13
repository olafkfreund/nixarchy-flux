package org.omarchy.flux.core

import android.app.NotificationManager
import android.content.Context
import android.os.SystemClock
import android.util.Log
import java.util.concurrent.Executors
import org.omarchy.flux.protocol.Packet
import org.omarchy.flux.protocol.Types
import org.omarchy.flux.protocol.bodyOf

private const val TAG = "FluxDnd"

/**
 * Syncs Do Not Disturb between this phone and the computers with flux.dnd.
 * Each side sends the state only after a local change. [guard] keeps a
 * change from going back to the side that made it.
 */
object DndSync {
    private val guard = DndGuard()
    private val logic = DndLogic(guard)

    /** One worker: the decisions and the filter writes run in arrival order. */
    private val queue = Executors.newSingleThreadExecutor { Thread(it, "flux-dnd").apply { isDaemon = true } }

    private fun manager(context: Context) = context.getSystemService(NotificationManager::class.java)

    /** Reports whether Flux may read and set Do Not Disturb. */
    fun hasAccess(context: Context): Boolean = manager(context)?.isNotificationPolicyAccessGranted == true

    /** Reports whether Do Not Disturb is on. Every filter that is not "all" counts as on. */
    fun isOn(context: Context): Boolean {
        val f = manager(context)?.currentInterruptionFilter ?: return false
        return f != NotificationManager.INTERRUPTION_FILTER_ALL && f != NotificationManager.INTERRUPTION_FILTER_UNKNOWN
    }

    /** Sets the start value. The service calls it when it starts, so that the start is not a change. */
    fun start(context: Context) {
        guard.local(isOn(context), SystemClock.elapsedRealtime())
    }

    private fun env(core: FluxCore) = object : DndLogic.Env {
        override fun syncOn() = core.settings.syncDnd
        override fun access() = hasAccess(core.app)
        override fun isOn() = isOn(core.app)
        override fun now() = SystemClock.elapsedRealtime()
        override fun sleep(ms: Long) = Thread.sleep(ms)
        override fun async(block: () -> Unit) = queue.execute(block)
        override fun setFilter(on: Boolean): Boolean {
            val filter = if (on) NotificationManager.INTERRUPTION_FILTER_PRIORITY else NotificationManager.INTERRUPTION_FILTER_ALL
            return runCatching { manager(core.app)?.setInterruptionFilter(filter) != null }
                .onFailure { Log.w(TAG, "set Do Not Disturb failed", it) }
                .getOrDefault(false)
        }
        override fun forward(on: Boolean, except: String?) {
            for (d in core.connectedPaired()) {
                if (d.id == except || Types.FLUX_DND !in d.identity.incoming) continue
                d.send(Packet(Types.FLUX_DND, bodyOf("on" to on)))
            }
        }
        override fun report(by: String, on: Boolean?, to: String?) {
            for (d in core.connectedPaired()) {
                if ((to != null && d.id != to) || Types.FLUX_DND_STATE !in d.identity.incoming) continue
                d.send(Packet(Types.FLUX_DND_STATE, bodyOf("on" to on, "by" to by)))
            }
        }
    }

    /** Handles a change of the interruption filter on this phone. */
    fun onLocalChange(core: FluxCore) = logic.onLocalChange(env(core))

    /** Handles flux.dnd from a computer. The core lock is held, so the work moves to [FluxCore.io]. */
    fun onPacket(core: FluxCore, d: Device, p: Packet) {
        logic.onPacket(env(core), d.id, p.bool("on") ?: return)
    }

    /** Handles flux.dnd.set: the phone sets only itself. */
    fun onSet(core: FluxCore, p: Packet) {
        logic.onSet(env(core), p.bool("on") ?: return)
    }

    /** Tells a computer that just connected the state of this phone. */
    fun onConnected(core: FluxCore, d: Device) = logic.onConnected(env(core), d.id)

    /** Tells all computers the state after the sync switch or the access changed. */
    fun reportState(core: FluxCore) = logic.reportState(env(core))
}

/**
 * The decisions of [DndSync], with the phone behind [Env] so that tests can
 * fake it. Only the values "phone", "sync", "set" and "connect" are ever
 * sent as `by`. A null `on` means that the phone cannot read or set the state.
 */
class DndLogic(private val guard: DndGuard) {
    interface Env {
        fun syncOn(): Boolean
        fun access(): Boolean
        fun isOn(): Boolean
        fun now(): Long
        fun sleep(ms: Long)
        fun async(block: () -> Unit)
        /** Sets the filter; false when the phone refused it. */
        fun setFilter(on: Boolean): Boolean
        fun forward(on: Boolean, except: String?)
        fun report(by: String, on: Boolean?, to: String?)
    }

    private fun Env.usable() = syncOn() && access()

    private fun Env.current(): Boolean? = if (usable()) isOn() else null

    // Android can report the old filter for a moment after a write, and the broadcast that follows is the guard's own
    // change, so it is never reported: wait up to a second for the state to settle before reporting it.
    private fun Env.settle(on: Boolean) {
        repeat(10) {
            if (isOn() == on) return
            sleep(100)
        }
    }

    // Every decision runs on env.async, one at a time, so the guard and the
    // filter never disagree and a later request never runs before an earlier one.
    fun onLocalChange(env: Env) = env.async {
        val on = env.isOn()
        val change = guard.local(on, env.now())
        if (change && env.usable()) {
            env.forward(on, except = null)
            env.report("phone", on, null)
        }
    }

    fun onPacket(env: Env, from: String, on: Boolean) = env.async {
        if (env.usable() && guard.remote(on, env.now())) {
            if (env.setFilter(on)) {
                env.settle(on)
                env.forward(on, except = from)
            } else guard.reset(env.isOn())
            env.report("sync", env.current(), null)
        }
    }

    fun onSet(env: Env, on: Boolean) = env.async {
        // Marked remote: the change is not a local one, so it is not sent as flux.dnd.
        if (env.usable() && guard.remote(on, env.now())) {
            if (env.setFilter(on)) env.settle(on) else guard.reset(env.isOn())
        }
        // The state the phone has now, not the one asked for: a refused write shows.
        env.report("set", env.current(), null)
    }

    fun onConnected(env: Env, id: String) = env.async { env.report("connect", env.current(), id) }

    fun reportState(env: Env) = env.async { env.report("phone", env.current(), null) }
}
