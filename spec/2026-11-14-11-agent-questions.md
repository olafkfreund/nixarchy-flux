---
status: approved
issue: 11
intent: intent/2026-11-14-11-agent-questions.md
---

# Spec: Reach Codex's pending questions from the phone

The owner approved the intent with its proposed answers:
- structured pending requests wait for a new issue;
- `⇧←` and `⇧→` show for Codex agents only; fluxd allows them for all;
- agent replies and live input get the keys; plain terminal input doesn't;
- choice buttons stay for blocked agents only;
- Claude's dialogs are out of scope;
- the Herdr key check first, then unit tests, then the Pixel.

Android paths are under `android/app/src/main/java/org/omarchy/flux/`,
Android tests under `android/app/src/test/java/org/omarchy/flux/`.

## Herdr check (intent Q6.1)

Done before this spec, with the installed Herdr 0.9.5. The owner's Herdr
session and fluxd weren't touched.

- On a separate Herdr server with its own config folder and socket,
  `herdr pane send-keys <pane> shift+left shift+right` was accepted and
  the pane read `033 [ 1 ; 2 D 033 [ 1 ; 2 C` (`od -c`). The server and
  its pane were closed and deleted afterwards.
- `herdr pane send-keys <pane> bogus+key` was refused:
  `{"code":"invalid_key","message":"unsupported key bogus+key"}`. So Herdr
  checks key names.
- `agent send-keys` (`agent.send_keys`, the call fluxd uses,
  `internal/herdr/herdr.go:377-378`) needs a detected agent, so the
  scratch pane got `agent_not_found` first. The binary settles it
  instead: `handle_agent_send_keys` and `handle_pane_send_keys` both call
  the same `herdr::app::api_helpers::encode_api_keys`, and then
  `try_write_user_input` (`objdump -d` of
  `herdr-0.9.5/bin/herdr`). `encode_api_keys` parses each name with
  `parse_key_combo` and encodes it with
  `PaneTerminal::encode_terminal_key`. So the agent call takes the same
  names and writes the same bytes as the pane call.
- `hc keys` types the same bytes (checked for the intent).

So the design keeps the full scope: the dock for Herdr and Herdr app
agents, and Live. Verification 3.1 still runs it on a real Codex.

## Design

### 1. fluxd (`internal/core/`)

`herdrReplyKeys` (`herdr_control.go:22-28`), the agent list, gets 2 names:

```go
	"up": true, "down": true, "left": true, "right": true,
	"shift+left": true, "shift+right": true,
```

`herdrTerminalInputKeys` (`herdr_terminal.go:525-528`), the live map,
gets 2 entries, the xterm codes for Shift with an arrow:

```go
	"up": "\x1b[A", "down": "\x1b[B", "left": "\x1b[D", "right": "\x1b[C",
	"shift+left": "\x1b[1;2D", "shift+right": "\x1b[1;2C",
```

- `herdrTerminalKeys` (`herdr_control.go:33-43`), plain terminal input,
  doesn't change.
- `herdrKeys` (`herdr_control.go:86-113`) checks, sends and logs as
  before; only the list grows. `ctrl+c` stays out.
- No packet changes: key names are strings already.

### 2. The key lists of the clients

- `core/HerdrModel.kt:708`: `HERDR_KEYS` gets `"shift+left", "shift+right"`
  (24 names).
- `core/HerdrModel.kt:716-717`: `HERDR_TERMINAL_INPUT_KEYS` gets the same 2.
- `HERDR_TERMINAL_KEYS` (`HerdrModel.kt:712`) doesn't change.
- `macos/Sources/FluxKit/Plugins/Herdr/HerdrWire.swift:327`: `allowedKeys`
  gets the same 2 (24 names). `terminalKeys` (`:330`) doesn't change. The
  Apple apps show no new key; their list only has to match fluxd.

### 3. A hardware keyboard in Live (`ui/HerdrTerminal.kt:162-172`)

```kotlin
    KeyEvent.KEYCODE_DPAD_LEFT -> if (shift) "shift+left" else "left"
    KeyEvent.KEYCODE_DPAD_RIGHT -> if (shift) "shift+right" else "right"
```

Shift with Up or Down stays `up` and `down`, as today. The KDoc of
`terminalKeyName` (`HerdrTerminal.kt:159`) names Shift with Left and
Right too.

### 4. The keys on the agent screen (`ui/AgentScreens.kt`)

