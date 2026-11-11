using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;

namespace FluxKit;

// The clipboard state that the UI shows.
public sealed class ClipboardModel : Model
{
    bool sync = true;
    ClipEvent? last;

    internal ClipboardModel()
    {
    }

    // True when clipboard changes go both ways by themselves.
    public bool Sync
    {
        get => sync;
        internal set => Set(ref sync, value);
    }

    // The last clip that this computer sent or received, with its time. Flux
    // keeps it only in memory.
    public ClipEvent? Last
    {
        get => last;
        internal set => Set(ref last, value);
    }
}

// A clip that the user sent with Send clipboard, for the Send thread of each
// computer. Text is empty for a secret. Stamp is the change count of the
// clipboard at the send.
public sealed record SentClip(ValueList<string> DeviceIds, string Text, long Stamp);

// Clipboard sync of text: flux.clipboard and flux.clipboard.connect in both
// directions. ClipboardPlugin.swift of the Mac app has the same rules, and
// internal/core/clipboard.go has the rules of fluxd. Windows syncs no images.
//
// The plugin polls the change count of the clipboard every 0.5 seconds while
// sync is on and a paired computer is connected. While no computer is
// connected, it reads only the change count every 2 seconds and keeps the
// time of a new copy, so that the copy wins over older copies of the
// computers on the next link. The poll runs on a thread of the pool, never on
// the UI thread.
//
// The steps of the plugin run one at a time, in order, on a thread of the
// pool: the poll, the packets, the links, and Send clipboard. Only a step
// reads or writes the clipboard and the fields of the step state. The core
// calls Handle, OnConnected, and OnDisconnected with the core lock held, so
// they only queue a step.
public sealed class ClipboardPlugin : IFluxPlugin, IDisposable
{
    // The keys in core.Settings. The Mac app uses the same keys.
    public const string SyncKey = "clipboard.sync";
    public const string TimestampKey = "clipboard.timestamp";

    // How often the plugin reads the change count while a computer is
    // connected.
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(0.5);

    // How often the plugin reads the change count while no computer is
    // connected. The time of a copy can be this much late.
    public static readonly TimeSpan NoteInterval = TimeSpan.FromSeconds(2);

    // The largest text in UTF-8 bytes that goes out or comes in. fluxd takes
    // at most 1 MiB from a device and sends at most 256 KiB. The Mac app
    // sends at most 1 MiB of shared text.
    public const int MaxText = 1 << 20;

    readonly IClipboard clipboard;

    // Runs the steps one at a time, in the order of the calls.
    readonly TaskScheduler steps = new ConcurrentExclusiveSchedulerPair().ExclusiveScheduler;

    // Ends the timers after Dispose.
    readonly CancellationTokenSource life = new();

    FluxCore? core;

    // The step state.
    Watch watching = Watch.Off;
    CancellationTokenSource? timer;
    // The change count that the plugin handled last.
    long changeCount;
    // The computer of the last text from a computer, and the change count
    // after its write, see SendsOnConnect.
    RemoteCopy? remoteCopy;
    // The last text that a computer put on the clipboard. Flux does not send
    // it back.
    string? lastRemote;

    public ClipboardPlugin(IClipboard clipboard) => this.clipboard = clipboard;

    public ClipboardModel Model { get; } = new();

    public IReadOnlyList<string> Incoming { get; } = [PacketType.Clipboard, PacketType.ClipboardConnect];
    public IReadOnlyList<string> Outgoing { get; } = [PacketType.Clipboard, PacketType.ClipboardConnect];

    // Called on the UI thread after Send clipboard reached a computer.
    public event Action<SentClip>? ClipSent;

    // The poll intervals. A test sets shorter times.
    internal TimeSpan SendEvery { get; init; } = PollInterval;
    internal TimeSpan NoteEvery { get; init; } = NoteInterval;

    public void Attach(FluxCore core)
    {
        this.core = core;
        var sync = Sync;
        core.Host.Post(() => Model.Sync = sync);
        // The time of a copy counts also before the first link.
        Post(UpdatePolling);
    }

    // Stops the timer for good.
    public void Dispose()
    {
        life.Cancel();
        Post(UpdatePolling);
    }

    // Settings

    // True when clipboard changes go both ways by themselves. The default is on.
    public bool Sync => core?.Settings.Bool(SyncKey, true) ?? true;

