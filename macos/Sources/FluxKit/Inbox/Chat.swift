import Foundation

/// 1 entry of the chat. The computers write the files that come in, the
/// last clip, the answers to approvals, and the Inbox items. This device
/// writes the links, texts, clips, and files that it sent.
public enum ChatEntry: Sendable, Equatable, Identifiable {
    /// A link, a text, a scan, or a clip that this device sent.
    case sent(SentItem)
    /// A file, in either direction.
    case file(FileTransfer)
    /// The last clip from a computer.
    case clip(ClipEvent)
    /// The answer of this device to an approval. Its `resolved` time is set.
    case answer(ApproveRecord)
    /// An agent, an approval, or a pair request, at the time that it got its state.
    case event(InboxItem, at: Date)

    public var id: String {
        switch self {
        case .sent(let s): return "sent|\(s.id.uuidString)"
        case .file(let t): return "file|\(t.id.uuidString)"
        case .clip: return "clip"
        case .answer(let r): return "answer|\(r.id.uuidString)"
        case .event(let item, _): return item.key
        }
    }

    /// The time of the entry.
    public var at: Date {
        switch self {
        case .sent(let s): return s.at
        case .file(let t): return t.started
        case .clip(let c): return c.at
        case .answer(let r): return r.resolved ?? r.received
        case .event(_, let at): return at
        }
    }

    /// True when the entry shows in `scope`, or always when `scope` is nil.
    /// A pair request shows in every scope.
    public func shows(in scope: String?) -> Bool {
        guard let scope else { return true }
        switch self {
        case .sent(let s): return s.deviceId == scope
        case .file(let t): return t.deviceId == scope
        case .clip(let c): return c.deviceIds.contains(scope)
        case .answer(let r): return r.computerId == scope
        case .event(let item, _): return item.deviceIds.isEmpty || item.deviceIds.contains(scope)
        }
    }
}

/// Keeps the first time that the chat showed each Inbox item, for the
/// items whose start is not known: an approval, a pair request, and an
/// agent whose status started before this device connected, see
/// `AgentClock`. It forgets an item that is gone for more than `grace`, so
/// that an item that comes back after a short disconnect keeps its place.
public struct SeenClock: Sendable {
    /// How long the clock keeps the time of an item that is gone, 10 minutes by default.
    public var grace: TimeInterval
    private var first: [String: Date] = [:]
    private var goneAt: [String: Date] = [:]

    public init(grace: TimeInterval = 10 * 60) {
        self.grace = grace
    }

    /// Takes the keys of the items at the time `now` and returns the first time of each key.
    public mutating func update(_ keys: [String], now: Date) -> [String: Date] {
        let live = Set(keys)
        for k in first.keys where !live.contains(k) && goneAt[k] == nil { goneAt[k] = now }
        let expired = goneAt.filter { now.timeIntervalSince($0.value) > grace }.map(\.key)
        for k in expired {
            first[k] = nil
            goneAt[k] = nil
        }
        var out: [String: Date] = [:]
        for k in live {
            let t = first[k] ?? now
            first[k] = t
            goneAt[k] = nil
            out[k] = t
        }
        return out
    }
}

/// The chat: 1 thread of the computers and this device, oldest first. It
/// is a port of ChatFeed.kt of the Android app.
public enum Chat {
    /// The entries of the chat in `scope`, oldest first. `clip` shows only
    /// when it came from a computer. `answers` are the approval records,
    /// and only the approved, enrolled, and denied ones with a `resolved`
    /// time show. An agent takes the start time of its status from
    /// `since`, by computer and pane. Any other item, and an agent with no
    /// start time, takes its time from `seen`, by key. Entries with the
    /// same time keep the order of the arguments.
    public static func entries(sent: [SentItem], transfers: [FileTransfer], clip: ClipEvent?, answers: [ApproveRecord],
                               items: [InboxItem], since: (_ deviceId: String, _ pane: String) -> Date?,
                               seen: [String: Date], scope: String? = nil) -> [ChatEntry] {
        var out: [ChatEntry] = sent.map(ChatEntry.sent) + transfers.map(ChatEntry.file)
        if let clip, !clip.sent { out.append(.clip(clip)) }
        for r in answers where r.resolved != nil {
            switch r.outcome {
            case .approved, .enrolled, .denied: out.append(.answer(r))
            default: break
            }
        }
        for item in items {
            var start: Date?
            if case .agent(let deviceId, let agent, _) = item.content { start = since(deviceId, agent.pane) }
            out.append(.event(item, at: start ?? seen[item.key] ?? Date(timeIntervalSince1970: 0)))
        }
        return out.filter { $0.shows(in: scope) }.enumerated()
            .sorted { a, b in a.element.at != b.element.at ? a.element.at < b.element.at : a.offset < b.offset }
            .map { $0.element }
    }

    /// True when `c` lets the user write what the agent must do in its
    /// place, for example "No, and tell Codex what to do differently (esc)".
    public static func isTellChoice(_ c: AgentChoice) -> Bool {
        c.label.range(of: #"\btell\b.*\bdifferently\b"#, options: [.regularExpression, .caseInsensitive]) != nil
    }
}
