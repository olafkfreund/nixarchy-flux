---
status: approved
issue: 12
spec: spec/2026-11-13-12-webcam-torch.md
coders:
  1: coder   # the config field, the saved JSON, the setCaps change, TorchWatch and 3 test files; all given in full below
  2: coder   # the CameraSource request, callback and failure paths, given in full; codex and agy then review this concurrency code independently
  3: coder   # onEnded(restart), restart, the controller wiring and the phone switch; given in full
  4: coder   # 2 Go struct fields, 1 CLI key and their tests; small and given in full
  5: coder   # the QML toggle, the fixture, 1 QML test and 2 doc lines; given in full
reviewers:
  - codex    # reads the branch diff with `codex review --base master`; strongest on the camera-thread races and the restart path
  - agy      # independent read of the diff against this plan, including the Compose switch, the QML test and the docs
---

# Plan: Turn on the phone's flashlight while it is the webcam

## The approved decisions

- **The setting.** `WebcamConfig.torch: Boolean`, key `torch`, UI word
  "Flashlight" on the phone and "Use the flashlight" on the desktop card.
  On or off only, no brightness level. Android only; the iPhone and the
  Mac ignore the key.
- **The caps.** `WebcamCaps.torch: Boolean` says the streaming camera can
  use the flash. It comes from `FLASH_INFO_AVAILABLE` of the logical
  camera `id` (the request keys belong to it), and is false for a camera
  name in `CameraSource.noTorch`. A new phone sends `"torch": false`
  without a flash; an older phone sends nothing. Both mean no flash.
- **Session state, never saved.**
  - Saved JSON is `toSaved()` = `toJson()` without `torch`.
  - `clamped` turns it off without `caps.torch`, so the `LOOSE` caps
    before a camera opens, and a camera without a flash, turn it off.
  - `reset` turns it off (it starts from `WebcamConfig()`).
  - `setCaps(caps, camera)` (only called when a camera opens) turns it
    off: a new Webcam screen, `resume`, and every camera switch.
  - The open's `fail` turns it off (another app took the camera, a camera
    error while live).
  - `onEnded(restart = false)` turns it off: every stream stop.
    `WebcamSession.restart` ends with `restart = true`, so a frame size
    restart keeps it. The first Start webcam from a lit preview has no
    listener yet, so it stays lit.
  - `camera=back torch=true` in one change from the front camera stays
    off; the docs say to turn it on after the switch.
- **The request.** Only on a camera with a flash: `CONTROL_AE_MODE_ON`
  and `FLASH_MODE_TORCH` or `FLASH_MODE_OFF`. A camera without a flash
  gets today's request.
- **The watch.** One `TorchWatch` per torch activation (off → on), kept
  across zoom or exposure changes. It reads `FLASH_STATE` from each frame
  result; after 1000 ms dark it reports once. Time, not frames.
  - It reports only if it is still the newest watch and the session is
    the same.
  - **Decision 1 (approved):** a lens that never lit goes into `noTorch`
    (until the app process ends); the controller sets
    `caps.torch = false`, which turns the setting off and hides the
    switch. A flash that lit and went dark only turns the setting off.
  - **Decision 2 (approved):** no `FLASH_STATE` (null) never reports;
    the switch stays On.
- **A failed request** (not the first one of a session), with a flash:
  - asking for on: the setting goes off;
  - asking for off (**Decision 3, approved**): close the camera and
    report "The camera stopped: …" through the open's `fail`.
- **fluxd** gets `Torch *bool` in `webcamConfig` and `webcamCaps`.
  `*bool` so `false` survives `omitempty`.
- **The CLI** accepts `torch`. **The desktop card** shows the toggle only
  when `caps.torch === true`; `torch` is not a restart key.
- **Docs**: a `torch` row and a note in `docs/camera.md`.

Five refinements of the spec's sketches, each recorded in Corrections
when its step is done:

- **P1 (step 2).** The spec's failed "on" request calls `torchLost` with
  a "never lit" result. In the controller, `never = true` sets
  `caps.torch = false`, which also hides the switch until the camera
  opens again; the spec only asks that "the setting goes off". The plan
  calls `onTorchLost(choice.name, never = false)` directly: the setting
  goes off, the switch stays, no `noTorch` entry. The owner can ask for
  the spec's literal version before approving.
- **P2 (steps 2, 3).** `onTorchLost` carries the camera name, and the
  controller handles it with `main.post` and a check
  `choice?.name == name`. The spec runs it on the camera thread and reads
  `WebcamSettings.caps.value` then calls `setCaps`. A camera switch on the
  main thread between the camera thread's session check and that
  read-modify-write would copy the new camera's caps with
  `torch = false`, hiding the switch of a camera with a flash, or would
  write the old camera's caps over the new ones. `openCamera` and
  `choice` live on the main thread (`WebcamHost.kt:34,68`), so the post
  orders the two. `release` sets `choice = null`, so a post that lands
  after the controller is gone does nothing.