    // The time of the last clipboard change, in milliseconds: a local copy,
    // or the text that a computer put on the clipboard.
    internal long Timestamp
    {
        get => core?.Settings.Long(TimestampKey) ?? 0;
        set => core?.Settings.Set(TimestampKey, value);
    }

    public void SetSync(bool on)
    {
        if (core is not { } c)
        {
            return;
        }
        c.Settings.Set(SyncKey, on);
        c.Host.Post(() => Model.Sync = on);
        Post(UpdatePolling);
    }

    // What the timer does with the clipboard.
    internal enum Watch
    {
        // No timer runs.
        Off,
        // It sends each new copy to the connected computers.
        Send,
        // It reads only the change count and keeps the time of a new copy.
        Note,
    }

    // What the timer does now. Read it after FlushAsync.
    internal Watch Watching => watching;

    // The plugin sends new copies while sync is on and a paired computer is
    // connected. While sync is on, a computer is paired, and none is
    // connected, it keeps the time of each new copy without a read.
    internal static Watch WatchFor(bool sync, bool paired, bool connected)
    {
        if (!sync)
        {
            return Watch.Off;
        }
        if (connected)
        {
            return Watch.Send;
        }
        return paired ? Watch.Note : Watch.Off;
    }

    // Reports whether a change of the watch keeps the time of a copy that the
    // old watch did not handle yet. The note watch keeps the time of a copy
    // from its last seconds. A copy from the last poll before the last link
    // dropped did not go out, so it keeps its time for the next link.
    internal static bool NotesOnChange(Watch old, Watch next) =>
        old == Watch.Note || (old == Watch.Send && next == Watch.Note);

    // A text that a computer put on the clipboard, and the change count after
    // the write.
    internal sealed record RemoteCopy(string Device, long Count);

    // Reports whether a new link to the computer gets the clipboard of this
    // computer. Text that the same computer put there does not go back while
    // the clipboard did not change. Its time is the time that it arrived,
    // which is later than the copy on the computer, so the computer would
    // take its own text again.
    internal static bool SendsOnConnect(string device, long count, RemoteCopy? remote) =>
        remote is null || remote.Device != device || remote.Count != count;

    // Reports whether text from a computer replaces the clipboard. A
    // flux.clipboard.connect packet with a time that is not later than last
    // is old. flux.clipboard has no time and always counts.
    internal static bool Takes(long? timestamp, long last) => timestamp is not { } t || t < 1 || t > last;

    // The time that the clipboard keeps for text from a computer: the time of
    // the packet, or now for a packet without a time.
    internal static long Time(long? timestamp, long now) => timestamp is { } t && t >= 1 ? t : now;

    // The clipboard packet of a clip that the user sent. The manual flag puts
    // the clip on the clipboard of the computer also while its automatic sync
    // is off, and the computer shows a notification.
    public static Packet ManualClip(string text) =>
        new(PacketType.Clipboard, new JsonObject { ["content"] = text, ["manual"] = true });

    // True when the text is not larger than MaxText.
    internal static bool Fits(string text) => Encoding.UTF8.GetByteCount(text) <= MaxText;

    // Links

    public void OnConnected(Device device)
    {
        var id = device.Id;
        Post(() =>
        {
            UpdatePolling();
            if (core is not { } c || !Sync)
            {
                return;
            }
            // Flux reads the clipboard at each link. Timestamp holds the time
            // of the last copy, also of a copy while no computer was
            // connected, so the newer copy wins on both sides.
            var count = clipboard.ChangeCount;
            changeCount = count;
            if (!SendsOnConnect(id, count, remoteCopy) || Shareable(count) is not { } text)
            {
                return;
            }
            if (!Fits(text))
            {
                FluxLog.Info("clipboard", $"did not send a clipboard text of {Encoding.UTF8.GetByteCount(text)} bytes on the link");
                return;
            }
            c.Send(new Packet(PacketType.ClipboardConnect, new JsonObject { ["content"] = text, ["timestamp"] = Timestamp }), id);
        });
    }

    public void OnDisconnected(Device device) => Post(UpdatePolling);

    // Receive

    public void Handle(Packet packet, Device device)
    {
        switch (packet.Type)
        {
            case PacketType.Clipboard:
                Receive(packet.String("content"), null, device.Id);
                break;
            case PacketType.ClipboardConnect:
                Receive(packet.String("content"), packet.Long("timestamp") ?? 0, device.Id);
                break;
        }
    }

