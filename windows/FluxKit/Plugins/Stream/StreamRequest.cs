namespace FluxKit;

// The stream that a computer asks for.
public enum StreamRequestKind
{
    Webcam,
    Mic,
}

// The wire names and the texts of each kind.
public static class StreamRequestKinds
{
    public static readonly IReadOnlyList<StreamRequestKind> All = [StreamRequestKind.Webcam, StreamRequestKind.Mic];

    // The kind in the body of flux.stream.request: "webcam" or "mic".
    public static string WireName(this StreamRequestKind kind) => kind == StreamRequestKind.Webcam ? "webcam" : "mic";

    // The kind of a flux.stream.request packet. It is null for another packet
    // type and for another kind. Other fields of the body do not change the
    // result.
    public static StreamRequestKind? Parse(Packet packet)
    {
        if (packet.Type != PacketType.StreamRequest)
        {
            return null;
        }
        return packet.String("kind") switch
        {
            "webcam" => StreamRequestKind.Webcam,
            "mic" => StreamRequestKind.Mic,
            _ => null,
        };
    }

    // The title of the prompt and of the notification.
    public static string Title(this StreamRequestKind kind, string computer) => kind == StreamRequestKind.Webcam
        ? $"{computer} asks for the webcam"
        : $"{computer} asks for the mic";

    // The label of the button and of the notification action that start the
    // stream.
    public static string StartLabel(this StreamRequestKind kind) => kind == StreamRequestKind.Webcam ? "Start webcam" : "Start the mic";

    // The text of the notification.
    public static string NotificationText(this StreamRequestKind kind) => kind == StreamRequestKind.Webcam
        ? "Click to start the webcam."
        : "Click to start the mic.";

    // The text under the title of the prompt.
    public static string Detail(this StreamRequestKind kind, string computer) => kind == StreamRequestKind.Webcam
        ? $"Apps on {computer} see this computer as Flux Camera. The camera stays off until you select {kind.StartLabel()}."
        : $"Apps on {computer} see this computer as Flux Microphone. The microphone stays off until you select {kind.StartLabel()}.";

    // The notification category. Each kind has its own category, because the
    // start action of each kind has its own label.
    public static string NotificationCategory(this StreamRequestKind kind) => $"stream.{kind.WireName()}";
}

// A request of a computer to start the webcam or the microphone of this
// computer. flux-cli webcam start and flux-cli mic start send it as
// flux.stream.request. The request only asks. The stream starts only after a
// click of the user on this computer. StreamRequest.swift of the Mac app has
// the same rules.
public sealed record StreamRequest(string ComputerId, string ComputerName, StreamRequestKind Kind, TimeSpan Received)
{
    // The label of the button that closes the prompt and starts nothing.
    public const string NotNowLabel = "Not now";

    // The shortest time between 2 requests of the same kind from the same
    // computer. This computer ignores a request that comes sooner after the
    // last request, also when it ignored that last request.
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(3);

    // The time after which an open request ends without an answer. The
    // notification goes away then.
    public static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(60);

    // The time for Received: a clock that also counts while the computer
    // sleeps.
    public static TimeSpan Now => TimeSpan.FromMilliseconds(Environment.TickCount64);

    // Each computer has at most 1 open request of each kind, so the ID comes
    // from the kind and the computer. The notification has the same ID.
    public string Id => MakeId(Kind, ComputerId);

    internal static string MakeId(StreamRequestKind kind, string computerId) => $"stream.{kind.WireName()}.{computerId}";

    // The title of the prompt and of the notification.
    public string Title => Kind.Title(ComputerName);
}

// What StreamRequestBook.Receive did with a request.
internal enum StreamRequestOutcome
{
    // The request is open. It replaced the open request of the same computer
    // and kind.
    Opened,

    // The request came less than StreamRequest.MinInterval after the last
    // request of the same computer and kind.
    TooSoon,

    // A stream of the same kind already runs to the computer.
    Streaming,
}

