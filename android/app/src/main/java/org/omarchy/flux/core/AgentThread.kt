package org.omarchy.flux.core

/**
 * The thread view of an agent: its output as messages, tool calls, file
 * changes, and prompts, as in a chat. The parser reads the text lines of the
 * output, as [tidyLines] gives them. It knows the transcripts of Claude Code,
 * Codex, and opencode. Lines that it does not know stay as raw terminal lines, so that
 * the view never loses output. The parser has no Android imports, so the JVM
 * tests can load it.
 */

/** A file that an agent changed, with the counts of added and removed lines. */
data class FileChange(val path: String, val added: Int, val removed: Int)

/** One block of the thread of an agent. */
sealed interface ThreadBlock {
    /** Text of the agent. Its lines keep their breaks. */
    data class Message(val text: String) : ThreadBlock

    /**
     * A tool call, for example `Bash(npm test)`: the [name] of the tool, its
     * [args], and the [result] lines under it. [failed] is true when the
     * result starts with an error. [change] is set for a file edit.
     */
    data class Tool(
        val name: String,
        val args: String,
        val result: List<String>,
        val failed: Boolean = false,
        val change: FileChange? = null,
    ) : ThreadBlock

    /** A run of 2 or more file edits, as 1 summary. */
    data class Changes(val files: List<FileChange>) : ThreadBlock {
        val added: Int get() = files.sumOf { it.added }
        val removed: Int get() = files.sumOf { it.removed }
    }

    /** A prompt that the user sent to the agent. */
    data class Prompt(val text: String) : ThreadBlock

    /** Output lines that the parser does not know: the lines from [from] until [to] of the output. */
    data class Raw(val from: Int, val to: Int) : ThreadBlock
}

/**
 * The thread of an agent. [step] is what a working agent does now, for
 * example "Running tests", and [elapsed] is its time as the agent shows it,
 * for example "2:14". [worked] is the time of the last turn of the agent
 * after it finished, for example "6m 12s". Each of these 3 is empty when
 * the output does not show it.
 */
data class AgentThread(
    val blocks: List<ThreadBlock>,
    val step: String = "",
    val elapsed: String = "",
    val worked: String = "",
)

/**
 * The question of a blocked agent, for the dialog of the agent screen and
 * the Inbox. [question] is the line that asks, for example "Do you want to
 * proceed?", or empty. [lines] are the lines above the choices, for example
 * the command that a choice approves. [command] is true when the lines are
 * a shell command.
 */
data class AgentAsk(val question: String, val lines: List<String>, val command: Boolean = false)

/** The bullet of a message or a tool call: ● and ⏺ of Claude Code, • of Codex. ※ marks a recap of Claude Code. */
private val bulletLine = Regex("""^([●⏺•※])\s+(.*)$""")

/** A prompt of the user: > and ❯ in Claude Code, › in Codex. */
private val promptLine = Regex("""^[>›❯](?:\s+(.*))?$""")

/** A tool call of Claude Code, for example Bash(npm test) or an MCP tool. */
private val toolCall = Regex("""^([A-Za-z][\w.-]*|[\w.-]+ - [\w.-]+ \(MCP\))\((.*)\)\s*$""")

/** The first line of a tool call of Claude Code whose arguments take more than 1 line. */
private val toolStart = Regex("""^([A-Za-z][\w.-]*|[\w.-]+ - [\w.-]+ \(MCP\))\((.*)$""")

/** The tools of Claude Code. A first line with one of these names starts a tool call also before its result shows. */
private val claudeTools = setOf(
    "Agent", "Bash", "Edit", "Fetch", "Glob", "Grep", "LS", "MultiEdit", "NotebookEdit", "Read", "Search", "Skill", "Task",
    "TodoWrite", "Update", "WebFetch", "WebSearch", "Workflow", "Write",
)

/**
 * A line of Claude Code that sums up the tool calls of a step, for example
 * "Read 2 files, listed 1 directory, ran 3 shell commands". Its first
 * part names a count, and it does not end like a sentence.
 */
private val summaryLine = Regex("""^(Read|Reading|Searched|Searching|Listed|Listing|Ran|Running|Wrote|Writing|Made|Making|Edited|Editing|Updated|Updating|Created|Creating|Fetched|Fetching|Recalled|Recalling|Called|Calling|Loaded|Loading|Found|Finding|Viewed|Viewing|Used|Using)( for)? \d+ [^\n]*[^.!?:\s…]$""")

/** The hint at the end of a line that the desktop can open, for example "(ctrl+o to expand)". */
private val expandHint = Regex("""\s*\((?:ctrl|shift)\+\w+ to [a-z ]+\)\s*$""")

/** The time at the end of a tool call that runs, for example " · 11s". */
private val runTime = Regex("""\s+·\s+\d[\dhms :.]*$""")

/** A tool call of Codex, for example "Ran npm test" or "Edited src/a.ts (+3 -1)". */
private val codexTool = Regex("""^(Ran|Running|Edited|Added|Deleted|Explored|Exploring|Read|Searched|Listed|Called|Calling|Waited|Updated Plan)\b\s*(.*)$""")

/** The counts at the end of a Codex edit, for example (+3 -1). */
private val codexCounts = Regex("""\s*\(\+(\d+)\s+-(\d+)\)\s*$""")

/** The marks at the start of a result line: ⎿ of Claude Code, └ and │ of Codex. */
private val resultMark = Regex("""^(\s*)([⎿└│])\s*""")

/** The tools of Claude Code and Codex that edit a file. */
private val editTools = setOf("Update", "Write", "Edit", "MultiEdit", "Create", "NotebookEdit", "Edited", "Added", "Deleted")

private val additions = Regex("""(\d+) additions?""")
private val removals = Regex("""(\d+) removals?""")
private val addedLines = Regex("""(?:Added|Wrote) (\d+) lines?""")
private val removedLines = Regex("""[Rr]emoved (\d+) lines?""")

/**
 * The working line of an agent, for example "✻ Running tests… (2:14 · esc
 * to interrupt)" of Claude Code or "• Working (12s • esc to interrupt)" of
 * Codex.
 */
private val workLine = Regex("""^\s*\S{1,2}\s+(.+?)\s*\(([^()]*\bto interrupt\b[^()]*)\)\s*$""")

/**
 * The working line of Claude Code 2.1, for example "✶ Orbiting… (1m 17s ·
 * ↓ 5.7k tokens)". The hint to interrupt is in the footer now, so the
 * spinner mark and the ellipsis tell the line.
 */
