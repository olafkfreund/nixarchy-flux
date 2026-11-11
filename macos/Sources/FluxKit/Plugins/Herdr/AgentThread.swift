import Foundation

// The thread view of an agent: its output as messages, tool calls, file
// changes, and prompts, as in a chat. The parser reads the text lines of the
// output, as `TermText.tidy` gives them. It knows the transcripts of Claude
// Code, Codex, and opencode. Lines that it does not know stay as raw terminal lines, so
// that the view never loses output. This is a port of AgentThread.kt of the
// Android app, so both apps show the same thread.

/// A file that an agent changed, with the counts of added and removed lines.
public struct FileChange: Sendable, Hashable {
    public var path: String
    public var added: Int
    public var removed: Int

    public init(_ path: String, _ added: Int, _ removed: Int) {
        self.path = path
        self.added = added
        self.removed = removed
    }
}

/// A tool call, for example `Bash(npm test)`: the `name` of the tool, its
/// `args`, and the `result` lines under it. `failed` is true when the result
/// starts with an error. `change` is set for a file edit.
public struct ThreadTool: Sendable, Hashable {
    public var name: String
    public var args: String
    public var result: [String]
    public var failed: Bool
    public var change: FileChange?

    public init(_ name: String, _ args: String, _ result: [String], failed: Bool = false, change: FileChange? = nil) {
        self.name = name
        self.args = args
        self.result = result
        self.failed = failed
        self.change = change
    }
}

/// A run of 2 or more file edits, as 1 summary.
public struct ThreadChanges: Sendable, Hashable {
    public var files: [FileChange]

    public init(_ files: [FileChange]) { self.files = files }

    public var added: Int { files.reduce(0) { $0 + $1.added } }
    public var removed: Int { files.reduce(0) { $0 + $1.removed } }
}

/// One block of the thread of an agent.
public enum ThreadBlock: Sendable, Hashable {
    /// Text of the agent. Its lines keep their breaks.
    case message(String)
    case tool(ThreadTool)
    case changes(ThreadChanges)
    /// A prompt that the user sent to the agent.
    case prompt(String)
    /// Output lines that the parser does not know: the lines from `from` until `to` of the output.
    case raw(from: Int, to: Int)
}

/// The thread of an agent. `step` is what a working agent does now, for
/// example "Running tests", and `elapsed` is its time as the agent shows
/// it, for example "2:14". `worked` is the time of the last turn of the
/// agent after it finished, for example "6m 12s". Each of these 3 is empty
/// when the output does not show it.
public struct AgentThread: Sendable, Hashable {
    public var blocks: [ThreadBlock]
    public var step: String
    public var elapsed: String
    public var worked: String

    public init(_ blocks: [ThreadBlock], step: String = "", elapsed: String = "", worked: String = "") {
        self.blocks = blocks
        self.step = step
        self.elapsed = elapsed
        self.worked = worked
    }
}

/// The question of a blocked agent, for the dialog of the agent screen and
/// the Inbox. `question` is the line that asks, for example "Do you want to
/// proceed?", or empty. `lines` are the lines above the choices, for
/// example the command that a choice approves. `command` is true when the
/// lines are a shell command.
public struct AgentAsk: Sendable, Hashable {
    public var question: String
    public var lines: [String]
    public var command: Bool

    public init(_ question: String, _ lines: [String], command: Bool = false) {
        self.question = question
        self.lines = lines
        self.command = command
    }
}

/// The kind of a line of a diff, for its color.
public enum DiffLineKind: Sendable, Hashable {
    case hunk, added, removed, context
}

/// A line of a diff.
public struct DiffLine: Sendable, Hashable {
    public var text: String
    public var kind: DiffLineKind

    public init(_ text: String, _ kind: DiffLineKind) {
        self.text = text
        self.kind = kind
    }
}

/// A file in a diff, with its counts and lines.
public struct DiffFile: Sendable, Hashable {
    public var path: String
    public var added: Int
    public var removed: Int
    public var lines: [DiffLine]

    public init(_ path: String, added: Int, removed: Int, lines: [DiffLine]) {
        self.path = path
        self.added = added
        self.removed = removed
        self.lines = lines
    }
}

// MARK: Patterns

/// A regular expression with the ASCII classes of Kotlin. ICU gives \s, \w,
/// \d, and \b a Unicode meaning, so the patterns spell the classes out.
struct ThreadPattern: @unchecked Sendable {
    let re: NSRegularExpression

    init(_ pattern: String) {
        re = try! NSRegularExpression(pattern: pattern)
    }

    /// The groups of a match of the whole text, or nil. Group 0 is the whole
    /// text. A group that takes no part in the match is empty.
    func whole(_ s: String) -> [String]? {
        let range = NSRange(s.startIndex..., in: s)
        guard let m = re.firstMatch(in: s, range: range), m.range == range else { return nil }
        return groups(m, s)
    }

    func matches(_ s: String) -> Bool { whole(s) != nil }

    /// The first match in the text: its range and its groups.
    func first(_ s: String) -> (range: Range<String.Index>, groups: [String])? {
        guard let m = re.firstMatch(in: s, range: NSRange(s.startIndex..., in: s)), let r = Range(m.range, in: s) else { return nil }
        return (r, groups(m, s))
    }

    func contains(_ s: String) -> Bool { first(s) != nil }

    /// The ranges of group 1 of each match in the text.
    func all(_ s: String) -> [Range<String.Index>] {
        re.matches(in: s, range: NSRange(s.startIndex..., in: s)).compactMap { Range($0.range(at: 1), in: s) }
    }

    /// The text with each match replaced by `with`.
    func replace(_ s: String, with: String) -> String {
        re.stringByReplacingMatches(in: s, range: NSRange(s.startIndex..., in: s), withTemplate: NSRegularExpression.escapedTemplate(for: with))
    }

    private func groups(_ m: NSTextCheckingResult, _ s: String) -> [String] {
        (0..<m.numberOfRanges).map { i in Range(m.range(at: i), in: s).map { String(s[$0]) } ?? "" }
    }
}

/// The whitespace of Kotlin's \s, and its complement.
private let sp = #"[ \t\n\x0B\f\r]"#
private let nsp = #"[^ \t\n\x0B\f\r]"#
/// The word characters of Kotlin's \w, and the word boundaries before and after a word.
private let wordStart = #"(?<![A-Za-z0-9_])"#
private let wordEnd = #"(?![A-Za-z0-9_])"#
/// A time: 12s, 2m 5s, 1h 2m, or 2:14.
private let time = #"[0-9]+h(?: [0-9]+m)?(?: [0-9]+s)?|[0-9]+m(?: [0-9]+s)?|[0-9]+s"#

