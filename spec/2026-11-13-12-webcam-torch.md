---
status: approved
issue: 12
intent: intent/2026-11-13-12-webcam-torch.md
---

# Spec: Turn on the phone's flashlight while it is the webcam

The owner approved the intent with its proposed answers:
- the light is session state, never saved: it stays on while the same
  camera runs (Start webcam from a lit preview, a frame size restart) and
  goes off when the stream stops, when a camera opens or closes, with
  Reset, and on a camera without a flash;
- the key is `torch`; the UI says "Flashlight";
- a caps field `torch` says the streaming camera can use the flash;
- on or off only, no brightness level;
- the phone reads the frame results and turns the setting off when the
  flash doesn't light;
- Android only; the iPhone and the Mac ignore the key.

Three choices in this spec need the owner's yes (see Decisions).

Android paths are under `android/app/src/main/java/org/omarchy/flux/webcam/`.

## Decisions for the owner

1. **A lens that can't light the flash.** Android can't say in advance
   whether a lens inside a logical camera lights the shared flash. So the
   switch shows on every lens of a camera with a flash. If the first try
   never lights, the setting goes off and the switch hides for that camera
   name until the app process ends (step 4). The approved intent says "no
   switch where the flash can't light"; this meets it after the first try,
   not before. The Pixel test records which lenses light.
2. **A phone that doesn't report `FLASH_STATE`.** The phone then can't
   tell whether the flash lit. Proposed: trust the request and keep the
   switch On. Turning it off would break the torch on every such phone.
3. **A request that fails while turning the light off.** The old request
   could keep the light on. Proposed: close the camera and show "The camera
   stopped", the same message as other camera failures. The user reopens
   the screen. A failure while turning it on only turns the setting off.

## Design

### 1. The setting and the caps (`WebcamConfig.kt`)

- `WebcamConfig` gets `val torch: Boolean = false` after `warmth` (`:40`).
  - `merged`: `torch = partial.flag("torch") ?: torch` (`:63`). `flag`
    takes `true`, `false` and their strings (`:180-183`).
  - `clamped`: `torch = torch && caps.torch` (`:90`). A camera without a
    flash, and the `LOOSE` caps before a camera opens, turn it off.
  - `reset` needs no change: it starts from `WebcamConfig()`, whose `torch`
    is `false` (`:95-98`).
  - `toJson` puts `torch` (`:114`). This is the wire format.
  - A new function for the saved settings:

    ```kotlin
    /** The settings that the phone saves for the next session. The light is not saved. */
    fun toSaved(): JsonObject = JsonObject(toJson() - "torch")
    ```
- `WebcamCaps` gets `val torch: Boolean = false` (`:127`). `toJson` puts
  it (`:145`), so a new phone sends `"torch": false` for a camera without a
  flash; an older phone sends nothing. Both mean no flash. `LOOSE` keeps
  `false` (`:131-134`).

### 2. Never saved; off when a camera opens (`WebcamSettings.kt`)

- `update` saves `next.toSaved()` instead of `next.toJson()` (`:46`).
- `load` needs no change: older saved settings have no `torch`, and the
  clamp to `LOOSE` turns it off anyway (`:35`).
- `setCaps` with a `camera` is called only when a camera opens
  (`WebcamController.kt:118-128`). It also turns the light off:

  ```kotlin
  update { if (camera != null) it.copy(camera = camera, torch = false) else it }
  ```

  This covers a new Webcam screen, `resume` after a pause or after another
  app took the camera (`WebcamController.kt:100-105`), and every camera
  switch, also between two lenses with a flash.
- KDoc of the object (`:13-14`): "Each change is saved, except the light,
  so the next session starts with the same settings." KDoc of `setCaps`:
  "A camera that opens starts with the light off."

### 3. The torch watch (new `TorchWatch.kt`, pure Kotlin)

The decision from the frame results, without Camera2 types, so a JVM test
covers it:

```kotlin
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
        if (lit) { everLit = true; darkSince = -1L; return false }
        if (darkSince < 0) darkSince = nowMs
        if (nowMs - darkSince < darkMs) return false
        reported = true
        return true
    }
}
```

Time, not frames, because `pickFps` can choose another rate than 30 fps
(`CameraSource.kt:420-424`).

### 4. The camera (`CameraSource.kt`)

