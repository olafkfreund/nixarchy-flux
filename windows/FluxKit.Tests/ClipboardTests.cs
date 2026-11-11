using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using FluxKit;
using Watch = FluxKit.ClipboardPlugin.Watch;

namespace FluxKit.Tests;

// A clipboard in memory. Copy acts like another app, and the plugin writes
// with WriteText. Busy makes each member throw, like a clipboard that
// another app holds open.
public sealed class FakeClipboard : IClipboard
{
    readonly Lock gate = new();
    string? text;
    bool isPrivate;
    long count = 1;
    int writes;
    bool busy;

    public string? Text
    {
        get
        {
            lock (gate)
            {
                return text;
            }
        }
    }

    // The writes of the plugin.
    public int Writes
    {
        get
        {
            lock (gate)
            {
                return writes;
            }
        }
    }

    public bool Busy
    {
        set
        {
            lock (gate)
            {
                busy = value;
            }
        }
    }

    // A copy in another app. A password manager marks it as private.
    public void Copy(string value, bool secret = false)
    {
        lock (gate)
        {
            text = value;
            isPrivate = secret;
            count++;
        }
    }

    public string? ReadText()
    {
        lock (gate)
        {
            ThrowIfBusy();
            return text;
        }
    }

    public void WriteText(string value)
    {
        lock (gate)
        {
            ThrowIfBusy();
            text = value;
            isPrivate = false;
            count++;
            writes++;
        }
    }

    public long ChangeCount
    {
        get
        {
            lock (gate)
            {
                return count;
            }
        }
    }

    public bool HoldsPrivate
    {
        get
        {
            lock (gate)
            {
                ThrowIfBusy();
                return isPrivate;
            }
        }
    }

    void ThrowIfBusy()
    {
        if (busy)
        {
            throw new ExternalException("OpenClipboard failed");
        }
    }
}

// The rules of clipboard sync, ported from LifecycleTests.swift,
// ClipImageTests.swift, SendThreadTests.swift, and InboxModelTests.swift of
// the Mac app, and the plugin with a fake clipboard.
public class ClipboardTests
{
    static readonly LanConfig Offline = new() { LoopbackOnly = true };

    static Device Computer(FluxCore core, string id = "desk_0123456789") =>
        new(core, new Identity(id, "omarchy-desk", "desktop", Identity.Version, [PacketType.Tunnel, PacketType.Clipboard], []))
        {
            PairState = PairState.Paired,
        };

    [Fact]
    public void TheComputerNotesCopiesWhileNoComputerIsConnected()
    {
        Assert.Equal(Watch.Send, ClipboardPlugin.WatchFor(sync: true, paired: true, connected: true));
        Assert.Equal(Watch.Note, ClipboardPlugin.WatchFor(sync: true, paired: true, connected: false));
        // No timer without a paired computer.
        Assert.Equal(Watch.Off, ClipboardPlugin.WatchFor(sync: true, paired: false, connected: false));
        Assert.Equal(Watch.Off, ClipboardPlugin.WatchFor(sync: false, paired: true, connected: false));
        Assert.Equal(Watch.Off, ClipboardPlugin.WatchFor(sync: false, paired: true, connected: true));
    }

    [Fact]
    public void TextFromAComputerKeepsItsTime()
    {
        // flux.clipboard is a new copy.
        Assert.True(ClipboardPlugin.Takes(null, 1000));
        // A packet without a time counts.
        Assert.True(ClipboardPlugin.Takes(0, 1000));
        // An older copy of the computer loses.
        Assert.False(ClipboardPlugin.Takes(900, 1000));
        // The same copy after a reconnect changes nothing.
        Assert.False(ClipboardPlugin.Takes(1000, 1000));
        Assert.True(ClipboardPlugin.Takes(1100, 1000));
        Assert.Equal(1100, ClipboardPlugin.Time(1100, 2000));
        // flux.clipboard gets the time that it arrived.
        Assert.Equal(2000, ClipboardPlugin.Time(null, 2000));
        Assert.Equal(2000, ClipboardPlugin.Time(0, 2000));
    }

    [Fact]
    public void ACopyBeforeTheLinkDropsKeepsItsTime()
    {
        // The last poll did not see the copy, so it did not go out.
        Assert.True(ClipboardPlugin.NotesOnChange(Watch.Send, Watch.Note));
        // A copy from the last seconds before the link.
        Assert.True(ClipboardPlugin.NotesOnChange(Watch.Note, Watch.Send));
        Assert.True(ClipboardPlugin.NotesOnChange(Watch.Note, Watch.Off));
        // A start does not make the old clipboard new.
        Assert.False(ClipboardPlugin.NotesOnChange(Watch.Off, Watch.Note));
        Assert.False(ClipboardPlugin.NotesOnChange(Watch.Off, Watch.Send));
        // With sync off, no copy goes out.
        Assert.False(ClipboardPlugin.NotesOnChange(Watch.Send, Watch.Off));
    }