private val spinnerLine = Regex("""^[·✢✳✶✻✽*]\s+(\S[^()]*?)…\s*(?:\(([^()]*)\))?\s*$""")

/** A time in a working line or a finished line, for example 12s, 2m 5s, or 2:14. */
private val timeText = Regex("""\b(\d+h(?: \d+m)?(?: \d+s)?|\d+m(?: \d+s)?|\d+s|\d+:\d{2}(?::\d{2})?)\b""")

/**
 * The line after a finished turn, for example "✻ Worked for 6m 12s", "─
 * Worked for 1m 03s ───", or "✻ Crunched for 1m 12s · done Thursday 4:15 PM".
 */
private val doneLine = Regex("""^[\s\W]*\p{Lu}\p{Ll}+ for (\d+h(?: \d+m)?(?: \d+s)?|\d+m(?: \d+s)?|\d+s)\b(?:\s+·\s+[^·]+)?[\s─━]*$""")

/** A line with only rule characters. */
private val ruleOnly = Regex("""^[\s─━═╌┄╍┈┉\-_]+$""")

/** A rule with a title, for example the rule above the input of Claude Code with the name of the session. */
private val titledRule = Regex("""^[─━═]{8,}.*$""")

/** A row of the logo of Claude Code at the start of a session, for example " ▐▛███▜▌   Claude Code v2.1.293". */
private val logoLine = Regex("""^\s*[▐▛▜▌▝▘▗▖▙▟█▀▄]""")

/** The most rows of the logo of Claude Code. */
private const val LOGO_ROWS = 4

/** A hint at the right edge above the input, for example "new task? /clear to save 671.7k tokens". */
private const val HINT_INDENT = 30

/** The choices of a notice of Claude Code above the input, for example "1: Learn more   2: Knew this already   0: Dismiss". */
private val noticeChoices = Regex("""^(?:\d: \S.*?\s{2,})+\d: \S.*$""")

/** The first numbered choice of a dialog. */
private val firstChoiceLine = Regex("""^\s*([❯›>]\s*)?1[.)]\s+.+$""")

/** The hints under the input of an agent. */
private val footerWords = listOf(
    "for shortcuts", "shift+tab", "context left", "accept edits", "bypass permissions", "plan mode", "auto-accept",
    "% context", "esc to cancel", "to cycle", "ctrl+", "Press enter", "auto mode", "for agents", "to manage", "esc to interrupt",
)

/** How far from the end the working line can be, in lines that are not empty. */
private const val WORK_SCAN_LINES = 12

/** How far above the first choice the dialog can start, in lines. */
private const val DIALOG_SCAN_LINES = 16

private fun isBox(t: String): Boolean = t.startsWith("╭") || t.startsWith("╰") || (t.startsWith("│") && t.endsWith("│") && t.length > 1)

/**
 * A hint line under the input. It is indented by 2 spaces at most, so that
 * a result line does not count. A working line is not a hint, also when it
 * tells how to interrupt.
 */
private fun isFooter(line: String): Boolean =
    indent(line) <= 2 && footerWords.any { line.contains(it, ignoreCase = true) } && !workLine.matches(line) && !spinnerLine.matches(line)

/** True when [line] is a summary of tool calls of Claude Code, see [summaryLine]. */
private fun isSummary(line: String): Boolean = indent(line) == 2 && summaryLine.matches(summaryText(line))

/** The text of a summary line without its hint and the ellipsis of a step that runs. */
private fun summaryText(line: String): String = line.trim().replace(expandHint, "").removeSuffix("…").trimEnd()

private fun indent(s: String): Int = s.indexOfFirst { it != ' ' }.let { if (it < 0) s.length else it }

/**
 * Finds the line where the dialog at the end of [lines] starts, or null
 * when the output ends with no dialog. The dialog starts at the rule or the
 * box above its first choice, as in Claude Code. Without one, it starts at
 * the question above the choices, as in Codex. Without a question, it takes
 * the lines directly above the choices.
 */
fun dialogStart(lines: List<String>): Int? {
    if (findChoices(lines).isEmpty()) return null
    val first = (lines.size - 1 downTo 0).firstOrNull { firstChoiceLine.matches(lines[it]) } ?: return null
    val stop = maxOf(0, first - DIALOG_SCAN_LINES)
    val range = first - 1 downTo stop
    // The thread above the dialog ends at a message, a tool call, or a prompt.
    val thread = range.firstOrNull { bulletLine.matches(lines[it]) || promptLine.matches(lines[it]) } ?: -1
    range.firstOrNull { it > thread && lines[it].trim().let { t -> t.isNotEmpty() && (ruleOnly.matches(t) || t.startsWith("╭")) } }?.let { return it }
    range.firstOrNull { it > thread && lines[it].trim().endsWith("?") }?.let { return it }
    var s = first
    while (s > maxOf(stop, thread + 1) && lines[s - 1].isNotBlank()) s--
    return s
}

/**
 * Returns the question of the dialog at the end of [lines], or null when
 * the output ends with no choices. It keeps the [maxLines] lines nearest
 * the choices, because they hold the command that a choice approves. The
 * title of the dialog, for example "Bash command", does not show. After a
 * command title, the command gets a "$ " in front.
 */
fun agentAsk(lines: List<String>, maxLines: Int = 4): AgentAsk? {
    if (isOpencode(lines)) return opencodeAsk(lines, maxLines)
    val start = dialogStart(lines) ?: return null
    val first = (lines.size - 1 downTo start).firstOrNull { firstChoiceLine.matches(lines[it]) } ?: return null
    // The lines of the dialog without rules, box edges, and empty lines. Each keeps its indent and whether a blank line follows it.
    data class Row(val text: String, val indent: Int, val gapAfter: Boolean)
    val rows = ArrayList<Row>()
    for (i in start until first) {
        var t = lines[i].trimEnd()
        val s = t.trim()
        if (s.isEmpty()) {
            if (rows.isNotEmpty()) rows[rows.size - 1] = rows.last().copy(gapAfter = true)
            continue
        }
        if (ruleOnly.matches(s) || s.startsWith("╭") || s.startsWith("╰")) continue
        // The header chips of a question of Claude Code, for example "☐ Tax rate".
        if (s.startsWith("☐") || s.startsWith("☒") || s.startsWith("✔") || s.startsWith("←  ☐")) continue
        if (s.startsWith("│")) t = s.removePrefix("│").removeSuffix("│").trimEnd()
        if (t.isBlank()) continue
        rows += Row(t.trim(), indent(t), gapAfter = false)
    }
    if (rows.isEmpty()) return AgentAsk("", emptyList())
    var question = ""
    var body: List<Row> = rows
    if (body.last().text.endsWith("?")) {
        question = body.last().text
        body = body.dropLast(1)
    } else if (body.first().text.endsWith("?")) {
        question = body.first().text
        body = body.drop(1)
    }
    // A title: the first line, with a blank line under it, and more indented lines after it.
    var command = false
    if (body.size >= 2 && body[0].gapAfter && body[1].indent > body[0].indent) {
        command = body[0].text.endsWith("command", ignoreCase = true)
        body = body.drop(1)
    }
    var text = body.map { it.text }.takeLast(maxLines)
    if (command && text.isNotEmpty() && !text[0].startsWith("$ ")) text = listOf("$ " + text[0]) + text.drop(1)
    if (!command) command = text.firstOrNull()?.startsWith("$ ") == true
    return AgentAsk(question, text, command)
}

