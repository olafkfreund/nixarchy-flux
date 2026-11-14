---
status: approved
issue: 11
spec: spec/2026-11-14-11-agent-questions.md
coders:
  1: coder   # 2 map lines and 3 Go test cases, all given in full below
  2: coder   # 3 list literals and their count tests, given in full; same agent keeps the key names in context
  3: coder   # a 2-line mapping and a 1-line hook in TerminalInput with a JVM test, given in full
  4: coder   # the Compose rows and choice resets are given in full; Codex and agy stay independent reviewers
  5: coder   # short sentence edits at 7 places in docs/herdr.md, given below; agy stays an independent reviewer
reviewers:
  - codex    # independent read of the full diff against this plan, strong on the Kotlin state and lock paths
  - agy      # independent read of the full diff; its absence doesn't block
---

# Plan: reach Codex's pending questions from the phone

Issue #11 (olafkfreund/nixarchy-flux; every `gh` command uses `-R olafkfreund/nixarchy-flux`). The intent
(`a56e2a6`) and the spec (`ee31da9`) are approved. This plan stands on its own. Work happens in the worktree
`/mnt/code/Source-home/GitHub/nixarchy-flux-11`, on the branch `feat/11-agent-questions`, which is already checked
out. Android paths are under `android/app/src/main/java/org/omarchy/flux/` (written `A/`), Android tests under
`android/app/src/test/java/org/omarchy/flux/` (written `T/`).

## The approved decisions

- **The keys.** The phone can send `shift+left` and `shift+right` (Codex: move to its pending questions and queued
  input, and back). Labels `⇧←` and `⇧→`; TalkBack reads "Shift Left" and "Shift Right".
- **Where they show.** Only on agents whose kind is `codex`, in Herdr and in the Herdr app, in the dock's key row
  (behind **Show keys** / **Keys**) and in the Live row. The row reads `esc tab ↑ ↓ ⇧← ⇧→ enter`. Other agents keep
  today's 5 keys. The Mac and iPhone show no new key.
- **Which lists.** fluxd's agent reply list and live input map get both keys; the phone's `HERDR_KEYS` and
  `HERDR_TERMINAL_INPUT_KEYS` and the Swift `allowedKeys` match (24 agent keys). The plain terminal list stays in all
  three places. `ctrl+c` stays out. Live bytes: `\x1b[1;2D` and `\x1b[1;2C`.
- **Herdr takes them.** Herdr 0.9.5's `agent.send_keys` and `pane.send_keys` share `encode_api_keys`; a scratch pane
  got `ESC [1;2D ESC [1;2C`. `hc keys` types the same. No Herdr or Codex change.
- **Hardware keyboard in Live.** Shift with Left or Right sends the shifted key. Shift with Up or Down stays as today.
- **Rows keep keys 48 dp wide, for every agent** (owner decision 1). A helper picks the fewest rows of even length
  in which every key is at least 48 dp wide, by the width the row gets. No row shows where none shows today.
- **Row heights stay** 40 dp in the dock and 44 dp in Live (owner decision 2).
- **Choices after a move.** A sent `⇧←` or `⇧→` turns the choice buttons off until the next completed read, as an
  answer does:
  - dock: inside the phone-lock guard, like `answer`, so a cancelled lock changes nothing;
  - Live: one `onMove` hook in `TerminalInput.send`, which covers the Live row and a hardware keyboard; it sets
    `answered` and calls `rereadSoon` (not for a sample computer).
  - `choicesOpen` doesn't change. The buttons can come back for a dialog that no longer has focus; the docs say so.
- **Not changed.** Herdr, Codex, packet formats, the desktop Qt app, plain terminal input and its key bar, `KeyBar`,
  `findChoices`, `findMenuChoices`, `choicesOpen`, when choice buttons show, `herdr_control`, the phone lock.
- **Known limits, in the docs.** The keys are plain key presses; a question answered on the desktop first can leave
  the focus elsewhere; the choices can come back for a dialog without focus; Enter on the question clears the
  unsent-text note; an older fluxd refuses the keys.
- **Version mix.** A new phone with an old fluxd shows the keys; fluxd refuses them (`The key "shift+left" is not
  allowed` in the dock, `fluxd does not know that key` in Live). No capability flag.

## Steps

