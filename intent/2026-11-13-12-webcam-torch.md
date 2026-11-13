---
status: draft
issue: 12
author: olafkfreund
---

# Intent: Turn on the phone's flashlight while it is the webcam

## Problem

When the phone is the webcam, there is no way to turn on its flashlight.
- The issue (mirrored from upstream `bjarneo/flux#128`) asks for "a
  flashlight toggle feature in the webcam settings for some of us with bad
  lighting".
- Exposure and brightness can't add light that isn't there. In a dark room
  the image stays noisy.
- While Flux has the camera open, Android's own flashlight tile can't turn
  the flash on: the open camera owns it.

### Facts

Android paths are under `android/app/src/main/java/org/omarchy/flux/webcam/`.

- **The settings** are `WebcamConfig` (`WebcamConfig.kt:29-41`): aspect,
  resolution, camera, mirror, zoom, exposure, white balance, and 4 image
  values. There is no flash or torch field.
  - `merged` reads only the keys it names (`WebcamConfig.kt:50-65`), so an
    unknown key from the computer is ignored.
  - `clamped` fits each field to `WebcamCaps` (`WebcamConfig.kt:68-92`).
  - `reset` keeps aspect, resolution and camera and sets the rest to neutral
    (`WebcamConfig.kt:95-98`). The phone's "Reset image" button
    (`WebcamPanel.kt:381`) and the computer's reset (`WebcamProtocol.kt:59`,
    `WebcamSettings.kt:51`) use it.
  - The same `toJson` is the wire format (`WebcamProtocol.kt:29-31`) and the
    saved format (`WebcamSettings.kt:46`).
- **What the camera supports** is `WebcamCaps` (`WebcamConfig.kt:119-128`).
  Nothing says whether the camera has a flash. Before the camera opens, the
  caps are the loose `LOOSE` limits (`WebcamConfig.kt:131-134`).
- **The settings live for the whole app process.** `WebcamSettings` is an
  `object`; `load` reads the saved settings once (`WebcamSettings.kt:29-37`),
  and each change stays in memory and is written to `SharedPreferences`
  (`WebcamSettings.kt:40-48`). So a torch value that is only kept out of
  `SharedPreferences` still comes back on the next stream, after a pause,
  and when the Webcam screen opens again, until the app process ends.
- **A remote change is clamped to the caps of the camera that is open now**
  (`WebcamSettings.kt:43`). `camera=back torch=true` sent while the front
  camera is open would lose `torch`, as zoom and exposure do today.
- **The camera** is Camera2 (`CameraSource.kt`).
  - Zoom, exposure and white balance are `Controls` (`CameraSource.kt:51`).
    `setControls` sends the repeating request again without a new session
    (`CameraSource.kt:198-203,206-238`). That is the place for
    `FLASH_MODE_TORCH`.
  - The request uses `TEMPLATE_RECORD` and never sets `CONTROL_AE_MODE`
    (`CameraSource.kt:212-220`). Camera2 lights the torch only with AE mode
    `ON` or `OFF`, so the spec sets it.
  - A failed request after the first one is only logged
    (`CameraSource.kt:198-202,234-236`), and there is no capture callback
    (`CameraSource.kt:233`). The phone can't tell today whether the flash
    really lit.
  - Lenses: `lenses()` lists each camera and the physical lenses inside a
    logical one (`CameraSource.kt:95-114`); `nameLenses` names them `back`,
    `back 0.6x`, `back 3x`, `front` (`CameraLenses.kt:32`). A physical lens
    streams through its logical camera (`CameraSource.kt:321`). Most request
    keys go to the logical camera (`CameraSource.kt:153-156`); only the crop
    can go to the lens (`CameraSource.kt:226`).
  - A lens that can't stream alone falls back to its logical camera
    (`CameraSource.kt:303-308,348-354`). The controller has already set the
    caps by then (`WebcamController.kt:118-128`) and isn't told.
