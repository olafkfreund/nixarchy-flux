using System.Text.Json.Nodes;
using FluxKit;

namespace FluxKit.Tests;

// StreamRequestTests.swift of the Mac app: the packet, the texts, and the
// time limits of StreamRequestBook.
public class StreamRequestTests
{
    static TimeSpan At(double seconds) => TimeSpan.FromSeconds(seconds);

    // The packet that fluxd sends, after a trip through the wire format.
    static Packet Wire(JsonObject body, string type = PacketType.StreamRequest) => Packet.Parse(new Packet(type, body).Serialize())!;

    static StreamRequestOutcome Receive(StreamRequestBook book, StreamRequestKind kind, string id, string name, bool streaming, double seconds) =>
        book.Receive(kind, id, name, streaming, At(seconds), out _);

    [Fact]
    public void ThePacketType() => Assert.Equal("flux.stream.request", PacketType.StreamRequest); // fluxd and Android use the same name

    [Fact]
    public void ParsesTheKinds()
    {
        Assert.Equal(StreamRequestKind.Webcam, StreamRequestKinds.Parse(Wire(new JsonObject { ["kind"] = "webcam" })));
        Assert.Equal(StreamRequestKind.Mic, StreamRequestKinds.Parse(Wire(new JsonObject { ["kind"] = "mic" })));
    }

    [Fact]
    public void IgnoresOtherKindsAndKeepsExtraFields()
    {
        Assert.Null(StreamRequestKinds.Parse(Wire(new JsonObject { ["kind"] = "screen" })));
        Assert.Null(StreamRequestKinds.Parse(Wire(new JsonObject { ["kind"] = "Webcam" }))); // the kind is exact
        Assert.Null(StreamRequestKinds.Parse(Wire(new JsonObject { ["kind"] = "" })));
        Assert.Null(StreamRequestKinds.Parse(Wire([])));
        Assert.Null(StreamRequestKinds.Parse(Wire(new JsonObject { ["kind"] = 1 })));
        Assert.Null(StreamRequestKinds.Parse(Wire(new JsonObject { ["kind"] = "webcam" }, PacketType.Webcam))); // only flux.stream.request asks
        // An extra field changes nothing.
        Assert.Equal(StreamRequestKind.Mic, StreamRequestKinds.Parse(Wire(new JsonObject { ["kind"] = "mic", ["start"] = true, ["port"] = 1745 })));
    }

    [Fact]
    public void TheTextsOfTheContract()
    {
        Assert.Equal("omarchy asks for the webcam", StreamRequestKind.Webcam.Title("omarchy"));
        Assert.Equal("omarchy asks for the mic", StreamRequestKind.Mic.Title("omarchy"));
        Assert.Equal("Start webcam", StreamRequestKind.Webcam.StartLabel());
        Assert.Equal("Start the mic", StreamRequestKind.Mic.StartLabel());
        Assert.Equal("Not now", StreamRequest.NotNowLabel);
        Assert.Equal("Click to start the webcam.", StreamRequestKind.Webcam.NotificationText());
        Assert.Equal("Click to start the mic.", StreamRequestKind.Mic.NotificationText());
        Assert.Equal("Apps on omarchy see this computer as Flux Camera. The camera stays off until you select Start webcam.",
            StreamRequestKind.Webcam.Detail("omarchy"));
        Assert.Equal("Apps on omarchy see this computer as Flux Microphone. The microphone stays off until you select Start the mic.",
            StreamRequestKind.Mic.Detail("omarchy"));
        Assert.Equal("stream.webcam", StreamRequestKind.Webcam.NotificationCategory());
        Assert.Equal("stream.mic", StreamRequestKind.Mic.NotificationCategory());
    }

    [Fact]
    public void TheTimeLimits()
    {
        Assert.Equal(TimeSpan.FromSeconds(3), StreamRequest.MinInterval);
        Assert.Equal(TimeSpan.FromSeconds(60), StreamRequest.Lifetime);
    }