The local host has no Android SDK and no Mac. Go tests run locally. CI (`build.yml`) is the first Android and Swift
compile (step 6). The lead commits each step after its Verify passes, one commit per step, citing the step
(`feat(herdr): step 1 …`, `(#11)`); each step's diff check is against the previous step's commit.

0. **Preflight** (lead). In the worktree: `git status --short` is clean, the branch is `feat/11-agent-questions`, and
   `go build ./...` passes. Record the set of failing `go test ./internal/core/` tests before any edit; some tests fail
   on this NixOS host for reasons outside this issue (fakes call `/usr/bin/cat`, umask).
   Traps: never `git switch` in the main checkout `/mnt/code/Source-home/GitHub/nixarchy-flux`.

1. **fluxd** (coder).
   - `internal/core/herdr_control.go:24`, in `herdrReplyKeys` (`:22-28`), after
     `"up": true, "down": true, "left": true, "right": true,` add:

     ```go
     	"shift+left": true, "shift+right": true,
     ```

   - `internal/core/herdr_terminal.go:527`, in `herdrTerminalInputKeys` (`:525-528`), after the arrows line add:

     ```go
     	"shift+left": "\x1b[1;2D", "shift+right": "\x1b[1;2C",
     ```

   - `herdrTerminalKeys` (`herdr_control.go:33-43`) and `herdrKeys` (`:86-113`) don't change.
   - Tests:
     - `internal/core/herdr_test.go`, `TestHerdrReplies` (`:1291`), after the `2 enter` check (`:1325-1331`):

       ```go
       	body = sentBody(t, d.herdrKeys(dev, "w1:p1", []string{"shift+left", "shift+right"}))
       	if body["error"] != nil || body["action"] != "keys" {
       		t.Errorf("shifted keys: %v", body)
       	}
       	if calls := f.takeCalls(); len(calls) != 1 || calls[0] != `agent.send_keys {"keys":["shift+left","shift+right"],"target":"w1:p1"}` {
       		t.Errorf("shifted keys calls %v", calls)
       	}
       ```

     - `TestHerdrInput` (`:990`), in `refused` (`:1002-1011`), after the `f1` case (`:1009`):

       ```go
       		{"shifted arrow", d.herdrInput(dev, "w1:p2", "", []string{"shift+left"}), `The key "shift+left" is not allowed`},
       ```

     - `internal/core/herdr_terminal_test.go`, `TestHerdrTerminalTypedInput` (`:1098`): add
       `{"kind": "terminal_input", "session": "ts1", "key": "shift+left"}` and the same with `shift+right` before the
       `enter` event (`:1111`), and `"\x1b[1;2D", "\x1b[1;2C"` before `"\r"` in `want` (`:1115`).

   Verify: `go test ./internal/core/ -run 'TestHerdrReplies|TestHerdrInput|TestHerdrTerminalTypedInput' -count=1`
   passes; `make vet` (go vet and gofmt) is clean.
   Traps: `ctrl+c` stays refused (`herdr_test.go:1309`). Tabs, not spaces, in Go. The plain terminal list must not
   get the keys; the new `TestHerdrInput` case proves it.

2. **Client key lists** (coder).
   - `A/core/HerdrModel.kt:708`, `HERDR_KEYS`: add `"shift+left", "shift+right"` after `"right"`.
   - `A/core/HerdrModel.kt:717`, `HERDR_TERMINAL_INPUT_KEYS`: add `"shift+left", "shift+right"` after `"right"`.
   - `HERDR_TERMINAL_KEYS` (`:712-713`) doesn't change.
   - `macos/Sources/FluxKit/Plugins/Herdr/HerdrWire.swift:327`, `allowedKeys`: add `"shift+left", "shift+right"`
     after `"right"`. `terminalKeys` (`:330-331`) doesn't change.
   - Tests:
     - `T/core/HerdrReplyTest.kt:199-206`, `keysMatchTheComputer`: add `"shift+left", "shift+right"` to the loop list,
       and `assertEquals(24, HERDR_KEYS.size)`. Add:

       ```kotlin
       for (k in listOf("shift+left", "shift+right")) {
           assertTrue(k, k in HERDR_TERMINAL_INPUT_KEYS)
           assertFalse(k, k in HERDR_TERMINAL_KEYS)
       }
       ```

       Add the imports of `HERDR_TERMINAL_INPUT_KEYS` and `HERDR_TERMINAL_KEYS` if the file lacks them (same package
       `org.omarchy.flux.core`, so none may be needed).
     - `macos/Tests/FluxKitTests/HerdrTests.swift:225-232`, `testKeysMatchTheComputer`: add both names to the loop
       list, count `24`, and `XCTAssertFalse(HerdrWire.terminalKeys.contains("shift+left"))` and the same for `"shift+right"`.

   Verify: `git diff --stat HEAD` (step 1 is committed) shows only these 4 files. Compile and tests run in CI (step 6).
   Traps: the phone drops live keys outside `HERDR_TERMINAL_INPUT_KEYS` (`A/core/Herdr.kt:314`) and dock keys outside
   `HERDR_KEYS` (`:536`) without an error, so step 4's keys do nothing without this step.