- **The controller** (`WebcamController.kt`) builds the caps from the chosen
  camera (`:107-140`) and the controls from the settings (`:142-149`). A
  camera switch opens a new camera (`:78-84`). A frame size change restarts
  the stream (`:86-88`, `WebcamSession.kt:135-139`).
- **Camera lifetime.**
  - When a stream stops, only the encoder goes (`WebcamController.kt:190-195`,
    `WebcamSession.kt:201-220`). With the Webcam screen open, the preview
    camera keeps running, and so would the light.
  - `WebcamHost` owns the one controller. It calls `pause` when the app
    goes to the background without a stream (`WebcamHost.kt:91,113`) and
    `release` when no screen and no stream need it (`WebcamHost.kt:122`).
    `pause` closes the camera (`WebcamController.kt:152-160`); `resume`
    opens it again with the same settings (`WebcamController.kt:100-105`).
  - With a stream, the camera keeps running with the screen locked
    (`docs/camera.md:62`).
- **The phone's settings screen** is `WebcamSettingsPanel`
  (`WebcamPanel.kt:301-383`). The Mirror switch (`WebcamPanel.kt:338-347`,
  a title plus one line of help) is the pattern for a torch switch. Flash
  icons exist (`Ic.flashAuto` and others, `camera/PhotoMode.kt:240-241`).
- **Other flash code.** The photo mode has a CameraX photo flash, shown
  only with a flash unit (`camera/PhotoMode.kt:77,89,180-181`). There is no
  torch anywhere in the Android app.
- **fluxd** filters what the phone reports through typed structs
  (`internal/core/webcam.go:117-141`, `cleanWebcamConfig` at `:146-197`).
  - An unknown key from the phone is dropped, not refused. So an older fluxd
    drops `torch` from a newer phone, and the desktop never sees it.
  - `ConfigureWebcam` refuses unknown keys from the desktop
    (`webcam.go:410-414`, `DisallowUnknownFields`) and refuses an empty
    result (`webcam.go:419-421`). So `torch` must be a `*bool` like
    `Mirror` (`webcam.go:121`): a plain `bool` with `omitempty` drops
    `false`, and "turn off" fails as `bad_params`.
  - fluxd forgets the settings when the stream stops (`webcam.go:235,352,368`).
  - Tests: `TestWebcamSettingsBelongToTheSession`, `TestCleanWebcamConfig`
    and `TestConfigureWebcamChecksSettings` (`internal/core/session_test.go:485,516,551`).
- **The CLI** checks keys against `webcamKeys` (`cmd/flux/main.go:1035-1037`,
  used at `:1057`). `flux-cli webcam set torch=true` fails until the key is
  added. Booleans already parse (`main.go:1061-1062`). `flux-cli webcam`
  prints every config key it gets (`main.go:1020-1031`).
- **The desktop card** is `gui/qml/components/CameraCard.qml`. It reads
  `config` and `caps` from the state (`CameraCard.qml:28-29`) and has a
  "Mirror the image" toggle (`CameraCard.qml:311-315`) to copy.
- **Other webcam senders.** The iPhone and the Mac can also be the webcam
  (`docs/camera.md:47`, `macos/Sources/FluxKit/Plugins/Stream/WebcamConfig.swift`).
  Their `merged` reads only the keys it names (`WebcamConfig.swift:33-50`),
  so they ignore `torch`. The iPhone has a torch only in its scan camera
  (`ios/App/Features/Camera/CameraController.swift:28-29,76-84`).
- **The docs** list the settings in `docs/camera.md:77-89`, say that the
  phone saves them for the next stream (`docs/camera.md:92`), and say that
  fluxd sends only the keys in the table (`docs/camera.md:97`).
  `docs/ipc.md:220` doesn't list the keys, so it needs no change.

## Proposed outcome

- The phone's webcam settings have a **Flashlight** switch, next to Mirror.
  - It shows only when the open camera can use its flash. On most phones
    that is the back camera, not the front one.
  - On lights the flash steadily while the camera runs, in the preview and
    in the stream. Off turns it off.
  - Switching to a camera without a flash turns it off and hides the
    switch. Switching back leaves it off.
  - If the phone can't light the flash, the switch goes back to off rather
    than showing On with no light (open question 5).
