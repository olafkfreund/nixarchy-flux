import XCTest
@testable import FluxKit

/// Ported from ChatFeedTest.kt of the Android app, plus the entries that the Apple apps keep in other models.
final class ChatTests: XCTestCase {
    private func at(_ s: TimeInterval) -> Date { Date(timeIntervalSince1970: s) }

    private let blocked = HerdrAgent(pane: "w2:p1", agent: "codex", status: .blocked, title: "Migrate")
    private let done = HerdrAgent(pane: "w3:p1", agent: "claude", status: .done, title: "Fix test")

    private func request(_ computer: String) -> ApproveRequest {
        ApproveRequest(computerId: computer, computerName: "name-\(computer)", id: "r1", kind: .approve, host: "host",
                       user: "dev", service: "sudo", tty: "pts/3", rhost: "", time: 0,
                       nonce: String(repeating: "0", count: 64), timeoutSeconds: 60)
    }

    func testTheEntriesAreInTimeOrder() {
        let ask = InboxItem(.agent(deviceId: "pc", agent: blocked, control: true), computer: "omarchy-xps")
        let fin = InboxItem(.agent(deviceId: "pc", agent: done, control: true), computer: "omarchy-xps")
        let pair = InboxItem(.pair(deviceId: "new"), computer: "framework-13")
        let link = SentItem(deviceId: "pc", kind: .link, text: "https://omarchy.org", at: at(50))
        let file = FileTransfer(deviceId: "pc", name: "invoice.pdf", incoming: true, size: 10, started: at(200))
        let since = ["w2:p1": at(300), "w3:p1": at(100)]
        let chat = Chat.entries(sent: [link], transfers: [file], clip: nil, answers: [], items: [ask, fin, pair],
                                since: { _, pane in since[pane] }, seen: [pair.key: at(250)])
        XCTAssertEqual(chat.map(\.id), ["sent|\(link.id.uuidString)", fin.key, "file|\(file.id.uuidString)", pair.key, ask.key])
        XCTAssertEqual(chat.last?.at, at(300))
    }

    func testAnAgentWithNoStartTakesTheTimeThatTheChatSawIt() {
        let ask = InboxItem(.agent(deviceId: "pc", agent: blocked, control: true), computer: "omarchy-xps")
        let link = SentItem(deviceId: "pc", kind: .link, text: "https://omarchy.org", at: at(500))
        let chat = Chat.entries(sent: [link], transfers: [], clip: nil, answers: [], items: [ask],
                                since: { _, _ in nil }, seen: [ask.key: at(400)])
        XCTAssertEqual(chat.first?.id, ask.key)
    }

    func testOnlyAClipFromAComputerAndTheEndedAnswersShow() {
        let sentClip = ClipEvent(deviceIds: ["pc"], computer: "omarchy-xps", sent: true, preview: "out", at: at(10))
        XCTAssertTrue(Chat.entries(sent: [], transfers: [], clip: sentClip, answers: [], items: [], since: { _, _ in nil }, seen: [:]).isEmpty,
                      "a clip that went out is a sent item already")
        let received = ClipEvent.received(from: "pc", computer: "omarchy-xps", text: "git push", at: at(20))
        var approved = ApproveRecord(requestId: "r1", computerId: "pc", kind: .approve, summary: "", received: at(30),
                                     outcome: .approved, request: request("pc"))
        approved.resolved = at(40)
        let open = ApproveRecord(requestId: "r2", computerId: "pc", kind: .approve, summary: "", received: at(35), outcome: .open)
        var expired = ApproveRecord(requestId: "r3", computerId: "pc", kind: .approve, summary: "", received: at(36), outcome: .expired)
        expired.resolved = at(37)
        let chat = Chat.entries(sent: [], transfers: [], clip: received, answers: [approved, open, expired], items: [],
                                since: { _, _ in nil }, seen: [:])
        XCTAssertEqual(chat.map(\.id), ["clip", "answer|\(approved.id.uuidString)"])
        XCTAssertEqual(chat.last?.at, at(40), "an answer takes the time that it ended")
    }

    func testTheScopeKeepsItsComputerAndThePairRequests() {
        let pair = InboxItem(.pair(deviceId: "new"), computer: "framework-13")
        let desk = SentItem(deviceId: "desk", kind: .text, text: "hello", at: at(1))
        let pc = SentItem(deviceId: "pc", kind: .text, text: "hello", at: at(2))
        let chat = Chat.entries(sent: [desk, pc], transfers: [], clip: nil, answers: [], items: [pair],
                                since: { _, _ in nil }, seen: [pair.key: at(3)], scope: "pc")
        XCTAssertEqual(chat.map(\.id), ["sent|\(pc.id.uuidString)", pair.key])
    }

    func testTheClockKeepsTheFirstTimeAndForgetsWhatIsGoneForLong() {
        var clock = SeenClock(grace: 100)
        XCTAssertEqual(clock.update(["a"], now: at(10)), ["a": at(10)])
        XCTAssertEqual(clock.update(["a", "b"], now: at(20)), ["a": at(10), "b": at(20)])
        XCTAssertEqual(clock.update(["b"], now: at(30)), ["b": at(20)])
        XCTAssertEqual(clock.update(["a", "b"], now: at(40)), ["a": at(10), "b": at(20)], "an item that comes back soon keeps its time")
        _ = clock.update(["b"], now: at(100))
        XCTAssertEqual(clock.update(["a", "b"], now: at(201)), ["a": at(201), "b": at(20)], "an item that is gone for long is new")
        XCTAssertEqual(clock.update(["a", "b"], now: at(9_999)), ["a": at(201), "b": at(20)], "an item that stays keeps its time without updates")
    }

    func testATellChoiceOpensAReply() {
        XCTAssertTrue(Chat.isTellChoice(AgentChoice("3", "No, and tell Codex what to do differently (esc)")))
        XCTAssertTrue(Chat.isTellChoice(AgentChoice("3", "No, and tell Claude what to do differently")))
        XCTAssertFalse(Chat.isTellChoice(AgentChoice("1", "Yes")))
        XCTAssertFalse(Chat.isTellChoice(AgentChoice("2", "Yes, and do not ask again for bin/migrate commands")))
    }
}