3. **Hardware keyboard and the move hook** (coder). `A/ui/HerdrTerminal.kt`.
   - `:159`, the KDoc of `terminalKeyName`: "Shift with Tab is Shift+Tab" → "Shift with Tab, Left or Right gives the
     shifted key."
   - `:169-170`:

     ```kotlin
         KeyEvent.KEYCODE_DPAD_LEFT -> if (shift) "shift+left" else "left"
         KeyEvent.KEYCODE_DPAD_RIGHT -> if (shift) "shift+right" else "right"
     ```

   - `TerminalInput` (`:271`): after `var onImage` (`:289`) add:

     ```kotlin
         /** Called after Shift+Left or Shift+Right went out, so the screen can drop choices that the move made stale. */
         var onMove: (() -> Unit)? = null
     ```

   - `send` (`:303-308`): after the `unsent` line (`:306`) add:

     ```kotlin
             if (sent && (name == "shift+left" || name == "shift+right")) onMove?.invoke()
     ```

   - Tests in `T/ui/HerdrTerminalFocusTest.kt`:
     - `theNamedKeysOfAKeyboard` (`:103-114`): add
       `assertEquals("right", terminalKeyName(KeyEvent.KEYCODE_DPAD_RIGHT, shift = false))`,
       `assertEquals("shift+left", terminalKeyName(KeyEvent.KEYCODE_DPAD_LEFT, shift = true))`,
       `assertEquals("shift+right", terminalKeyName(KeyEvent.KEYCODE_DPAD_RIGHT, shift = true))`,
       `assertEquals("up", terminalKeyName(KeyEvent.KEYCODE_DPAD_UP, shift = true))`.
     - New test:

       ```kotlin
       @Test
       fun aShiftedArrowThatWentOutCallsOnMove() {
           var moves = 0
           var accept = true
           val input = TerminalInput().apply {
               ready = true
               onKey = { accept }
               onMove = { moves++ }
           }
           assertTrue(input.send("shift+left"))
           assertTrue(input.send("shift+right"))
           assertTrue(input.send("left"))
           assertEquals(2, moves)
           accept = false
           assertFalse(input.send("shift+left"))
           assertEquals("a refused key is no move", 2, moves)
       }
       ```

   Verify: in CI (step 6), `HerdrTerminalFocusTest` passes.
   Traps: the hook goes in `send`, not in `key` or `pressKey`: the Live row (`key` → `press { send }`, `:335`) and a
   hardware keyboard (`handleKey` → `pressKey` → `press { i.send }`, `:481-487,503`) both reach `send`. `onMove` is
   called only when `onKey` returned true.

