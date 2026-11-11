using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using FluxKit;
using Watch = FluxKit.ClipboardPlugin.Watch;

namespace FluxKit.Tests;

// Clipboard sync against a headless fluxd. The headless fluxd keeps its
// clipboard in memory and never reports a local copy, so the tests check the
// clipboard history of fluxd, which flux-cli clipboard prints. Each test uses
// its own block of ports in 31000 to 31999.
public class ClipboardEndToEndTests
{
    static LanConfig Lan(Fluxd fluxd, int udpPort, int tcpPort) => new()
    {
        UdpPort = udpPort,
        PeerUdpPort = fluxd.UdpPort,
        FirstTcpPort = tcpPort,
        LastTcpPort = tcpPort + 8,
        LoopbackOnly = true,
    };

    // The clipboard history of fluxd, newest first.
    static async Task<JsonArray> HistoryAsync(Fluxd fluxd) =>
        JsonNode.Parse(await fluxd.CliAsync("clipboard")) as JsonArray ?? [];

    // The history entry of fluxd with the text and the direction, or null.
    static async Task<JsonObject?> EntryAsync(Fluxd fluxd, string text, string dir = "in") =>
        (await HistoryAsync(fluxd)).OfType<JsonObject>()
            .FirstOrDefault(e => Json.String(e["text"]) == text && Json.String(e["dir"]) == dir);

    [FluxdFact]
    public async Task ClipFromFluxdReachesTheClipboard()
    {
        await using var fluxd = await Fluxd.StartAsync(31016, 31020);
        var fake = new FakeClipboard();
        using var clipboard = new ClipboardPlugin(fake);
        using var t = new TestCore(Lan(fluxd, 31031, 31040), [clipboard]);
        var computer = await EndToEndTests.PairFromWindowsAsync(fluxd, t);
        Assert.Contains("clipboard", (await fluxd.DeviceAsync(t.Core.Local.DeviceId))!["plugins"]!.AsArray().Select(Json.String));

        await fluxd.CliAsync("clip", "hello from omarchy");
        await Wait.UntilAsync(() => fake.Text == "hello from omarchy", "the clip on the clipboard", fluxd);
        await Wait.UntilAsync(() => clipboard.Model.Last is { Sent: false }, "the clip in the model", fluxd);
        Assert.Equal(computer.Name, clipboard.Model.Last!.Computer);
        Assert.Equal("hello from omarchy", clipboard.Model.Last.Preview);
        Assert.Equal([computer.Id], clipboard.Model.Last.DeviceIds);

        // The text from the computer does not go back to it.
        await Task.Delay(TimeSpan.FromSeconds(2));
        Assert.Null(await EntryAsync(fluxd, "hello from omarchy"));
        Assert.NotNull(await EntryAsync(fluxd, "hello from omarchy", "out"));
        Assert.Equal(1, fake.Writes);
    }