- **P3 (step 2).** The open's `fail` is kept as the third member of
  `active` (`Triple<Choice, Surface, (String) -> Unit>`), which the spec
  describes as "startSession keeps that fail with active". It is set and
  cleared in the same places as today's pair (`CameraSource.kt:301,327,366`).
  The stored `fail` is wrapped in the open's `current()` check, like
  every other `fail` call (`:266,279,288,298`), so an older camera never
  shows its error on a newer one.
- **P4 (step 2).** Turning the torch off sets `torchWatch = null`, not
  only off → on making a new one. Otherwise frames of the old torch
  request still in flight could finish the dark time of a watch that the
  user already turned off, and add the lens to `noTorch`.
- **P5 (step 3).** `WebcamSession.end` returns whether it ended the
  stream, and `restart` starts again only if its own `end` did. With the
  spec's version, a stop that wins the race makes `restart`'s `end` a
  no-op, and `start` then brings back a stream the user stopped (today's
  `restart` has the same gap). A stop that lands between `restart`'s
  `end` and `start` still loses, as today; the light then follows the
  new stream and goes off when it stops.

One spec item needs no code:

- **Spec 9, printing `torch=`.** `flux-cli webcam` already prints every
  key of the config (`cmd/flux/main.go:1020-1031`). Only `webcamKeys`
  changes.

## Steps

Steps 1-5 run in order on the branch `feat/12-webcam-torch`, and the lead
commits each one before the next starts. Each Android step compiles on
its own: new fields have defaults, and step 3 is the first user of step
2's API. There is no local Android SDK, so CI in step 7 is the first
Android compile.

Android paths below are under
`android/app/src/main/java/org/omarchy/flux/webcam/` unless they start
with `android/app/src/test/`. Tests are in
`android/app/src/test/java/org/omarchy/flux/webcam/`.

1. **The setting, saving, the watch: `WebcamConfig.kt`,
   `WebcamSettings.kt`, new `TorchWatch.kt`, tests**

   - `WebcamConfig.kt`:
     - After `val warmth: Float = 0f,` (`:40`) add
       `val torch: Boolean = false,`.
     - In `merged`, after the `warmth = …` line (`:63`) add
       `torch = partial.flag("torch") ?: torch,`.
     - In `clamped`, after the `warmth = …` line (`:90`) add
       `torch = torch && caps.torch,`.
     - In `toJson`, after `put("warmth", warmth)` (`:114`) add
       `put("torch", torch)`.
     - After `toJson` (ends `:115`), inside the class, add:

       ```kotlin
       /** The settings that the phone saves for the next session. The light is not saved. */
       fun toSaved(): JsonObject = JsonObject(toJson() - "torch")
       ```
     - `WebcamCaps`: after `val resolutions: List<Int> = RESOLUTIONS,`
       (`:127`) add `val torch: Boolean = false,`. In its `toJson`,
       after the `resolutions` line (`:145`) add `put("torch", torch)`.
       `LOOSE` (`:131-134`) stays as it is (its `torch` is `false`).
     - `reset` (`:95-98`) stays as it is.
   - `WebcamSettings.kt`:
     - KDoc of the object (`:11-15`):

       ```kotlin
       /**
        * The webcam settings of this phone. The settings screen and the computer
        * both change them through [update]. Each change is saved, except the
        * light, so the next session starts with the same settings.
        */
       ```
     - `:46`: `prefs?.edit()?.putString(KEY, next.toSaved().toString())?.apply()`.
     - `setCaps` (`:53-63`) becomes:

       ```kotlin
       /**
        * Sets what the current camera supports, and clamps the settings to it.
        * With [camera], the settings name the camera that opened, in the same
        * change, so the camera does not open twice. A camera that opens
        * starts with the light off.
        */
       fun setCaps(caps: WebcamCaps, camera: String? = null) {
           synchronized(lock) {
               _caps.value = caps
               update { if (camera != null) it.copy(camera = camera, torch = false) else it }
           }
       }
       ```
     - `load` (`:29-37`) stays as it is.
   - New `TorchWatch.kt`:

     ```kotlin
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
     ```
   - `WebcamConfigTest.kt`, before `private fun assertNotNullObj`
     (`:161`):

     ```kotlin
     @Test
     fun torchIsABooleanThatNeedsAFlash() {
         assertTrue(WebcamConfig().merged(obj("""{"torch":true}""")).torch)
         assertTrue(WebcamConfig().merged(obj("""{"torch":"true"}""")).torch)
         assertFalse(WebcamConfig().merged(obj("""{"torch":"yes"}""")).torch)
         val on = WebcamConfig(torch = true)
         assertTrue(on.clamped(caps.copy(torch = true)).torch)
         assertFalse(on.clamped(caps).torch)
         assertFalse(on.clamped(WebcamCaps.LOOSE).torch)
         assertFalse(on.reset().torch)
     }

     @Test
     fun torchIsSentButNotSaved() {
         val c = WebcamConfig(zoom = 2f, torch = true)
         assertEquals("true", c.toJson()["torch"].toString())
         assertFalse("torch" in c.toSaved())
         val back = WebcamConfig().merged(c.toSaved())
         assertFalse(back.torch)
         assertEquals(2f, back.zoom, 0f)
         assertEquals("true", caps.copy(torch = true).toJson()["torch"].toString())
         assertEquals("false", caps.toJson()["torch"].toString())
     }
     ```
   - New `TorchWatchTest.kt`:

     ```kotlin
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
     ```
   - New `WebcamSettingsTest.kt`:

     ```kotlin
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
     ```

   Verify: CI in step 7. Locally, `git diff --stat` shows only these 6
   files.

   Traps:
   - `toJson` stays the wire format (`WebcamProtocol.kt:31`); only the
     saved string uses `toSaved`. `jsonRoundTrip` (`WebcamConfigTest.kt:128`)
     keeps passing because its `torch` is `false`.
   - `JsonObject(toJson() - "torch")`: `JsonObject` is a
     `Map<String, JsonElement>`, and `JsonObject(map)` is a public
     constructor. No new import (`JsonObject` is imported at `:4`).
   - `WebcamSettings` is a process-wide object; each test starts from
     `clear()`. Don't call `load` in a test.