4. **The agent screen** (coder). `A/ui/AgentScreens.kt`.
   - **Keys** (`:1888-1899`). Keep `DockKeys`; change its KDoc to "The keys of the key rows of the dock and of Live".
     After it add:

     ```kotlin
     /** The keys of Codex that move to its pending questions and back. */
     private val CodexKeys = listOf(Triple("⇧←", "shift+left", 1f), Triple("⇧→", "shift+right", 1f))

     /** The keys of the key rows for the agent kind [agent]. Codex also gets [CodexKeys], before Enter. */
     internal fun dockKeys(agent: String): List<Triple<String, String, Float>> =
         if (agent == "codex") DockKeys.dropLast(1) + CodexKeys + DockKeys.last() else DockKeys

     /** True for the keys that move the focus of Codex. The choices on screen can then belong to another question. */
     internal fun movesFocus(key: String): Boolean = key == "shift+left" || key == "shift+right"
     ```

     `keyName` (`:1892`): `private` → `internal`, and add `"shift+left" -> "Shift Left"` and
     `"shift+right" -> "Shift Right"` before `else`.
   - **Rows**. After `KeyMinWidth` (`:2078`) add:

     ```kotlin
     /** True when keys of [shares] fit 1 row of [width] at [KeyMinWidth] each, with [gap] between them. */
     internal fun keysFitOneRow(shares: List<Float>, width: Dp, gap: Dp): Boolean =
         KeyMinWidth * shares.sum() + gap * (shares.size - 1) <= width

     /** The fewest rows of even length in which keys of [shares] keep [KeyMinWidth]. A key gets a row of its own at worst. */
     internal fun keyRows(shares: List<Float>, width: Dp, gap: Dp): List<List<Int>> =
         (1..shares.size).firstNotNullOfOrNull { n ->
             shares.indices.chunked((shares.size + n - 1) / n)
                 .takeIf { rows -> rows.all { r -> keysFitOneRow(r.map { shares[it] }, width, gap) } }
         } ?: shares.indices.map { listOf(it) }

     /** Lays out keys of [shares] in the rows of [keyRows], each [height] tall. [draw] draws key i with its modifier. */
     @Composable
     private fun SmallKeyRows(shares: List<Float>, height: Dp, draw: @Composable (Int, Modifier) -> Unit) {
         BoxWithConstraints(Modifier.fillMaxWidth()) {
             Column(verticalArrangement = Arrangement.spacedBy(SmallKeyGap)) {
                 for (r in keyRows(shares, maxWidth, SmallKeyGap)) {
                     Row(Modifier.fillMaxWidth().height(height), horizontalArrangement = Arrangement.spacedBy(SmallKeyGap)) {
                         for (i in r) draw(i, Modifier.weight(shares[i]))
                     }
                 }
             }
         }
     }
     ```

   - **Dock** (`AgentDock`, `:1657`). After `answer` (`:1699-1703`) add:

     ```kotlin
         // A move to another question of Codex also makes the choices on screen stale.
         fun move(key: String) = guarded {
             answered = out
             HerdrSync.sendKeys(FluxCore, d.id, agent.pane, listOf(key))
         }
     ```

     Replace the key row (`:1800-1806`):

     ```kotlin
             if (keysOpen) {
                 val row = dockKeys(agent.agent)
                 SmallKeyRows(row.map { it.third }, 40.dp) { i, m ->
                     val (label, key, _) = row[i]
                     SmallKey(label, keyName(key), m, fill = Tn.bg, weight = FontWeight.Medium) {
                         if (movesFocus(key)) move(key) else keys(key)
                     }
                 }
             }
     ```

   - **Live** (`LiveControls`, `:2022`). KDoc (`:2013-2016`): "1 row of keys" → "rows of keys", and the key list
     "Esc, Tab, Up, Down, Enter" → "Esc, Tab, Up, Down, Shift+Left and Shift+Right for Codex, Enter". After `answered`
     (`:2024`) add:

     ```kotlin
         // A sent Shift+Left or Shift+Right, from the row or a keyboard, moves Codex to another question.
         SideEffect {
             input.onMove = {
                 answered = out
                 if (!isDemo(d.id)) HerdrSync.rereadSoon(FluxCore, d.id, agent.pane)
             }
         }
         DisposableEffect(input) { onDispose { input.onMove = null } }
     ```

     Replace the key `Row` (`:2037-2058`, its closing brace included) with:

     ```kotlin
             // A choice of 2 digits would go as 2 keys, and the first key can select another choice.
             val digits = choices.filter { it.key.length == 1 && it.keys.isNotEmpty() }
             val row = dockKeys(agent.agent)
             SmallKeyRows(digits.map { 1f } + row.map { it.third } + 1f, 44.dp) { i, m ->
                 when {
                     i < digits.size -> {
                         val c = digits[i]
                         SmallKey(c.key, "Answer ${c.key}: ${c.label}", m, accent = true, enabled = open) {
                             // A digit goes as typed text. The arrows and Enter of a menu go as keys.
                             val sent = if (c.keys == listOf(c.key)) input.choose(c.key) else c.keys.all { input.key(it) }
                             if (sent) {
                                 answered = out
                                 // A typed digit gets no sent answer, so the screen reads the next dialog itself.
                                 if (!isDemo(d.id)) HerdrSync.rereadSoon(FluxCore, d.id, agent.pane)
                             }
                         }
                     }
                     i < digits.size + row.size -> {
                         val (label, key, _) = row[i - digits.size]
                         SmallKey(label, keyName(key), m, enabled = on) { input.key(key) }
                     }
                     else -> {
                         val shape = RoundedCornerShape(8.dp)
                         Box(
                             m.fillMaxHeight().alpha(if (on) 1f else DimAlpha).clip(shape).background(Tn.tile).border(1.dp, Tn.line, shape)
                                 .clickable(enabled = on, onClickLabel = "Show the keyboard", role = Role.Button) { input.showKeyboard() }
                                 .clearAndSetSemantics { contentDescription = "Show the keyboard" },
                             contentAlignment = Alignment.Center,
                         ) { Sym(Ic.keyboard, tint = Tn.sub, size = 20.dp) }
                     }
                 }
             }
     ```

     The Live row's `⇧←` needs no own reset: `input.key` reaches `send`, which calls `onMove`.
   - Tests, new file `T/ui/AgentKeysTest.kt` (package `org.omarchy.flux.ui`):
     - `dockKeys("codex")` keys are `esc tab up down shift+left shift+right enter`; `dockKeys("claude")` and
       `dockKeys("opencode")` are `esc tab up down enter`.
     - `keyName("shift+left") == "Shift Left"`, `keyName("shift+right") == "Shift Right"`.
     - `movesFocus` is true for both shifted keys, false for `left`, `right`, `tab`.
     - `keyRows` with gap `6.dp`, for each row below at content widths `296.dp`, `336.dp` and `387.dp` (dock: 320, 360,
       411 minus 24) and `300.dp`, `340.dp`, `391.dp` (Live: minus 20): every row fits
       (`keysFitOneRow`), all indices appear once in order, and, when the expected count is above 1, the even split into
       one row fewer doesn't fit. Expected row counts:

       | Shares | Content 296/300 | 336/340 | 387/391 |
       | --- | --- | --- | --- |
       | dock other `[1,1,1,1,1.5]` | 1 | 1 | 1 |
       | dock Codex `[1×6,1.5]` | 2 | 2 | 2 |
       | Live other `[1×4,1.5,1]` | 2 | 2 | 1 |
       | Live Codex `[1×6,1.5,1]` | 2 | 2 | 2 |
       | Live 3 choices other `[1×7,1.5,1]` | 2 | 2 | 2 |
       | Live 3 choices Codex `[1×9,1.5,1]` | 3 | 2 | 2 |
       | Live 9 choices Codex `[1×15,1.5,1]` | 4 | 3 | 3 |

     - `keyRows(emptyList(), …)` is empty; `keyRows(listOf(1.5f), 40.dp, 6.dp)` is `[[0]]`.

   Verify: in CI (step 6), `AgentKeysTest` passes, `lintDebug` is clean and both builds compile.
   Traps:
   - Step 4 needs step 3 (`TerminalInput.onMove`) to compile, and step 2 for the keys to reach fluxd.
   - `keys` is already a function in `AgentDock` (`:1694`); the row list is named `row`.
   - `answered` is declared at `:1696`, after `keys`; `move` goes after it.
   - `SmallKey` applies `fillMaxHeight` itself (`ui/AgentThreadUi.kt:723-724`); pass only the weight modifier `m`.
   - `SideEffect` and `DisposableEffect` are already imported (used at `:1405,1416`). `BoxWithConstraints`, `Dp`,
     `Column` and `Row` are too (`:15-19,96`). No `RowScope` import is needed. The lambda is named `draw`, because `androidx.compose.runtime.key` is imported (`:48`).
   - `LiveTerminalView` (`:1375`) clears `onKey` and the other senders in its own `DisposableEffect` (`:1416-1424`); `LiveControls`
     clears only `onMove`.
   - Keep `KeyBar` (`:2087-2106`) and its 2-row rule as they are.
   - Keep the order of the Live row: digits, the key row, the keyboard key.