    [Fact]
    public void ARequestOpens()
    {
        var book = new StreamRequestBook();
        var outcome = book.Receive(StreamRequestKind.Webcam, "pc1", "omarchy", false, At(0), out var opened);
        var r = new StreamRequest("pc1", "omarchy", StreamRequestKind.Webcam, At(0));
        Assert.Equal(StreamRequestOutcome.Opened, outcome);
        Assert.Equal(r, opened);
        Assert.Equal([r], book.Requests);
        Assert.Equal("stream.webcam.pc1", r.Id);
        Assert.Equal("omarchy asks for the webcam", r.Title);
    }

    [Fact]
    public void TheSameKindFromTheSameComputerWaits3Seconds()
    {
        var book = new StreamRequestBook();
        Receive(book, StreamRequestKind.Webcam, "pc1", "omarchy", false, 0);
        Assert.Equal(StreamRequestOutcome.TooSoon, Receive(book, StreamRequestKind.Webcam, "pc1", "omarchy", false, 2.9));
        Assert.Equal([At(0)], book.Requests.Select(r => r.Received)); // the ignored request keeps the open request
        // The limit counts from the ignored request at 2.9 seconds.
        Assert.Equal(StreamRequestOutcome.TooSoon, Receive(book, StreamRequestKind.Webcam, "pc1", "omarchy", false, 3));
        Assert.Equal(StreamRequestOutcome.Opened, book.Receive(StreamRequestKind.Webcam, "pc1", "omarchy", false, At(6), out var next));
        Assert.Equal([next!], book.Requests); // it replaces the open request of the same computer and kind
        Assert.Equal(At(6), next!.Received);
    }

    [Fact]
    public void AnIgnoredRequestMovesTheLimit()
    {
        var book = new StreamRequestBook();
        Receive(book, StreamRequestKind.Mic, "pc1", "omarchy", false, 0);
        Assert.Equal(StreamRequestOutcome.TooSoon, Receive(book, StreamRequestKind.Mic, "pc1", "omarchy", false, 2));
        // The limit counts from the last request, also one that this computer ignored, as on Android.
        Assert.Equal(StreamRequestOutcome.TooSoon, Receive(book, StreamRequestKind.Mic, "pc1", "omarchy", false, 3.5));
        Assert.Equal(StreamRequestOutcome.Opened, Receive(book, StreamRequestKind.Mic, "pc1", "omarchy", false, 6.5));
    }

    [Fact]
    public void ARequestThatRepeatsFasterShowsOnce()
    {
        var book = new StreamRequestBook();
        var opened = 0;
        for (var second = 0.0; second < 20; second += 2)
        {
            if (Receive(book, StreamRequestKind.Webcam, "pc1", "omarchy", false, second) == StreamRequestOutcome.Opened)
            {
                opened++;
            }
        }
        Assert.Equal(1, opened); // a request every 2 seconds shows 1 time
    }

    [Fact]
    public void OtherKindsAndComputersDoNotWait()
    {
        var book = new StreamRequestBook();
        Receive(book, StreamRequestKind.Webcam, "pc1", "omarchy", false, 0);
        // The limit is per computer and kind.
        Assert.Equal(StreamRequestOutcome.Opened, Receive(book, StreamRequestKind.Mic, "pc1", "omarchy", false, 0.5));
        Assert.Equal(StreamRequestOutcome.Opened, Receive(book, StreamRequestKind.Webcam, "pc2", "studio", false, 1));
        Assert.Equal(["stream.webcam.pc1", "stream.mic.pc1", "stream.webcam.pc2"], book.Requests.Select(r => r.Id)); // the oldest first
    }