/** The counts of added and removed lines in the [result] of an edit tool. */
private fun editCounts(result: List<String>): Pair<Int, Int> {
    val text = result.joinToString("\n")
    val added = additions.find(text)?.groupValues?.get(1)?.toInt() ?: addedLines.find(text)?.groupValues?.get(1)?.toInt() ?: 0
    val removed = removals.find(text)?.groupValues?.get(1)?.toInt() ?: removedLines.find(text)?.groupValues?.get(1)?.toInt() ?: 0
    return added to removed
}

/**
 * Removes the input box of the agent and the hints under it from the end
 * of [lines], until [end]. It returns the new end. Claude Code puts the
 * name of the session in the rule above the input, and hints at the right
 * edge above that rule.
 */
private fun stripInput(lines: List<String>, end: Int): Int {
    var i = end
    var input = false
    while (i > 0) {
        val t = lines[i - 1].trim()
        when {
            t.isEmpty() || ruleOnly.matches(t) || input && titledRule.matches(t) -> i--
            isBox(t) -> {
                i--
                input = true
            }
            !input && promptLine.matches(t) -> {
                i--
                input = true
            }
            !input && isFooter(lines[i - 1]) -> i--
            input && indent(lines[i - 1]) >= HINT_INDENT -> i--
            // A notice of Claude Code above the input, for example "✦ Heads up · …", and its choices.
            input && (lines[i - 1].startsWith("✦ ") || noticeChoices.matches(t)) -> i--
            else -> return i
        }
    }
    return i
}

/** Parses the output [lines] of an agent into its thread. */
fun agentThread(lines: List<String>): AgentThread {
    if (isOpencode(lines)) return opencodeThread(lines)
    val dialog = dialogStart(lines)
    var end = dialog ?: stripInput(lines, lines.size)
    // The dialog takes the place of the input, so only the empty lines and the rules above it go.
    while (dialog != null && end > 0 && lines[end - 1].trim().let { it.isEmpty() || ruleOnly.matches(it) }) end--
    var step = ""
    var elapsed = ""
    var worked = ""
    // The working line sits above the input, and a list of tasks can follow it.
    var seen = 0
    var w = end - 1
    while (w >= 0 && seen < WORK_SCAN_LINES) {
        val t = lines[w]
        if (t.isNotBlank()) {
            val m = workLine.matchEntire(t) ?: spinnerLine.matchEntire(t)
            if (m != null) {
                step = m.groupValues[1].trim().removeSuffix("…").removeSuffix("...").trim()
                elapsed = timeText.find(m.groupValues[2])?.value.orEmpty()
                end = w
                while (end > 0 && lines[end - 1].isBlank()) end--
                break
            }
            seen++
        }
        w--
    }
    if (step.isEmpty() && end > 0) {
        doneLine.matchEntire(lines[end - 1].trimEnd())?.let { m ->
            worked = m.groupValues[1]
            end--
            while (end > 0 && lines[end - 1].isBlank()) end--
        }
    }
    return AgentThread(mergeEdits(parseBlocks(lines, end)), step, elapsed, worked)
}

/** Parses the lines until [end] into blocks. */
private fun parseBlocks(lines: List<String>, end: Int): List<ThreadBlock> {
    val blocks = ArrayList<ThreadBlock>()
    var rawFrom = -1
    var rawTo = -1
    fun flushRaw() {
        if (rawFrom >= 0) blocks += ThreadBlock.Raw(rawFrom, rawTo)
        rawFrom = -1
    }
    // The end of a block that starts at [from]: the lines under it that are indented. A blank line
    // belongs to the block only when an indented line follows it. A summary of tool calls after a
    // blank line starts its own block. A line at the left edge right under the first line of the
    // block continues it, when it is not a line of its own kind.
    // Claude Code blinks the bullet of a tool call that runs, so the first line of the call can have
    // 2 blanks in place of its bullet. The result mark under it tells it.
    fun blinkHead(j: Int): Boolean =
        j > 0 && j + 1 < end && lines[j - 1].isBlank() && indent(lines[j]) == 2 && lines[j].trim().isNotEmpty() &&
            indent(lines[j + 1]) == 2 && lines[j + 1].trimStart().startsWith("⎿")
    fun blockEnd(from: Int): Int {
        var j = from + 1
        var last = from + 1
        while (j < end) {
            val l = lines[j]
            if (l.isBlank()) {
                j++
                continue
            }
            if (j > last && (isSummary(l) || blinkHead(j))) break
            if (bulletLine.matches(l)) break
            if (indent(l) < 2 && (j != from + 1 || !continuesHead(l))) break
            j++
            last = j
        }
        return last
    }
    var i = 0
    while (i < end) {
        val line = lines[i]
        if (line.isBlank()) {
            i++
            continue
        }
        // The logo of Claude Code and the line after an earlier turn do not show.
        if (logoLine.containsMatchIn(line)) {
            var k = i
            while (k < end && k - i < LOGO_ROWS && logoLine.containsMatchIn(lines[k])) k++
            if ((i until k).any { lines[it].contains("Claude Code") }) {
                flushRaw()
                i = k
                continue
            }
        }
        if (doneLine.matches(line.trimEnd())) {
            flushRaw()
            i++
            continue
        }
        if (isSummary(line)) {
            flushRaw()
            // A result can follow the summary, for example the note of a message that went to another session.
            var k = i + 1
            while (k < end && lines[k].isNotBlank() && indent(lines[k]) >= 2 && !isSummary(lines[k])) k++
            val text = summaryText(line)
            blocks += ThreadBlock.Tool(text.substringBefore(' '), text.substringAfter(' '), resultLines(lines.subList(i + 1, k)))
            i = k
            continue
        }
        val bullet = bulletLine.matchEntire(line) ?: if (blinkHead(i)) bulletLine.matchEntire("●" + line.drop(1)) else null
        val prompt = if (bullet == null) promptLine.matchEntire(line) else null
        if (bullet == null && prompt == null) {
            if (rawFrom < 0) rawFrom = i
            rawTo = i + 1
            i++
            continue
        }
        flushRaw()
        val to = blockEnd(i)
        val body = lines.subList(i + 1, to)
        if (prompt != null) {
            // The notes of Claude Code under a prompt, for example "⎿  1 skill available", are not part of it.
            val own = body.takeWhile { !it.trimStart().startsWith("⎿") }
            val text = (listOf(prompt.groupValues[1]) + dedent(own)).joinToString("\n").trim().replace(expandHint, "")
            if (text.isNotEmpty()) blocks += ThreadBlock.Prompt(text)
        } else {
            blocks += bulletBlock(bullet!!.groupValues[1], bullet.groupValues[2].trimEnd(), body)
        }
        i = to
    }
    flushRaw()
    return blocks
}

