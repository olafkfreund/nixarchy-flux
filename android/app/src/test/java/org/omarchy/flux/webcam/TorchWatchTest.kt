package org.omarchy.flux.webcam

import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class TorchWatchTest {
    @Test
    fun litFramesNeverReport() {
        val w = TorchWatch()
        for (t in 0L..5000L step 33) assertFalse(w.onResult(true, t))
        assertTrue(w.everLit)
    }

    @Test
    fun oneSecondDarkReportsOnce() {
        val w = TorchWatch()
        assertFalse(w.onResult(false, 0))
        assertFalse(w.onResult(false, 999))
        assertTrue(w.onResult(false, 1000))
        assertFalse(w.onResult(false, 2000))
        assertFalse(w.onResult(true, 2100))
        assertFalse(w.onResult(false, 4000))
    }

    @Test
    fun aLitFrameResetsTheDarkTime() {
        val w = TorchWatch()
        assertFalse(w.onResult(false, 0))
        assertFalse(w.onResult(true, 500))
        assertFalse(w.onResult(false, 1000))
        assertFalse(w.onResult(false, 1999))
        assertTrue(w.onResult(false, 2000))
        assertTrue(w.everLit)
    }

    @Test
    fun noStateNeverReports() {
        val w = TorchWatch()
        for (t in 0L..5000L step 33) assertFalse(w.onResult(null, t))
        assertFalse(w.everLit)
    }
}
