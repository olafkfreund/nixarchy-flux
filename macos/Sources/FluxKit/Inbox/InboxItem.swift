import Foundation

/// The kinds of Inbox items. The first 3 kinds need the user: an agent
/// that waits for input, an approval, and a pair request. The agents that
/// are done or that work follow. The chat shows the items in the order of
/// their time, see `Chat.entries`. Nothing stores the order.
public enum InboxKind: Int, CaseIterable, Sendable, Comparable {
    case agentInput, approval, pairRequest, agentDone, agentWorking

    /// True for the kinds that need the user.
    public var needsYou: Bool { self == .agentInput || self == .approval || self == .pairRequest }

    /// "AgentInput", "Approval", "PairRequest", "AgentDone", or
    /// "AgentWorking", as on Android. Item keys use it.
    public var keyName: String {
        switch self {
        case .agentInput: return "AgentInput"
        case .approval: return "Approval"
        case .pairRequest: return "PairRequest"
        case .agentDone: return "AgentDone"
        case .agentWorking: return "AgentWorking"
        }
    }

    public static func < (a: InboxKind, b: InboxKind) -> Bool { a.rawValue < b.rawValue }
}

/// The color role of the state of an item. The apps map it to the palette.
/// Red marks only what needs the user and errors.
public enum InboxTone: Sendable, Equatable {
    case red, green, accent, cyan, sub
}

/// 1 item of the Inbox: something on a computer that has a state now.
/// `key` stays the same while the item is the same, so the UI keeps its
/// place. `deviceIds` are the computers of the item. An item with no
/// computers shows in every scope.
public struct InboxItem: Sendable, Equatable, Identifiable {
    public enum Content: Sendable, Equatable {
        /// A herdr agent that waits for input, finished, or works. `control`
        /// is true when the computer takes replies.
        case agent(deviceId: String, agent: HerdrAgent, control: Bool)
        /// A sudo, polkit, or enrollment request that waits for the fingerprint.
        case approval(ApproveRequest)
        /// A computer that is not paired and asks to pair.
        case pair(deviceId: String)
    }

    public var content: Content
    /// The name of the computer.
    public var computer: String

    public init(_ content: Content, computer: String) {
        self.content = content
        self.computer = computer
    }

    public var kind: InboxKind {
        switch content {
        case .agent(_, let agent, _):
            switch agent.status {
            case .blocked: return .agentInput
            case .done: return .agentDone
            default: return .agentWorking
            }
        case .approval: return .approval
        case .pair: return .pairRequest
        }
    }

    /// A new status of an agent is a new key, so an agent with a new status is a new chat entry.
    public var key: String {
        switch content {
        case .agent(let deviceId, let agent, _): return "agent|\(deviceId)|\(agent.pane)|\(kind.keyName)"
        case .approval(let r): return "approve|\(r.computerId)|\(r.id)"
        case .pair(let deviceId): return "pair|\(deviceId)"
        }
    }

    /// The computers of the item. A pair request has none, so it shows in every scope.
    public var deviceIds: Set<String> {
        switch content {
        case .agent(let deviceId, _, _): return [deviceId]
        case .approval(let r): return [r.computerId]
        case .pair: return []
        }
    }

    public var id: String { key }
}

/// The text of an item, as in the chat of Flux for Android.
public extension InboxItem {
    /// The state as a short word in sentence case, for the top row of a card and for VoiceOver.
    var stateWord: String {
        switch content {
        case .agent:
            switch kind {
            case .agentInput: return "Needs input"
            case .agentDone: return "Done"
            default: return "Working"
            }
        case .approval: return "Needs approval"
        case .pair: return "Pair request"
        }
    }

    /// The color role of the state dot and the state word.
    var tone: InboxTone {
        if kind.needsYou { return .red }
        return kind == .agentDone ? .green : .accent
    }

    /// The source of the item for the top row of its card, without the
    /// computer: for example the agent and its project. Blank parts are dropped.
    var sourceParts: [String] {
        let parts: [String]
        switch content {
        case .agent(_, let agent, _): parts = [agent.agent, agent.project.isEmpty ? agent.workspace : agent.project]
        case .approval(let r): parts = [r.service]
        case .pair: parts = []
        }
        return parts.filter { !$0.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }
    }

    /// The title of the card of the item.
    var title: String {
        switch content {
        case .agent(_, let agent, _):
            if !agent.title.isEmpty { return agent.title }
            return agent.project.isEmpty ? agent.pane : agent.project
        case .approval(let r):
            return r.kind == .approve ? "Approve \(r.service) on \(r.host)?"
                : "Enroll \(FluxPlatform.current.deviceNoun) on \(r.host)?"
        case .pair: return "\(computer) asks to pair"
        }
    }
}