/**
 * True when [line], at the left edge right under the first line of a
 * block, continues that line. A bullet, a prompt, a rule, a box, and the
 * lines of the status of the agent start something else.
 */
private fun continuesHead(line: String): Boolean {
    val t = line.trim()
    return !promptLine.matches(t) && !ruleOnly.matches(t) && !titledRule.matches(t) && !isBox(t) &&
        !doneLine.matches(t) && !workLine.matches(t) && !spinnerLine.matches(t)
}

/** Removes the indent that the [lines] share, and keeps the blank lines between them as 1 blank line. */
private fun dedent(lines: List<String>): List<String> {
    val pad = lines.filter { it.isNotBlank() }.minOfOrNull(::indent) ?: 0
    val out = ArrayList<String>()
    for (l in lines) {
        if (l.isBlank()) {
            if (out.isNotEmpty() && out.last().isNotEmpty()) out += ""
        } else {
            out += l.drop(pad).trimEnd()
        }
    }
    while (out.isNotEmpty() && out.last().isEmpty()) out.removeAt(out.size - 1)
    return out
}

/** The result lines under a tool call, without their marks. Each line keeps its indent under the first line. */
private fun resultLines(body: List<String>): List<String> {
    val first = body.firstOrNull { it.isNotBlank() } ?: return emptyList()
    val mark = resultMark.find(first)
    val col = mark?.range?.last?.plus(1) ?: indent(first)
    return dedentFrom(body, col)
}

/** Removes up to [col] spaces from each line, and the blank lines at the ends. */
private fun dedentFrom(lines: List<String>, col: Int): List<String> {
    val out = lines.map { l ->
        val m = resultMark.find(l)
        if (m != null) {
            l.substring(m.range.last + 1).trimEnd()
        } else {
            l.drop(minOf(col, indent(l))).trimEnd()
        }
    }.toMutableList()
    while (out.isNotEmpty() && out.first().isBlank()) out.removeAt(0)
    while (out.isNotEmpty() && out.last().isBlank()) out.removeAt(out.size - 1)
    return out
}

/** A block that starts with a bullet: a tool call or a message. */
private fun bulletBlock(bullet: String, head: String, body: List<String>): ThreadBlock {
    if (bullet != "•") claudeTool(head, body)?.let { return it }
    val call = if (bullet == "•") null else toolCall.matchEntire(head)
    if (call != null) {
        val name = call.groupValues[1]
        val args = call.groupValues[2]
        val result = resultLines(body)
        val failed = result.firstOrNull()?.trimStart()?.startsWith("Error") == true
        val change = if (name in editTools && args.isNotBlank()) {
            val (a, r) = editCounts(result)
            FileChange(args.substringBefore(", ").trim(), a, r)
        } else {
            null
        }
        return ThreadBlock.Tool(name, args, result, failed, change)
    }
    val codex = if (bullet == "•") codexTool.matchEntire(head) else null
    val marked = body.firstOrNull { it.isNotBlank() }?.let { resultMark.find(it) } != null
    if (codex != null && (marked || codexCounts.containsMatchIn(head))) {
        val name = codex.groupValues[1]
        var args = codex.groupValues[2]
        var change: FileChange? = null
        val counts = codexCounts.find(args)
        if (name in editTools) {
            val path = args.replace(codexCounts, "").trim()
            change = FileChange(path, counts?.groupValues?.get(1)?.toInt() ?: 0, counts?.groupValues?.get(2)?.toInt() ?: 0)
            args = path
        }
        val result = resultLines(body)
        return ThreadBlock.Tool(name, args, result, failed = false, change = change)
    }
    val text = (listOf(head) + dedent(body)).joinToString("\n").trim()
    return ThreadBlock.Message(text)
}

/**
 * A tool call of Claude Code 2.1 that the plain [toolCall] does not match,
 * or null:
 * - A shell command that runs shows its description and then the command
 *   as the result, for example "Run the tests · 11s" and "⎿  $ npm test".
 * - The arguments of a long call take more than 1 line, and the closing
 *   parenthesis is on the last of them or cut.
 */
private fun claudeTool(head: String, body: List<String>): ThreadBlock.Tool? {
    val first = body.indexOfFirst { it.isNotBlank() }
    if (first < 0) return null
    val mark = resultMark.find(body[first])
    if (mark != null && mark.groupValues[2] == "⎿" && body[first].substring(mark.range.last + 1).startsWith("$ ")) {
        val result = resultLines(body).filter { !expandHint.matches(it) && it.trim() != "(ctrl+b to run in background)" }
        return ThreadBlock.Tool("Bash", head.replace(runTime, "").trim(), result)
    }
    if (toolCall.matches(head)) return null
    val start = toolStart.matchEntire(head) ?: return null
    val name = start.groupValues[1]
    val resultAt = body.indexOfFirst { resultMark.find(it)?.groupValues?.get(2) == "⎿" }
    if (resultAt < 0 && name !in claudeTools) return null
    val argLines = (if (resultAt < 0) body else body.subList(0, resultAt)).map { it.trim() }.filter { it.isNotEmpty() }
    val args = (listOf(start.groupValues[2].trim()) + argLines).joinToString(" ").removeSuffix(")").trim()
    val result = if (resultAt < 0) emptyList() else resultLines(body.subList(resultAt, body.size))
    val failed = result.firstOrNull()?.trimStart()?.startsWith("Error") == true
    val change = if (name in editTools && args.isNotBlank()) {
        val (a, r) = editCounts(result)
        FileChange(args.substringBefore(", ").trim(), a, r)
    } else {
        null
    }
    return ThreadBlock.Tool(name, args, result, failed, change)
}