2. **The camera: `CameraSource.kt`**

   - Imports, in order with the others: after
     `android.hardware.camera2.CaptureRequest` (`:12`) add
     `import android.hardware.camera2.CaptureResult` and
     `import android.hardware.camera2.TotalCaptureResult`; after
     `android.os.HandlerThread` (`:17`) add `import android.os.SystemClock`.
   - `active` (`:39`) becomes
     `private var active: Triple<Choice, Surface, (String) -> Unit>? = null`
     (P3).
   - After `@Volatile private var released = false` (`:48`) add:

     ```kotlin

     /** Called on the camera thread when the flash of camera [name] did not light. [never] is true when it never lit. */
     @Volatile var onTorchLost: (name: String, never: Boolean) -> Unit = { _, _ -> }

     // The watch of the newest torch activation. A zoom or exposure change keeps it.
     @Volatile private var torchWatch: TorchWatch? = null
     ```
   - `Controls` (`:50-51`) becomes:

     ```kotlin
     /** Zoom, exposure, white balance, and the torch. They change without a new session. */
     data class Controls(val zoom: Float = 1f, val exposureIndex: Int = 0, val awbMode: Int = CameraMetadata.CONTROL_AWB_MODE_AUTO, val torch: Boolean = false)
     ```
   - `Choice` (`:62-76`): after `val activeArray: Rect?,` (`:75`) add
     `val torch: Boolean,`. Its KDoc (`:57-61`) gets a last line:
     "[torch] is true when the opened camera has a flash that has not
     failed to light."
   - `choiceFor`: after `activeArray = …,` (`:193`) add:

     ```kotlin
     torch = c.get(CameraCharacteristics.FLASH_INFO_AVAILABLE) == true && named.name !in noTorch,
     ```
     `c` is the logical camera `id` (`:156`). `logicalChoice` (`:349`)
     calls `choiceFor` with the same id and name, so the fallback keeps
     the value.
   - `setControls` (`:197-203`) becomes:

     ```kotlin
     /** Changes zoom, exposure, white balance, and the torch on the running camera. */
     fun setControls(next: Controls) {
         if (next == controls) return
         // A new watch for each off → on; none while off, so late frames of an old request don't count (P4).
         if (next.torch != controls.torch) torchWatch = if (next.torch) TorchWatch() else null
         controls = next
         val attempt = sessionAttempt
         handler.post { repeat(attempt) }
     }
     ```
   - `repeat` (`:205-238`) becomes:

     ```kotlin
     /** Sends the repeating request again with the current controls. */
     private fun repeat(attempt: Int, firstFailure: ((String) -> Unit)? = null) {
         if (attempt != sessionAttempt) return
         val s = session ?: return
         val (choice, target, fail) = active ?: return
         // The request and its callback read the same controls.
         val c = controls
         try {
             // A request that sets keys of a lens must name that lens when it is made.
             val builder = choice.physicalId?.takeIf { choice.physicalCrop }?.let { s.device.createCaptureRequest(CameraDevice.TEMPLATE_RECORD, setOf(it)) }
                 ?: s.device.createCaptureRequest(CameraDevice.TEMPLATE_RECORD)
             val request = builder.apply {
                 addTarget(target)
                 set(CaptureRequest.CONTROL_AF_MODE, CaptureRequest.CONTROL_AF_MODE_CONTINUOUS_VIDEO)
                 choice.fps?.let { set(CaptureRequest.CONTROL_AE_TARGET_FPS_RANGE, it) }
                 set(CaptureRequest.CONTROL_AE_EXPOSURE_COMPENSATION, c.exposureIndex.coerceIn(choice.exposureRange.lower, choice.exposureRange.upper))
                 set(CaptureRequest.CONTROL_AWB_MODE, c.awbMode)
                 val zoom = c.zoom.coerceIn(1f, choice.zoomMax)
                 val physical = choice.physicalId
                 if (physical != null) {
                     // A zoom ratio on the logical camera can switch lenses, so a chosen lens zooms by its own crop.
                     if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) set(CaptureRequest.CONTROL_ZOOM_RATIO, 1f)
                     if (choice.physicalCrop) choice.activeArray?.let { setPhysicalCameraKey(CaptureRequest.SCALER_CROP_REGION, cropFor(it, zoom), physical) }
                 } else if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.R) {
                     set(CaptureRequest.CONTROL_ZOOM_RATIO, zoom)
                 } else {
                     choice.activeArray?.let { set(CaptureRequest.SCALER_CROP_REGION, cropFor(it, zoom)) }
                 }
                 if (choice.torch) {
                     // The torch lights only with AE mode ON or OFF.
                     set(CaptureRequest.CONTROL_AE_MODE, CaptureRequest.CONTROL_AE_MODE_ON)
                     set(CaptureRequest.FLASH_MODE, if (c.torch) CaptureRequest.FLASH_MODE_TORCH else CaptureRequest.FLASH_MODE_OFF)
                 }
             }.build()
             val watch = torchWatch.takeIf { choice.torch && c.torch }
             s.setRepeatingRequest(request, watch?.let { callbackFor(it, attempt, choice) }, handler)
         } catch (e: Exception) {
             Log.w(TAG, "camera request failed", e)
             when {
                 firstFailure != null -> firstFailure("The camera stopped: ${e.message}")
                 !choice.torch -> Unit
                 // The request failed, not the flash: the setting goes off and the switch stays (P1).
                 c.torch -> onTorchLost(choice.name, false)
                 else -> {
                     // The old request may still light the torch.
                     closeNow()
                     fail("The camera stopped: ${e.message}")
                 }
             }
         }
     }

     private fun callbackFor(watch: TorchWatch, attempt: Int, choice: Choice) = object : CameraCaptureSession.CaptureCallback() {
         override fun onCaptureCompleted(s: CameraCaptureSession, r: CaptureRequest, result: TotalCaptureResult) {
             val lit = result.get(CaptureResult.FLASH_STATE)?.let { it == CaptureResult.FLASH_STATE_FIRED }
             if (watch.onResult(lit, SystemClock.elapsedRealtime())) torchLost(watch, attempt, choice)
         }
     }

     private fun torchLost(watch: TorchWatch, attempt: Int, choice: Choice) {
         // Only the newest torch request of this session reports.
         if (watch !== torchWatch || attempt != sessionAttempt) return
         if (!watch.everLit) noTorch += choice.name
         onTorchLost(choice.name, !watch.everLit)
     }
     ```
   - `startSession`, `onConfigured` (`:327`), with `current` the
     `startSession` parameter (P3):

     ```kotlin
     active = Triple(choice, target) { message: String -> if (current()) fail(message) }
     ```
   - `companion object` (`:385`): as its first member add:

     ```kotlin
     /** Cameras whose flash did not light, by name, until the app process ends. Their switch stays hidden. */
     val noTorch: MutableSet<String> = java.util.Collections.newSetFromMap(java.util.concurrent.ConcurrentHashMap())
     ```

   Verify: CI in step 7, and

   ```sh
   grep -nw "active" android/app/src/main/java/org/omarchy/flux/webcam/CameraSource.kt
   ```
   shows the declaration, `repeat`'s destructuring, and the 3 writes
   (`attemptFailed`, `onConfigured`, `closeNow`), nothing else (`-w`
   skips `activeArray`).

   Traps:
   - `val c = controls` stays above `try`: the `catch` reads `c.torch`.
     Don't leave a second `val c` inside `apply`.
   - `val (choice, target, fail) = active` reads `fail` before any
     `closeNow()` nulls `active`.
   - The first request of a session (`firstFailure != null`) keeps
     today's path through `attemptFailed`, which may try the logical
     fallback. Don't add torch handling to it.
   - A camera without a flash gets exactly today's request and `null` as
     the callback. Don't set `CONTROL_AE_MODE` outside `if (choice.torch)`.
   - `torchWatch` is written only in `setControls` (main thread) and read
     on the camera thread; both are `@Volatile`. `controls` is still
     assigned after the watch, so a `repeat` that sees `c.torch` sees the
     new watch. `torchLost`'s `watch !== torchWatch` check also rejects a
     watch whose torch was turned off (P4).
   - The `Triple`'s lambda needs the explicit `message: String` type, or
     the third type argument can't be inferred.
   - `onTorchLost` runs on the camera thread; step 3 moves its work to
     the main thread (P2). Keep its signature `(String, Boolean) -> Unit`.
   - `CaptureResult.FLASH_STATE` is `Integer`; compare with
     `FLASH_STATE_FIRED` as shown, so a null stays null (Decision 2).

