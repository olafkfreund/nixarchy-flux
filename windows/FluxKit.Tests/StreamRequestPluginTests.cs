using System.Text.Json.Nodes;
using FluxKit;

namespace FluxKit.Tests;

// An app that records what the stream request plugin asks of it.
public sealed class FakeStreamStarter : IStreamStarter
{
    public bool IsAppActive { get; set; }
    public int Presented { get; private set; }
    public List<StreamRequest> Started { get; } = [];
    public List<string> Pages { get; } = [];

    // The streams that run, as "kind computer".
    public HashSet<string> Running { get; } = [];

    public void Present() => Presented++;

    public bool Streams(StreamRequestKind kind, string computerId) => Running.Contains($"{kind.WireName()} {computerId}");

    public void Start(StreamRequest request) => Started.Add(request);

    public void OpenPage(string computerId, StreamRequestKind kind) => Pages.Add($"{kind.WireName()} {computerId}");
}

// StreamRequestPluginTests.swift of the Mac app: the capability, the
// prompt, and the rule that only a click on start starts a stream.
public class StreamRequestPluginTests
{
    static (StreamRequestPlugin Plugin, FakeStreamStarter Starter, SystemPluginCore Core) Make(TimeSpan? lifetime = null)
    {
        var starter = new FakeStreamStarter();
        var plugin = new StreamRequestPlugin(starter, lifetime);
        return (plugin, starter, new SystemPluginCore([plugin]));
    }

    [Fact]
    public void ListsTheTypeOnlyWithAStarter()
    {
        var (both, _, core) = Make();
        using (core)
        {
            Assert.Equal([PacketType.StreamRequest], both.Incoming);
            Assert.Contains(PacketType.StreamRequest, core.Core.IncomingCapabilities);
            Assert.Empty(both.Outgoing); // this computer only takes the request
        }
        var none = new StreamRequestPlugin();
        using var other = new SystemPluginCore([none]);
        Assert.Empty(none.Incoming); // a computer that cannot stream both kinds does not list the type
        Assert.DoesNotContain(PacketType.StreamRequest, other.Core.IncomingCapabilities);
        Assert.Equal([PacketType.StreamRequest], ((IFluxPlugin)none).HandledTypes);
    }

    [Fact]
    public void TheRequestOnlyAsks()
    {
        var (plugin, starter, core) = Make();
        using var _ = core;
        plugin.Receive(StreamRequestKind.Webcam, "pc1", "omarchy", StreamRequest.Now);
        Assert.Equal("omarchy asks for the webcam", plugin.Model.Current?.Title);
        Assert.Empty(starter.Started); // the packet starts nothing

        plugin.Start("stream.webcam.pc1");
        Assert.Equal(["stream.webcam.pc1"], starter.Started.Select(r => r.Id)); // the click on start opens the stream
        Assert.Empty(plugin.Model.Requests); // the request ends

        plugin.Start("stream.webcam.pc1");
        Assert.Single(starter.Started); // a request starts once
    }

    [Fact]
    public void NotNowStartsNothing()
    {
        var (plugin, starter, core) = Make();
        using var _ = core;
        plugin.Receive(StreamRequestKind.Mic, "pc1", "omarchy", StreamRequest.Now);
        plugin.Dismiss("stream.mic.pc1");
        Assert.Empty(plugin.Model.Requests);
        plugin.Start("stream.mic.pc1");
        Assert.Empty(starter.Started); // a request that ended starts nothing
    }

    [Fact]
    public void AnOldRequestStartsNothing()
    {
        var (plugin, starter, core) = Make();
        using var _ = core;
        var t0 = StreamRequest.Now;
        plugin.Receive(StreamRequestKind.Webcam, "pc1", "omarchy", t0);
        plugin.Start("stream.webcam.pc1", t0 + TimeSpan.FromSeconds(61));
        Assert.Empty(starter.Started); // a click on an old notification starts nothing
        Assert.Empty(plugin.Model.Requests);
    }

    [Fact]
    public void AFreshClickOnTheNotificationStartsTheStream()
    {
        var (plugin, starter, core) = Make();
        using var _ = core;
        plugin.Receive(StreamRequestKind.Mic, "pc1", "omarchy", StreamRequest.Now);
        plugin.StartFromNotification("stream.mic.pc1", StreamRequestKind.Mic, "pc1");
        Assert.Equal(["stream.mic.pc1"], starter.Started.Select(r => r.Id));
        Assert.Empty(starter.Pages); // Start opens the page and starts the stream
    }