/** Turns each run of 2 or more file edits into 1 [ThreadBlock.Changes]. The same file shows once, with the sum of its counts. */
private fun mergeEdits(blocks: List<ThreadBlock>): List<ThreadBlock> {
    val out = ArrayList<ThreadBlock>()
    var run = ArrayList<ThreadBlock.Tool>()
    fun flush() {
        if (run.size >= 2) {
            val files = LinkedHashMap<String, FileChange>()
            for (t in run) {
                val c = t.change!!
                files[c.path] = files[c.path]?.let { it.copy(added = it.added + c.added, removed = it.removed + c.removed) } ?: c
            }
            out += ThreadBlock.Changes(files.values.toList())
        } else {
            out += run
        }
        run = ArrayList()
    }
    for (b in blocks) {
        if (b is ThreadBlock.Tool && b.change != null && !b.failed) {
            run += b
        } else {
            flush()
            out += b
        }
    }
    flush()
    return out
}

// ───────────────────────── opencode ─────────────────────────

/** The line after a turn of opencode: the mode, the model, and the time, for example "▣  Build · Big Pickle · 21.2s". */
private val ocFooter = Regex("""^\s*▣\s+\S.*$""")

/** The time at the end of a footer of opencode. */
private val ocTime = Regex("""·\s*(\d+(?:\.\d+)?s|\d+m(?: \d+(?:\.\d+)?s)?|\d+h(?: \d+m)?)\s*$""")

/** A tool line of opencode, for example "→ Read invoice.js", "← Edit invoice.js", or "✱ Grep total". */
private val ocTool = Regex("""^([→←✱⚙◇])\s+([A-Za-z][\w-]*)\s*(.*)$""")

/** A shell command of opencode: "$ node invoice.test.js". */
private val ocShell = Regex("""^\$\s+(.+)$""")

/** A changed line of a diff of opencode: the line number, then + or -. */
private val ocDiffLine = Regex("""^\s*\d+\s+([+-])\s""")

/** A numbered line of a diff of opencode. */
private val ocNumbered = Regex("""^\s*\d+\s""")

/** The options of a tool call of opencode, for example " [replaceAll=false]". */
private val ocOptions = Regex("""\s+\[[^\]]*\]\s*$""")

/** The scroll bar at the right edge of a dialog of opencode. */
private val ocScrollBar = Regex("""\s+[█▀▄▌▐░▒▓]\s*$""")

/** The line of opencode that tells how long the model thought, for example "+ Thought: 8.3s" or "+ Thought: Planning the commit · 1.8s". */
private val ocThought = Regex("""^\+\s+Thought:\s.*$""")

/** The first choice of a question of opencode. */
private val ocChoiceStart = Regex("""^1[.)]\s+.+$""")

/** The first line of a permission dialog of opencode. */
private const val OC_ASK = "△ Permission required"

/** The text of the panel line [line] of opencode without its bar, or null when it is not a panel line. */
private fun ocPanel(line: String): String? {
    val t = line.trimStart()
    if (!t.startsWith("┃")) return null
    val c = t.removePrefix("┃")
    return if (c.startsWith("  ")) c.substring(2) else c.trimStart()
}

/**
 * True when [lines] come from opencode: they have the line after a turn,
 * the status line at the bottom, a permission dialog, or a question.
 */
fun isOpencode(lines: List<String>): Boolean =
    lines.takeLast(6).any { it.contains("ctrl+p commands") } ||
        lines.any { l -> ocFooter.matches(l) || ocPanel(l)?.let { c -> c.startsWith(OC_ASK) || ocQuestionHint.all { c.contains(it) } } == true }

/**
 * The end of the thread of opencode, the start of its dialog or null, and
 * whether it works now. [question] is true when the dialog is a question of
 * the agent, not a permission.
 */
private data class OcEnd(val end: Int, val dialog: Int?, val working: Boolean, val question: Boolean = false)

/** The hint under the choices of a question of opencode. */
private val ocQuestionHint = listOf("↑↓ select", "enter submit")

/** The first line of a question of opencode that the user answered. */
private const val OC_QUESTIONS = "# Questions"

/**
 * Finds the end of the thread of opencode: the status line, the input box,
 * and the permission dialog at the bottom do not belong to it.
 */
private fun opencodeEnd(lines: List<String>): OcEnd {
    var end = lines.size
    var working = false
    while (end > 0 && lines[end - 1].isBlank()) end--
    if (end > 0 && (lines[end - 1].contains("ctrl+p commands") || lines[end - 1].contains("esc interrupt"))) {
        working = lines[end - 1].contains("esc interrupt")
        end--
    }
    while (end > 0 && (lines[end - 1].isBlank() || lines[end - 1].trimStart().startsWith("╹"))) end--
    // A permission dialog or a question takes the place of the input box.
    var i = end
    while (i > 0 && ocPanel(lines[i - 1]) != null) i--
    val ask = (i until end).firstOrNull { ocPanel(lines[it])?.startsWith(OC_ASK) == true }
    val question = ask == null && (i until end).any { k -> ocPanel(lines[k])?.let { c -> ocQuestionHint.any { c.contains(it) } } == true }
    if (ask != null || question) {
        var e = ask ?: i
        // The tool call that waits for the answer and the line of the turn are in the dialog too.
        while (e > 0) {
            val l = lines[e - 1]
            val t = l.trim()
            val pending = ocPanel(l) == null && (t.startsWith("$ ") || t.startsWith("← ") || t.startsWith("→ Asked") || ocFooter.matches(l))
            if (t.isEmpty() || ocPanel(l)?.isBlank() == true || pending) e-- else break
        }
        return OcEnd(e, ask ?: i, working, question)
    }
    // The input box: empty lines and the line with the mode and the model.
    while (end > 0) {
        val c = ocPanel(lines[end - 1]) ?: break
        if (c.isBlank() || c.contains(" · ")) end-- else break
    }
    return OcEnd(end, null, working)
}

