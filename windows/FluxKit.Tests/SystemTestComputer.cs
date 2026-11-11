using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using FluxKit;

namespace FluxKit.Tests;

// A computer on loopback that speaks the Flux wire like fluxd. It answers a
// pairing request at once and records the packets that it gets. It covers
// the features whose backends a headless fluxd does not have: media, Do Not
// Disturb, and stream requests.
public sealed class SystemTestComputer : ILanBackendDelegate, IDisposable
{
    readonly string dir = CoreTests.TempDir();
    readonly ConcurrentQueue<Packet> received = new();
    readonly Lock gate = new();
    Link? link;

    public LocalCertificate Certificate { get; }
    public LanBackend Backend { get; }
    public string Name { get; }
    public string Id => Certificate.DeviceId;

    // udpPort and the 5 TCP ports from tcpPort belong to this computer. It
    // announces itself to a port that nobody reads, so only its dial makes
    // a link.
    public SystemTestComputer(int udpPort, int tcpPort, IReadOnlyList<string> incoming, IReadOnlyList<string> outgoing, string name = "omarchy-test")
    {
        Name = name;
        Certificate = LocalCertificate.LoadOrCreate(dir, new PlainKeyProtector());
        var config = new LanConfig
        {
            UdpPort = udpPort,
            PeerUdpPort = tcpPort + 7,
            FirstTcpPort = tcpPort,
            LastTcpPort = tcpPort + 4,
            LoopbackOnly = true,
        };
        Backend = new LanBackend(new FluxTls(Certificate), config, port => new Identity(
            Certificate.DeviceId, name, "laptop", Identity.Version, [PacketType.Tunnel, .. incoming], outgoing, port, "fluxd", "0.0.0"), this);
    }

    // The LAN configuration of a core that links with this computer, on the
    // ports from first: UDP first, TCP first + 10 to first + 18.
    public static LanConfig CoreLan(int first) => new()
    {
        UdpPort = first,
        PeerUdpPort = first + 19,
        FirstTcpPort = first + 10,
        LastTcpPort = first + 18,
        LoopbackOnly = true,
    };

    // Starts the core and this computer, dials the core, and pairs from the
    // core. The core can have other computers.
    public async Task<DeviceSnapshot> PairAsync(TestCore t)
    {
        t.Core.Start();
        Backend.Start();
        await Wait.UntilAsync(() => t.Core.IsRunning && t.Core.State.TcpPort > 0, "the start of the core");
        Backend.Connect("127.0.0.1", t.Core.State.TcpPort, null);
        await Wait.UntilAsync(() => Snapshot(t) is { Online: true }, "the link from the computer");
        t.Core.Pair(Id, DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        await Wait.UntilAsync(() => Snapshot(t) is { Paired: true }, "the pairing");
        return Snapshot(t)!;
    }

    // This computer in the state of the core, or null.
    public DeviceSnapshot? Snapshot(TestCore t) => t.Core.State.Devices.FirstOrDefault(d => d.Id == Id);

    // Closes the link. The core then sees the computer offline.
    public void Disconnect()
    {
        Link? l;
        lock (gate)
        {
            l = link;
            link = null;
        }
        l?.Close();
    }

    // Dials the core again after Disconnect.
    public async Task ReconnectAsync(TestCore t)
    {
        Backend.Connect("127.0.0.1", t.Core.State.TcpPort, null);
        await Wait.UntilAsync(() => Snapshot(t) is { Online: true, Paired: true }, "the link after the reconnect");
    }

    public void Send(Packet p)
    {
        lock (gate)
        {
            link?.Send(p);
        }
    }

    // The packets of the type that this computer got, the oldest first.
    public IReadOnlyList<Packet> Got(string type) => received.Where(p => p.Type == type).ToArray();

    // Forgets the packets that this computer got.
    public void ClearReceived() => received.Clear();

    public byte[]? TrustedCertificate(string deviceId) => null;

    public bool HasLink(string deviceId)
    {
        lock (gate)
        {
            return link is { IsOpen: true };
        }
    }

    public bool DialsFrom(string deviceId) => false;

    public IReadOnlyList<string> KnownAddresses() => [];

    public void OnLink(Link l)
    {
        lock (gate)
        {
            link?.Close();
            link = l;
        }
        // The test packets can be large, so the link takes paired lines at once.
        l.SetPaired(true);
        l.Start(p => OnPacket(l, p), () => { });
    }

    void OnPacket(Link l, Packet p)
    {
        // A pairing request from the core gets a yes at once.
        if (p.Type == PacketType.Pair && p.Bool("pair") == true && p.Has("timestamp"))
        {
            l.Send(new Packet(PacketType.Pair, new JsonObject { ["pair"] = true }));
        }
        received.Enqueue(p);
    }

    public void Dispose()
    {
        Disconnect();
        Backend.Stop();
        Certificate.Dispose();
        try
        {
            Directory.Delete(dir, true);
        }
        catch (IOException)
        {
        }
    }
}

// Records the notifications that a notifier shows and removes.
public sealed class SystemNotificationRecorder : INotificationBackend
{
    readonly Lock gate = new();
    readonly List<SystemNotification> shown = [];
    readonly Dictionary<string, SystemNotification> current = [];
    readonly List<string> removed = [];

    public void Show(SystemNotification notification, IReadOnlyList<NotificationAction> actions)
    {
        lock (gate)
        {
            shown.Add(notification);
            current[notification.Id] = notification;
        }
    }

    public void Remove(string id)
    {
        lock (gate)
        {
            removed.Add(id);
            current.Remove(id);
        }
    }

    // Each post, the oldest first.
    public IReadOnlyList<SystemNotification> Shown
    {
        get
        {
            lock (gate)
            {
                return shown.ToArray();
            }
        }
    }

    // The notifications that show now, by ID.
    public IReadOnlyDictionary<string, SystemNotification> Current
    {
        get
        {
            lock (gate)
            {
                return new Dictionary<string, SystemNotification>(current);
            }
        }
    }

    // Each removed ID, the oldest first.
    public IReadOnlyList<string> Removed
    {
        get
        {
            lock (gate)
            {
                return removed.ToArray();
            }
        }
    }
}

// A core without a network for the tests of one plugin. Post runs each
// action at once, so a test sees each model change after the call.
public sealed class SystemPluginCore : IDisposable
{
    public FluxCore Core { get; }
    public SystemNotificationRecorder Notifications { get; } = new();
    public string Data { get; } = CoreTests.TempDir();

    public SystemPluginCore(IReadOnlyList<IFluxPlugin> plugins, string? data = null)
    {
        if (data is not null)
        {
            Data = data;
        }
        var host = new FluxHost { DeviceName = () => "Windows Test", Post = action => action() };
        host.Notifier.Backend = Notifications;
        Core = new FluxCore(new FluxPaths(Data), host, new LanConfig { LoopbackOnly = true }, plugins);
    }

    public void Dispose()
    {
        Core.Dispose();
        try
        {
            Directory.Delete(Data, true);
        }
        catch (IOException)
        {
        }
    }
}