    // A flux.clipboard.connect packet carries the time of the last change on
    // the computer. It loses to a newer local change.
    internal void Receive(string? text, long? timestamp, string deviceId)
    {
        if (string.IsNullOrEmpty(text) || !Sync)
        {
            return;
        }
        if (!Fits(text))
        {
            FluxLog.Info("clipboard", $"ignored a clipboard text of {Encoding.UTF8.GetByteCount(text)} bytes");
            return;
        }
        Post(() =>
        {
            // A copy from the last seconds before the link keeps its time first.
            if (watching == Watch.Note)
            {
                NoteChange();
            }
            if (!Takes(timestamp, Timestamp))
            {
                return;
            }
            Put(text, deviceId);
            // The same text after a reconnect then changes nothing.
            Timestamp = Time(timestamp, Packet.Now());
        });
    }

    // Puts text from a computer on the clipboard, so that it does not go
    // back. Other features, such as shared text, use it too.
    public void PutFromComputer(string text, string deviceId) => Post(() => Put(text, deviceId));

    void Put(string text, string deviceId)
    {
        clipboard.WriteText(text);
        lastRemote = text;
        changeCount = clipboard.ChangeCount;
        remoteCopy = new RemoteCopy(deviceId, changeCount);
        if (core?.WithDevice(deviceId, d => d.Name) is { } name)
        {
            var e = ClipEvent.ReceivedFrom(deviceId, name, text);
            core.Host.Post(() => Model.Last = e);
        }
    }

    // Send

    // Sends the clipboard text to a computer with Send clipboard. The task
    // ends with true when the text went out.
    public Task<bool> SendClipboardAsync(string deviceId) => SendClipboardAsync([deviceId]);

    // Sends the clipboard text to the computers with Send clipboard. The
    // user sends a secret on purpose, so a private copy goes out too. The task
    // ends with true when the text reached a computer.
    public Task<bool> SendClipboardAsync(IReadOnlyList<string> deviceIds) =>
        Task.Factory.StartNew(() => SendClipboard(deviceIds), CancellationToken.None, TaskCreationOptions.DenyChildAttach, steps);

    bool SendClipboard(IReadOnlyList<string> deviceIds)
    {
        if (core is not { } c)
        {
            return false;
        }
        var known = deviceIds.Where(id => c.WithDevice(id, _ => true)).ToArray();
        if (known.Length == 0)
        {
            return false;
        }
        string? text;
        long count;
        bool secret;
        try
        {
            count = clipboard.ChangeCount;
            changeCount = count;
            text = clipboard.ReadText();
            // A copy that changed after count counts as a secret too, because
            // the text and the private flag can then come from 2 copies.
            secret = clipboard.HoldsPrivate || clipboard.ChangeCount != count;
        }
        catch (ExternalException e)
        {
            FluxLog.Info("clipboard", $"cannot read the clipboard: {e.Message}");
            c.Toast("Cannot read the clipboard. Try again in a moment");
            return false;
        }
        if (string.IsNullOrEmpty(text))
        {
            c.Toast("The clipboard is empty");
            return false;
        }
        if (!Fits(text))
        {
            c.Toast("The text is larger than 1 MB. Send it as a file");
            return false;
        }
        Timestamp = Packet.Now();
        var p = ManualClip(text);
        var got = Computers(known.Where(id => c.Send(p, id)));
        if (got.Count == 0)
        {
            c.Toast("Not connected. Try again in a moment");
            return false;
        }
        var sent = new SentClip(ValueList.From(got.Select(g => g.Id)), secret ? "" : text, count);
        var clip = ClipEvent.SentTo(got, text, secret);
        c.Host.Post(() =>
        {
            Model.Last = clip;
            ClipSent?.Invoke(sent);
        });
        c.Toast($"Clipboard sent to {string.Join(", ", got.Select(g => g.Name))}");
        return true;
    }

    // The ID and the name of each computer in ids that Flux knows.
    List<(string Id, string Name)> Computers(IEnumerable<string> ids)
    {
        var list = new List<(string Id, string Name)>();
        if (core is null)
        {
            return list;
        }
        foreach (var id in ids)
        {
            if (core.WithDevice(id, d => d.Name) is { } name)
            {
                list.Add((id, name));
            }
        }
        return list;
    }