/** Parses the output [lines] of opencode into its thread. */
private fun opencodeThread(lines: List<String>): AgentThread {
    var (end, _, working) = opencodeEnd(lines)
    var worked = ""
    while (end > 0 && lines[end - 1].isBlank()) end--
    if (end > 0 && ocFooter.matches(lines[end - 1])) {
        if (!working) worked = ocTime.find(lines[end - 1])?.groupValues?.get(1).orEmpty()
        end--
    }
    val blocks = ArrayList<ThreadBlock>()
    val message = ArrayList<String>()
    fun flushMessage() {
        while (message.isNotEmpty() && message.last().isEmpty()) message.removeAt(message.size - 1)
        // The lines keep their indent under the first line, for example the lines of code in a message.
        // A line with 1 more blank than the first line is a new line of the text, not an indent.
        val text = dedent(message).map { if (it.startsWith(" ") && !it.startsWith("  ")) it.drop(1) else it }
        if (text.isNotEmpty()) blocks += ThreadBlock.Message(text.joinToString("\n"))
        message.clear()
    }
    var i = 0
    while (i < end) {
        val line = lines[i]
        val panel = ocPanel(line)
        if (panel != null) {
            flushMessage()
            var j = i
            while (j < end && ocPanel(lines[j]) != null) j++
            // A panel starts with a blank row. A panel at the start of the output without one lost
            // its first rows to the limit of the read, so it shows as terminal lines.
            blocks += if (i == 0 && panel.isNotBlank()) listOf(ThreadBlock.Raw(i, j)) else opencodePanel(lines.subList(i, j).map { ocPanel(it)!! })
            i = j
            continue
        }
        val t = line.trim()
        when {
            t.isEmpty() -> if (message.isNotEmpty()) message += ""
            ocFooter.matches(line) -> flushMessage()
            ocShell.matches(t) || ocTool.matches(t) -> {
                flushMessage()
                blocks += opencodeTool(t, emptyList())
            }
            // The time that the model thought, for example "+ Thought: 37.7s", is not a message.
            ocThought.matches(t) -> Unit
            // The rule where opencode compacted the session, for example "─── Compaction ───".
            titledRule.matches(t) || ruleOnly.matches(t) -> flushMessage()
            t.startsWith("···") -> {
                flushMessage()
                blocks += ThreadBlock.Raw(i, i + 1)
            }
            else -> message += line
        }
        i++
    }
    flushMessage()
    return AgentThread(mergeEdits(blocks), if (working) "Working" else "", "", worked)
}

/**
 * The blocks of a panel of opencode: a prompt of the user, then tool calls
 * with their output. A question that the user answered shows as the
 * question of the agent and the answer of the user.
 */
private fun opencodePanel(content: List<String>): List<ThreadBlock> {
    val out = ArrayList<ThreadBlock>()
    val rows = content.map { it.trim() }.filter { it.isNotEmpty() }
    if (rows.firstOrNull() == OC_QUESTIONS) {
        // Each line that ends with a question mark starts a question. The lines after it are the answer.
        var q: String? = null
        val answer = ArrayList<String>()
        fun flush() {
            q?.let { out += ThreadBlock.Message(it) }
            if (answer.isNotEmpty()) out += ThreadBlock.Prompt(answer.joinToString("\n"))
            answer.clear()
        }
        for (r in rows.drop(1)) {
            if (r.endsWith("?")) {
                flush()
                q = r
            } else {
                answer += r
            }
        }
        flush()
        return out
    }
    // A tool shows its output in a panel with a title, for example "# Todos" or "# Created a.ts".
    val title = rows.firstOrNull()?.takeIf { it.startsWith("# ") }
    if (title != null) {
        val rest = content.subList(content.indexOfFirst { it.trim() == title } + 1, content.size)
        // "# Running in /tmp/x" names the folder of the shell command under it.
        return if (rest.any(::isOcMarker)) opencodePanel(rest) else listOf(opencodeTitled(title.removePrefix("# ").trim(), rest))
    }
    var k = 0
    val prompt = ArrayList<String>()
    while (k < content.size && !isOcMarker(content[k])) prompt += content[k++]
    val text = prompt.map { it.trim() }.dropWhile { it.isEmpty() }.dropLastWhile { it.isEmpty() }.joinToString("\n")
    if (text.isNotEmpty()) out += ThreadBlock.Prompt(text)
    while (k < content.size) {
        var head = content[k++].trim()
        // A long command wraps to more rows, until the first blank row.
        if (ocShell.matches(head)) {
            while (k < content.size && content[k].isNotBlank() && !isOcMarker(content[k])) head = joinWrapped(head, content[k++].trim())
        }
        val body = ArrayList<String>()
        while (k < content.size && !isOcMarker(content[k])) body += content[k++]
        out += opencodeTool(head, body)
    }
    return out
}

/** The hints of opencode at the end of the output of a tool, for example "Click to expand". */
private val ocHints = setOf("Click to expand", "Click to collapse")

/**
 * A tool call of opencode from the title of its panel and the [body]
 * lines under it:
 * - "Created a.ts" and "Wrote a.ts" write a file, "Deleted a.ts" deletes
 *   one, and "Edited a.ts" edits one. They count the changed lines.
 * - "Todos" lists the tasks of the agent. Its arguments tell how many are done.
 * - Another title is the name of the tool.
 */
private fun opencodeTitled(title: String, body: List<String>): ThreadBlock.Tool {
    val verb = title.substringBefore(' ')
    val path = title.substringAfter(' ', "")
    val lines = dedent(body.filter { it.trim() !in ocHints })
    if (verb == "Todos") {
        val tasks = lines.filter { it.isNotBlank() }
        val done = tasks.count { it.startsWith("[✓]") || it.startsWith("[x]") }
        return ThreadBlock.Tool("Todos", if (tasks.isEmpty()) "" else "$done of ${tasks.size} done", tasks)
    }
    val name = when (verb) {
        "Created", "Wrote", "Written" -> "Write"
        "Deleted", "Removed" -> "Delete"
        "Edited", "Modified", "Patched", "Updated" -> "Edit"
        else -> return ThreadBlock.Tool(title, "", lines)
    }
    if (path.isEmpty()) return ThreadBlock.Tool(title, "", lines)
    val unified = unifySplitDiff(lines)
    val result = unified ?: lines
    val (added, minus) = diffCounts(result)
    var removed = minus
    if (name == "Delete" && removed == 0) removed = ocLineCount.find(lines.joinToString("\n"))?.groupValues?.get(1)?.toInt() ?: 0
    return ThreadBlock.Tool(name, path, result, change = FileChange(path, added, removed))
}