3. **The session, the controller, the switch: `WebcamSession.kt`,
   `WebcamController.kt`, `WebcamPanel.kt`**

   - `WebcamSession.kt`:
     - `Listener` (`:46-50`):

       ```kotlin
       /** The camera side, which starts and stops the encoder. */
       interface Listener {
           fun onConnected(out: OutputStream, width: Int, height: Int)
           /** [restart] is true when [WebcamSession.restart] ends the stream to start it again. */
           fun onEnded(restart: Boolean)
       }
       ```
     - `restart` (`:131-139`) becomes:

       ```kotlin
       /**
        * Starts the stream again with a new frame size: "stop", then a new
        * "start" with a new port. It does nothing when no stream runs. The
        * listener hears that this end is a restart. A stop that came first
        * wins, and no new stream starts.
        */
       fun restart(core: FluxCore, width: Int, height: Int) {
           val (target, l, id) = synchronized(lock) { Triple(deviceId, listener, attempt) }
           if (target == null || l == null) return
           if (!end(core, notify = true, Status(), id, restart = true)) return
           start(core, target, width, height, l)
       }
       ```
     - `end` (`:201-220`) returns whether it ended the stream (P5):
       - `:201`:
         `private fun end(core: FluxCore, notify: Boolean, status: Status, id: Int, restart: Boolean = false): Boolean {`
       - `:203`: `if (attempt != id) return false`
       - `:215`: `if (l != null) synchronized(listenerLock) { l.onEnded(restart) }`
       - after `_status.value = status` (`:219`): `return true`
       - KDoc above it: `/** Ends attempt [id]. Returns false when another stop or start came first. */`
   - `WebcamController.kt`:
     - `init` (`:59-61`) becomes:

       ```kotlin
       init {
           if (orientation.canDetectOrientation()) orientation.enable()
           camera.onTorchLost = { name, never ->
               // On the main thread, where openCamera runs, so a camera that
               // opened since is not changed (P2).
               main.post {
                   if (choice?.name != name) return@post
                   // A lens that never lit hides its switch; a flash that went dark only turns off.
                   if (never) WebcamSettings.setCaps(WebcamSettings.caps.value.copy(torch = false))
                   else WebcamSettings.update { it.copy(torch = false) }
               }
           }
       }
       ```
     - `openCamera`: `:117` becomes
       `applied = config.copy(camera = c.name, torch = false)`; in the
       `WebcamCaps(...)` (`:119-126`) after the `cameras = …` line add
       `torch = c.torch,`.
     - `:137`:

       ```kotlin
       if (want == cameraWanted) camera.open(c, surface) { message ->
           _cameraError.value = message
           WebcamSettings.update { it.copy(torch = false) }
       }
       ```
     - `controlsFor` (`:142-149`): after the `awbMode = …` line add
       `torch = config.torch,`.
     - `onEnded` (`:190-195`) becomes:

       ```kotlin
       override fun onEnded(restart: Boolean) {
           // Stop drawing into the encoder before the encoder frees its surface.
           renderer.setEncoder(null, 0, 0)
           encoder?.release()
           encoder = null
           // A stop turns the light off. A restart for a new frame size keeps it.
           if (!restart) WebcamSettings.update { it.copy(torch = false) }
       }
       ```
     - `release` (`:202`): `onEnded(restart = false)`; after
       `cameraSurface = null` (`:210`) add `choice = null` (P2).
   - `WebcamPanel.kt`, after the Mirror `Row` (ends `:347`), before the
     microphone `Row`:

     ```kotlin
     if (caps.torch) {
         Row(
             Modifier.fillMaxWidth().heightIn(min = 48.dp).toggleable(value = config.torch, role = Role.Switch) { on -> set { it.copy(torch = on) } },
             verticalAlignment = Alignment.CenterVertically,
         ) {
             Column(Modifier.weight(1f)) {
                 Text("Flashlight", style = MaterialTheme.typography.bodyLarge)
                 Text("Light the room with the phone's flash", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
             }
             Switch(checked = config.torch, onCheckedChange = null)
         }
     }
     ```

   Verify: CI in step 7, and

   ```sh
   grep -rn "onEnded\|\.restart(\|fun end(" android/app/src/main/java
   ```
   shows `onEnded(restart: Boolean)` in `Listener` and the controller,
   `onEnded(restart = false)` in `release`, `l.onEnded(restart)` in `end`,
   the KDoc mention at `WebcamSession.kt:58`, and the one
   `WebcamSession.restart` caller (`WebcamController.kt:88`).

   Traps:
   - `Listener` has one implementer (`WebcamController`) and no test
     fakes; the grep above proves it.
   - Every `end` caller except `restart` keeps the default `false`
     (`WebcamSession.kt:123,150,159,165,196`) and ignores the new return
     value. Don't pass `restart` from `stop` or `stopOwnedBy`.
   - A stop between `restart`'s `end` and its `start` still loses (P5),
     as today. No unit test reaches this (it needs a `FluxCore` and a
     socket); it is for review.
   - `start`'s own `stop` (`:73`) after `restart`'s `end` finds no
     listener and only bumps the attempt; it calls no `onEnded`.
   - `choice` and `openCamera` are main-thread only; the `main.post` is
     what makes the `choice?.name` check and the caps read safe. A
     logical fallback keeps the name, so it still matches.
   - `WebcamPanel.kt` needs no new import: `Row`, `toggleable`, `Role`,
     `Switch` are already used by the Mirror row.
   - `release` runs `onEnded(false)`, which may change the settings while
     the controller goes away. That is wanted: the light is off.