- Imports `CaptureResult`, `TotalCaptureResult` and `SystemClock`.
- `Choice` gets `val torch: Boolean` (`:62-76`). `choiceFor` sets it:

  ```kotlin
  torch = c.get(CameraCharacteristics.FLASH_INFO_AVAILABLE) == true && named.name !in noTorch,
  ```

  `c` is the logical camera `id` (`:156`), because the request keys belong
  to it (`:153-156`). The logical fallback builds its `Choice` from the same
  `id` and name (`:349`), so the value stays the same after a fallback.
- `noTorch` holds the names of cameras whose first try never lit
  (Decision 1). It lives as long as the app process:

  ```kotlin
  companion object {
      /** Cameras whose flash did not light, by name. Their switch stays hidden. */
      val noTorch: MutableSet<String> = java.util.Collections.newSetFromMap(java.util.concurrent.ConcurrentHashMap())
  }
  ```

  (Added to the existing `companion object`, `:385-425`.)
- `Controls` gets `val torch: Boolean = false` (`:51`). Its KDoc: "Zoom,
  exposure, white balance, and the torch."
- One watch per torch activation, kept across other control changes:

  ```kotlin
  /** Called on the camera thread when the flash did not light. [never] is true when it never lit. */
  @Volatile var onTorchLost: (never: Boolean) -> Unit = {}
  @Volatile private var torchWatch: TorchWatch? = null
  ```

  In `setControls` (`:198-203`), before `controls = next`:
  `if (next.torch && !controls.torch) torchWatch = TorchWatch()`.
  So a zoom or exposure change keeps the same watch and its dark time.
- In `repeat` (`:206-238`), `val c = controls` moves above the builder
  (today it is inside `apply`, `:218`), so the request and the callback
  read the same controls. Inside the builder, only with a flash:

  ```kotlin
  if (choice.torch) {
      // The torch lights only with AE mode ON or OFF.
      set(CaptureRequest.CONTROL_AE_MODE, CaptureRequest.CONTROL_AE_MODE_ON)
      set(CaptureRequest.FLASH_MODE, if (c.torch) CaptureRequest.FLASH_MODE_TORCH else CaptureRequest.FLASH_MODE_OFF)
  }
  ```

  A camera without a flash gets the same request as today. Then:

  ```kotlin
  val watch = torchWatch.takeIf { choice.torch && c.torch }
  s.setRepeatingRequest(request, watch?.let { callbackFor(it, attempt, choice) }, handler)
  ```

  ```kotlin
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
      onTorchLost(!watch.everLit)
  }
  ```

  The callback runs on the camera thread (`handler`). Without the torch,
  the request has no callback, as today.
- **A failed request** (the `catch`, `:234-237`), with a flash:
  - asking for the light on: `torchLost` with a fresh "never lit" result,
    so the setting goes off (no `noTorch` entry: the request failed, not
    the flash);
  - asking for the light off: the old torch request may still run. Close
    the camera (`closeNow()`) and report "The camera stopped: …" through
    the open's `fail` (Decision 3). `startSession` keeps that `fail` with
    `active` (`:327`), and `closeNow` clears it (`:362-369`).

### 5. The controller (`WebcamController.kt`)

- `openCamera` passes `torch = c.torch` into `WebcamCaps` (`:119-126`) and
  sets `applied = config.copy(camera = c.name, torch = false)` (`:117`).
- `controlsFor` passes `torch = config.torch` (`:144-148`).
- `init` sets the callback. `WebcamSettings` is thread safe (`:41,59`):

  ```kotlin
  camera.onTorchLost = { never ->
      // A lens that never lit hides its switch; a flash that went dark only turns off.
      if (never) WebcamSettings.setCaps(WebcamSettings.caps.value.copy(torch = false))
      else WebcamSettings.update { it.copy(torch = false) }
  }
  ```

  `setCaps` without a camera clamps the setting off (step 2). The change
  reaches the switch, and `sendConfig` tells the computer.
- **Off when the camera is lost.** The open's `fail` (`:137`) also turns
  it off: `{ message -> _cameraError.value = message; WebcamSettings.update { it.copy(torch = false) } }`.
  That covers another app taking the camera and a camera error while live.
- **Off when the stream stops, kept on a restart.** `onEnded` gets the
  reason from the session (step 6):

  ```kotlin
  override fun onEnded(restart: Boolean) {
      renderer.setEncoder(null, 0, 0)
      encoder?.release()
      encoder = null
      if (!restart) WebcamSettings.update { it.copy(torch = false) }
  }
  ```

  `release` calls `onEnded(restart = false)` (`:202`).