/** The count of the lines of a deleted file, for example "-93 lines". */
private val ocLineCount = Regex("""-(\d+) lines?""")

/** The counts of added and removed lines in the lines of a diff of opencode. */
private fun diffCounts(lines: List<String>): Pair<Int, Int> {
    val added = lines.count { ocDiffLine.find(it)?.groupValues?.get(1) == "+" || it.startsWith("+ ") || it == "+" }
    val removed = lines.count { ocDiffLine.find(it)?.groupValues?.get(1) == "-" || it.startsWith("- ") || it == "-" }
    return added to removed
}

/** A number of a line in a split diff of opencode and its mark: a blank, + or -. The group is the number. */
private val splitMark = Regex("""(?<=^| )(\d+) [ +-](?= |$)""")

/** A side of a row of a split diff: the number of the line, its mark, and its text. */
private val splitSide = Regex("""^\s*(\d+)(?: ([ +-])(?: (.*))?)?\s*$""")

/** The least column of the numbers of the right side of a split diff. */
private const val SPLIT_MIN_COL = 16

/**
 * Turns a split diff of opencode into a unified diff, or returns null when
 * [lines] are not a split diff. opencode shows the old lines at the left
 * and the new lines at the right in a wide terminal, and each side wraps
 * its long lines. On a phone, 1 column reads better: context lines start
 * with 2 blanks, removed lines with "- ", and added lines with "+ ". The
 * removed lines of a change come before its added lines.
 */
internal fun unifySplitDiff(lines: List<String>): List<String>? {
    // The numbers are aligned at their right end, so the column after the numbers of the right side is the same in each row.
    val ends = HashMap<Int, Int>()
    var leftRows = 0
    for (l in lines) {
        for (m in splitMark.findAll(l)) {
            val g = m.groups[1]!!.range
            if (g.first < SPLIT_MIN_COL) leftRows++ else ends.merge(g.last + 1, 1, Int::plus)
        }
    }
    val (right, rows) = ends.maxByOrNull { it.value } ?: return null
    if (rows < 2 || rows * 2 < leftRows) return null
    val digits = lines.maxOf { l -> if (l.length > right) l.substring(0, right).takeLastWhile { it.isDigit() }.length else 0 }
    val split = right - digits
    val out = ArrayList<String>()
    val removed = ArrayList<String>()
    val added = ArrayList<String>()
    // The line that a wrapped row continues on each side: a list and the index in it.
    var lastLeft: Pair<MutableList<String>, Int>? = null
    var lastRight: Pair<MutableList<String>, Int>? = null
    fun flush() {
        removed.forEach { out += "- $it" }
        added.forEach { out += "+ $it" }
        removed.clear()
        added.clear()
    }
    fun extend(last: Pair<MutableList<String>, Int>?, text: String) {
        if (last == null || text.isBlank()) return
        val (list, at) = last
        list[at] = joinWrapped(list[at], text.trim())
    }
    for (l in lines) {
        val leftText = l.take(split)
        val rightText = if (l.length > split) l.substring(split) else ""
        val left = splitSide.matchEntire(leftText)
        val rightSide = splitSide.matchEntire(rightText)
        if (left == null && rightSide == null) {
            // A wrapped row: each side continues its last line.
            extend(lastLeft, leftText)
            extend(lastRight, rightText)
            continue
        }
        val lm = left?.groupValues?.get(2)
        val rm = rightSide?.groupValues?.get(2)
        if (lm != "-" && rm != "+") {
            // A context line shows once.
            flush()
            out += "  " + (left ?: rightSide)!!.groupValues[3].trimEnd()
            lastLeft = out to out.lastIndex
            lastRight = null
            continue
        }
        lastLeft = null
        lastRight = null
        if (lm == "-") {
            removed += left!!.groupValues[3].trimEnd()
            lastLeft = removed to removed.lastIndex
        }
        if (rm == "+") {
            added += rightSide!!.groupValues[3].trimEnd()
            lastRight = added to added.lastIndex
        }
    }
    flush()
    return out
}

/** True when a line of a panel starts a tool call. */
private fun isOcMarker(c: String): Boolean = c.isNotEmpty() && !c.startsWith(" ") && (ocShell.matches(c) || ocTool.matches(c))

/** A tool call of opencode from its [head] line and the [body] lines under it. */
private fun opencodeTool(head: String, body: List<String>): ThreadBlock.Tool {
    val lines = dedent(body.filter { it.trim() !in ocHints })
    ocShell.matchEntire(head)?.let { return ThreadBlock.Tool("Bash", it.groupValues[1], lines) }
    val m = ocTool.matchEntire(head) ?: return ThreadBlock.Tool(head, "", lines)
    val name = m.groupValues[2]
    val args = m.groupValues[3].replace(ocOptions, "").trim()
    val result = (if (m.groupValues[1] == "←") unifySplitDiff(lines) else null) ?: lines
    val failed = result.firstOrNull()?.trimStart()?.startsWith("Error") == true
    val change = if (m.groupValues[1] == "←" && args.isNotEmpty()) {
        val (plus, removed) = diffCounts(result)
        var added = plus
        // A new file shows its lines with numbers and no mark.
        if (added == 0 && removed == 0 && name == "Write") added = result.count { ocNumbered.containsMatchIn(it) }
        FileChange(args, added, removed)
    } else {
        null
    }
    return ThreadBlock.Tool(name, args, result, failed, change)
}

/**
 * The question of the permission dialog of opencode, or null when the
 * output has no dialog. The title of the dialog, for example "# Shell
 * command", does not show. A shell command shows as the command. An edit
 * shows the file, then the first [maxLines] lines of its diff.
 */