**Which keys.** Next to `DockKeys` (`AgentScreens.kt:1889`):

```kotlin
/** The keys of Codex that move to its pending questions and back. */
private val CodexKeys = listOf(Triple("⇧←", "shift+left", 1f), Triple("⇧→", "shift+right", 1f))

/** The keys of the key rows for the agent kind [agent]. Codex also gets [CodexKeys], before Enter. */
internal fun dockKeys(agent: String): List<Triple<String, String, Float>> =
    if (agent == "codex") DockKeys.dropLast(1) + CodexKeys + DockKeys.last() else DockKeys
```

The row reads `esc tab ↑ ↓ ⇧← ⇧→ enter`. Herdr calls Codex `codex`
(`herdr --skill` uses `--kind codex`), and the Herdr app keeps its name;
only `claude-code` is mapped (`internal/core/herdr_app.go:162-167`).

`keyName` (`AgentScreens.kt:1892-1899`) becomes `internal` and gets
`"shift+left" -> "Shift Left"` and `"shift+right" -> "Shift Right"` for
TalkBack.

**Rows that keep keys 48 dp wide.** Both rows are one fixed `Row` today
(`AgentScreens.kt:1801`, `:2037`). A helper next to `KeyBar`
(`AgentScreens.kt:2087`) picks the fewest rows in which every key keeps
`KeyMinWidth`, 48 dp:

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