### 6. The session (`WebcamSession.kt`)

- `Listener.onEnded()` becomes `onEnded(restart: Boolean)` (`:49`). There
  are no test fakes of `Listener`.
- `end` gets `restart: Boolean = false` and calls `l.onEnded(restart)`
  (`:201,215`).
- `restart` ends the running attempt itself, as a restart, then starts.
  `start`'s own `stop` (`:73`) then finds no listener and does nothing:

  ```kotlin
  fun restart(core: FluxCore, width: Int, height: Int) {
      val (target, l, id) = synchronized(lock) { Triple(deviceId, listener, attempt) }
      if (target == null || l == null) return
      end(core, notify = true, Status(), id, restart = true)
      start(core, target, width, height, l)
  }
  ```

  Every other end passes `false`: Stop on the phone, stop from the
  computer, an error, an unpair, a dropped link, a failed start. A stop
  that wins the race with a restart ends the attempt first; then
  `end(…, id, restart = true)` finds another attempt and returns (`:203`),
  and the light went off with that stop. The first Start webcam has no
  listener yet (`:76,209`), so a lit preview stays lit.

### 7. The phone's switch (`WebcamPanel.kt`)

After the Mirror row (`:338-347`), the same shape, shown only with a flash:

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

`WebcamSettingsPanel` already gets `caps` (`:301-303`). No icon: the
Mirror row has none.

### 8. fluxd (`internal/core/webcam.go`)

- `webcamConfig` gets `Torch *bool \`json:"torch,omitempty"\`` after
  `Warmth` (`:128`), and `webcamCaps` the same after `Resolutions` (`:140`).
  `*bool`, like `Mirror` (`:121`), so `false` survives `omitempty` and
  `{"torch":false}` isn't refused as empty (`:419-421`).
- Nothing else changes. A wrong type (`"torch":"yes"`) fails `Unmarshal`,
  as `"mirror":"yes"` does today.

### 9. The CLI (`cmd/flux/main.go`)

- `webcamKeys` gets `"torch"` after `"warmth"` (`:1037`).
- `flux-cli webcam` prints `torch=` with the other settings (`:1020-1031`).

### 10. The desktop card (`gui/qml/components/CameraCard.qml`)

After the Mirror toggle (`:311-315`):

```qml
Toggle {
  objectName: "torchToggle"
  visible: root.caps.torch === true
  text: "Use the flashlight"
  checked: !!root.config.torch
  onToggled: function (checked) { root.setKey("torch", checked) }
}
```

- A verb phrase like "Mirror the image", the card's style.
- `=== true` hides it for an older phone or an older fluxd.
- `torch` is not in `restartKeys` (`:54`), so no "Restarting…".
- The comment at `:250` becomes "Chips, and the mirror and flashlight
  switches".
- The mock backend copies any key (`gui/qml/tools/MockBackend.qml:117-134`).
  `gui/qml/tools/fixture.json` gets `"torch": false` in the webcam config
  (`:378`) and `"torch": true` in the caps (`:381-395`).

### 11. Docs (`docs/camera.md`)

- A row after `warmth` (`:89`):

  `| \`torch\` | \`true\`, \`false\`. Only on Flux for Android, with a camera that has a flash. | \`false\` |`
- After `:92`: "`torch` is not saved. It is off when a camera opens, and it
  goes off when the stream stops and with `reset`. Turn it on after a
  camera change, not in the same `set`. If the flash doesn't light, the
  phone turns `torch` off, and hides the switch when that lens never lit."

## Alternatives rejected

- **Save the torch like the other settings.** A light that comes on by
  itself in a later call. Rejected in the intent.
- **A `restarting` flag in the controller around `WebcamSession.restart`.**
  The first draft. `onEnded` also runs on the I/O and watchdog threads
  (`WebcamSession.kt:123,181,196`), and a stop that clears the listener
  first makes `restart` return while the flag is still set, so the light
  stays on with no stream (review). The session knows which end is a
  restart, so it says so.
- **Watch `WebcamSession.status` for the end of a stream.** A restart
  passes through `Idle` too, and a `StateFlow` can skip or show it.
- **Turn the light off in `pause` and `release`.** A closed camera has no
  light, and the next open turns the setting off (step 2).
- **`CameraManager.setTorchMode` or `TorchCallback`.** The torch API works
  only while no app has the camera open, and its callback reports the
  torch unavailable whenever a camera is open.