    [FluxdFact]
    public async Task ACopyHereReachesFluxd()
    {
        await using var fluxd = await Fluxd.StartAsync(31066, 31070);
        var fake = new FakeClipboard();
        using var clipboard = new ClipboardPlugin(fake);
        using var t = new TestCore(Lan(fluxd, 31081, 31090), [clipboard]);
        var sent = new ConcurrentQueue<SentClip>();
        clipboard.ClipSent += sent.Enqueue;
        var computer = await EndToEndTests.PairFromWindowsAsync(fluxd, t);
        var id = t.Core.Local.DeviceId;
        await Wait.UntilAsync(async () => { await clipboard.FlushAsync(); return clipboard.Watching == Watch.Send; }, "the poll", fluxd);

        fake.Copy("copied on windows");
        await Wait.UntilAsync(async () => await EntryAsync(fluxd, "copied on windows") is not null, "the copy on fluxd", fluxd);
        var entry = (await EntryAsync(fluxd, "copied on windows"))!;
        Assert.Equal(id, Json.String(entry["device"]));
        // Automatic sync is not a clip that the user sent.
        Assert.Null(Json.String(entry["source"]));
        await Wait.UntilAsync(() => clipboard.Model.Last is { Sent: true, Preview: "copied on windows" }, "the clip in the model", fluxd);
        Assert.Equal(computer.Name, clipboard.Model.Last!.Computer);

        // A copy that the source marks as private stays here.
        fake.Copy("hunter2", secret: true);
        await Task.Delay(TimeSpan.FromSeconds(2));
        Assert.Null(await EntryAsync(fluxd, "hunter2"));

        // The user sends a secret on purpose with Send clipboard, with the
        // manual flag. fluxd keeps it as a shared clip.
        Assert.True(await clipboard.SendClipboardAsync(computer.Id));
        await Wait.UntilAsync(async () => await EntryAsync(fluxd, "hunter2") is not null, "the manual clip on fluxd", fluxd);
        Assert.Equal("share", Json.String((await EntryAsync(fluxd, "hunter2"))!["source"]));
        await Wait.UntilAsync(() => clipboard.Model.Last is { Secret: true } && !sent.IsEmpty, "the secret in the model", fluxd);
        Assert.Equal(ClipEvent.HiddenText, clipboard.Model.Last!.Preview);
        sent.TryPeek(out var clip);
        Assert.Equal("", clip!.Text);
        Assert.Equal([computer.Id], clip.DeviceIds);
        await Wait.UntilAsync(() => t.Toasts.Contains($"Clipboard sent to {computer.Name}"), "the toast", fluxd);

        // An empty clipboard sends nothing.
        fake.Copy("");
        Assert.False(await clipboard.SendClipboardAsync(computer.Id));
        await Wait.UntilAsync(() => t.Toasts.Contains("The clipboard is empty"), "the empty toast", fluxd);

        // With sync off, a copy stays here.
        clipboard.SetSync(false);
        await clipboard.FlushAsync();
        Assert.Equal(Watch.Off, clipboard.Watching);
        fake.Copy("sync is off");
        await Task.Delay(TimeSpan.FromSeconds(2));
        Assert.Null(await EntryAsync(fluxd, "sync is off"));
    }

    [FluxdFact]
    public async Task TheLinkStartSendsTheNewerCopy()
    {
        await using var fluxd = await Fluxd.StartAsync(31116, 31120);
        var fake = new FakeClipboard();
        fake.Copy("copied before the pairing");
        using var clipboard = new ClipboardPlugin(fake);
        using var t = new TestCore(Lan(fluxd, 31131, 31140), [clipboard]);
        await EndToEndTests.PairFromWindowsAsync(fluxd, t);
        var id = t.Core.Local.DeviceId;

        // The pairing starts the link, so the clipboard goes out as a connect
        // packet with its time.
        await Wait.UntilAsync(async () => await EntryAsync(fluxd, "copied before the pairing") is not null, "the connect packet after the pairing", fluxd);

        // A text from fluxd does not go back on the next link.
        await fluxd.CliAsync("clip", "from omarchy");
        await Wait.UntilAsync(() => fake.Text == "from omarchy", "the clip on the clipboard", fluxd);
        await ReconnectAsync(fluxd, t, clipboard, () => { });
        await Task.Delay(TimeSpan.FromSeconds(2));
        Assert.Null(await EntryAsync(fluxd, "from omarchy"));

        // A copy while the link is down keeps its time and goes out on the
        // next link. The poll does not send it, so only the connect packet
        // can bring it to fluxd.
        long before = 0;
        await ReconnectAsync(fluxd, t, clipboard, () =>
        {
            before = clipboard.Timestamp;
            fake.Copy("copied while offline");
        });
        Assert.True(clipboard.Timestamp > before);
        await Wait.UntilAsync(async () => await EntryAsync(fluxd, "copied while offline") is not null, "the connect packet after the link", fluxd);
        Assert.Equal(id, Json.String((await EntryAsync(fluxd, "copied while offline"))!["device"]));
    }

    // Closes the link, runs offline while no computer is connected, and waits
    // for the next link.
    static async Task ReconnectAsync(Fluxd fluxd, TestCore t, ClipboardPlugin clipboard, Action offline)
    {
        t.Core.Stop();
        await Wait.UntilAsync(async () => { await clipboard.FlushAsync(); return clipboard.Watching == Watch.Note; }, "the note watch", fluxd);
        offline();
        // The note watch reads the change count every 2 seconds.
        await Task.Delay(ClipboardPlugin.NoteInterval + TimeSpan.FromMilliseconds(500));
        t.Core.Start();
        await Wait.UntilAsync(() => t.Computer is { Online: true, Paired: true }, "the link after the stop", fluxd, seconds: 30);
        await Wait.UntilAsync(async () => { await clipboard.FlushAsync(); return clipboard.Watching == Watch.Send; }, "the poll after the link", fluxd);
    }
}