// The open stream requests of all computers, with the time limits. It holds
// no timer. The caller gives the time of each change.
internal sealed class StreamRequestBook
{
    readonly List<StreamRequest> requests = [];
    // The time of the last request, by request ID. An ignored request counts too.
    Dictionary<string, TimeSpan> last = [];

    // The time after which an open request ends. A test sets a shorter time.
    public TimeSpan Lifetime { get; set; } = StreamRequest.Lifetime;

    // The open requests, the oldest first.
    public IReadOnlyList<StreamRequest> Requests => requests.ToArray();

    // Takes a request, or ignores it. streaming is true when a stream of the
    // kind already runs to the computer. An ignored request also moves the
    // time of the last request, so that a computer that repeats a request
    // faster than StreamRequest.MinInterval shows 1 request only. Android
    // counts the requests in the same way.
    public StreamRequestOutcome Receive(StreamRequestKind kind, string computerId, string computerName, bool streaming, TimeSpan now, out StreamRequest? opened)
    {
        opened = null;
        var id = StreamRequest.MakeId(kind, computerId);
        var hadPrevious = last.TryGetValue(id, out var previous);
        last = last.Where(e => now - e.Value < StreamRequest.MinInterval).ToDictionary();
        last[id] = now;
        if (hadPrevious && now - previous < StreamRequest.MinInterval)
        {
            return StreamRequestOutcome.TooSoon;
        }
        if (streaming)
        {
            return StreamRequestOutcome.Streaming;
        }
        var request = new StreamRequest(computerId, computerName, kind, now);
        requests.RemoveAll(r => r.Id == id);
        requests.Add(request);
        opened = request;
        return StreamRequestOutcome.Opened;
    }

    // True while the request is younger than Lifetime.
    public bool Fresh(StreamRequest request, TimeSpan now) => now - request.Received < Lifetime;

    // Ends the request with the ID and returns it, or null when it is not open.
    public StreamRequest? Remove(string id)
    {
        var i = requests.FindIndex(r => r.Id == id);
        if (i < 0)
        {
            return null;
        }
        var r = requests[i];
        requests.RemoveAt(i);
        return r;
    }

    // Ends the requests that are Lifetime old or older, and returns them.
    public IReadOnlyList<StreamRequest> Expire(TimeSpan now)
    {
        var old = requests.Where(r => now - r.Received >= Lifetime).ToArray();
        requests.RemoveAll(r => now - r.Received >= Lifetime);
        return old;
    }

    // Ends the requests of a computer, and returns them.
    public IReadOnlyList<StreamRequest> Forget(string computerId)
    {
        var gone = requests.Where(r => r.ComputerId == computerId).ToArray();
        requests.RemoveAll(r => r.ComputerId == computerId);
        foreach (var kind in StreamRequestKinds.All)
        {
            last.Remove(StreamRequest.MakeId(kind, computerId));
        }
        return gone;
    }
}

// A start that waits after a click on start. Both streams need the link to
// the computer. A click on the notification opens Flux, and the link can
// come back a moment later. The webcam and the microphone pages use it.
public sealed record PendingStart(StreamRequestKind Kind, string ComputerId, string ComputerName, TimeSpan Deadline)
{
    // How long a start waits for the link.
    public static readonly TimeSpan Wait = TimeSpan.FromSeconds(15);

    // The start of the request, with the deadline Wait after now.
    public static PendingStart Of(StreamRequest request, TimeSpan now) =>
        new(request.Kind, request.ComputerId, request.ComputerName, now + Wait);

    // The next step of the start. This computer gives true for active,
    // because its camera also works while Flux is not the active app.
    public PendingStep Step(bool active, bool online, TimeSpan now)
    {
        if (active && online)
        {
            return PendingStep.Start;
        }
        return now < Deadline ? PendingStep.Wait : PendingStep.GiveUp;
    }

    // The message when the stream did not start.
    public string FailedText => Kind == StreamRequestKind.Webcam
        ? $"The webcam did not start, because {ComputerName} is not connected."
        : $"The microphone did not start, because {ComputerName} is not connected.";
}

// The next step of a PendingStart.
public enum PendingStep
{
    Wait,
    Start,

    // The link did not come back before the deadline.
    GiveUp,
}
