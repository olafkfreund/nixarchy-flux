package org.omarchy.flux.webcam

/**
 * Decides from the frame results of 1 torch request whether the flash
 * lit. [onResult] returns true once, when the flash has been dark for
 * [darkMs]. A null state means that the camera does not report it.
 */
class TorchWatch(private val darkMs: Long = 1000) {
    /** True after any frame with the flash lit. */
    @Volatile var everLit = false
        private set
    private var darkSince = -1L
    private var reported = false

    fun onResult(lit: Boolean?, nowMs: Long): Boolean {
        if (lit == null || reported) return false
        if (lit) {
            everLit = true
            darkSince = -1L
            return false
        }
        if (darkSince < 0) darkSince = nowMs
        if (nowMs - darkSince < darkMs) return false
        reported = true
        return true
    }
}