    [Fact]
    public void TextFromAComputerDoesNotGoBackOnConnect()
    {
        var remote = new ClipboardPlugin.RemoteCopy("desk", 5);
        Assert.True(ClipboardPlugin.SendsOnConnect("desk", 5, null));
        // The computer has this text already.
        Assert.False(ClipboardPlugin.SendsOnConnect("desk", 5, remote));
        // Another computer gets the text.
        Assert.True(ClipboardPlugin.SendsOnConnect("laptop", 5, remote));
        // A new copy on this computer goes out.
        Assert.True(ClipboardPlugin.SendsOnConnect("desk", 6, remote));
    }

    [Fact]
    public void WindowsSyncsOnlyText()
    {
        var clipboard = new ClipboardPlugin(new FakeClipboard());
        Assert.Equal([PacketType.Clipboard, PacketType.ClipboardConnect], clipboard.Incoming);
        Assert.Equal([PacketType.Clipboard, PacketType.ClipboardConnect], clipboard.Outgoing);
        using var t = new TestCore(Offline, [clipboard]);
        Assert.DoesNotContain("flux.clipboard.image", t.Core.IncomingCapabilities);
        Assert.DoesNotContain("flux.clipboard.image", t.Core.OutgoingCapabilities);
        Assert.Contains(PacketType.ClipboardConnect, t.Core.IncomingCapabilities);
    }

    [Fact]
    public void ManualClipPackets()
    {
        var p = ClipboardPlugin.ManualClip("git push");
        Assert.Equal(PacketType.Clipboard, p.Type);
        Assert.Equal("git push", p.String("content"));
        Assert.True(p.Bool("manual"));
        var parsed = Packet.Parse(p.Serialize())!;
        Assert.True(parsed.Bool("manual"));
    }

    [Fact]
    public void TheSizeLimitCountsUtf8Bytes()
    {
        Assert.Equal(1 << 20, ClipboardPlugin.MaxText);
        Assert.True(ClipboardPlugin.Fits(new string('a', ClipboardPlugin.MaxText)));
        Assert.False(ClipboardPlugin.Fits(new string('a', ClipboardPlugin.MaxText + 1)));
        // "é" takes 2 bytes.
        Assert.False(ClipboardPlugin.Fits(new string('é', ClipboardPlugin.MaxText / 2 + 1)));
    }

    [Fact]
    public void ThePreviewIsOneShortLine()
    {
        Assert.Equal("a b c", ClipEvent.MakePreview("  a\n\tb   c \n"));
        Assert.Equal("a b", ClipEvent.MakePreview("a\r\nb"));
        var cut = ClipEvent.MakePreview(new string('x', 500), 10);
        Assert.Equal(10, cut.Length);
        Assert.EndsWith("…", cut);
        // 160 characters stay whole.
        Assert.Equal(160, ClipEvent.MakePreview(new string('x', 160)).Length);
    }

    [Fact]
    public void TheEventNamesTheComputersOfAClip()
    {
        var c = ClipEvent.SentTo([("a", "desk"), ("b", "laptop")], "hello\nworld")!;
        Assert.Equal("2 computers", c.Computer);
        Assert.Equal("hello world", c.Preview);
        Assert.Equal(["a", "b"], c.DeviceIds);
        Assert.True(c.Sent);
        // A clip that reached no computer is no event.
        Assert.Null(ClipEvent.SentTo([], "x"));
        var secret = ClipEvent.SentTo([("a", "desk")], "hunter2", secret: true)!;
        Assert.Equal("desk", secret.Computer);
        Assert.True(secret.Secret);
        Assert.Equal(ClipEvent.HiddenText, secret.Preview);
        var received = ClipEvent.ReceivedFrom("a", "desk", "git push");
        Assert.False(received.Sent);
        Assert.Equal(["a"], received.DeviceIds);
        Assert.Equal("git push", received.Preview);
    }

    [Fact]
    public async Task TheSyncSettingStaysAndReachesTheModel()
    {
        var clipboard = new ClipboardPlugin(new FakeClipboard());
        using var t = new TestCore(Offline, [clipboard]);
        Assert.True(clipboard.Sync);
        await Wait.UntilAsync(() => clipboard.Model.Sync, "the model");
        clipboard.SetSync(false);
        Assert.False(clipboard.Sync);
        Assert.False(t.Core.Settings.Bool(ClipboardPlugin.SyncKey, true));
        await Wait.UntilAsync(() => !clipboard.Model.Sync, "the model after the change");
    }