5. **Docs** (coder). `docs/herdr.md`, one sentence per line, in the file's plain style:
   - `:177`: "…Esc, Tab, Up, Down, Enter, and a keyboard key." → "…Esc, Tab, Up, Down, Enter, and a keyboard key. A
     Codex agent also gets ⇧← and ⇧→ before Enter. When the keys do not fit 1 row at 48 dp each, they take 2 or more
     rows."
   - `:198-199`: Live also reads the output after ⇧← or ⇧→, and the choices take no tap until that read.
   - `:225`: "…Esc, Backspace, and the arrows as keys." → add "Shift with Left or Right sends Shift+Left or
     Shift+Right."
   - `:359`: add "On a Codex agent, it also sends Shift+Left and Shift+Right (⇧← and ⇧→), which move to the pending
     questions and queued input of Codex and back. The Mac and the iPhone do not show them."
   - `:370`: add `shift+left`, `shift+right` after `right` in the list.
   - After `:373` (the end of that paragraph, before `## Start an agent`), a new paragraph: the keys are plain key presses. When the desktop answers or cancels a question
     first, ⇧← can land on queued input, and the next Enter acts there. The choices can come back for a dialog that no
     longer has focus, because Flux cannot tell which question has focus. Enter on the question clears the note about
     unsent text, although the text still waits. An older `fluxd` refuses the keys.
   - `:810`: ``…`left`, or `right`.`` → ``…`left`, `right`, `shift+left`, or `shift+right`.``
   - `:448`, the terminal list, doesn't change.

   Verify: `git diff docs/herdr.md` touches only these places. `grep -n "shift+left" docs/herdr.md` matches the
   `fluxd accepts these keys only:` line, the `` `key` is `enter` `` line and the new text, never the
   `fluxd accepts these keys for a terminal:` line. Line numbers shift as lines are added; match by text.
   Traps: house style is short plain sentences, one per line, with no contractions ("do not", "cannot"). No other
   section changes.