    // Poll

    // Starts, changes, or stops the timer for the sync setting and the links.
    void UpdatePolling()
    {
        if (core is not { } c)
        {
            return;
        }
        // After Dispose, the timer stops and the plugin keeps no time.
        if (life.IsCancellationRequested)
        {
            timer = null;
            watching = Watch.Off;
            return;
        }
        var next = WatchFor(Sync, c.Trust.All().Count > 0, c.ConnectedPairedIds().Count > 0);
        if (next == watching)
        {
            return;
        }
        if (NotesOnChange(watching, next))
        {
            NoteChange();
        }
        timer?.Cancel();
        timer = null;
        watching = next;
        if (next == Watch.Off)
        {
            return;
        }
        changeCount = clipboard.ChangeCount;
        var t = CancellationTokenSource.CreateLinkedTokenSource(life.Token);
        timer = t;
        _ = TickAsync(next == Watch.Send ? SendEvery : NoteEvery, t.Token);
    }

    async Task TickAsync(TimeSpan interval, CancellationToken token)
    {
        using var ticks = new PeriodicTimer(interval);
        try
        {
            while (await ticks.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                // WhenAny ends with the step and does not throw, so that a
                // failed step does not end the timer. Run logs the failure.
                await Task.WhenAny(Run(() =>
                {
                    if (!token.IsCancellationRequested)
                    {
                        Poll();
                    }
                })).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    void Poll()
    {
        switch (watching)
        {
            case Watch.Send:
                var count = clipboard.ChangeCount;
                if (count == changeCount)
                {
                    return;
                }
                changeCount = count;
                SendLocalCopy(count);
                break;
            case Watch.Note:
                NoteChange();
                break;
        }
    }

    // Keeps the time of a new copy without a read of its content.
    void NoteChange()
    {
        var count = clipboard.ChangeCount;
        if (count == changeCount)
        {
            return;
        }
        changeCount = count;
        Timestamp = Packet.Now();
    }

    // Sends a local copy to every connected computer, unless a computer put
    // the text there or the source marks it as private.
    void SendLocalCopy(long count)
    {
        if (core is not { } c || !Sync || Shareable(count) is not { } text || text == lastRemote)
        {
            return;
        }
        if (!Fits(text))
        {
            FluxLog.Info("clipboard", $"did not sync a clipboard text of {Encoding.UTF8.GetByteCount(text)} bytes");
            c.Toast("The copied text is larger than 1 MB. Flux did not sync it");
            return;
        }
        Timestamp = Packet.Now();
        var p = new Packet(PacketType.Clipboard, new JsonObject { ["content"] = text });
        var sent = c.ConnectedPairedIds(PacketType.Clipboard).Where(id => c.Send(p, id)).ToArray();
        if (ClipEvent.SentTo(Computers(sent), text) is { } e)
        {
            c.Host.Post(() => Model.Last = e);
        }
    }

    // The text of the copy with the change count, or null when the copy holds
    // no text or is private. A copy that changed during the read can mix 2
    // copies, so it gives null too, and the next poll reads the new copy.
    string? Shareable(long count)
    {
        if (clipboard.HoldsPrivate)
        {
            return null;
        }
        var text = clipboard.ReadText();
        if (string.IsNullOrEmpty(text) || clipboard.ChangeCount != count || clipboard.HoldsPrivate)
        {
            return null;
        }
        return text;
    }

    // Steps

    void Post(Action step) => _ = Run(step);

    // Runs a step after the steps before it. A clipboard that another app
    // holds open throws an ExternalException. The step then ends, and the
    // next poll tries again.
    Task Run(Action step)
    {
        var task = Task.Factory.StartNew(() =>
        {
            try
            {
                step();
            }
            catch (ExternalException e)
            {
                FluxLog.Info("clipboard", $"the clipboard is busy: {e.Message}");
            }
        }, CancellationToken.None, TaskCreationOptions.DenyChildAttach, steps);
        // The log gets the type of the error and no text of the clipboard.
        _ = task.ContinueWith(t => FluxLog.Error("clipboard", $"a clipboard step failed: {t.Exception?.GetBaseException().GetType().Name}"),
            CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        return task;
    }

    // Ends after the steps that came before. Tests use it.
    internal Task FlushAsync() => Run(() => { });
}