- The desktop's **Phone camera** card has the same toggle, shown only when
  the phone reports a flash. `flux-cli webcam set torch=true|false` works.
- The light is never on by itself:
  - It is off when the camera opens: a new Webcam screen, the app back from
    the background, another app giving the camera back.
  - It goes off when the stream stops, when the camera closes, and with
    Reset.
  - It stays on when **Start webcam** starts a stream from a lit preview,
    and when a format or resolution change restarts the stream (open
    question 1).
- Older peers keep working:
  - New phone, older fluxd: the light works from the phone; the desktop
    shows no toggle.
  - Older phone, new fluxd and desktop: no toggle, because the phone
    reports no flash.
  - A new desktop card with an older fluxd shows no toggle, because the
    older fluxd drops the flash from the caps.
- `docs/camera.md` lists `torch`, says it is not saved, and says it works
  only on Flux for Android.
- Tests cover the config and caps field, the clamp to a camera without a
  flash, that the saved JSON has no torch, explicit `false` through fluxd,
  and the CLI key.

## Affected users and systems

- Android users who use the phone as a webcam, and the Linux desktop that
  shows it.
- Android: `WebcamConfig.kt` (field, caps, clamp, reset, JSON),
  `WebcamSettings.kt` (not saved; off on camera open), `CameraSource.kt`
  (flash info, AE mode, the request), `WebcamController.kt` (caps,
  controls, stream end), possibly `WebcamHost.kt` (pause and release),
  `WebcamPanel.kt` (switch), and `WebcamConfigTest.kt`.
- fluxd: `internal/core/webcam.go` (config and caps structs) and its tests
  in `session_test.go`.
- CLI: `cmd/flux/main.go` (`webcamKeys`).
- Qt: `gui/qml/components/CameraCard.qml`.
- Docs: `docs/camera.md`.
- Not affected: the iPhone and Mac webcam senders and the Windows client;
  they ignore the key. No new packet type.

## Constraints

- The field is additive. Older phones don't send it, an older fluxd drops
  it, and older apps ignore it.
- fluxd, the CLI and the Qt card change in the same release.
- The torch is never saved and never comes on by itself.
- The flash is set only on a camera that reports one, checked against the
  camera that is actually streaming, also after the logical fallback.
- No new permission. The camera permission covers the flash.
- Keep shared QML in `gui/qml/` working on both desktop hosts (AGENTS.md).
- Text follows the house style of the screen it is on: a title plus help on
  the phone, a short phrase on the desktop card.

## Open questions

1. **When does the light go off?** Proposed: it is session state, never
   saved. It stays on while the same camera runs, including Start webcam
   from a lit preview and a frame size restart. It goes off when the
   stream stops, when the camera closes or opens, with Reset, and on a
   camera without a flash. A light that comes back on by itself in a later
   call would be a surprise, and heat and battery matter on long sessions.
2. **Key name.** Proposed: `torch`, as in Camera2 `FLASH_MODE_TORCH` and the
   iPhone's `hasTorch`. The UI says "Flashlight", the issue's word.
3. **How the phone knows a camera has a flash.** Proposed: a caps field
   `torch: true` when the camera that streams can use the flash; absent
   means no. The spec settles, on the Pixel, whether that comes from the
   logical camera or the lens and how the 0.6x and 3x lenses behave. The
   intent only promises: no switch where the flash can't light.
4. **Steady light only, or a brightness level?** Proposed: on or off only.
   A level inside an open camera needs Android 15 and a phone that supports
   it. It can come later if asked for.
5. **Failure and overheating.** Android can refuse the torch or stop it when
   the phone is hot. Proposed: the spec reads the result of the request
   (`FLASH_STATE` on a capture callback) and turns the setting off when the
   flash doesn't light, so both sides show off. Not `CameraManager`'s torch
   callback: it reports the torch unavailable whenever a camera is open.