6. **Full check** (lead).

   ```sh
   make vet
   go test -race ./...
   nix flake check path:.
   git status --short                           # clean: steps 1-5 are committed
   git push -u origin feat/11-agent-questions   # only after the owner's OK to push
   gh workflow run build.yml -R olafkfreund/nixarchy-flux --ref feat/11-agent-questions
   gh run list -R olafkfreund/nixarchy-flux --branch feat/11-agent-questions --limit 1 --json headSha,status,conclusion
   ```

   The run's `headSha` equals `git rev-parse HEAD`.

   `go test` failures equal the set recorded in step 0. CI runs `./gradlew :app:testDebugUnitTest :app:lintDebug
   :app:assembleDebug :app:assembleRelease`, `make test-macos` and `make test-ios` (`docs/development.md:181`). Fix
   compile errors in CI and record any deviation here in the same commit.
   Traps: CI is the first Android, macOS and iOS compile. A provider or CI job that fails is reported verbatim.

7. **Device test** (lead; **optional and skippable**). The owner deferred the spec's device checks (Verification 3) on
   2026-11-14, relayed by the lead with the plan request. If skipped, the PR says so and lists them as open.
   - fluxd: p620 runs the real `fluxd.service` (Home Manager user unit; `flux-cli` is
     `/etc/profiles/per-user/olafkfreund/bin/flux-cli`). The spec says agents do not restart it and the owner
     installs the build. So the owner stops the unit, runs the branch-built fluxd with the same config, and starts
     the unit again. If the owner tells the lead to do it instead, the lead announces on the agent bus first, and
     the PR records it as a deviation from the spec.
   - Before the new fluxd runs, one `⇧←` from the new app against the old fluxd shows
     `The key "shift+left" is not allowed` (spec Risks, old fluxd).
   - App: the debug APK from CI can't install over the old one (another debug key). Ask the owner, then uninstall,
     install, and pair again, on the Pixel 11 Pro XL and the Galaxy Tab S8 Ultra (serials from `adb devices`).
   - Checks (spec Verification 3): Codex with a pending question: **Show keys**, `⇧←`, answer with the arrows and
     Enter, `⇧→`; the same in Live and with a hardware keyboard; once in the Herdr app; a Claude agent shows 5 keys;
     largest font size: no key under 48 dp, labels fit, `⇧` renders (else labels `S-←` / `S-→`, a deviation);
     TalkBack names; on a blocked Codex, `⇧←` turns the choices off until the next read from the key row, the Live
     row and a keyboard; a cancelled phone lock on `⇧←` leaves them on; the tablet's dock in the side column wraps by
     its width.
   Traps: never send keys to an existing pane or agent other than the test Codex. Restart `fluxd.service` even when a
   check fails.