    [Fact]
    public void ARunningStreamIgnoresTheRequest()
    {
        var book = new StreamRequestBook();
        Assert.Equal(StreamRequestOutcome.Streaming, Receive(book, StreamRequestKind.Webcam, "pc1", "omarchy", true, 0));
        Assert.Empty(book.Requests);
        // A request that this computer ignored also starts the limit.
        Assert.Equal(StreamRequestOutcome.TooSoon, Receive(book, StreamRequestKind.Webcam, "pc1", "omarchy", false, 1));
        Assert.Equal(StreamRequestOutcome.Opened, Receive(book, StreamRequestKind.Webcam, "pc1", "omarchy", false, 4));
    }

    [Fact]
    public void ARequestEndsAfter60Seconds()
    {
        var book = new StreamRequestBook();
        Receive(book, StreamRequestKind.Webcam, "pc1", "omarchy", false, 0);
        Receive(book, StreamRequestKind.Mic, "pc1", "omarchy", false, 30);
        var webcam = book.Requests[0];
        Assert.True(book.Fresh(webcam, At(59.9)));
        Assert.False(book.Fresh(webcam, At(60)));
        Assert.Empty(book.Expire(At(59.9)));
        Assert.Equal(["stream.webcam.pc1"], book.Expire(At(60)).Select(r => r.Id));
        Assert.Equal(["stream.mic.pc1"], book.Requests.Select(r => r.Id));
        Assert.Equal(["stream.mic.pc1"], book.Expire(At(90)).Select(r => r.Id));
        Assert.Empty(book.Requests);
    }

    [Fact]
    public void RemoveAndForget()
    {
        var book = new StreamRequestBook();
        Receive(book, StreamRequestKind.Webcam, "pc1", "omarchy", false, 0);
        Receive(book, StreamRequestKind.Mic, "pc1", "omarchy", false, 0);
        Receive(book, StreamRequestKind.Mic, "pc2", "studio", false, 0);
        Assert.Equal("studio", book.Remove("stream.mic.pc2")?.ComputerName);
        Assert.Null(book.Remove("stream.mic.pc2")); // a request ends once
        Assert.Equal(["stream.webcam.pc1", "stream.mic.pc1"], book.Forget("pc1").Select(r => r.Id));
        Assert.Empty(book.Requests);
        // A computer that pairs again starts with no limit.
        Assert.Equal(StreamRequestOutcome.Opened, Receive(book, StreamRequestKind.Webcam, "pc1", "omarchy", false, 1));
    }

    // The start that waits

    static PendingStart Pending(StreamRequestKind kind, TimeSpan deadline) => new(kind, "pc1", "omarchy", deadline);

    [Fact]
    public void TheStartWaitsForTheLink()
    {
        var now = StreamRequest.Now;
        var p = Pending(StreamRequestKind.Webcam, now + TimeSpan.FromSeconds(15));
        Assert.Equal(PendingStep.Start, p.Step(true, true, now));
        Assert.Equal(PendingStep.Wait, p.Step(false, true, now));
        Assert.Equal(PendingStep.Wait, p.Step(true, false, now)); // the link comes back after Flux opens
        Assert.Equal(PendingStep.GiveUp, p.Step(true, false, now + TimeSpan.FromSeconds(15)));
        Assert.Equal(PendingStep.Start, p.Step(true, true, now + TimeSpan.FromSeconds(20))); // a late link still starts
        Assert.Equal(TimeSpan.FromSeconds(15), PendingStart.Wait);
    }

    [Fact]
    public void TheStartOfARequest()
    {
        var r = new StreamRequest("pc1", "omarchy", StreamRequestKind.Mic, At(0));
        Assert.Equal(Pending(StreamRequestKind.Mic, At(17)), PendingStart.Of(r, At(2)));
    }

    [Fact]
    public void TheTextWhenTheStreamDidNotStart()
    {
        var now = StreamRequest.Now;
        Assert.Equal("The webcam did not start, because omarchy is not connected.", Pending(StreamRequestKind.Webcam, now).FailedText);
        Assert.Equal("The microphone did not start, because omarchy is not connected.", Pending(StreamRequestKind.Mic, now).FailedText);
    }
}