/** Lays out keys of [shares] in the rows of [keyRows], each [height] tall. [key] draws key i with its modifier. */
@Composable
private fun SmallKeyRows(shares: List<Float>, height: Dp, key: @Composable (Int, Modifier) -> Unit) {
    BoxWithConstraints(Modifier.fillMaxWidth()) {
        Column(verticalArrangement = Arrangement.spacedBy(SmallKeyGap)) {
            for (r in keyRows(shares, maxWidth, SmallKeyGap)) {
                Row(Modifier.fillMaxWidth().height(height), horizontalArrangement = Arrangement.spacedBy(SmallKeyGap)) {
                    for (i in r) key(i, Modifier.weight(shares[i]))
                }
            }
        }
    }
}
```

`key` has no `RowScope` receiver: the helper adds the weight itself, so
`AgentScreens.kt` needs no new import.

- The dock's key row (`AgentScreens.kt:1800-1806`) draws
  `dockKeys(agent.agent)` with `SmallKeyRows(…, 40.dp)`. It still shows
  only after **Show keys** or **Keys**.
- `LiveControls` (`AgentScreens.kt:2037-2058`) draws its choice digits,
  `dockKeys(agent.agent)` and the keyboard key with
  `SmallKeyRows(…, 44.dp)`, in today's order. Its KDoc ("1 row of keys",
  `:2013-2016`) and the KDoc of `DockKeys` (`:1888`) say rows.
- Labels already shrink to fit (`SmallKey`, `fit = true`,
  `ui/AgentThreadUi.kt:731`).
- The rule uses the width the row gets, so the dock in a wide window's
  side column (`AgentScreens.kt:717-728`) wraps by its column width.
- Rows by screen width, for a row across a portrait phone (dock padding
  24 dp, `AgentThreadUi.kt:739`; Live padding 20 dp, `AgentScreens.kt:2028`):

  | Row | Shares | 1 row from | 360 dp | 411 dp | Narrowest key today at 360 dp |
  | --- | --- | --- | --- | --- | --- |
  | Dock, other agents | 5.5 | 312 dp | 1 row | 1 row | 57 dp |
  | Dock, Codex | 7.5 | 420 dp | 2 rows | 2 rows | (40 dp in 1 row) |
  | Live, other agents | 6.5 | 362 dp | 2 rows | 1 row | 48 dp |
  | Live, Codex | 8.5 | 470 dp | 2 rows | 2 rows | (35 dp in 1 row) |
  | Live, 3 choices, other agents | 9.5 | 524 dp | 2 rows | 2 rows | 31 dp |
  | Live, 3 choices, Codex | 11.5 | 632 dp | 2 rows | 2 rows | (24 dp in 1 row) |
  | Live, 9 choices, Codex | 17.5 | 956 dp | 3 rows | 3 rows | (14 dp in 1 row) |

  So on a phone the Codex rows take 2 lines, inside the space that the
  key row or Live already has. No row shows where none shows today.

### 5. Choices after `⇧←` or `⇧→`

A tap on `⇧←` or `⇧→` moves Codex's focus, so a choice button from
before can answer the wrong question. Both rows treat them like an
answer, and only after the key went out:

- `AgentDock` (`AgentScreens.kt:1690-1703`): a new
  `fun move(key: String) = guarded { answered = out; HerdrSync.sendKeys(…, listOf(key)) }`,
  like `answer` (`:1699-1703`). The key row calls it for `shift+left` and
  `shift+right`. A cancelled phone lock sends nothing and changes
  nothing. A failed send reads again (`:1705-1707`); a sent one reads
  again too (`core/Herdr.kt:107-117`).
- Live (`ui/HerdrTerminal.kt:302-308`): `TerminalInput.send` calls a new
  `onMove` callback after a `shift+left` or `shift+right` went out.
  Both the Live row (`input.key`) and a hardware keyboard
  (`handleKey` → `pressKey` → `send`, `HerdrTerminal.kt:481-487,503`) go
  through `send`, so one hook covers both. `LiveControls` sets `onMove`
  in a `SideEffect` and clears it on dispose, as `LivePane` does with
  `onKey` (`AgentScreens.kt:1405-1424`). It sets `answered = out` and
  calls `HerdrSync.rereadSoon` unless the computer is a sample, as a
  typed digit does (`AgentScreens.kt:2044-2048`). Live doesn't poll
  (`AgentScreens.kt:403-407`), so without it the choices would wait.

What this protects, by `choicesOpen` (`core/HerdrModel.kt:191-192`): the
buttons stay off until the next completed read, about 700 ms
(`Herdr.kt:116,485`). That read is a new object even when the text
didn't change. If Codex still shows the old dialog's numbered lines at
the end, the buttons come back for them. Flux can't tell which question
has focus; the docs say so.

Other keys keep today's behaviour.

### 6. Docs (`docs/herdr.md`)

- Line 177: the Live row lists `⇧←` and `⇧→` for Codex.
- Lines 198-199: Live also reads the output after `⇧←` and `⇧→`, and the
  choices take no tap until that read.
- Line 225: a hardware keyboard sends Shift+Left and Shift+Right as keys.
- Line 359: the key bar sends Shift+Left and Shift+Right on a Codex
  agent, which move to Codex's pending questions and back; the Mac and
  iPhone don't show them.
- Line 370: fluxd accepts `shift+left` and `shift+right`.
- Line 810: `terminal_input` `key` can be `shift+left` or `shift+right`.
- One paragraph after line 370 with the limits: the keys are plain key
  presses; a question answered on the desktop first can leave the focus
  elsewhere; the choices can come back for a dialog that no longer has
  focus; an older fluxd refuses the keys.
- Line 448, the terminal list, doesn't change.

### 7. What doesn't change

- Herdr, Codex, the packet formats, the desktop Qt app.
- Plain terminal input and the terminal key bar (`ui/HerdrScreens.kt:544`).
- `KeyBar` and its 2-row rule.
- `findChoices`, `findMenuChoices`, `choicesOpen`, and when choice buttons
  show.
- The heights of the key rows: 40 dp in the dock, 44 dp in Live.
- `herdr_control`, the phone lock, `ctrl+c` refusal.

## Owner decisions

1. **The row rule for every agent?** `SmallKeyRows` also wraps other
   agents' rows: on a phone under 362 dp, Live gets 2 rows; Live with
   choice digits wraps on most phones. Today those keys get narrower
   than 48 dp (31 dp with 3 choices at 360 dp). Proposed: yes, one rule
   for all. The alternative is to wrap only Codex rows and leave the rest
   as today.
2. **Row height.** The keys are 40 dp tall in the dock and 44 dp in Live
   (`AgentScreens.kt:1801,2037`), under the 48 dp of a full touch target.
   That is today's height; this issue makes the keys wide enough, not
   tall enough. Proposed: keep the heights, so a wrapped row costs
   46 dp or 50 dp, not 54 dp. The alternative is 48 dp rows for both;
   the dock and Live then grow by 8 dp and 4 dp a row, for every agent.

## Alternatives rejected

- **A Herdr or Codex App Server integration.** Herdr has no API for
  pending questions; out of scope (intent Q1).
- **A third row only for Codex keys.** The issue asks not to add rows;
  the wrap adds one only where the keys don't fit.
- **At most 2 rows, as `KeyBar` does.** With choice digits a half row
  is still too wide: 3 choices and Codex at 320 dp give 45 dp keys, and
  4 or more choices fail at 360 dp. `keyRows` takes as many rows as the
  keys need.
- **`overhang` to make the keys 48 dp tall to the touch**
  (`AgentThreadUi.kt:215-225`). Wrapped rows sit 6 dp apart, so the
  touch areas would overlap.
- **Swapping Tab out for the Codex keys.** Tab moves in Codex's menus;
  dropping it breaks other dialogs.
- **A long press on ← and → for the shifted key.** Hidden, and TalkBack
  users can't find it.
- **Showing `⇧←`/`⇧→` only when a question is pending.** Flux can't tell;
  that is the structured model the intent defers.
- **Keeping the choices off until the text changes.** A blocked agent
  doesn't poll (`AgentScreens.kt:403-407`). If `⇧←` changes nothing on
  screen, the buttons would stay off until **Refresh**.
- **`KeyBar` for the dock and Live rows.** Its `KeyTile` has another look
  and 48 dp height; the dock and Live keep `SmallKey`.
- **A capability flag so a new phone hides the keys from an old fluxd.**
  A new field for a refusal that is already clear. The keys come with
  the fluxd of the same release.
- **Shift with Up and Down too.** Not needed for Codex; no use case.

## Risks

- **Wrong focus.** If the desktop answers or cancels the question first,
  `⇧←` can land on queued input, and the next Enter acts there. The
  thread shows the result after each key; the docs say so.
- **Choices for a dialog without focus.** See Design 5. Accepted and in
  the docs.
- **Old fluxd.** A new phone shows the keys, and an old fluxd refuses
  them: `The key "shift+left" is not allowed` in the dock, `fluxd does not
  know that key` in Live, after which Live starts a new line
  (`AgentScreens.kt:1428-1433`). Nothing reaches the agent.
- **Codex in Herdr reads the bytes.** Codex may ask its terminal for an
  extended key mode. Herdr encodes the key name for the pane's terminal
  (`encode_terminal_key`); Live sends fixed xterm bytes, as it does for
  the arrows today. Verification 3 tests both paths on Codex.
- **The unsent-text note.** Text typed into Codex's composer in Live,
  then `⇧←` and Enter on the question, clears `unsent`
  (`HerdrTerminal.kt:306`), so the note after Live (`docs/herdr.md:195`)
  doesn't show, although the text still waits. Accepted: Flux can't tell
  which input Enter went to. The docs say so.
- **Layout change for other agents.** With owner decision 1, a narrow
  phone gets 2 key rows where it had 1 cramped row.
- **More rows in Live.** With many choice digits on a narrow phone, Live
  takes 3 rows of 44 dp, and the terminal gets less height.
- **`⇧` glyph.** The mono font may lack U+21E7. Verification 3.5 checks it;
  if it is missing, the labels become `S-←` and `S-→`.

## Verification

1. **Unit tests.**
   - Go (`make test vet`):
     - `herdr_test.go:1325-1331`, next to the `2 enter` keys: `herdrKeys(dev,
       "w1:p1", []string{"shift+left", "shift+right"})` has no error, and
       the fake Herdr gets `agent.send_keys` with both names. `ctrl+c` stays
       refused (`:1309`).
     - `herdr_terminal_test.go:1106-1115`: add `shift+left` and
       `shift+right` events; want `"\x1b[1;2D"` and `"\x1b[1;2C"`.
     - New: a plain terminal `input` with `shift+left` is refused, next to
       `f1` (`herdr_test.go:1009`).
   - Android (`./gradlew :app:testDebugUnitTest :app:lintDebug
     :app:assembleDebug :app:assembleRelease`):
     - `HerdrReplyTest.kt:199-206`: both names in `HERDR_KEYS`, size 24;
       both in `HERDR_TERMINAL_INPUT_KEYS`; neither in `HERDR_TERMINAL_KEYS`.
     - `HerdrTerminalFocusTest.kt:103-114`: Shift with DPAD_LEFT and
       DPAD_RIGHT gives the shifted names; DPAD_RIGHT without Shift gives
       `right`; Shift with DPAD_UP gives `up`.
     - New tests: `dockKeys("codex")` (7 keys, Enter last),
       `dockKeys("claude")` (today's 5); `keyName` for both new keys;
       `keyRows` for each row of the table at 320, 360 and 411 dp minus
       the padding, where every row fits at 48 dp and uses the fewest
       rows; and a `TerminalInput` whose `onKey` returns true calls
       `onMove` once for `shift+left`, not for `left`, and not when
       `onKey` returns false.
   - Swift (`make test-macos test-ios`, on a Mac or in CI;
     `docs/development.md:181`): `HerdrTests.swift:225-232` count 24 and
     both names.
2. `nix flake check path:.`.
3. **On the Pixel**, with the running fluxd on p620 (not restarted by
   the agents; the owner installs the build) and the branch's debug APK:
   1. Codex in Herdr asks a question that stays pending while its
      composer has focus. **Show keys**: the row shows `⇧←` and `⇧→`.
      `⇧←` moves to the question, the arrows and Enter answer it, `⇧→`
      goes back. The thread follows each key.
   2. The same in Live, and with a hardware keyboard's Shift+Left and
      Shift+Right.
   3. Once with Codex in the Herdr app (no Live there).
   4. A Claude agent shows today's 5 keys.
   5. At the largest font size: no key is narrower than 48 dp, labels
      fit, `⇧` renders.
   6. TalkBack reads "Shift Left" and "Shift Right".
   7. While a blocked Codex shows choices, `⇧←` turns them off until the
      next read, from the key row, the Live row and a hardware keyboard.
      A cancelled phone lock on `⇧←` leaves them on.

## Review changes

Codex and agy reviewed the first draft of this spec, read only. Each
finding was checked against the code.

| Finding | Source | Verdict | Change |
| --- | --- | --- | --- |
| 2 rows don't keep 48 dp: 3 choices and Codex at 320 dp give 45 dp; 4+ choices fail at 360 dp | Codex (high), agy | Confirmed, `AgentScreens.kt:2028`; recomputed | `keyRows` takes the fewest rows that fit; alternative rejected; tests per width |
| A hardware Shift+Left in Live skips the choice reset and the reread | Codex (high), agy | Confirmed, `HerdrTerminal.kt:481-487,503` vs `AgentScreens.kt:2044-2048` | One `onMove` hook in `TerminalInput.send`; test and Verification 3.7 |
| `answered = out` before the guarded send keeps choices off after a cancelled lock | agy (high), Codex | Confirmed, `AgentScreens.kt:1694,1699-1703` | `move` sets it inside `guarded`, like `answer`; Live sets it only after a sent key |
| `SmallKeyRows` uses `RowScope`, not imported | agy (high), Codex | Confirmed, no `RowScope` in `AgentScreens.kt` | Receiver dropped |
| `agent.send_keys` never checked; only `pane.send_keys` | Codex | Confirmed then resolved: both handlers call `encode_api_keys` (`objdump` of Herdr 0.9.5) | Herdr check rewritten; no key sent to any agent |
| `choicesOpen` checks identity; the 700 ms reread opens the choices on the same text | agy, Codex | Confirmed, `HerdrModel.kt:191-192`, `Herdr.kt:116,485` | Design 5 says what it protects; Risk and docs; Verification 3.7 says "next read"; text compare rejected |
| Keys stay 40/44 dp tall, under a 48 dp target | Codex, agy | Confirmed, today's height | Owner decision 2; `overhang` rejected |
| FluxKit needs `make test-ios` too | Codex, agy | Confirmed, `docs/development.md:181` | Added |
| Table: 3 choices and Codex is 632 dp, not "always"; wide side column wraps too | agy, Codex | Confirmed, `AgentScreens.kt:717-728,898-901` | Table redone by width; side column noted |
| Success test is at `herdr_test.go:1325`; `:1009` tests `f1` | Codex | Confirmed | Cites fixed; shifted refusal is a new test |
| `herdrKeys` is `:86-113`; dock is `:1690-1703`, Live `:2037-2058` | agy (low) | Confirmed | Cites fixed |
| No unit test for TalkBack names; no unshifted DPAD_RIGHT test | agy (low) | Confirmed, `keyName` is private; `HerdrTerminalFocusTest.kt:110` | `keyName` internal and tested; DPAD_RIGHT added |
| `docs/herdr.md:198-199` and 3 KDocs miss the new behaviour | agy (low) | Confirmed | Added to Design 3, 4 and 6 |
| Enter on the question clears the unsent-text note | agy (low) | Confirmed, `HerdrTerminal.kt:306` | Risk, accepted; in the docs |
| Allowlists, 24 counts, `dockKeys` order, bytes, hardware mapping | both | Agreed | Kept |
| Codex runtime, extended key modes, TalkBack, the `⇧` glyph, font scale | both | Unverified | Left to Verification 3 |