6. **iPhone webcam torch.** The iPhone can also be the webcam and has a
   torch. Proposed: not in this issue; the iPhone ignores the key, and the
   docs say Android only. A separate issue if asked for.
7. **Test:** unit tests in `WebcamConfigTest.kt` (with the saved JSON built
   by a pure function, so it is testable without `SharedPreferences`),
   `session_test.go`, and the CLI. Then on the Pixel with the #13 test setup:
   1. Back camera: the switch on the phone and the desktop turns the light
      on and off; the other side follows.
   2. Switch to the front camera: the light goes off and the switch hides.
      Back again: off.
   3. Each back lens (0.6x, 1x, 3x): the switch shows and lights, or hides.
   4. Light the preview, press Start webcam: it stays on. Change the
      resolution: it stays on. Stop: it goes off.
   5. Leave and reopen the Webcam screen, and send the app to the
      background and back: the light starts off.
   6. Lock the phone while live: the light stays on with the stream.

## Review changes

Codex and agy reviewed the first draft, read only. Each finding was checked
against the code.

| Finding | Verdict | Change |
| --- | --- | --- |
| Codex 1, agy H1: not saving the torch doesn't turn it off; settings live in memory, and a stream stop keeps the preview camera | confirmed (`WebcamSettings.kt:29-48`, `WebcamController.kt:190-195`) | new fact; outcome lists when the light goes off; open question 1 rewritten |
| agy M1: a reset at stream start would turn off a lit preview, and a frame size restart is a new start | confirmed (`WebcamSession.kt:135-139`) | stays on for Start and restarts |
| Codex 4, agy H2: logical vs lens flash; the fallback doesn't update the caps | confirmed (`CameraSource.kt:303-308,321`, `WebcamController.kt:118-128`) | facts; constraint checks the streaming camera; open question 3 leaves the source to the spec |
| Codex 2: a failed request is only logged; the UI can show On with no light | confirmed (`CameraSource.kt:198-202,234-236`) | outcome: the switch goes back to off; open question 5 |
| Codex 3, agy: AE mode is never set | confirmed (`CameraSource.kt:212-220`) | fact; the spec sets it |
| agy M2: `TorchCallback` reports unavailable whenever a camera is open | plausible (Android API, not checked on a device) | open question 5 names `FLASH_STATE` instead |
| agy M5: torch strength in an open camera needs Android 15, not 13 | confirmed (Android API reference) | open question 4 corrected |
| Codex 5, agy L1: the compatibility line had old and new reversed | confirmed | constraint and outcome rewritten, with each combination |
| agy scope: `torch` must be `*bool`, or `false` fails | confirmed (`webcam.go:121,419-421`) | fact |
| agy L3: `camera=back torch=true` from the front camera loses `torch` | confirmed (`WebcamSettings.kt:43`); same as zoom today | fact; for the spec |
| Codex 6, agy M3: tests don't prove the lifecycle; "not saved" can't be unit tested as is | confirmed (`WebcamSettings.kt` needs a `Context`) | tests and device steps 4-5 added |
| agy M4: `WebcamHost` missing; the docs say settings are saved | confirmed (`WebcamHost.kt:91,113,122`, `camera.md:92`) | affected list and docs outcome |
| Codex 8, agy: the iPhone is a webcam sender with a torch | confirmed (`camera.md:47`) | open question 6, Android only |
| agy L2: line ranges for lenses, `pause`, tests | confirmed | citations fixed |
| agy L4: label style differs between phone and desktop | confirmed (`WebcamPanel.kt:343-344`, `CameraCard.qml:312`) | constraint |
| Draft: "no torch or flash code in the Android app" | refuted (`camera/PhotoMode.kt:77-181` has a photo flash) | fact corrected |
| Codex 7: add a `docs/ipc.md` reference | not needed: it lists no keys (`ipc.md:220`) | none |