/// The bullet of a message or a tool call: ● and ⏺ of Claude Code, • of Codex. ※ marks a recap of Claude Code.
private let bulletLine = ThreadPattern("^([●⏺•※])\(sp)+(.*)$")
/// A prompt of the user: > and ❯ in Claude Code, › in Codex.
private let promptLine = ThreadPattern("^[>›❯](?:\(sp)+(.*))?$")
/// A tool call of Claude Code, for example Bash(npm test) or an MCP tool.
private let toolCall = ThreadPattern(#"^([A-Za-z][A-Za-z0-9_.-]*|[A-Za-z0-9_.-]+ - [A-Za-z0-9_.-]+ \(MCP\))\((.*)\)"# + "\(sp)*$")
/// The first line of a tool call of Claude Code whose arguments take more than 1 line.
private let toolStart = ThreadPattern(#"^([A-Za-z][A-Za-z0-9_.-]*|[A-Za-z0-9_.-]+ - [A-Za-z0-9_.-]+ \(MCP\))\((.*)$"#)
/// The tools of Claude Code. A first line with one of these names starts a tool call also before its result shows.
private let claudeTools: Set<String> = [
    "Agent", "Bash", "Edit", "Fetch", "Glob", "Grep", "LS", "MultiEdit", "NotebookEdit", "Read", "Search", "Skill", "Task",
    "TodoWrite", "Update", "WebFetch", "WebSearch", "Workflow", "Write",
]
/// A line of Claude Code that sums up the tool calls of a step, for example
/// "Read 2 files, listed 1 directory, ran 3 shell commands". Its first part
/// names a count, and it does not end like a sentence.
private let summaryLine = ThreadPattern(
    "^(Read|Reading|Searched|Searching|Listed|Listing|Ran|Running|Wrote|Writing|Made|Making|Edited|Editing|Updated|Updating|Created|Creating|"
        + "Fetched|Fetching|Recalled|Recalling|Called|Calling|Loaded|Loading|Found|Finding|Viewed|Viewing|Used|Using)( for)? [0-9]+ "
        + #"[^\n]*[^.!?:… \t\n\x0B\f\r]$"#)
/// The hint at the end of a line that the desktop can open, for example "(ctrl+o to expand)".
private let expandHint = ThreadPattern("\(sp)*" + #"\((?:ctrl|shift)\+[A-Za-z0-9_]+ to [a-z ]+\)"# + "\(sp)*$")
/// The time at the end of a tool call that runs, for example " · 11s".
private let runTime = ThreadPattern("\(sp)+·\(sp)+[0-9][0-9hms :.]*$")
/// A tool call of Codex, for example "Ran npm test" or "Edited src/a.ts (+3 -1)".
private let codexTool = ThreadPattern(
    "^(Ran|Running|Edited|Added|Deleted|Explored|Exploring|Read|Searched|Listed|Called|Calling|Waited|Updated Plan)\(wordEnd)\(sp)*(.*)$")
/// The counts at the end of a Codex edit, for example (+3 -1).
private let codexCounts = ThreadPattern("\(sp)*" + #"\(\+([0-9]+)"# + "\(sp)+" + #"-([0-9]+)\)"# + "\(sp)*$")
/// The marks at the start of a result line: ⎿ of Claude Code, └ and │ of Codex.
private let resultMark = ThreadPattern("^(\(sp)*)([⎿└│])\(sp)*")

/// The tools of Claude Code and Codex that edit a file.
private let editTools: Set<String> = ["Update", "Write", "Edit", "MultiEdit", "Create", "NotebookEdit", "Edited", "Added", "Deleted"]

private let additions = ThreadPattern("([0-9]+) additions?")
private let removals = ThreadPattern("([0-9]+) removals?")
private let addedLines = ThreadPattern("(?:Added|Wrote) ([0-9]+) lines?")
private let removedLines = ThreadPattern("[Rr]emoved ([0-9]+) lines?")

/// The working line of an agent, for example "✻ Running tests… (2:14 · esc
/// to interrupt)" of Claude Code or "• Working (12s • esc to interrupt)" of
/// Codex.
private let workLine = ThreadPattern(
    "^\(sp)*\(nsp){1,2}\(sp)+(.+?)\(sp)*" + #"\(([^()]*"# + "\(wordStart)to interrupt\(wordEnd)" + #"[^()]*)\)"# + "\(sp)*$")
/// A time in a working line or a finished line, for example 12s, 2m 5s, or 2:14.
private let timeText = ThreadPattern("\(wordStart)(\(time)|[0-9]+:[0-9]{2}(?::[0-9]{2})?)\(wordEnd)")
/// The working line of Claude Code 2.1, for example "✶ Orbiting… (1m 17s ·
/// ↓ 5.7k tokens)". The hint to interrupt is in the footer now, so the
/// spinner mark and the ellipsis tell the line.
private let spinnerLine = ThreadPattern("^[·✢✳✶✻✽*]\(sp)+(\(nsp)[^()]*?)…\(sp)*" + #"(?:\(([^()]*)\))?"# + "\(sp)*$")
/// The line after a finished turn, for example "✻ Worked for 6m 12s", "─
/// Worked for 1m 03s ───", or "✻ Crunched for 1m 12s · done Thursday 4:15 PM".
private let doneLine = ThreadPattern(
    "^[^\\p{L}\\p{N}_]*\\p{Lu}\\p{Ll}+ for (\(time))\(wordEnd)(?:\(sp)+·\(sp)+[^·]+)?[ \t\n\u{0B}\u{0C}\r─━]*$")
/// A line with only rule characters.
private let ruleOnly = ThreadPattern(#"^[ \t\n\x0B\f\r─━═╌┄╍┈┉\-_]+$"#)
/// A rule with a title, for example the rule above the input of Claude Code with the name of the session.
private let titledRule = ThreadPattern("^[─━═]{8,}.*$")
/// A row of the logo of Claude Code at the start of a session, for example " ▐▛███▜▌   Claude Code v2.1.293".
private let logoLine = ThreadPattern("^\(sp)*[▐▛▜▌▝▘▗▖▙▟█▀▄]")
/// The most rows of the logo of Claude Code.
private let logoRows = 4
/// A hint at the right edge above the input, for example "new task? /clear to save 671.7k tokens".
private let hintIndent = 30
/// The choices of a notice of Claude Code above the input, for example "1: Learn more   2: Knew this already   0: Dismiss".
private let noticeChoices = ThreadPattern("^(?:[0-9]: \(nsp).*?\(sp){2,})+[0-9]: \(nsp).*$")
/// The first numbered choice of a dialog.
private let firstChoiceLine = ThreadPattern("^\(sp)*([❯›>]\(sp)*)?1[.)]\(sp)+.+$")

/// The hints under the input of an agent.
private let footerWords = [
    "for shortcuts", "shift+tab", "context left", "accept edits", "bypass permissions", "plan mode", "auto-accept",
    "% context", "esc to cancel", "to cycle", "ctrl+", "press enter", "auto mode", "for agents", "to manage", "esc to interrupt",
]

/// How far from the end the working line can be, in lines that are not empty.
private let workScanLines = 12
/// How far above the first choice the dialog can start, in lines.
private let dialogScanLines = 16

// MARK: Text helpers with the meaning of Kotlin

private extension String {
    /// The text without whitespace at the start and the end.
    var trimmed: String { trimmingCharacters(in: .whitespacesAndNewlines) }

    /// The text without whitespace at the end.
    var trimmedEnd: String {
        var s = Substring(self)
        while let c = s.last, c.unicodeScalars.allSatisfy({ CharacterSet.whitespacesAndNewlines.contains($0) }) { s = s.dropLast() }
        return String(s)
    }

    /// True when the text is empty or has only whitespace.
    var isBlank: Bool { trimmed.isEmpty }

    /// The number of blanks at the start, or the length of a text of blanks.
    var indent: Int { prefix(while: { $0 == " " }).count }

    /// The text after the first `delimiter`, or `missing` without it.
    func after(_ delimiter: String, or missing: String) -> String {
        guard let r = range(of: delimiter) else { return missing }
        return String(self[r.upperBound...])
    }

    /// The text before the first `delimiter`, or the whole text without it.
    func before(_ delimiter: String) -> String {
        guard let r = range(of: delimiter) else { return self }
        return String(self[..<r.lowerBound])
    }
}

private func isBox(_ t: String) -> Bool {
    t.hasPrefix("╭") || t.hasPrefix("╰") || (t.hasPrefix("│") && t.hasSuffix("│") && t.count > 1)
}

/// A hint line under the input. It is indented by 2 spaces at most, so that
/// a result line does not count. A working line is not a hint, also when it
/// tells how to interrupt.
private func isFooter(_ line: String) -> Bool {
    let lower = line.lowercased()
    return line.indent <= 2 && footerWords.contains { lower.contains($0) } && !workLine.matches(line) && !spinnerLine.matches(line)
}

/// The text of a summary line without its hint and the ellipsis of a step that runs.
private func summaryText(_ line: String) -> String {
    var t = expandHint.replace(line.trimmed, with: "")
    if t.hasSuffix("…") { t.removeLast() }
    return t.trimmedEnd
}

/// True when `line` is a summary of tool calls of Claude Code, see `summaryLine`.
private func isSummary(_ line: String) -> Bool {
    line.indent == 2 && summaryLine.matches(summaryText(line))
}

/// True when `line`, at the left edge right under the first line of a
/// block, continues that line. A bullet, a prompt, a rule, a box, and the
/// lines of the status of the agent start something else.
private func continuesHead(_ line: String) -> Bool {
    let t = line.trimmed
    return !promptLine.matches(t) && !ruleOnly.matches(t) && !titledRule.matches(t) && !isBox(t) &&
        !doneLine.matches(t) && !workLine.matches(t) && !spinnerLine.matches(t)
}

// MARK: Dialog

extension AgentAsk {
    /// Finds the line where the dialog at the end of `lines` starts, or nil
    /// when the output ends with no dialog. The dialog starts at the rule or
    /// the box above its first choice, as in Claude Code. Without one, it
    /// starts at the question above the choices, as in Codex. Without a
    /// question, it takes the lines directly above the choices.
    public static func dialogStart(_ lines: [String]) -> Int? {
        if AgentChoice.find(lines).isEmpty { return nil }
        guard let first = lines.indices.reversed().first(where: { firstChoiceLine.matches(lines[$0]) }) else { return nil }
        let stop = max(0, first - dialogScanLines)
        let range = stride(from: first - 1, through: stop, by: -1)
        // The thread above the dialog ends at a message, a tool call, or a prompt.
        let thread = range.first { bulletLine.matches(lines[$0]) || promptLine.matches(lines[$0]) } ?? -1
        if let rule = range.first(where: { i in
            let t = lines[i].trimmed
            return i > thread && !t.isEmpty && (ruleOnly.matches(t) || t.hasPrefix("╭"))
        }) { return rule }
        if let ask = range.first(where: { $0 > thread && lines[$0].trimmed.hasSuffix("?") }) { return ask }
        var s = first
        while s > max(stop, thread + 1) && !lines[s - 1].isBlank { s -= 1 }
        return s
    }

    /// Returns the question of the dialog at the end of `lines`, or nil
    /// when the output ends with no choices. It keeps the `maxLines` lines
    /// nearest the choices, because they hold the command that a choice
    /// approves. The title of the dialog, for example "Bash command", does
    /// not show. After a command title, the command gets a "$ " in front.
    public static func find(_ lines: [String], maxLines: Int = 4) -> AgentAsk? {
        if AgentThread.isOpencode(lines) { return AgentThread.opencodeAsk(lines, maxLines: maxLines) }
        guard let start = dialogStart(lines) else { return nil }
        guard let first = stride(from: lines.count - 1, through: start, by: -1).first(where: { firstChoiceLine.matches(lines[$0]) }) else { return nil }
        // The lines of the dialog without rules, box edges, and empty lines. Each keeps its indent and whether a blank line follows it.
        struct Row {
            var text: String
            var indent: Int
            var gapAfter: Bool
        }
        var rows: [Row] = []
        for i in start..<first {
            var t = lines[i].trimmedEnd
            let s = t.trimmed
            if s.isEmpty {
                if !rows.isEmpty { rows[rows.count - 1].gapAfter = true }
                continue
            }
            if ruleOnly.matches(s) || s.hasPrefix("╭") || s.hasPrefix("╰") { continue }
            // The header chips of a question of Claude Code, for example "☐ Tax rate".
            if s.hasPrefix("☐") || s.hasPrefix("☒") || s.hasPrefix("✔") || s.hasPrefix("←  ☐") { continue }
            if s.hasPrefix("│") {
                var inner = Substring(s).dropFirst()
                if inner.hasSuffix("│") { inner = inner.dropLast() }
                t = String(inner).trimmedEnd
            }
            if t.isBlank { continue }
            rows.append(Row(text: t.trimmed, indent: t.indent, gapAfter: false))
        }
        if rows.isEmpty { return AgentAsk("", []) }
        var question = ""
        var body = rows
        if let last = body.last, last.text.hasSuffix("?") {
            question = last.text
            body.removeLast()
        } else if let firstRow = body.first, firstRow.text.hasSuffix("?") {
            question = firstRow.text
            body.removeFirst()
        }
        // A title: the first line, with a blank line under it, and more indented lines after it.
        var command = false
        if body.count >= 2 && body[0].gapAfter && body[1].indent > body[0].indent {
            command = body[0].text.lowercased().hasSuffix("command")
            body.removeFirst()
        }
        var text = Array(body.map(\.text).suffix(max(0, maxLines)))
        if command, let head = text.first, !head.hasPrefix("$ ") { text[0] = "$ " + head }
        if !command { command = text.first?.hasPrefix("$ ") == true }
        return AgentAsk(question, text, command: command)
    }
}

// MARK: Thread

extension AgentThread {
    /// Parses the output `lines` of an agent into its thread. The input is
    /// the plain text of each line: `HerdrOutput.lines.map(\.text)`.
    public static func parse(_ lines: [String]) -> AgentThread {
        if isOpencode(lines) { return opencode(lines) }
        let dialog = AgentAsk.dialogStart(lines)
        var end = dialog ?? stripInput(lines, end: lines.count)
        // The dialog takes the place of the input, so only the empty lines and the rules above it go.
        while dialog != nil && end > 0 && { let t = lines[end - 1].trimmed; return t.isEmpty || ruleOnly.matches(t) }() { end -= 1 }
        var step = ""
        var elapsed = ""
        var worked = ""
        // The working line sits above the input, and a list of tasks can follow it.
        var seen = 0
        var w = end - 1
        while w >= 0 && seen < workScanLines {
            let t = lines[w]
            if !t.isBlank {
                if let m = workLine.whole(t) ?? spinnerLine.whole(t) {
                    var s = m[1].trimmed
                    if s.hasSuffix("…") { s.removeLast() }
                    if s.hasSuffix("...") { s.removeLast(3) }
                    step = s.trimmed
                    elapsed = timeText.first(m[2])?.groups[0] ?? ""
                    end = w
                    while end > 0 && lines[end - 1].isBlank { end -= 1 }
                    break
                }
                seen += 1
            }
            w -= 1
        }
        if step.isEmpty && end > 0, let m = doneLine.whole(lines[end - 1].trimmedEnd) {
            worked = m[1]
            end -= 1
            while end > 0 && lines[end - 1].isBlank { end -= 1 }
        }
        return AgentThread(mergeEdits(parseBlocks(lines, end: end)), step: step, elapsed: elapsed, worked: worked)
    }

    /// Removes the input box of the agent and the hints under it from the
    /// end of `lines`, until `end`. It returns the new end. Claude Code puts
    /// the name of the session in the rule above the input, and hints at
    /// the right edge above that rule.
    static func stripInput(_ lines: [String], end: Int) -> Int {
        var i = end
        var input = false
        while i > 0 {
            let t = lines[i - 1].trimmed
            if t.isEmpty || ruleOnly.matches(t) || input && titledRule.matches(t) {
                i -= 1
            } else if isBox(t) {
                i -= 1
                input = true
            } else if !input && promptLine.matches(t) {
                i -= 1
                input = true
            } else if !input && isFooter(lines[i - 1]) {
                i -= 1
            } else if input && lines[i - 1].indent >= hintIndent {
                i -= 1
            } else if input && (lines[i - 1].hasPrefix("✦ ") || noticeChoices.matches(t)) {
                // A notice of Claude Code above the input, for example "✦ Heads up · …", and its choices.
                i -= 1
            } else {
                return i
            }
        }
        return i
    }

    /// Parses the lines until `end` into blocks.
    static func parseBlocks(_ lines: [String], end: Int) -> [ThreadBlock] {
        var blocks: [ThreadBlock] = []
        var rawFrom = -1
        var rawTo = -1
        func flushRaw() {
            if rawFrom >= 0 { blocks.append(.raw(from: rawFrom, to: rawTo)) }
            rawFrom = -1
        }
        // Claude Code blinks the bullet of a tool call that runs, so the first line of the call can have
        // 2 blanks in place of its bullet. The result mark under it tells it.
        func blinkHead(_ j: Int) -> Bool {
            j > 0 && j + 1 < end && lines[j - 1].isBlank && lines[j].indent == 2 && !lines[j].isBlank &&
                lines[j + 1].indent == 2 && trimStart(lines[j + 1]).hasPrefix("⎿")
        }
        // The end of a block that starts at `from`: the lines under it that are indented. A blank line
        // belongs to the block only when an indented line follows it. A summary of tool calls after a
        // blank line starts its own block. A line at the left edge right under the first line of the
        // block continues it, when it is not a line of its own kind.
        func blockEnd(_ from: Int) -> Int {
            var j = from + 1
            var last = from + 1
            while j < end {
                let l = lines[j]
                if l.isBlank {
                    j += 1
                    continue
                }
                if j > last && (isSummary(l) || blinkHead(j)) { break }
                if bulletLine.matches(l) { break }
                if l.indent < 2 && (j != from + 1 || !continuesHead(l)) { break }
                j += 1
                last = j
            }
            return last
        }
        var i = 0
        while i < end {
            let line = lines[i]
            if line.isBlank {
                i += 1
                continue
            }
            // The logo of Claude Code and the line after an earlier turn do not show.
            if logoLine.contains(line) {
                var k = i
                while k < end && k - i < logoRows && logoLine.contains(lines[k]) { k += 1 }
                if (i..<k).contains(where: { lines[$0].contains("Claude Code") }) {
                    flushRaw()
                    i = k
                    continue
                }
            }
            if doneLine.matches(line.trimmedEnd) {
                flushRaw()
                i += 1
                continue
            }
            if isSummary(line) {
                flushRaw()
                // A result can follow the summary, for example the note of a message that went to another session.
                var k = i + 1
                while k < end && !lines[k].isBlank && lines[k].indent >= 2 && !isSummary(lines[k]) { k += 1 }
                let text = summaryText(line)
                blocks.append(.tool(ThreadTool(text.before(" "), text.after(" ", or: ""), resultLines(Array(lines[(i + 1)..<k])))))
                i = k
                continue
            }
            let bullet = bulletLine.whole(line) ?? (blinkHead(i) ? bulletLine.whole("●" + line.dropFirst()) : nil)
            let prompt = bullet == nil ? promptLine.whole(line) : nil
            if bullet == nil && prompt == nil {
                if rawFrom < 0 { rawFrom = i }
                rawTo = i + 1
                i += 1
                continue
            }
            flushRaw()
            let to = blockEnd(i)
            let body = Array(lines[(i + 1)..<to])
            if let prompt {
                // The notes of Claude Code under a prompt, for example "⎿  1 skill available", are not part of it.
                let own = Array(body.prefix(while: { !trimStart($0).hasPrefix("⎿") }))
                let text = expandHint.replace(([prompt[1]] + dedent(own)).joined(separator: "\n").trimmed, with: "")
                if !text.isEmpty { blocks.append(.prompt(text)) }
            } else if let bullet {
                blocks.append(bulletBlock(bullet[1], bullet[2].trimmedEnd, body))
            }
            i = to
        }
        flushRaw()
        return blocks
    }

    /// Removes the indent that the `lines` share, and keeps the blank lines between them as 1 blank line.
    static func dedent(_ lines: [String]) -> [String] {
        let pad = lines.filter { !$0.isBlank }.map(\.indent).min() ?? 0
        var out: [String] = []
        for l in lines {
            if l.isBlank {
                if let last = out.last, !last.isEmpty { out.append("") }
            } else {
                out.append(String(l.dropFirst(pad)).trimmedEnd)
            }
        }
        while out.last?.isEmpty == true { out.removeLast() }
        return out
    }

    /// The result lines under a tool call, without their marks. Each line keeps its indent under the first line.
    static func resultLines(_ body: [String]) -> [String] {
        guard let first = body.first(where: { !$0.isBlank }) else { return [] }
        let col: Int
        if let m = resultMark.first(first) {
            col = first.distance(from: first.startIndex, to: m.range.upperBound)
        } else {
            col = first.indent
        }
        return dedent(body, from: col)
    }

    /// Removes up to `col` spaces from each line, and the blank lines at the ends.
    static func dedent(_ lines: [String], from col: Int) -> [String] {
        var out = lines.map { l -> String in
            if let m = resultMark.first(l) { return String(l[m.range.upperBound...]).trimmedEnd }
            return String(l.dropFirst(min(col, l.indent))).trimmedEnd
        }
        while out.first?.isBlank == true { out.removeFirst() }
        while out.last?.isBlank == true { out.removeLast() }
        return out
    }

    /// The counts of added and removed lines in the `result` of an edit tool.
    static func editCounts(_ result: [String]) -> (Int, Int) {
        let text = result.joined(separator: "\n")
        let added = (additions.first(text) ?? addedLines.first(text)).flatMap { Int($0.groups[1]) } ?? 0
        let removed = (removals.first(text) ?? removedLines.first(text)).flatMap { Int($0.groups[1]) } ?? 0
        return (added, removed)
    }

    /// A block that starts with a bullet: a tool call or a message.
    static func bulletBlock(_ bullet: String, _ head: String, _ body: [String]) -> ThreadBlock {
        if bullet != "•", let tool = claudeTool(head, body) { return .tool(tool) }
        if bullet != "•", let call = toolCall.whole(head) {
            let name = call[1]
            let args = call[2]
            let result = resultLines(body)
            let failed = result.first?.drop(while: { $0.isWhitespace }).hasPrefix("Error") == true
            var change: FileChange?
            if editTools.contains(name) && !args.isBlank {
                let (a, r) = editCounts(result)
                change = FileChange(args.before(", ").trimmed, a, r)
            }
            return .tool(ThreadTool(name, args, result, failed: failed, change: change))
        }
        let codex = bullet == "•" ? codexTool.whole(head) : nil
        let marked = body.first(where: { !$0.isBlank }).map { resultMark.contains($0) } ?? false
        if let codex, marked || codexCounts.contains(head) {
            let name = codex[1]
            var args = codex[2]
            var change: FileChange?
            let counts = codexCounts.first(args)
            if editTools.contains(name) {
                let path = codexCounts.replace(args, with: "").trimmed
                change = FileChange(path, counts.flatMap { Int($0.groups[1]) } ?? 0, counts.flatMap { Int($0.groups[2]) } ?? 0)
                args = path
            }
            return .tool(ThreadTool(name, args, resultLines(body), failed: false, change: change))
        }
        return .message(([head] + dedent(body)).joined(separator: "\n").trimmed)
    }

    /// A tool call of Claude Code 2.1 that the plain `toolCall` does not
    /// match, or nil:
    /// - A shell command that runs shows its description and then the
    ///   command as the result, for example "Run the tests · 11s" and
    ///   "⎿  $ npm test".
    /// - The arguments of a long call take more than 1 line, and the closing
    ///   parenthesis is on the last of them or cut.
    static func claudeTool(_ head: String, _ body: [String]) -> ThreadTool? {
        guard let first = body.firstIndex(where: { !$0.isBlank }) else { return nil }
        if let mark = resultMark.first(body[first]), mark.groups[2] == "⎿", body[first][mark.range.upperBound...].hasPrefix("$ ") {
            let result = resultLines(body).filter { !expandHint.matches($0) && $0.trimmed != "(ctrl+b to run in background)" }
            return ThreadTool("Bash", runTime.replace(head, with: "").trimmed, result)
        }
        if toolCall.matches(head) { return nil }
        guard let start = toolStart.whole(head) else { return nil }
        let name = start[1]
        let resultAt = body.firstIndex { resultMark.first($0)?.groups[2] == "⎿" }
        if resultAt == nil && !claudeTools.contains(name) { return nil }
        let argLines = body[..<(resultAt ?? body.count)].map(\.trimmed).filter { !$0.isEmpty }
        var args = ([start[2].trimmed] + argLines).joined(separator: " ")
        if args.hasSuffix(")") { args.removeLast() }
        args = args.trimmed
        let result = resultAt.map { resultLines(Array(body[$0...])) } ?? []
        let failed = result.first.map { trimStart($0).hasPrefix("Error") } ?? false
        var change: FileChange?
        if editTools.contains(name) && !args.isBlank {
            let (a, r) = editCounts(result)
            change = FileChange(args.before(", ").trimmed, a, r)
        }
        return ThreadTool(name, args, result, failed: failed, change: change)
    }

    /// Turns each run of 2 or more file edits into 1 `ThreadBlock.changes`. The same file shows once, with the sum of its counts.
    static func mergeEdits(_ blocks: [ThreadBlock]) -> [ThreadBlock] {
        var out: [ThreadBlock] = []
        var run: [ThreadTool] = []
        func flush() {
            if run.count >= 2 {
                var files: [FileChange] = []
                for t in run {
                    guard let c = t.change else { continue }
                    if let i = files.firstIndex(where: { $0.path == c.path }) {
                        files[i].added += c.added
                        files[i].removed += c.removed
                    } else {
                        files.append(c)
                    }
                }
                out.append(.changes(ThreadChanges(files)))
            } else {
                out.append(contentsOf: run.map { .tool($0) })
            }
            run = []
        }
        for b in blocks {
            if case let .tool(t) = b, t.change != nil, !t.failed {
                run.append(t)
            } else {
                flush()
                out.append(b)
            }
        }
        flush()
        return out
    }
}

// MARK: opencode

/// The line after a turn of opencode: the mode, the model, and the time, for example "▣  Build · Big Pickle · 21.2s".
private let ocFooter = ThreadPattern("^\(sp)*▣\(sp)+\(nsp).*$")
/// The time at the end of a footer of opencode.
private let ocTime = ThreadPattern("·\(sp)*([0-9]+(?:\\.[0-9]+)?s|[0-9]+m(?: [0-9]+(?:\\.[0-9]+)?s)?|[0-9]+h(?: [0-9]+m)?)\(sp)*$")
/// A tool line of opencode, for example "→ Read invoice.js", "← Edit invoice.js", or "✱ Grep total".
private let ocTool = ThreadPattern("^([→←✱⚙◇])\(sp)+([A-Za-z][A-Za-z0-9_-]*)\(sp)*(.*)$")
/// A shell command of opencode: "$ node invoice.test.js".
private let ocShell = ThreadPattern("^\\$\(sp)+(.+)$")
/// A changed line of a diff of opencode: the line number, then + or -.
private let ocDiffLine = ThreadPattern("^\(sp)*[0-9]+\(sp)+([+-])\(sp)")
/// A numbered line of a diff of opencode.
private let ocNumbered = ThreadPattern("^\(sp)*[0-9]+\(sp)")
/// The options of a tool call of opencode, for example " [replaceAll=false]".
private let ocOptions = ThreadPattern("\(sp)+\\[[^\\]]*\\]\(sp)*$")
/// The scroll bar at the right edge of a dialog of opencode.
private let ocScrollBar = ThreadPattern("\(sp)+[█▀▄▌▐░▒▓]\(sp)*$")
/// The line of opencode that tells how long the model thought, for example "+ Thought: 8.3s" or "+ Thought: Planning the commit · 1.8s".
private let ocThought = ThreadPattern("^\\+\(sp)+Thought:\(sp).*$")
/// The first choice of a question of opencode.
private let ocChoiceStart = ThreadPattern("^1[.)]\(sp)+.+$")
/// The first line of a permission dialog of opencode.
private let ocAsk = "△ Permission required"
/// The hint under the choices of a question of opencode.
private let ocQuestionHint = ["↑↓ select", "enter submit"]
/// The first line of a question of opencode that the user answered.
private let ocQuestions = "# Questions"
/// The hints of opencode at the end of the output of a tool, for example "Click to expand".
private let ocHints: Set<String> = ["Click to expand", "Click to collapse"]
/// The count of the lines of a deleted file, for example "-93 lines".
private let ocLineCount = ThreadPattern("-([0-9]+) lines?")
/// A number of a line in a split diff of opencode and its mark: a blank, + or -. Group 1 is the number.
private let splitMark = ThreadPattern("(?<=^| )([0-9]+) [ +-](?= |$)")
/// A side of a row of a split diff: the number of the line, its mark, and its text.
private let splitSide = ThreadPattern("^\(sp)*([0-9]+)(?: ([ +-])(?: (.*))?)?\(sp)*$")
/// The least column of the numbers of the right side of a split diff.
private let splitMinCol = 16

/// The text without whitespace at the start, as Kotlin's trimStart.
private func trimStart(_ s: String) -> String {
    String(s.drop(while: { $0.unicodeScalars.allSatisfy { CharacterSet.whitespacesAndNewlines.contains($0) } }))
}

/// The text of the panel line `line` of opencode without its bar, or nil when it is not a panel line.
private func ocPanel(_ line: String) -> String? {
    let t = trimStart(line)
    guard t.hasPrefix("┃") else { return nil }
    let c = String(t.dropFirst())
    return c.hasPrefix("  ") ? String(c.dropFirst(2)) : trimStart(c)
}

/// True when a line of a panel starts a tool call.
private func isOcMarker(_ c: String) -> Bool {
    !c.isEmpty && !c.hasPrefix(" ") && (ocShell.matches(c) || ocTool.matches(c))
}

extension AgentThread {
    /// True when `lines` come from opencode: they have the line after a
    /// turn, the status line at the bottom, a permission dialog, or a
    /// question.
    public static func isOpencode(_ lines: [String]) -> Bool {
        lines.suffix(6).contains { $0.contains("ctrl+p commands") } ||
            lines.contains { l in
                if ocFooter.matches(l) { return true }
                guard let c = ocPanel(l) else { return false }
                return c.hasPrefix(ocAsk) || ocQuestionHint.allSatisfy { c.contains($0) }
            }
    }

    /// Finds the end of the thread of opencode: the status line, the input
    /// box, and the permission dialog or the question at the bottom do not
    /// belong to it. It gives the end, the start of the dialog or nil,
    /// whether the agent works now, and whether the dialog is a question of
    /// the agent, not a permission.
    static func opencodeEnd(_ lines: [String]) -> (end: Int, dialog: Int?, working: Bool, question: Bool) {
        var end = lines.count
        var working = false
        while end > 0 && lines[end - 1].isBlank { end -= 1 }
        if end > 0 && (lines[end - 1].contains("ctrl+p commands") || lines[end - 1].contains("esc interrupt")) {
            working = lines[end - 1].contains("esc interrupt")
            end -= 1
        }
        while end > 0 && (lines[end - 1].isBlank || trimStart(lines[end - 1]).hasPrefix("╹")) { end -= 1 }
        // A permission dialog or a question takes the place of the input box.
        var i = end
        while i > 0 && ocPanel(lines[i - 1]) != nil { i -= 1 }
        let ask = (i..<end).first(where: { ocPanel(lines[$0])?.hasPrefix(ocAsk) == true })
        let question = ask == nil && (i..<end).contains { k in
            guard let c = ocPanel(lines[k]) else { return false }
            return ocQuestionHint.contains { c.contains($0) }
        }
        if ask != nil || question {
            var e = ask ?? i
            // The tool call that waits for the answer and the line of the turn are in the dialog too.
            while e > 0 {
                let l = lines[e - 1]
                let t = l.trimmed
                let pending = ocPanel(l) == nil && (t.hasPrefix("$ ") || t.hasPrefix("← ") || t.hasPrefix("→ Asked") || ocFooter.matches(l))
                if t.isEmpty || ocPanel(l)?.isBlank == true || pending { e -= 1 } else { break }
            }
            return (e, ask ?? i, working, question)
        }
        // The input box: empty lines and the line with the mode and the model.
        while end > 0 {
            guard let c = ocPanel(lines[end - 1]) else { break }
            if c.isBlank || c.contains(" · ") { end -= 1 } else { break }
        }
        return (end, nil, working, false)
    }

    /// Parses the output `lines` of opencode into its thread.
    static func opencode(_ lines: [String]) -> AgentThread {
        let found = opencodeEnd(lines)
        var end = found.end
        let working = found.working
        var worked = ""
        while end > 0 && lines[end - 1].isBlank { end -= 1 }
        if end > 0 && ocFooter.matches(lines[end - 1]) {
            if !working { worked = ocTime.first(lines[end - 1])?.groups[1] ?? "" }
            end -= 1
        }
        var blocks: [ThreadBlock] = []
        var message: [String] = []
        func flushMessage() {
            while message.last?.isEmpty == true { message.removeLast() }
            // The lines keep their indent under the first line, for example the lines of code in a message.
            // A line with 1 more blank than the first line is a new line of the text, not an indent.
            let text = dedent(message).map { $0.hasPrefix(" ") && !$0.hasPrefix("  ") ? String($0.dropFirst()) : $0 }
            if !text.isEmpty { blocks.append(.message(text.joined(separator: "\n"))) }
            message = []
        }
        var i = 0
        while i < end {
            let line = lines[i]
            if let panel = ocPanel(line) {
                flushMessage()
                var j = i
                while j < end && ocPanel(lines[j]) != nil { j += 1 }
                // A panel starts with a blank row. A panel at the start of the output without one lost
                // its first rows to the limit of the read, so it shows as terminal lines.
                if i == 0 && !panel.isBlank {
                    blocks.append(.raw(from: i, to: j))
                } else {
                    blocks.append(contentsOf: opencodePanel(lines[i..<j].compactMap(ocPanel)))
                }
                i = j
                continue
            }
            let t = line.trimmed
            if t.isEmpty {
                if !message.isEmpty { message.append("") }
            } else if ocFooter.matches(line) {
                flushMessage()
            } else if ocShell.matches(t) || ocTool.matches(t) {
                flushMessage()
                blocks.append(.tool(opencodeTool(t, [])))
            } else if ocThought.matches(t) {
                // The time that the model thought, for example "+ Thought: 37.7s", is not a message.
            } else if titledRule.matches(t) || ruleOnly.matches(t) {
                // The rule where opencode compacted the session, for example "─── Compaction ───".
                flushMessage()
            } else if t.hasPrefix("···") {
                flushMessage()
                blocks.append(.raw(from: i, to: i + 1))
            } else {
                message.append(line)
            }
            i += 1
        }
        flushMessage()
        return AgentThread(mergeEdits(blocks), step: working ? "Working" : "", elapsed: "", worked: worked)
    }

    /// The blocks of a panel of opencode: a prompt of the user, then tool
    /// calls with their output. A question that the user answered shows as
    /// the question of the agent and the answer of the user.
    static func opencodePanel(_ content: [String]) -> [ThreadBlock] {
        var out: [ThreadBlock] = []
        let rows = content.map(\.trimmed).filter { !$0.isEmpty }
        if rows.first == ocQuestions {
            // Each line that ends with a question mark starts a question. The lines after it are the answer.
            var q: String?
            var answer: [String] = []
            func flush() {
                if let q { out.append(.message(q)) }
                if !answer.isEmpty { out.append(.prompt(answer.joined(separator: "\n"))) }
                answer = []
            }
            for r in rows.dropFirst() {
                if r.hasSuffix("?") {
                    flush()
                    q = r
                } else {
                    answer.append(r)
                }
            }
            flush()
            return out
        }
        // A tool shows its output in a panel with a title, for example "# Todos" or "# Created a.ts".
        if let title = rows.first, title.hasPrefix("# ") {
            let at = content.firstIndex { $0.trimmed == title } ?? 0
            let rest = Array(content[(at + 1)...])
            // "# Running in /tmp/x" names the folder of the shell command under it.
            if rest.contains(where: isOcMarker) { return opencodePanel(rest) }
            return [.tool(opencodeTitled(String(title.dropFirst(2)).trimmed, rest))]
        }
        var k = 0
        var prompt: [String] = []
        while k < content.count && !isOcMarker(content[k]) {
            prompt.append(content[k])
            k += 1
        }
        var trimmedPrompt = prompt.map(\.trimmed)
        while trimmedPrompt.first?.isEmpty == true { trimmedPrompt.removeFirst() }
        while trimmedPrompt.last?.isEmpty == true { trimmedPrompt.removeLast() }
        let text = trimmedPrompt.joined(separator: "\n")
        if !text.isEmpty { out.append(.prompt(text)) }
        while k < content.count {
            var head = content[k].trimmed
            k += 1
            // A long command wraps to more rows, until the first blank row.
            if ocShell.matches(head) {
                while k < content.count && !content[k].isBlank && !isOcMarker(content[k]) {
                    head = TermText.joinWrapped(head, content[k].trimmed)
                    k += 1
                }
            }
            var body: [String] = []
            while k < content.count && !isOcMarker(content[k]) {
                body.append(content[k])
                k += 1
            }
            out.append(.tool(opencodeTool(head, body)))
        }
        return out
    }

    /// A tool call of opencode from the title of its panel and the `body`
    /// lines under it:
    /// - "Created a.ts" and "Wrote a.ts" write a file, "Deleted a.ts"
    ///   deletes one, and "Edited a.ts" edits one. They count the changed lines.
    /// - "Todos" lists the tasks of the agent. Its arguments tell how many are done.
    /// - Another title is the name of the tool.
    static func opencodeTitled(_ title: String, _ body: [String]) -> ThreadTool {
        let verb = title.before(" ")
        let path = title.after(" ", or: "")
        let lines = dedent(body.filter { !ocHints.contains($0.trimmed) })
        if verb == "Todos" {
            let tasks = lines.filter { !$0.isBlank }
            let done = tasks.filter { $0.hasPrefix("[✓]") || $0.hasPrefix("[x]") }.count
            return ThreadTool("Todos", tasks.isEmpty ? "" : "\(done) of \(tasks.count) done", tasks)
        }
        let name: String
        switch verb {
        case "Created", "Wrote", "Written": name = "Write"
        case "Deleted", "Removed": name = "Delete"
        case "Edited", "Modified", "Patched", "Updated": name = "Edit"
        default: return ThreadTool(title, "", lines)
        }
        if path.isEmpty { return ThreadTool(title, "", lines) }
        let result = unifySplitDiff(lines) ?? lines
        let (added, minus) = diffCounts(result)
        var removed = minus
        if name == "Delete" && removed == 0 { removed = ocLineCount.first(lines.joined(separator: "\n")).flatMap { Int($0.groups[1]) } ?? 0 }
        return ThreadTool(name, path, result, change: FileChange(path, added, removed))
    }

    /// The counts of added and removed lines in the lines of a diff of opencode.
    static func diffCounts(_ lines: [String]) -> (Int, Int) {
        let added = lines.filter { ocDiffLine.first($0)?.groups[1] == "+" || $0.hasPrefix("+ ") || $0 == "+" }.count
        let removed = lines.filter { ocDiffLine.first($0)?.groups[1] == "-" || $0.hasPrefix("- ") || $0 == "-" }.count
        return (added, removed)
    }

    /// Turns a split diff of opencode into a unified diff, or returns nil
    /// when `lines` are not a split diff. opencode shows the old lines at
    /// the left and the new lines at the right in a wide terminal, and each
    /// side wraps its long lines. On a phone, 1 column reads better: context
    /// lines start with 2 blanks, removed lines with "- ", and added lines
    /// with "+ ". The removed lines of a change come before its added lines.
    public static func unifySplitDiff(_ lines: [String]) -> [String]? {
        // The numbers are aligned at their right end, so the column after the numbers of the right side is the same in each row.
        var ends: [Int: Int] = [:]
        var leftRows = 0
        for l in lines {
            for g in splitMark.all(l) {
                let start = l.distance(from: l.startIndex, to: g.lowerBound)
                if start < splitMinCol { leftRows += 1 } else { ends[start + l[g].count, default: 0] += 1 }
            }
        }
        guard let best = ends.max(by: { $0.value < $1.value || $0.value == $1.value && $0.key > $1.key }) else { return nil }
        let right = best.key
        if best.value < 2 || best.value * 2 < leftRows { return nil }
        let digits = lines.map { l -> Int in
            let c = Array(l)
            guard c.count > right else { return 0 }
            return c[..<right].reversed().prefix(while: { $0.isASCII && $0.isNumber }).count
        }.max() ?? 0
        let split = right - digits
        var out: [String] = []
        var removed: [String] = []
        var added: [String] = []
        // The line that a wrapped row continues on each side.
        enum Target { case out(Int), removed(Int), added(Int) }
        var lastLeft: Target?
        var lastRight: Target?
        func flush() {
            out += removed.map { "- " + $0 }
            out += added.map { "+ " + $0 }
            removed = []
            added = []
        }
        func extend(_ target: Target?, _ text: String) {
            guard let target, !text.isBlank else { return }
            switch target {
            case let .out(i): out[i] = TermText.joinWrapped(out[i], text.trimmed)
            case let .removed(i): removed[i] = TermText.joinWrapped(removed[i], text.trimmed)
            case let .added(i): added[i] = TermText.joinWrapped(added[i], text.trimmed)
            }
        }
        for l in lines {
            let c = Array(l)
            let leftText = String(c.prefix(split))
            let rightText = c.count > split ? String(c[split...]) : ""
            let left = splitSide.whole(leftText)
            let rightSide = splitSide.whole(rightText)
            if left == nil && rightSide == nil {
                // A wrapped row: each side continues its last line.
                extend(lastLeft, leftText)
                extend(lastRight, rightText)
                continue
            }
            let lm = left?[2]
            let rm = rightSide?[2]
            if lm != "-" && rm != "+" {
                // A context line shows once.
                flush()
                out.append("  " + (left ?? rightSide!)[3].trimmedEnd)
                lastLeft = .out(out.count - 1)
                lastRight = nil
                continue
            }
            lastLeft = nil
            lastRight = nil
            if lm == "-", let left {
                removed.append(left[3].trimmedEnd)
                lastLeft = .removed(removed.count - 1)
            }
            if rm == "+", let rightSide {
                added.append(rightSide[3].trimmedEnd)
                lastRight = .added(added.count - 1)
            }
        }
        flush()
        return out
    }

    /// A tool call of opencode from its `head` line and the `body` lines under it.
    static func opencodeTool(_ head: String, _ body: [String]) -> ThreadTool {
        let lines = dedent(body.filter { !ocHints.contains($0.trimmed) })
        if let m = ocShell.whole(head) { return ThreadTool("Bash", m[1], lines) }
        guard let m = ocTool.whole(head) else { return ThreadTool(head, "", lines) }
        let name = m[2]
        let args = ocOptions.replace(m[3], with: "").trimmed
        let result = (m[1] == "←" ? unifySplitDiff(lines) : nil) ?? lines
        let failed = result.first.map { trimStart($0).hasPrefix("Error") } ?? false
        var change: FileChange?
        if m[1] == "←" && !args.isEmpty {
            let (plus, removed) = diffCounts(result)
            var added = plus
            // A new file shows its lines with numbers and no mark.
            if added == 0 && removed == 0 && name == "Write" { added = result.filter { ocNumbered.contains($0) }.count }
            change = FileChange(args, added, removed)
        }
        return ThreadTool(name, args, result, failed: failed, change: change)
    }

    /// The question of the permission dialog of opencode, or nil when the
    /// output has no dialog. The title of the dialog, for example "# Shell
    /// command", does not show. A shell command shows as the command. An
    /// edit shows the file, then the first `maxLines` lines of its diff.
    static func opencodeAsk(_ lines: [String], maxLines: Int) -> AgentAsk? {
        let end = opencodeEnd(lines)
        guard let ask = end.dialog else { return nil }
        if end.question {
            // The question is the text above the first choice. The choices show as buttons.
            var text: [String] = []
            for l in lines[ask...] {
                guard let c = ocPanel(l)?.trimmed else { continue }
                if ocChoiceStart.matches(c) { break }
                if !c.isEmpty { text.append(c) }
            }
            return AgentAsk(text.joined(separator: " "), [])
        }
        var rows: [String] = []
        for l in lines[(ask + 1)...] {
            guard let c = ocPanel(l) else { break }
            if c.contains("⇆") || c.contains("enter confirm") { break }
            rows.append(ocScrollBar.replace(c, with: "").trimmedEnd)
        }
        let content = dedent(rows)
        let question = String(ocAsk.dropFirst(2))
        guard let first = content.first else { return AgentAsk(question, []) }
        let title = first.trimmed
        let body = content.dropFirst().drop(while: { $0.isBlank }).filter { !$0.isBlank }
        let n = max(0, maxLines)
        if title.hasPrefix("#") && body.first?.hasPrefix("$ ") == true {
            return AgentAsk(question, Array(body.prefix(n)), command: true)
        }
        if title.hasPrefix("→ ") || title.hasPrefix("← ") {
            return AgentAsk(question, Array(([String(title.dropFirst(2))] + body).prefix(n)))
        }
        return AgentAsk(question, Array(([title] + body).prefix(n)))
    }
}

// MARK: Message parts

/// A part of a message: text, or a table that the agent drew with box characters.
public struct MessagePart: Sendable, Hashable {
    public var text: String
    public var table: Bool

    public init(_ text: String, table: Bool) {
        self.text = text
        self.table = table
    }
}

/// The first character of a row of a table that Claude Code draws.
private let tableEdges = Set("┌├└│╭╰┏┣┗┃╔╠╚║")
/// The characters after the first one that make a row of a table.
private let tableInner = Set("│┃║┐┤┘┬┼┴╮╯┓┫┛╗╣╝")
/// The bars between the cells of a row of a table.
private let tableBars = Set("│┃║")
/// The mark of a changed line in the result of an edit: + or -, after the number of the line when it has one.
private let changeMarkLine = ThreadPattern("^\(sp)*(?:[0-9]+ ([+-])|([+-])(?: |$))")

extension AgentThread {
    /// True when `line` is a row of a table: it starts with an edge and has another box character after it.
    static func isTableRow(_ line: String) -> Bool {
        let t = trimStart(line)
        guard let first = t.first, t.count > 1, tableEdges.contains(first) else { return false }
        return t.dropFirst().contains { tableInner.contains($0) }
    }

    /// Splits the text of a message into text and tables. A table keeps its
    /// rows as they are, because its columns line up only in a mono font.
    public static func messageParts(_ text: String) -> [MessagePart] {
        var out: [MessagePart] = []
        var run: [String] = []
        var table = false
        func flush() {
            var t = run.joined(separator: "\n")
            if !table {
                while t.hasPrefix("\n") { t.removeFirst() }
                while t.hasSuffix("\n") { t.removeLast() }
            }
            if !t.isBlank { out.append(MessagePart(t, table: table)) }
            run = []
        }
        for l in text.split(separator: "\n", omittingEmptySubsequences: false).map(String.init) {
            let row = isTableRow(l)
            if row != table {
                flush()
                table = row
            }
            run.append(l)
        }
        flush()
        return out
    }

    /// The cells of a table that the agent drew with box characters, row by
    /// row, or nil when `text` is not such a table or its rows have
    /// different counts of cells. A rule separates 2 rows. The rows between
    /// 2 rules are 1 row whose cells wrapped, so their parts join. The first
    /// row is the header when a rule follows it.
    public static func parseTable(_ text: String) -> [[String]]? {
        var rows: [[String]] = []
        var parts: [[String]]?
        func flush() {
            if let parts { rows.append(parts.map { $0.joined(separator: " ") }) }
            parts = nil
        }
        for raw in text.split(separator: "\n", omittingEmptySubsequences: false) {
            let l = String(raw).trimmed
            guard let first = l.first else { continue }
            if !tableBars.contains(first) {
                flush()
                continue
            }
            let cells = l.split(omittingEmptySubsequences: false, whereSeparator: { tableBars.contains($0) }).dropFirst().dropLast().map { String($0).trimmed }
            if cells.isEmpty { return nil }
            var p = parts ?? Array(repeating: [], count: cells.count)
            if p.count != cells.count { return nil }
            for (i, c) in cells.enumerated() where !c.isEmpty { p[i].append(c) }
            parts = p
        }
        flush()
        guard let width = rows.first?.count, rows.allSatisfy({ $0.count == width }) else { return nil }
        return rows
    }

    /// The mark of a changed line in the result of an edit, "+" or "-", or nil for another line.
    public static func changeMark(_ line: String) -> Character? {
        guard let m = changeMarkLine.first(line) else { return nil }
        return (m.groups[1].isEmpty ? m.groups[2] : m.groups[1]).first
    }
}

// MARK: Diff

extension DiffFile {
    /// Parses the review diff that fluxd sends, a `git diff` with a list of
    /// the changed files above it, into files. The lines above the first
    /// file and the headers of each file do not show.
    public static func parse(_ lines: [String]) -> [DiffFile] {
        var files: [DiffFile] = []
        var path: String?
        var added = 0
        var removed = 0
        var body: [DiffLine] = []
        var inHunk = false
        func flush() {
            // fluxd puts an empty line above each new file. It is not a line of the file before it.
            while let last = body.last, last.kind == .context, last.text.isBlank { body.removeLast() }
            if let path { files.append(DiffFile(path, added: added, removed: removed, lines: body)) }
            path = nil
            added = 0
            removed = 0
            body = []
            inHunk = false
        }
        for l in lines {
            if l.hasPrefix("diff --git ") {
                flush()
                let rest = String(l.dropFirst("diff --git ".count))
                path = l.after(" b/", or: rest.after("a/", or: rest)).trimmed
                continue
            }
            if path == nil { continue }
            if l.hasPrefix("@@") {
                inHunk = true
                body.append(DiffLine(l, .hunk))
            } else if !inHunk {
                // A new file of fluxd has no hunk. Its lines start with + at once.
                if l.hasPrefix("+") && !l.hasPrefix("+++") {
                    inHunk = true
                    added += 1
                    body.append(DiffLine(l, .added))
                } else if l == "Binary file" || l.hasPrefix("The new file is larger") {
                    body.append(DiffLine(l, .context))
                }
            } else if l.hasPrefix("+") {
                added += 1
                body.append(DiffLine(l, .added))
            } else if l.hasPrefix("-") {
                removed += 1
                body.append(DiffLine(l, .removed))
            } else {
                body.append(DiffLine(l, .context))
            }
        }
        flush()
        // fluxd adds an empty line at the end of each new file.
        return files.map { f in
            guard f.lines.last?.text == "+" else { return f }
            return DiffFile(f.path, added: f.added - 1, removed: f.removed, lines: Array(f.lines.dropLast()))
        }
    }
}