4. **fluxd and the CLI: `internal/core/webcam.go`, `cmd/flux/main.go`,
   tests**

   - `internal/core/webcam.go`: in `webcamConfig`, after `Warmth`
     (`:128`) add ``Torch *bool `json:"torch,omitempty"` ``; in
     `webcamCaps`, after `Resolutions` (`:140`) add the same field. Run
     `gofmt -w internal/core/webcam.go internal/core/session_test.go cmd/flux/main_test.go`
     for the column alignment and the tabs.
   - `cmd/flux/main.go:1037`: add `"torch"` after `"warmth"` in
     `webcamKeys`.
   - `internal/core/session_test.go`, `TestCleanWebcamConfig`:
     - add to `bad` (`:521-528`):
       ``{`{"torch":"yes"}`, ``},`` and ``{``, `{"torch":"yes"}`},``;
     - before the closing `}` (`:547`):

       ```go
       cfg, caps, err = cleanWebcamConfig(rawOrNil(`{"torch":false}`), rawOrNil(`{"torch":false}`))
       if err != nil || string(cfg) != `{"torch":false}` || string(caps) != `{"torch":false}` {
           t.Fatalf("torch false: %s %s %v", cfg, caps, err)
       }
       ```
   - `TestConfigureWebcamChecksSettings`: add `` `{"torch":"yes"}` `` to
     the bad list (`:559`); before the closing `}` (`:583`):

     ```go
     if err := d.ConfigureWebcam(json.RawMessage(`{"torch":false}`), false); err != nil {
         t.Fatal(err)
     }
     l.mu.Lock()
     got = l.sent[1]["config"]
     l.mu.Unlock()
     if !reflect.DeepEqual(got, map[string]any{"torch": false}) {
         t.Fatalf("sent %#v", got)
     }
     ```
   - `cmd/flux/main_test.go`, `TestWebcamSettings`, before the closing
     `}` (`:38`):

     ```go
     for _, v := range []bool{true, false} {
         cfg, err := webcamSettings([]string{fmt.Sprintf("torch=%t", v)})
         if err != nil || cfg["torch"] != v {
             t.Errorf("torch=%t: %#v %v", v, cfg, err)
         }
     }
     ```
     `fmt` is already imported (`main_test.go:8`).

   Verify: `make test vet` passes.

   Traps:
   - `*bool`, not `bool`: with `omitempty`, a plain `false` would vanish
     and `{"torch":false}` would be refused as empty (`webcam.go:419-421`).
   - `cleanWebcamConfig` needs no new check: a `*bool` has no range, and
     a wrong type fails `json.Unmarshal` (`:153`, `:173`).
   - `got` in the last block reuses the variable declared at `:577`.