private fun opencodeAsk(lines: List<String>, maxLines: Int): AgentAsk? {
    val end = opencodeEnd(lines)
    val ask = end.dialog ?: return null
    if (end.question) {
        // The question is the text above the first choice. The choices show as buttons.
        val text = ArrayList<String>()
        for (l in lines.subList(ask, lines.size)) {
            val c = ocPanel(l)?.trim() ?: continue
            if (ocChoiceStart.matches(c)) break
            if (c.isNotEmpty()) text += c
        }
        return AgentAsk(text.joinToString(" "), emptyList())
    }
    val rows = ArrayList<String>()
    for (l in lines.subList(ask + 1, lines.size)) {
        val c = ocPanel(l) ?: break
        if (c.contains("⇆") || c.contains("enter confirm")) break
        rows += c.replace(ocScrollBar, "").trimEnd()
    }
    val content = dedent(rows)
    if (content.isEmpty()) return AgentAsk(OC_ASK.removePrefix("△ "), emptyList())
    val title = content.first().trim()
    val body = content.drop(1).dropWhile { it.isBlank() }.filter { it.isNotBlank() }
    val question = OC_ASK.removePrefix("△ ")
    return when {
        title.startsWith("#") && body.firstOrNull()?.startsWith("$ ") == true -> AgentAsk(question, body.take(maxLines), command = true)
        title.startsWith("→ ") || title.startsWith("← ") -> AgentAsk(question, (listOf(title.drop(2)) + body).take(maxLines))
        else -> AgentAsk(question, (listOf(title) + body).take(maxLines))
    }
}

/** A part of a message: text, or a table that the agent drew with box characters. */
data class MessagePart(val text: String, val table: Boolean)

/** The first character of a row of a table that Claude Code draws. */
private const val TABLE_EDGES = "┌├└│╭╰┏┣┗┃╔╠╚║"

/** True when [line] is a row of a table: it starts with an edge and has another box character after it. */
private fun isTableRow(line: String): Boolean {
    val t = line.trimStart()
    return t.length > 1 && t[0] in TABLE_EDGES && t.drop(1).any { it in "│┃║┐┤┘┬┼┴╮╯┓┫┛╗╣╝" }
}

/**
 * Splits the text of a message into text and tables. A table keeps its
 * rows as they are, because its columns line up only in a mono font.
 */
fun messageParts(text: String): List<MessagePart> {
    val out = ArrayList<MessagePart>()
    val run = ArrayList<String>()
    var table = false
    fun flush() {
        val t = if (table) run.joinToString("\n") else run.joinToString("\n").trim('\n')
        if (t.isNotBlank()) out += MessagePart(t, table)
        run.clear()
    }
    for (l in text.split('\n')) {
        val row = isTableRow(l)
        if (row != table) {
            flush()
            table = row
        }
        run += l
    }
    flush()
    return out
}

/** The bars between the cells of a row of a table. */
private const val TABLE_BARS = "│┃║"

/**
 * The cells of a table that the agent drew with box characters, row by
 * row, or null when [text] is not such a table or its rows have different
 * counts of cells. A rule separates 2 rows. The rows between 2 rules are 1
 * row whose cells wrapped, so their parts join. The first row is the
 * header when a rule follows it.
 */
fun parseTable(text: String): List<List<String>>? {
    val rows = ArrayList<List<String>>()
    var parts: MutableList<MutableList<String>>? = null
    fun flush() {
        parts?.let { p -> rows += p.map { it.joinToString(" ") } }
        parts = null
    }
    for (raw in text.split('\n')) {
        val l = raw.trim()
        if (l.isEmpty()) continue
        if (l[0] !in TABLE_BARS) {
            flush()
            continue
        }
        val cells = l.split(*TABLE_BARS.toCharArray()).drop(1).dropLast(1).map { it.trim() }
        if (cells.isEmpty()) return null
        val p = parts ?: MutableList<MutableList<String>>(cells.size) { ArrayList() }.also { parts = it }
        if (p.size != cells.size) return null
        cells.forEachIndexed { i, c -> if (c.isNotEmpty()) p[i] += c }
    }
    flush()
    return rows.takeIf { it.isNotEmpty() && it.all { r -> r.size == it[0].size } }
}

/** The mark of a changed line in the result of an edit: + or -, after the number of the line when it has one. */
private val changeMark = Regex("""^\s*(?:\d+ ([+-])|([+-])(?: |$))""")

/** The mark of a changed line in the result of an edit, '+' or '-', or null for another line. */
fun changeMark(line: String): Char? = changeMark.find(line)?.let { m -> (m.groups[1] ?: m.groups[2])?.value?.single() }

/** The kind of a line of a diff, for its color. */
enum class DiffLineKind { Hunk, Added, Removed, Context }

/** A line of a diff. */
data class DiffLine(val text: String, val kind: DiffLineKind)

/** A file in a diff, with its counts and lines. */
data class DiffFile(val path: String, val added: Int, val removed: Int, val lines: List<DiffLine>)

/**
 * Parses the review diff that fluxd sends, a `git diff` with a list of the
 * changed files above it, into files. The lines above the first file and
 * the headers of each file do not show.
 */
fun parseDiff(lines: List<String>): List<DiffFile> {
    val files = ArrayList<DiffFile>()
    var path: String? = null
    var added = 0
    var removed = 0
    var body = ArrayList<DiffLine>()
    var inHunk = false
    fun flush() {
        // fluxd puts an empty line above each new file. It is not a line of the file before it.
        while (body.lastOrNull()?.let { it.kind == DiffLineKind.Context && it.text.isBlank() } == true) body.removeAt(body.size - 1)
        path?.let { files += DiffFile(it, added, removed, body) }
        path = null
        added = 0
        removed = 0
        body = ArrayList()
        inHunk = false
    }
    for (l in lines) {
        if (l.startsWith("diff --git ")) {
            flush()
            path = l.substringAfter(" b/", l.removePrefix("diff --git ").substringAfter("a/")).trim()
            continue
        }
        if (path == null) continue
        when {
            l.startsWith("@@") -> {
                inHunk = true
                body += DiffLine(l, DiffLineKind.Hunk)
            }
            !inHunk -> {
                // A new file of fluxd has no hunk. Its lines start with + at once.
                if (l.startsWith("+") && !l.startsWith("+++")) {
                    inHunk = true
                    added++
                    body += DiffLine(l, DiffLineKind.Added)
                } else if (l == "Binary file" || l.startsWith("The new file is larger")) {
                    body += DiffLine(l, DiffLineKind.Context)
                }
            }
            l.startsWith("+") -> {
                added++
                body += DiffLine(l, DiffLineKind.Added)
            }
            l.startsWith("-") -> {
                removed++
                body += DiffLine(l, DiffLineKind.Removed)
            }
            else -> body += DiffLine(l, DiffLineKind.Context)
        }
    }
    flush()
    // fluxd adds an empty line at the end of each new file.
    return files.map { f -> if (f.lines.lastOrNull()?.text == "+") f.copy(added = f.added - 1, lines = f.lines.dropLast(1)) else f }
}
