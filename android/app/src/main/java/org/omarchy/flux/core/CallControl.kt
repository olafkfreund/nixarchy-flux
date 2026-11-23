package org.omarchy.flux.core

import android.Manifest
import android.annotation.SuppressLint
import android.content.Context
import android.content.pm.PackageManager
import android.os.Build
import android.telecom.TelecomManager
import android.telephony.TelephonyManager
import android.util.Log
import androidx.core.content.ContextCompat

/** Answers or rejects only the ringing call identified by the computer. */
object CallControl {
    private const val TAG = "FluxCalls"

    fun allowed(snap: CallSnapshot, packetId: String?, action: String?, hasPermission: Boolean): Boolean =
        hasPermission && snap.state == LineState.Ringing && !snap.waiting &&
            packetId != null && packetId == snap.id && (action == "answer" || action == "reject")

    /** The phone's own line state right now; an extra condition on top of the tracker, never instead of it. */
    @SuppressLint("MissingPermission")
    private fun liveRinging(context: Context): Boolean {
        val tm = context.getSystemService(TelephonyManager::class.java) ?: return false
        val state = if (Build.VERSION.SDK_INT >= 31) tm.callStateForSubscription else @Suppress("DEPRECATION") tm.callState
        return state == TelephonyManager.CALL_STATE_RINGING
    }

    @SuppressLint("MissingPermission")
    fun act(context: Context, tracker: CallTracker, packetId: String?, action: String?) {
        tracker.withLock { snap ->
            val permitted = ContextCompat.checkSelfPermission(context, Manifest.permission.ANSWER_PHONE_CALLS) == PackageManager.PERMISSION_GRANTED
            if (!allowed(snap, packetId, action, permitted)) {
                Log.w(TAG, "refused call action")
                return@withLock
            }
            val telecom = context.getSystemService(TelecomManager::class.java)
            if (telecom == null) {
                Log.w(TAG, "cannot access Telecom")
                return@withLock
            }
            runCatching {
                // The tracker follows TelephonyCallback, which can lag an answer on the phone. Telecom's live
                // ringing state is checked last: endCall() with nothing ringing would hang up the answered call.
                if (!telecom.isInManagedCall || !liveRinging(context)) {
                    Log.w(TAG, "refused call action: no ringing call")
                } else if (action == "answer") telecom.acceptRingingCall()
                else if (!telecom.endCall()) Log.w(TAG, "reject call returned false")
            }.onFailure { Log.w(TAG, "call action failed", it) }
        }
    }
}
