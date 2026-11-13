package org.omarchy.flux.webcam

import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.jsonObject
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.After
import org.junit.Before
import org.junit.Test
import org.omarchy.flux.protocol.json

/** load() is never called, so nothing is saved and no Context is needed. */
class WebcamSettingsTest {
    private val flash = WebcamCaps(cameras = listOf("back", "front"), torch = true)
    private val front = WebcamCaps(cameras = listOf("back", "front"))

    private fun obj(s: String): JsonObject = json.parseToJsonElement(s).jsonObject
    private fun torch() = WebcamSettings.config.value.torch
    private fun lit() {
        WebcamSettings.setCaps(flash)
        WebcamSettings.update { it.copy(torch = true) }
    }

    // The object lives for the whole test run, so it is reset before and after each test.
    @Before
    @After
    fun clear() {
        WebcamSettings.setCaps(WebcamCaps.LOOSE)
        WebcamSettings.update { WebcamConfig() }
    }

    @Test
    fun turnsOnWithAFlash() {
        lit()
        assertTrue(torch())
    }

    @Test
    fun aCameraThatOpensTurnsItOff() {
        lit()
        WebcamSettings.setCaps(flash, camera = "back")
        assertFalse(torch())
    }

    @Test
    fun capsWithoutAFlashTurnItOff() {
        lit()
        WebcamSettings.setCaps(flash.copy(torch = false))
        assertFalse(torch())
    }

    @Test
    fun resetTurnsItOff() {
        lit()
        WebcamSettings.applyRemote(reset = true, partial = null)
        assertFalse(torch())
    }

    @Test
    fun aCameraSwitchWithTheTorchLeavesItOff() {
        WebcamSettings.setCaps(front, camera = "front")
        WebcamSettings.applyRemote(reset = false, partial = obj("""{"camera":"back","torch":true}"""))
        assertFalse(torch())
    }
}