    [Fact]
    public async Task TheTimerFollowsTheSettingAndThePairing()
    {
        var fake = new FakeClipboard();
        using var clipboard = new ClipboardPlugin(fake) { NoteEvery = TimeSpan.FromMilliseconds(50) };
        using var t = new TestCore(Offline, [clipboard]);
        await clipboard.FlushAsync();
        // No paired computer, so no timer runs.
        Assert.Equal(Watch.Off, clipboard.Watching);
        Assert.Equal(0, clipboard.Timestamp);

        t.Core.Trust.Put(new TrustedDevice("desk_0123456789", "omarchy-desk", "desktop", Convert.ToBase64String([1, 2, 3]), "", true));
        clipboard.SetSync(true);
        await clipboard.FlushAsync();
        Assert.Equal(Watch.Note, clipboard.Watching);
        // The start does not make the old clipboard new.
        await Task.Delay(200);
        Assert.Equal(0, clipboard.Timestamp);

        // A copy while no computer is connected keeps its time, without a read.
        var before = Packet.Now();
        fake.Copy("copied while offline");
        await Wait.UntilAsync(() => clipboard.Timestamp >= before, "the time of the copy");

        clipboard.SetSync(false);
        await clipboard.FlushAsync();
        Assert.Equal(Watch.Off, clipboard.Watching);
        var kept = clipboard.Timestamp;
        fake.Copy("copied with sync off");
        await Task.Delay(200);
        Assert.Equal(kept, clipboard.Timestamp);

        // Dispose stops the timer for good.
        clipboard.SetSync(true);
        await clipboard.FlushAsync();
        Assert.Equal(Watch.Note, clipboard.Watching);
        clipboard.Dispose();
        await clipboard.FlushAsync();
        Assert.Equal(Watch.Off, clipboard.Watching);
        kept = clipboard.Timestamp;
        fake.Copy("copied after the end");
        await Task.Delay(200);
        Assert.Equal(kept, clipboard.Timestamp);
    }

    [Fact]
    public async Task TextFromAComputerReplacesOnlyAnOlderCopy()
    {
        var fake = new FakeClipboard();
        using var clipboard = new ClipboardPlugin(fake);
        using var t = new TestCore(Offline, [clipboard]);
        var desk = Computer(t.Core);

        clipboard.Handle(new Packet(PacketType.Clipboard, new JsonObject { ["content"] = "first" }), desk);
        await clipboard.FlushAsync();
        Assert.Equal("first", fake.Text);
        var arrived = clipboard.Timestamp;
        Assert.True(arrived > 0);

        // A connect packet with an older time loses to the clipboard here.
        clipboard.Handle(new Packet(PacketType.ClipboardConnect, new JsonObject { ["content"] = "older", ["timestamp"] = arrived - 1 }), desk);
        await clipboard.FlushAsync();
        Assert.Equal("first", fake.Text);

        // A connect packet with a newer time wins and keeps its time.
        clipboard.Handle(new Packet(PacketType.ClipboardConnect, new JsonObject { ["content"] = "newer", ["timestamp"] = arrived + 5000 }), desk);
        await clipboard.FlushAsync();
        Assert.Equal("newer", fake.Text);
        Assert.Equal(arrived + 5000, clipboard.Timestamp);

        // Empty text and other types change nothing.
        clipboard.Handle(new Packet(PacketType.Clipboard, new JsonObject { ["content"] = "" }), desk);
        clipboard.Handle(new Packet(PacketType.Clipboard, new JsonObject { ["text"] = "wrong key" }), desk);
        clipboard.Handle(new Packet(PacketType.Ping, new JsonObject { ["content"] = "ping" }), desk);
        await clipboard.FlushAsync();
        Assert.Equal("newer", fake.Text);
        Assert.Equal(2, fake.Writes);
    }

    [Fact]
    public async Task TextOverTheLimitOrWithSyncOffStaysOut()
    {
        var fake = new FakeClipboard();
        using var clipboard = new ClipboardPlugin(fake);
        using var t = new TestCore(Offline, [clipboard]);
        var desk = Computer(t.Core);
        clipboard.Handle(new Packet(PacketType.Clipboard, new JsonObject { ["content"] = new string('a', ClipboardPlugin.MaxText + 1) }), desk);
        clipboard.SetSync(false);
        clipboard.Handle(new Packet(PacketType.Clipboard, new JsonObject { ["content"] = "sync is off" }), desk);
        await clipboard.FlushAsync();
        Assert.Null(fake.Text);
        Assert.Equal(0, fake.Writes);
    }

    [Fact]
    public async Task ABusyClipboardSkipsTheStep()
    {
        var fake = new FakeClipboard();
        using var clipboard = new ClipboardPlugin(fake);
        using var t = new TestCore(Offline, [clipboard]);
        var desk = Computer(t.Core);
        fake.Busy = true;
        clipboard.Handle(new Packet(PacketType.Clipboard, new JsonObject { ["content"] = "while busy" }), desk);
        await clipboard.FlushAsync();
        // The time stays, so that the next copy of the computer still wins.
        Assert.Equal(0, clipboard.Timestamp);
        fake.Busy = false;
        clipboard.Handle(new Packet(PacketType.Clipboard, new JsonObject { ["content"] = "after" }), desk);
        await clipboard.FlushAsync();
        Assert.Equal("after", fake.Text);
    }

    [Fact]
    public async Task SendClipboardNeedsAKnownComputer()
    {
        var fake = new FakeClipboard();
        using var clipboard = new ClipboardPlugin(fake);
        using var t = new TestCore(Offline, [clipboard]);
        fake.Copy("git push");
        Assert.False(await clipboard.SendClipboardAsync("unknown"));
        Assert.False(await clipboard.SendClipboardAsync([]));
        Assert.Empty(t.Toasts);
    }
}