    [Fact]
    public void ALateClickOnTheNotificationOpensOnlyThePage()
    {
        var (plugin, starter, core) = Make();
        using var _ = core;
        var t0 = StreamRequest.Now;
        plugin.Receive(StreamRequestKind.Webcam, "pc1", "omarchy", t0);
        plugin.StartFromNotification("stream.webcam.pc1", StreamRequestKind.Webcam, "pc1", t0 + TimeSpan.FromSeconds(63));
        Assert.Empty(starter.Started); // a click after the 60 seconds starts no stream
        Assert.Equal(["webcam pc1"], starter.Pages); // the page opens, and the user starts the stream there
        Assert.Empty(plugin.Model.Requests);

        plugin.StartFromNotification("stream.mic.pc1", StreamRequestKind.Mic, "pc1");
        Assert.Equal(["webcam pc1", "mic pc1"], starter.Pages); // a request that ended also opens only the page
        plugin.StartFromNotification("stream.mic.pc1", StreamRequestKind.Mic, null);
        Assert.Equal(2, starter.Pages.Count); // a notification that does not name the computer opens nothing
        Assert.Empty(starter.Started);
    }

    [Fact]
    public void TheNewestRequestShowsFirst()
    {
        var (plugin, _, core) = Make();
        using var _ = core;
        plugin.Receive(StreamRequestKind.Webcam, "pc1", "omarchy", StreamRequest.Now);
        plugin.Receive(StreamRequestKind.Mic, "pc2", "studio", StreamRequest.Now);
        Assert.Equal("stream.mic.pc2", plugin.Model.Current?.Id);
        plugin.Dismiss("stream.mic.pc2");
        Assert.Equal("stream.webcam.pc1", plugin.Model.Current?.Id); // the next request shows after Not now
    }

    [Fact]
    public void ThePromptShowsOnlyWhileTheAppIsActive()
    {
        var (plugin, starter, core) = Make();
        using var _ = core;
        starter.IsAppActive = false;
        plugin.Receive(StreamRequestKind.Webcam, "pc1", "omarchy", StreamRequest.Now);
        Assert.Equal(0, starter.Presented); // a notification shows the request
        var shown = Assert.Single(core.Notifications.Shown);
        Assert.Equal("stream.webcam.pc1", shown.Id);
        Assert.Equal("stream.webcam", shown.Category);
        Assert.Equal("omarchy asks for the webcam", shown.Title);
        Assert.Equal("Click to start the webcam.", shown.Body);
        Assert.Equal("pc1", shown.UserInfo["computer"]);

        starter.IsAppActive = true;
        plugin.Receive(StreamRequestKind.Mic, "pc1", "omarchy", StreamRequest.Now);
        Assert.Equal(1, starter.Presented);
        plugin.Receive(StreamRequestKind.Mic, "pc1", "omarchy", StreamRequest.Now);
        Assert.Equal(1, starter.Presented); // a request within 3 seconds shows nothing
        Assert.Single(core.Notifications.Shown);
    }

    [Fact]
    public async Task ARequestEndsByItself()
    {
        var (plugin, starter, core) = Make(TimeSpan.FromMilliseconds(50));
        using var _ = core;
        plugin.Receive(StreamRequestKind.Webcam, "pc1", "omarchy", StreamRequest.Now);
        Assert.Single(plugin.Model.Requests);
        await Wait.UntilAsync(() => plugin.Model.Requests.Count == 0, "the end of the request", seconds: 5);
        // The notification goes away too.
        Assert.Contains("stream.webcam.pc1", core.Notifications.Removed);
        plugin.Start("stream.webcam.pc1");
        Assert.Empty(starter.Started);
    }

    [Fact]
    public void AnUnpairEndsTheRequestsOfTheComputer()
    {
        var (plugin, _, core) = Make();
        using var _ = core;
        plugin.Receive(StreamRequestKind.Webcam, "pc1", "omarchy", StreamRequest.Now);
        plugin.Receive(StreamRequestKind.Mic, "pc2", "studio", StreamRequest.Now);
        plugin.Forget("pc1");
        Assert.Equal(["stream.mic.pc2"], plugin.Model.Requests.Select(r => r.Id));
        Assert.Contains("stream.webcam.pc1", core.Notifications.Removed);
    }