5. **The desktop card, the fixture, the docs:
   `gui/qml/components/CameraCard.qml`, `gui/qml/tools/fixture.json`,
   `gui/tests/tst_views.qml`, `docs/camera.md`**

   - `CameraCard.qml:250`: `// Chips, and the mirror and flashlight switches`.
   - After the Mirror `Toggle` (`:311-315`):

     ```qml
     Toggle {
       objectName: "torchToggle"
       visible: root.caps.torch === true
       text: "Use the flashlight"
       checked: !!root.config.torch
       onToggled: function (checked) { root.setKey("torch", checked) }
     }
     ```
   - `fixture.json`: in the webcam config, `"warmth": 0.0` (`:378`)
     becomes `"warmth": 0.0,` followed by `"torch": false`; in the caps,
     the `resolutions` array (`:402-405`) gets a comma after `]` and then
     `"torch": true`. Keep the file's 1-space indent.
   - `tst_views.qml`, in the `Limits` `TestCase`, after
     `test_cameraShowsAtMost16Chips` (ends `:790`):

     ```qml
     function test_cameraTorchToggle() {
       var view = createTemporaryObject(viewComponent, top)
       view.selectedId = top.pixel
       view.tab = "device"
       var card = null
       tryVerify(function () { card = findBy(page(view), "objectName", "cameraCard"); return !!card })
       card.settingsOpen = true
       var t = findBy(card, "objectName", "torchToggle")
       verify(!!t)
       tryVerify(function () { return t.visible })
       verify(!t.checked)
       t.toggled(true)
       compare(requestsOf("webcam.config").length, 1)
       compare(JSON.stringify(requestsOf("webcam.config")[0].params.config), '{"torch":true}')
       verify(!card.restarting)
       tryVerify(function () { return t.checked })
       t.toggled(false)
       compare(JSON.stringify(requestsOf("webcam.config")[1].params.config), '{"torch":false}')
       tryVerify(function () { return !t.checked })
       // A change from the phone checks it.
       mock.setState(function (s) { s.webcam.config.torch = true })
       tryVerify(function () { return t.checked })
       mock.setState(function (s) { s.webcam.caps.torch = false })
       tryVerify(function () { return !t.visible })
       mock.setState(function (s) { s.webcam.caps.torch = true })
       tryVerify(function () { return t.visible })
       mock.setState(function (s) { delete s.webcam.caps.torch })
       tryVerify(function () { return !t.visible })
     }
     ```
   - `docs/camera.md`: after the `warmth` row (`:89`):

     ```markdown
     | `torch` | `true`, `false`. Only on Flux for Android, with a camera that has a flash. | `false` |
     ```
     and after `:92`:

     ```markdown
     `torch` is not saved. It is off when a camera opens, and it goes off when the stream stops and with `reset`.
     Turn it on after a camera change, not in the same `set`.
     If the flash doesn't light, the phone turns `torch` off, and hides the switch when that lens never lit.
     ```

   Verify: `make test-gui` passes, and
   `python3 -I -m json.tool gui/qml/tools/fixture.json > /dev/null`.

   Traps:
   - `torch` stays out of `restartKeys` (`CameraCard.qml:54`) and out of
     the mock's restart list (`MockBackend.qml:127`).
   - The settings grid is hidden until `settingsOpen`
     (`CameraCard.qml:244`); the test opens it first.
   - `=== true` hides the toggle for an older phone or fluxd that sends
     no `torch`.
   - Shared QML must work on both desktop hosts (AGENTS.md); `Toggle` is
     the card's own component, nothing host-specific.
   - Compare request params as `JSON.stringify` strings, as the file
     does elsewhere (`tst_views.qml:1062,1067`), not as object literals.
   - `Limits.init()` (`tst_views.qml:735-738`) resets the state and the
     requests, so the test starts from the fixture.