- **Read the flash from the lens (`p`).** The request goes to the logical
  camera, so the logical camera decides. A lens that reports no flash
  under a logical camera with one would hide a working switch.
- **Count frames instead of time.** The frame rate isn't always 30.
- **Set `CONTROL_AE_MODE_ON` on every camera.** Only cameras with a flash
  need it; the others keep today's request.
- **Torch with a camera switch in one change** (`camera=back torch=true`
  from the front camera). The clamp drops it, and the open turns it off
  anyway. Supporting it means holding a pending torch across the open. The
  docs say to turn it on after the switch.
- **A brightness level.** Needs Android 15 (`FLASH_STRENGTH_LEVEL`).
  Rejected in the intent.

## Risks

- **The first try on a lens that can't light.** The switch shows, the
  light doesn't come on, and after 1 second the setting goes off and the
  switch hides (Decision 1). Checked lens by lens on the Pixel.
- **`FLASH_STATE` not reported** (Decision 2). The switch may show On with
  no light on such a phone.
- **Overheating.** Not shown on a device: there is no safe way to heat the
  Pixel here. If the HAL reports the flash dark, the setting goes off and
  the switch stays. If the HAL keeps `FIRED` while it dims, the switch
  stays On.
- **A user re-enabling at the instant a watch reports.** The old watch
  checks `watch !== torchWatch` on the camera thread, but a re-enable on
  the main thread between that check and the setting change can be turned
  off. The user taps again.
- **Closing the camera after a failed off request** (Decision 3) stops the
  image of a live stream until the screen is reopened. It is a failure
  case that today only logs.
- **Start webcam to another computer while a stream runs** stops the first
  stream, so the light goes off. Accepted: it is a stop.
- **AE mode.** Only cameras with a flash get `CONTROL_AE_MODE_ON`, the
  default of `TEMPLATE_RECORD`.
- **Heat and battery.** A torch plus the encoder on a long call heats the
  phone. The light is never on by itself, and it goes off at each stop.
- **Mixed versions.** A new card with an older fluxd: no toggle, because
  the older fluxd drops the cap. `flux-cli webcam set torch=true` against
  an older, not restarted fluxd gets `bad_params`. Both are expected.

## Verification

1. **Android unit tests**, from `android/`:
   `./gradlew :app:testDebugUnitTest :app:lintDebug :app:assembleDebug --no-daemon`.
   - `WebcamConfigTest.kt`:
     - `merged` reads `torch` as a boolean and as `"true"`;
     - `clamped` keeps `torch` with `caps.torch`, drops it without and
       with `LOOSE`;
     - `reset` turns it off;
     - `toJson` has `torch`, `toSaved` doesn't, and
       `WebcamConfig().merged(c.toSaved())` has `torch == false`;
     - `WebcamCaps.toJson` has `torch`.
   - New `TorchWatchTest.kt`: lit frames never report; 1 second dark
     reports once, then never again; a lit frame resets the dark time;
     `everLit` after a lit frame; null states never report.
   - New `WebcamSettingsTest.kt` (`load` is never called, so no
     `SharedPreferences`; each test starts with `setCaps(LOOSE)` and
     `update { WebcamConfig() }`): with flash caps, `update` turns it on;
     `setCaps(caps, camera = "back")` turns it off; `setCaps` with
     `torch = false` caps turns it off; `applyRemote(reset = true)` turns it
     off; `applyRemote` with `{"camera":"back","torch":true}` from front caps
     leaves it off.
2. **Go tests**, `make test vet`.
   - `TestCleanWebcamConfig`: `{"torch":false}` keeps `"torch":false` in
     the config and in the caps; `{"torch":"yes"}` fails in each.
   - `TestConfigureWebcamChecksSettings`: `{"torch":"yes"}` is
     `bad_params`; `{"torch":false}` is sent as `"torch": false`.
   - `TestWebcamSettings` (`cmd/flux/main_test.go:19`): `torch=true` gives
     `true`, `torch=false` gives `false`.
3. **QML view test**, `make test-gui`. In `gui/tests/tst_views.qml`, next to
   `test_cameraShowsAtMost16Chips` (`:765`):
   - set `card.settingsOpen = true` first (the settings grid is hidden
     until then, `CameraCard.qml:244`);
   - with the fixture's `caps.torch: true`, `torchToggle` is visible;
     toggling sends `webcam.config` with `{config: {torch: true}}`, then
     `{config: {torch: false}}`, and `card.restarting` stays false;
   - a state change to `config.torch: true` checks it;
   - with `caps.torch: false`, and with it removed, it is hidden.

   This is how the desktop toggle is checked: p620 has no v4l2loopback, so
   fluxd ends each stream at once (`webcam.go:279-283`), and the real card
   never shows a live stream.