8. **Review and PR** (lead). Commit any open fixes first. An Opus review given only this plan and `git diff master...`, plus the Codex and agy
   reviewers. Fix findings; record deviations here in the same commit. Commit, then open the PR against `master`
   linking intent, spec and plan, saying which steps the coder did, and that device tests were skipped if
   step 7 was skipped. Merge and close #11 only after the owner's OK.

## Tests

| Command | Expected |
| --- | --- |
| `go test -race ./...` | the same failures as step 0, none new; the new cases pass |
| `make vet` | clean: `go vet` and `gofmt -l` |
| `nix flake check path:.` | passes |
| CI `build.yml`: Android unit tests, lint, debug and release builds | pass, including `AgentKeysTest`, `HerdrReplyTest`, `HerdrTerminalFocusTest` |
| CI `build.yml`: `make test-macos`, `make test-ios` | pass, `HerdrTests` count 24 |
| Step 7 (optional) | the checks listed there |

## Rollback

Revert the PR. The key names are strings in existing packets. An older phone never sends the new keys. A newer phone
against a reverted fluxd shows the keys, and fluxd refuses them with a clear message; nothing reaches the agent.

## Review changes

Codex and agy reviewed the first draft of this plan, read only. Each finding was checked against the code. Both
found no compile error in the given code and confirmed the `keyRows` counts.

| Finding | Source | Verdict | Change |
| --- | --- | --- | --- |
| CI pushed before any commit; step diffs mix steps | Codex (high, low), agy | Confirmed: commits only in step 8 | One commit per step after its Verify; step 6 checks a clean tree and the run's `headSha` |
| Device checks made optional without a record | Codex | Confirmed | Step 7 cites the owner's deferral; the PR lists the checks as open |
| Step 7 lets the lead restart `fluxd.service`; spec says the owner does | agy | Confirmed, spec Verification 3 | The owner stops and starts the unit; the lead only on the owner's word, as a deviation |
| Step 7 drops the spec's old-fluxd device check | agy | Refuted: the spec has no such check (`spec:347` is the cancelled lock) | Added anyway as a free check before the new fluxd runs |
| "One row fewer" breaks for an expected count of 1 | agy | Confirmed: `chunked(0)` throws | Only checked when the count is above 1 |
| Live row replacement `:2037-2057` leaves a stray brace | agy | Confirmed, `AgentScreens.kt:2058` | `:2037-2058` |
| Step 4 needs step 3 to compile | agy | Confirmed, `onMove` | Trap added |
| Layout test misses "Live, 3 choices, other agents" | Codex | Confirmed, spec table | Row added, 2/2/2 |
| `keys` `:1694`, `answered` `:1696`; senders cleared in `LiveTerminalView`, not `LivePane` | Codex, agy | Confirmed, `AgentScreens.kt:1375,1694,1696` | Cites fixed |
| Lambda named `key` shadows the imported `androidx.compose.runtime.key` | Codex, agy | Valid Kotlin, but confusing | Renamed `draw` (done before the reviews finished) |
| agy on step 5 and as an independent reviewer | Codex, agy | Agreed | Step 5 goes to coder |
| Docs: contractions, `:810` "or" list, pre-edit line numbers in Verify | agy | Confirmed: `docs/herdr.md` has no contractions | Text fixed; Verify matches by text |
| No gofmt check | agy | Confirmed, `Makefile:59-61` | `make vet` in steps 1 and 6 |
| Off-by-one ranges; Swift checks only `shift+left` against the terminal list | agy (low) | Confirmed | Ranges fixed; `shift+right` added |
| Compile, CI, devices, Codex at runtime | both | Unverified | Steps 6 and 7 |

## Corrections

| # | Step | What changed and why |
| --- | --- | --- |