6. **Local checks (the lead).**
   - `make test vet` and `make test-gui` pass.
   - `nix flake check path:.` passes.
   - `git status --porcelain` shows only the files of steps 1-5.

7. **CI (the lead).**
   - Push, after an agent-bus announcement.
   - Dispatch `build.yml` on the branch:
     ```sh
     gh workflow run build.yml -R olafkfreund/nixarchy-flux --ref feat/12-webcam-torch
     ```
   - Poll every 5 minutes. The Go job (`make vet test`) and the android
     job must pass: unit tests (with `WebcamConfigTest`, `TorchWatchTest`,
     `WebcamSettingsTest`), `lintDebug`, both assembles, and the ABI
     check.

8. **Device tests (the lead and the owner)**, with the #13 setup (`fluxd`
   on p620, paired) and the branch's `android-debug` APK.

   `$b` is
   `/tmp/claude-1000/-mnt-code-Source-home-GitHub/9f0892e4-8d93-4a6d-a1da-d5bcdbb3eba1/scratchpad/r13/flux-core`,
   the build that runs `fluxd` now. Use `$b/bin/flux-cli`, never a
   `flux-cli` from `PATH`.

   **Setup:**
   1. Read the serials with `adb devices`, because wireless ports change.
      The Pixel 11 Pro XL is the main device; the Galaxy Tab S8 Ultra
      repeats checks 1-3.
   2. Compare the signing certificate of the installed app with the APK:
      `adb -s <serial> shell pm dump org.omarchy.flux | grep -A1 signatures`
      against `keytool -printcert -jarfile app-debug.apk`.
      - The same: `adb -s <serial> install -r app-debug.apk`.
      - Different (a CI debug key differs from the old one): ask the
        owner, then `adb -s <serial> uninstall org.omarchy.flux`,
        install, and pair again with `$b/bin/flux-cli pair` and
        `accept`.
   3. `adb -s <serial> logcat -c`, then keep
      `adb -s <serial> logcat -s FluxWebcamCamera` running.

   **Then, on the Webcam screen, in the preview:**

   | # | Check | How |
   | --- | --- | --- |
   | 1 | On and off | back camera: Flashlight on lights the flash; off turns it off |
   | 2 | Front camera | switch to the front camera: the switch hides. Back: it shows, off |
   | 3 | Each lens | each back lens (0.6x, 1x, 3x, and any other): record whether the switch lights the flash, or turns off and hides within about 1 second (Decision 1). Either is a pass. On and no light that stays On is a fail |
   | 4 | Reopen | light it, leave the Webcam screen and open it again: off. Light it, send the app to the background and back: off |
   | 5 | Stop path | light it, press Start webcam. p620 has no v4l2loopback, so fluxd ends the stream at once (`webcam.go:279-283`): the light goes off with the error |
   | 6 | Reset | light it, Reset image: off |
   | 7 | Another app | light it, open the system camera app over Flux in split screen so it takes the camera: the switch shows off and the camera error shows |
   | 8 | Log | the logcat shows no `camera request failed` |

   **Not shown here** (no v4l2loopback on p620, which needs
   `services.flux.webcam.enable` in its NixOS config and a rebuild: the
   owner's call, `cmd/flux/setup.go:392`):
   - the light staying on from Start webcam into a live stream, and
     through a resolution restart (`restart = true`);
   - the desktop card and `$b/bin/flux-cli webcam set torch=true` /
     `torch=false` on a live stream, with the phone's switch following;
   - the light staying on with the phone locked while live.

   No unit test reaches `WebcamSession.restart` (it needs a `FluxCore`
   and a socket); its change is for review. Record the results, with the
   lens table of check 3, in the PR.

9. **PR (the lead).** Open the PR linking the intent, spec and plan, with
   the CI and device results, what stays unshown (step 8), and which
   steps each implementer did.

## Tests

- `make test vet`: `TestCleanWebcamConfig` (wrong type and `false` in
  config and caps), `TestConfigureWebcamChecksSettings` (`"yes"` refused,
  `false` sent), `TestWebcamSettings` (`torch=true|false`).
- `make test-gui`: `test_cameraTorchToggle` (visible with the cap, sends
  `{torch: true}` and `{torch: false}`, no restart, follows the state,
  hidden with `false` and without the cap).
- CI `build.yml` android job: `WebcamConfigTest.torchIsABooleanThatNeedsAFlash`,
  `torchIsSentButNotSaved`; `TorchWatchTest` (4 tests);
  `WebcamSettingsTest` (5 tests); lint and assemble.
- The device checks of step 8: the request, the frame callback, the
  lifecycle and the switch need a real phone.
- `nix flake check path:.`.

## Rollback

- Revert the PR's squash commit, rebuild fluxd and the CLI, and install
  the reverted APK. The changed signatures are `Listener.onEnded`,
  `WebcamSession.end`, `CameraSource.Choice`, `CameraSource.Controls` and
  `active`; the revert restores all their users.
- Nothing is stored: the torch is never saved, and `noTorch` lives only
  in memory. Saved settings written by the new build have no `torch`, so
  the old build reads them as before.
- Mixed versions after a partial rollback are safe: an older fluxd drops
  the key and the cap, an older phone ignores the key.

## Review changes

Codex and agy reviewed the first draft of this plan, read only. Each
finding was checked against the code.

| Finding | Source | Verdict | Change |
| --- | --- | --- | --- |
| A stop that wins the race with `restart` lets `restart` start a new stream anyway | Codex (high) | Confirmed, but the light follows the stream: a stop before `restart`'s `end` already turned it off, and a stop between `end` and `start` leaves a running stream whose next stop turns it off (`WebcamSession.kt:135-139,201-215`). The revived stream is today's behavior | P5: `end` returns whether it ended; `restart` starts only then. The narrower gap after `end` is a trap in step 3, for review |
| Turning the torch off keeps the old watch; late dark frames can add the lens to `noTorch` | Codex (medium), agy (low) | Confirmed (`setControls` replaced the watch only off → on) | P4: `torchWatch = null` on off |
| The name check of P2 doesn't cover a same-name reopen or a released controller | Codex (medium) | Partly confirmed. Release: `choice` was never cleared (`WebcamController.kt:198-211`). Same-name reopen: the late report only turns off a torch that the open already turned off, or hides a switch for a lens that `noTorch` now holds, which the reopened `Choice` would hide too | `release` sets `choice = null`; same-name reopen accepted |
| `compare(obj, {torch: true})` may compare JS objects by reference | agy (high) | Unverified (the Qt `TestCase.qml` source wasn't found in time); the file compares params with `JSON.stringify` (`tst_views.qml:1062,1067`) | `JSON.stringify` compares; a check that Off unchecks |
| The stored `fail` skips the open's `current()` check, so an old camera's error can show on a new one | agy (medium) | Confirmed: every other `fail` call checks `current()` (`CameraSource.kt:266,279,288,298`); `generation` changes before `closeNow` runs | P3: the `Triple` stores `{ if (current()) fail(it) }` |
| `grep -n "active"` also matches `activeArray` | agy (low) | Confirmed (`CameraSource.kt:75,193,226,230`) | `grep -nw`; step 3's grep also lists the KDoc line |
| `WebcamSettingsTest` leaves the singleton changed for later test classes | agy (low) | Confirmed | `@After` on `clear()` |
| `gofmt` only on `webcam.go` | agy (low) | Confirmed: the test snippets need tabs | `gofmt -w` on both test files |
| P1, P2, P3 and the no-code spec 9 item are correct; line references match; routing is reasonable | both | Agreed | None |
| Android compile, `make test vet`, `make test-gui` not run | both (unverified) | Expected: CI is the first Android compile; the lead runs the local checks in step 6 | None |

## Corrections

| # | Step | What changed and why |
| --- | --- | --- |