4. **Pixel**, with the branch's debug APK and the #13 setup (fluxd on
   p620, paired). On the Webcam screen, in the preview:
   1. Back camera: the Flashlight switch lights the flash; off turns it
      off.
   2. Switch to the front camera: the switch hides. Back: it shows, off.
   3. Each back lens (0.6x, 1x, 3x): record whether the switch lights the
      flash, or turns off and hides within about 1 second (Decision 1).
      Either is a pass. A switch that stays On with no light is a fail.
   4. Light the preview, leave the screen and open it again: off. Light
      it, send the app to the background and back: off.
   5. Light the preview, press Start webcam. On p620 the stream fails at
      once, so this shows the stop path: the light goes off with the
      error.
   6. Reset image: off.
   7. Light it, open the system camera app over Flux in split screen, so
      it takes the camera: the switch shows off.
   8. `adb logcat -s FluxWebcamCamera` shows no `camera request failed`.
5. **Not shown here, without v4l2loopback on p620:**
   - the light staying on from Start webcam into a live stream, and
     through a resolution restart (`restart = true`);
   - the desktop card and these commands on a live stream, with the
     phone's switch following them:
     `flux-cli webcam set torch=true`, then `flux-cli webcam set torch=false`;
   - the light staying on with the phone locked while live.

   No unit test reaches `WebcamSession.restart`: it needs a `FluxCore`
   and a socket. The restart path is the 4-line change in step 6, for
   review. These checks wait for a desktop with v4l2loopback. On p620
   that means `services.flux.webcam.enable` in its NixOS config and a
   rebuild, which is the owner's call (`cmd/flux/setup.go:392`).
6. `nix flake check path:.`.

## Review changes

Codex and agy reviewed the first draft, read only. Each finding was checked
against the code.

| Finding | Verdict | Change |
| --- | --- | --- |
| Codex 1, agy 1: the `restarting` flag isn't volatile; a stop that clears the listener makes `restart` return with the flag set, light stays on | confirmed (`WebcamSession.kt:136-137,202-215`; `onEnded` runs on I/O and watchdog threads `:123,181,196`) | flag removed; `onEnded(restart)` from the session (steps 5, 6); moved to Alternatives |
| Codex 4, agy 2: a lens that can't light still shows the switch, against the intent | confirmed (`intent:196-198`) | the switch hides after a first try that never lights (`noTorch`); Decision 1; Pixel step 3 accepts either |
| Codex 2, agy 3: a failed request leaves On with no light, or the light on after Off | confirmed (`CameraSource.kt:198-203,234-237`) | on: setting off; off: close the camera (Decision 3) |
| Codex 3: the watch is per session, resets on any control change, posts stale results; 30 frames isn't 1 s at every rate | confirmed (`CameraSource.kt:198-203,420-424`) | one `TorchWatch` per activation, time based, checked against the newest watch on the camera thread |
| agy 4: another app taking the camera leaves the setting On | confirmed (`CameraSource.kt:262-285`, `WebcamController.kt:137`) | the open's `fail` turns it off; Pixel step 7 |
| agy 5: `c` is out of scope at `setRepeatingRequest`; imports | confirmed (`CameraSource.kt:214-218,233`) | `val c` hoisted; imports named |
| Codex 5: no `FLASH_STATE` weakens the approved outcome | confirmed | Decision 2 |
| Codex 6, agy 6.3: Android lifecycle untested; step 5 overstated | confirmed | `TorchWatchTest`, `WebcamSettingsTest`; step 5 says what no test reaches |
| Codex 7, agy 6.1: the QML test must open Settings | confirmed (`CameraCard.qml:244`) | `settingsOpen = true`; off, incoming state, `false` cap |
| agy 6.2, Codex 8: Gradle runs from `android/`; literal CLI commands | confirmed (`docs/development.md:90-93`) | fixed |
| agy 6.4: `modprobe` alone isn't enough on NixOS | confirmed (`setup.go:392`) | names `services.flux.webcam.enable` |
| agy 7, Codex 8: docs miss the camera-change note and Reset | confirmed | docs text extended |
| agy 8: caps send `false`, not absent; test gaps; KDoc and QML comment drift | confirmed | caps note, extra test cases, both comments |