    [Fact]
    public void ARunningStreamOfTheKindIgnoresTheRequest()
    {
        var (plugin, starter, core) = Make();
        using var _ = core;
        starter.Running.Add("webcam pc1");
        plugin.Receive(StreamRequestKind.Webcam, "pc1", "omarchy", StreamRequest.Now);
        Assert.Empty(plugin.Model.Requests);
        // A stream to another computer or of another kind does not count.
        plugin.Receive(StreamRequestKind.Webcam, "pc2", "studio", StreamRequest.Now);
        plugin.Receive(StreamRequestKind.Mic, "pc1", "omarchy", StreamRequest.Now);
        Assert.Equal(2, plugin.Model.Requests.Count);
        // A stream that starts after the request makes the click do nothing.
        starter.Running.Add("mic pc1");
        plugin.Start("stream.mic.pc1");
        Assert.Empty(starter.Started);
    }

    [Fact]
    public void TheNotificationStartsOnlyWithAClick()
    {
        Assert.True(StreamRequestPlugin.StartsStream(StreamRequestPlugin.StartAction));
        Assert.True(StreamRequestPlugin.StartsStream(Notifier.DefaultAction)); // the text asks for a click
        Assert.False(StreamRequestPlugin.StartsStream("dismiss"));
        Assert.False(StreamRequestPlugin.StartsStream("other"));
        var actions = StreamRequestPlugin.NotificationActions(StreamRequestKind.Webcam);
        Assert.Equal(["start"], actions.Select(a => a.Id));
        Assert.Equal(["Start webcam"], actions.Select(a => a.Title));
        Assert.Equal(["Start the mic"], StreamRequestPlugin.NotificationActions(StreamRequestKind.Mic).Select(a => a.Title));
    }

    [Fact]
    public void AClickOnTheStartActionStartsTheStream()
    {
        var (plugin, starter, core) = Make();
        using var _ = core;
        var notifier = core.Core.Host.Notifier;
        plugin.Receive(StreamRequestKind.Webcam, "pc1", "omarchy", StreamRequest.Now);
        var info = core.Notifications.Shown[0].UserInfo;
        // A dismissal starts nothing.
        notifier.Activate("stream.webcam", "dismiss", info);
        Assert.Empty(starter.Started);
        notifier.Activate("stream.webcam", StreamRequestPlugin.StartAction, info);
        Assert.Equal(["stream.webcam.pc1"], starter.Started.Select(r => r.Id));
        // A second click finds no request and opens only the page.
        notifier.Activate("stream.webcam", Notifier.DefaultAction, info);
        Assert.Equal(["webcam pc1"], starter.Pages);
        // The category gives the kind. A computer that does not match the ID opens nothing.
        notifier.Activate("stream.mic", StreamRequestPlugin.StartAction, info);
        Assert.Single(starter.Pages);
        Assert.Single(starter.Started);
    }

    // The request from a computer on loopback reaches the model.
    [Fact]
    public async Task ARequestPacketOpensARequest()
    {
        using var computer = new SystemTestComputer(32600, 32601, [], [PacketType.StreamRequest]);
        var starter = new FakeStreamStarter();
        var plugin = new StreamRequestPlugin(starter);
        using var t = new TestCore(SystemTestComputer.CoreLan(32620), [plugin]);
        var shown = new SystemNotificationRecorder();
        t.Core.Host.Notifier.Backend = shown;
        await computer.PairAsync(t);

        computer.Send(new Packet(PacketType.StreamRequest, new JsonObject { ["kind"] = "mic" }));
        computer.Send(new Packet(PacketType.StreamRequest, new JsonObject { ["kind"] = "screen" }));
        await Wait.UntilAsync(() => plugin.Model.Current is not null, "the request");
        Assert.Equal($"stream.mic.{computer.Id}", plugin.Model.Current!.Id);
        Assert.Equal($"{computer.Name} asks for the mic", plugin.Model.Current.Title);
        await Wait.UntilAsync(() => shown.Shown.Count == 1, "the notification");
        Assert.Empty(starter.Started);

        // An unpair ends the request.
        t.Core.Unpair(computer.Id);
        await Wait.UntilAsync(() => plugin.Model.Requests.Count == 0, "the end of the request after the unpair");
    }
}
