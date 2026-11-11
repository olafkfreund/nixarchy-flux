using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace FluxKit;

// A snapshot of the core for the UI.
public sealed record CoreState
{
    public string DeviceName { get; init; } = "";
    public string DeviceId { get; init; } = "";
    public ValueList<DeviceSnapshot> Devices { get; init; } = ValueList<DeviceSnapshot>.Empty;
    public bool ListeningUdp { get; init; } = true;
    public int TcpPort { get; init; }
    public bool Enabled { get; init; } = true;

    // True while a search for computers runs.
    public bool Searching { get; init; }
}

// What the app gives the core about the computer that it runs on.
public sealed record FluxHost
{
    // The name of this computer in the identity.
    public Func<string> DeviceName { get; init; } = () => Environment.MachineName;

    // "laptop" when the computer has a battery, else "desktop".
    public string DeviceType { get; init; } = "desktop";

    // The name of this app in the identity, and its version.
    public string AppName { get; init; } = "windows";
    public string AppVersion { get; init; } = "";

    // Protects the private key of this device at rest.
    public IKeyProtector KeyProtector { get; init; } = new PlainKeyProtector();

    // Runs an action on the UI thread. The actions keep their order.
    public Action<Action> Post { get; init; } = SerialQueue.Shared.Post;

    // The system notifications. The app sets its backend.
    public Notifier Notifier { get; init; } = new();

    // The folder for received and downloaded files.
    public Func<string> Downloads { get; init; } = () =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
}

// Runs actions one at a time, in order, on a thread of the pool. Tests and
// the core without a UI use it.
public sealed class SerialQueue
{
    public static readonly SerialQueue Shared = new();

    readonly Channel<Action> actions = Channel.CreateUnbounded<Action>(new UnboundedChannelOptions { SingleReader = true });

    public SerialQueue() => _ = Task.Run(RunAsync);

    public void Post(Action action) => actions.Writer.TryWrite(action);

    async Task RunAsync()
    {
        await foreach (var action in actions.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                action();
            }
            catch (Exception e)
            {
                FluxLog.Error("core", $"a queued action failed: {e}");
            }
        }
    }
}

// The process-wide state of Flux: the certificate, the paired computers, the
// live links, and the actions that the UI calls. Device state changes happen
// inside Locked, which publishes a new state at the end. FluxCore.swift of
// the Mac app has the same rules.
public sealed class FluxCore : IDisposable
{
    // The most computers that are not paired and have a link. A new link
    // closes the oldest one, so that strangers cannot fill the list.
    internal const int MaxUnpairedLinks = 8;

    // How long the link of a computer that is not paired stays open without a
    // pairing. fluxd closes such a link after the same time.
    internal static readonly TimeSpan UnpairedIdle = TimeSpan.FromSeconds(120);

    // How long a search for computers runs.
    internal static readonly TimeSpan SearchTime = TimeSpan.FromSeconds(10);

    // The core lock. Monitor is reentrant, so a plugin may call the core from
    // a handler.
    readonly object gate = new();
    readonly Dictionary<string, Device> devices = [];
    readonly List<string> order = [];
    readonly Dictionary<string, List<IFluxPlugin>> routes = [];
    readonly PairCooldown pairCooldown = new();
    readonly Stopwatch uptime = Stopwatch.StartNew();
    LanBackend? backend;
    Mdns? mdns;
    // Counts the searches, so that the end of an old search does not end a new one.
    int searchCount;
    bool searching;
    // The last state that went to Changed.
    CoreState? published;

    public LocalCertificate Local { get; }
    public FluxTls Tls { get; }
    public TrustStore Trust { get; }
    public FluxPaths Paths { get; }
    public FluxSettings Settings { get; }
    public LanConfig LanConfig { get; }
    public FluxHost Host { get; }
    public IReadOnlyList<IFluxPlugin> Plugins { get; }

    // How long an incoming pairing request stays open. A test sets a shorter time.
    internal TimeSpan IncomingPairTime { get; set; } = Device.IncomingPairTimeout;

    // Called on the UI thread after each state change.
    public event Action<CoreState>? Changed;

    // Called on the UI thread with a short message for the user.
    public event Action<string>? Toasted;

    // Called on the UI thread when a computer asks to pair.
    public event Action<DeviceSnapshot>? PairRequested;

    public FluxCore(FluxPaths paths, FluxHost host, LanConfig lanConfig, IReadOnlyList<IFluxPlugin> plugins)
    {
        Paths = paths;
        Host = host;
        LanConfig = lanConfig;
        Plugins = plugins;
        Directory.CreateDirectory(paths.Data);
        Settings = new FluxSettings(paths.File("settings.json"));
        Local = LocalCertificate.LoadOrCreate(paths.File("identity"), host.KeyProtector);
        Tls = new FluxTls(Local);
        Trust = new TrustStore(paths.File("trusted.json"));
        foreach (var t in Trust.All())
        {
            // A computer without a pinned certificate is not paired.
            if (t.CertificateDer is not { } pinned)
            {
                continue;
            }
            var identity = new Identity(t.Id, t.Name, t.Type, FluxKit.Identity.Version, t.IsFlux ? [PacketType.Tunnel] : [], []);
            // Only Omarchy computers pair with Flux for Windows.
            if (!identity.IsFlux)
            {
                FluxLog.Info("core", $"removed the pairing with {t.Name}, which is not an Omarchy computer");
                Trust.Remove(t.Id);
                continue;
            }
            var d = new Device(this, identity) { LastIp = t.LastIp, Certificate = pinned };
            d.PairState = PairState.Paired;
            devices[t.Id] = d;
            order.Add(t.Id);
        }
        foreach (var p in plugins)
        {
            foreach (var type in p.HandledTypes)
            {
                if (!routes.TryGetValue(type, out var list))
                {
                    routes[type] = list = [];
                }
                list.Add(p);
            }
        }
        foreach (var p in plugins)
        {
            p.Attach(this);
        }
    }

    // Identity

    public string DeviceName => FluxKit.Identity.CleanName(Host.DeviceName());

    public IReadOnlyList<string> IncomingCapabilities => Plugins.SelectMany(p => p.Incoming).Distinct().ToArray();
    public IReadOnlyList<string> OutgoingCapabilities => Plugins.SelectMany(p => p.Outgoing).Distinct().ToArray();

    public Identity MakeIdentity(int tcpPort) => new(
        Local.DeviceId, DeviceName, Host.DeviceType, FluxKit.Identity.Version,
        IncomingCapabilities, OutgoingCapabilities, tcpPort, Host.AppName, Host.AppVersion);

    // Sends the identity again to each connected, paired computer, after the
    // capabilities changed. fluxd takes the new capabilities from it.
    public void SendIdentity()
    {
        var p = MakeIdentity(backend?.TcpPort ?? 0).ToPacket();
        lock (gate)
        {
            foreach (var d in devices.Values.Where(d => d.Paired))
            {
                d.Send(p);
            }
        }
    }

    // Settings

    // False after the user turns Flux off. Flux then uses no network.
    public bool Enabled
    {
        get => Settings.Bool("enabled", true);
        set
        {
            Settings.Set("enabled", value);
            if (value)
            {
                Start();
            }
            else
            {
                Stop();
            }
            Publish();
        }
    }

    // Network

    // Starts discovery and the link listener unless the user turned Flux off.
    public void Start()
    {
        if (!Enabled)
        {
            return;
        }
        LanBackend b;
        lock (gate)
        {
            if (backend is not null)
            {
                return;
            }
            b = new LanBackend(Tls, LanConfig, MakeIdentity, new BackendDelegate(this));
            backend = b;
        }
        _ = Task.Run(() =>
        {
            b.Start();
            Mdns? old = null;
            lock (gate)
            {
                // A stop during the start wins: the backend ends.
                if (backend != b)
                {
                    b.Stop();
                    return;
                }
                if (!LanConfig.LoopbackOnly)
                {
                    old = mdns;
                    mdns = new Mdns(Local.DeviceId, AnnounceTo);
                    mdns.Publish(DeviceName, Host.DeviceType, b.TcpPort);
                }
            }
            old?.Dispose();
            Search();
        });
    }

    // True while the network runs.
    public bool IsRunning
    {
        get
        {
            lock (gate)
            {
                return backend is not null;
            }
        }
    }

    // Closes every link and stops discovery.
    public void Stop()
    {
        LanBackend? b;
        Mdns? m;
        Link[] links;
        lock (gate)
        {
            b = backend;
            m = mdns;
            links = devices.Values.Select(d => d.Link).OfType<Link>().ToArray();
            backend = null;
            mdns = null;
            searching = false;
            searchCount++;
        }
        m?.Dispose();
        b?.Stop();
        foreach (var l in links)
        {
            l.Close();
        }
        Publish();
    }

    // Looks for computers for 10 seconds: mDNS browses, and the identity goes
    // out at once and again after 3 and 6 seconds. Flux does not search all
    // the time. After a search ends, a paired computer still gets a dial from
    // this device when its UDP identity arrives. A computer that is not paired
    // finds this device through its mDNS service and connects.
    public void Search()
    {
        LanBackend? b;
        Mdns? m;
        int count;
        lock (gate)
        {
            count = ++searchCount;
            searching = backend is not null;
            b = backend;
            m = mdns;
        }
        if (b is null)
        {
            return;
        }
        m?.Browse();
        b.Broadcast();
        Publish();
        foreach (var delay in new[] { 3, 6 })
        {
            _ = Task.Delay(TimeSpan.FromSeconds(delay)).ContinueWith(_ =>
            {
                lock (gate)
                {
                    if (searchCount != count)
                    {
                        return;
                    }
                }
                b.Broadcast();
            }, TaskScheduler.Default);
        }
        _ = Task.Delay(SearchTime).ContinueWith(_ =>
        {
            Mdns? current;
            lock (gate)
            {
                // A newer search keeps the state.
                if (searchCount != count)
                {
                    return;
                }
                searching = false;
                current = mdns;
            }
            current?.StopBrowsing();
            Publish();
        }, TaskScheduler.Default);
    }

    // Dials a computer without its UDP identity. Tests use it.
    internal void Dial(string host, int port)
    {
        LanBackend? b;
        lock (gate)
        {
            b = backend;
        }
        b?.Connect(host, port, null);
    }

    // Sends the identity to one host, for example one that mDNS found.
    public void AnnounceTo(string ip)
    {
        LanBackend? b;
        lock (gate)
        {
            b = backend;
        }
        b?.AnnounceTo(ip);
    }

    void Attach(Link link)
    {
        if (!link.Identity.IsFlux)
        {
            FluxLog.Info("core", $"closed the link from {link.Identity.DeviceName}, which is not an Omarchy computer");
            link.Close();
            return;
        }
        Locked(() =>
        {
            // Flux is off, or a newer start replaced the backend.
            if (backend is null)
            {
                link.Close();
                return;
            }
            var id = link.Identity.DeviceId;
            if (Refusal(link) is { } refusal)
            {
                FluxLog.Error("core", $"closed the link from {link.Identity.DeviceName}: {refusal}");
                link.Close();
                return;
            }
            devices.TryGetValue(id, out var existing);
            var old = existing?.Link;
            if (old is { IsOpen: true } && old != link &&
                FluxKit.Link.KeepsOld(old.Outgoing, link.Outgoing, DateTime.UtcNow - old.Started, Local.DeviceId, id))
            {
                FluxLog.Info("core", $"kept the link that {existing!.Name} also keeps, after a crossed dial");
                link.Close();
                return;
            }
            Device d;
            if (existing is not null)
            {
                d = existing;
            }
            else
            {
                EvictUnpairedLink();
                d = new Device(this, link.Identity);
                devices[id] = d;
                order.Add(id);
            }
            d.Identity = link.Identity;
            // A pairing is bound to the link on which it started. fluxd ends
            // its side when a new link replaces that link, so this side ends
            // too, before the new link can accept it.
            if (old is not null && old != link && d.PairState is PairState.Requested or PairState.Incoming)
            {
                FluxLog.Info("core", $"ended the pairing with {d.Name}: a new link replaced its link");
                // The reset of an incoming pairing makes its host wait.
                var incoming = d.PairState == PairState.Incoming;
                d.ResetPair();
                Toast(Device.PairStoppedText(d.Name, incoming));
            }
            // Set the new link first, so that the close of the old link does
            // not mark the computer offline.
            d.Link = link;
            if (old is not null && old != link)
            {
                old.Close();
            }
            d.Certificate = link.PeerCertificate;
            d.LastIp = link.Address;
            if (Trust.Get(id) is not null)
            {
                d.PairState = PairState.Paired;
                Trust.Update(id, t => t with { Name = link.Identity.DeviceName, LastIp = d.LastIp, IsFlux = link.Identity.IsFlux });
            }
            link.Start(
                p =>
                {
                    // Only a pair packet changes the state. Plugin packets skip
                    // the publish. A packet counts only when it comes on the
                    // current link of the computer.
                    if (p.Type == PacketType.Pair)
                    {
                        Locked(() =>
                        {
                            if (d.Link == link)
                            {
                                Dispatch(d, p);
                            }
                        });
                    }
                    else
                    {
                        lock (gate)
                        {
                            if (d.Link == link)
                            {
                                Dispatch(d, p);
                            }
                        }
                    }
                },
                () => Detach(d, link));
            if (d.Paired)
            {
                OnConnected(d);
            }
            else
            {
                CloseWhenIdle(link, id);
            }
        });
    }

    // Closes the link of a computer that is not paired when no pairing runs on
    // it after UnpairedIdle. A pairing that runs moves the check to later.
    void CloseWhenIdle(Link link, string id)
    {
        _ = Task.Delay(UnpairedIdle).ContinueWith(_ =>
        {
            PairState? state;
            lock (gate)
            {
                state = devices.TryGetValue(id, out var d) && d.Link == link ? d.PairState : null;
            }
            switch (state)
            {
                case null:
                case PairState.Paired:
                    return;
                case PairState.Requested:
                case PairState.Incoming:
                    CloseWhenIdle(link, id);
                    return;
                case PairState.None:
                    FluxLog.Info("core", $"closed the link from {link.Identity.DeviceName}: no pairing in {(int)UnpairedIdle.TotalSeconds} seconds");
                    link.Close();
                    return;
            }
        }, TaskScheduler.Default);
    }

    // The reason to refuse a new link, or null. The lock is held. A paired
    // computer must show its pinned certificate, and a computer with an open
    // pairing or a live link must show the certificate that it has.
    string? Refusal(Link link)
    {
        var id = link.Identity.DeviceId;
        devices.TryGetValue(id, out var d);
        byte[]? live = d?.Link is { IsOpen: true } old && old != link ? old.PeerCertificate : null;
        // An entry whose certificate does not read matches no certificate.
        var pinned = Trust.Get(id) is { } t ? t.CertificateDer ?? [] : null;
        return LinkRefusal(link.PeerCertificate, pinned, d?.PairState ?? PairState.None, d?.PairCertificate, live);
    }

    // The reason to refuse a new link that shows the certificate presented,
    // or null. pinned is the certificate of the trust entry, pairState and
    // pairCertificate are the pairing of the computer, and live is the
    // certificate of its other open link.
    internal static string? LinkRefusal(byte[] presented, byte[]? pinned, PairState pairState, byte[]? pairCertificate, byte[]? live)
    {
        if (pinned is not null && !pinned.AsSpan().SequenceEqual(presented))
        {
            return "the certificate differs from the paired one";
        }
        if (live is not null && !live.AsSpan().SequenceEqual(presented))
        {
            return "a live link has another certificate";
        }
        // A pairing is bound to the certificate behind its key.
        if (pairState is PairState.Requested or PairState.Incoming &&
            (pairCertificate is null || !pairCertificate.AsSpan().SequenceEqual(presented)))
        {
            return "a pairing with another certificate is open";
        }
        return null;
    }

    // Drops the oldest computer that is not paired and has a link, when the
    // list holds too many of them. A computer without an open pairing goes
    // first. The lock is held.
    void EvictUnpairedLink()
    {
        var unpaired = order.Select(id => devices[id]).Where(d => !d.Paired && d.Link is not null).ToList();
        if (unpaired.Count < MaxUnpairedLinks)
        {
            return;
        }
        var d = unpaired.FirstOrDefault(d => d.PairState == PairState.None) ?? unpaired[0];
        FluxLog.Info("core", $"closed the link from {d.Name}: too many computers that are not paired");
        d.CancelPair();
        var link = d.Link;
        // Without its link, the computer ignores the packets and the close of
        // the old link.
        d.Link = null;
        devices.Remove(d.Id);
        order.Remove(d.Id);
        link?.Close();
    }

    void Detach(Device d, Link link)
    {
        Locked(() =>
        {
            if (d.Link != link)
            {
                return;
            }
            d.Link = null;
            if (d.PairState is PairState.Requested or PairState.Incoming)
            {
                d.PairState = PairState.None;
            }
            if (d.Paired)
            {
                foreach (var p in Plugins)
                {
                    Guard(p, () => p.OnDisconnected(d));
                }
            }
            else if (devices.TryGetValue(d.Id, out var current) && current == d)
            {
                devices.Remove(d.Id);
                order.Remove(d.Id);
            }
        });
    }

    // State

    // Runs the action under the core lock and publishes the new state.
    public void Locked(Action action)
    {
        lock (gate)
        {
            action();
        }
        Publish();
    }

    public T Locked<T>(Func<T> action)
    {
        T result;
        lock (gate)
        {
            result = action();
        }
        Publish();
        return result;
    }

    public CoreState State
    {
        get
        {
            lock (gate)
            {
                return new CoreState
                {
                    DeviceName = DeviceName,
                    DeviceId = Local.DeviceId,
                    Devices = ValueList.From(order.Select(id => devices[id].Snapshot())),
                    ListeningUdp = backend?.ListeningUdp ?? true,
                    TcpPort = backend?.TcpPort ?? 0,
                    Enabled = Enabled,
                    Searching = searching,
                };
            }
        }
    }

    // Sends the state to Changed when it differs from the last state that
    // went out. The lock keeps the snapshots in order.
    public void Publish()
    {
        lock (gate)
        {
            var snapshot = State;
            if (snapshot == published)
            {
                return;
            }
            published = snapshot;
            Host.Post(() => Changed?.Invoke(snapshot));
        }
    }

    public void Toast(string message)
    {
        // Toasts can name files and hold text from the computer, so the log
        // gets only the fact.
        FluxLog.Debug("core", "toast");
        Host.Post(() => Toasted?.Invoke(message));
    }

    public Device? GetDevice(string id)
    {
        lock (gate)
        {
            return devices.GetValueOrDefault(id);
        }
    }

    // Reads fields of a computer under the core lock, without a publish.
    public T? WithDevice<T>(string id, Func<Device, T> body)
    {
        lock (gate)
        {
            return devices.TryGetValue(id, out var d) ? body(d) : default;
        }
    }

    // True while a search for computers runs.
    internal bool IsSearching
    {
        get
        {
            lock (gate)
            {
                return searching;
            }
        }
    }

    // True when a computer other than id has an open incoming request. The
    // lock is held.
    internal bool HasIncomingPair(string except) =>
        devices.Values.Any(d => d.Id != except && d.PairState == PairState.Incoming);

    // Makes the host of the computer wait after its incoming request ended
    // without a pairing. The lock is held.
    internal void IncomingPairEnded(Device d) =>
        pairCooldown.Add(d.Id, d.Link?.Address ?? d.LastIp, uptime.Elapsed);

    // True while the host of the computer waits after its last incoming
    // request. The lock is held.
    internal bool PairCooldownBlocks(Device d) =>
        pairCooldown.Blocks(d.Id, d.Link?.Address ?? d.LastIp, uptime.Elapsed);

    public IReadOnlyList<Device> ConnectedPaired()
    {
        lock (gate)
        {
            return order.Select(id => devices[id]).Where(d => d.Paired && d.Online).ToArray();
        }
    }

    // The IDs of the connected, paired computers, and with accepting only
    // those that accept the packet type.
    public IReadOnlyList<string> ConnectedPairedIds(string? accepting = null)
    {
        lock (gate)
        {
            return order.Select(id => devices[id])
                .Where(d => d.Paired && d.Online && (accepting is null || d.Accepts(accepting)))
                .Select(d => d.Id).ToArray();
        }
    }

    public T? Plugin<T>() where T : class, IFluxPlugin => Plugins.OfType<T>().FirstOrDefault();

    // Events

    // Handles one packet from a computer. The core lock is held.
    void Dispatch(Device d, Packet p)
    {
        if (p.Type == PacketType.Pair)
        {
            d.OnPairPacket(p);
            return;
        }
        if (!d.Paired)
        {
            FluxLog.Debug("core", $"ignored {p.Type} from unpaired {d.Name}");
            return;
        }
        if (routes.TryGetValue(p.Type, out var list))
        {
            foreach (var plugin in list)
            {
                Guard(plugin, () => plugin.Handle(p, d));
            }
        }
    }

    // Runs a call of a plugin. An exception in one feature does not end the
    // link or the other features.
    static void Guard(IFluxPlugin plugin, Action call)
    {
        try
        {
            call();
        }
        catch (Exception e)
        {
            FluxLog.Error("core", $"{plugin.GetType().Name} failed: {e}");
        }
    }

    internal void OnPaired(Device d) => OnConnected(d);

    internal void DidUnpair(Device d)
    {
        foreach (var p in Plugins)
        {
            Guard(p, () => p.OnDisconnected(d));
        }
    }

    // Tells every plugin that a paired computer is ready.
    void OnConnected(Device d)
    {
        foreach (var p in Plugins)
        {
            Guard(p, () => p.OnConnected(d));
        }
    }

    internal void NotifyPairRequest(Device d)
    {
        var snapshot = d.Snapshot();
        Host.Post(() => PairRequested?.Invoke(snapshot));
    }

    // Actions

    public string PreviewKey(string id, long timestamp)
    {
        lock (gate)
        {
            return devices.TryGetValue(id, out var d) ? d.PreviewKey(timestamp) : "";
        }
    }

    public void Pair(string id, long timestamp) => Locked(() => GetDevice(id)?.RequestPair(timestamp));
    public void AcceptPair(string id) => Locked(() => GetDevice(id)?.AcceptPair());
    public void CancelPair(string id) => Locked(() => GetDevice(id)?.CancelPair());

    public void Unpair(string id)
    {
        Locked(() =>
        {
            if (!devices.TryGetValue(id, out var d))
            {
                return;
            }
            var wasPaired = d.Paired;
            d.Unpair();
            if (wasPaired)
            {
                DidUnpair(d);
            }
            if (!d.Online)
            {
                devices.Remove(id);
                order.Remove(id);
            }
        });
    }

    // Sends a packet to a paired, connected computer. It returns false when
    // the computer is offline or not paired.
    public bool Send(Packet p, string id)
    {
        lock (gate)
        {
            return devices.TryGetValue(id, out var d) && d.Send(p);
        }
    }

    // Sends a packet like Send and waits until the link wrote it to the
    // network. It returns false when the computer is offline or not paired,
    // or the write failed.
    public async Task<bool> SendFlushedAsync(Packet p, string id)
    {
        Link? link;
        lock (gate)
        {
            link = devices.TryGetValue(id, out var d) && d.Paired && d.Link is { IsOpen: true } l ? l : null;
        }
        return link is not null && await link.SendFlushedAsync(p).ConfigureAwait(false);
    }

    public void Dispose()
    {
        Stop();
        Local.Dispose();
    }

    // Connects the backend to the core.
    sealed class BackendDelegate(FluxCore core) : ILanBackendDelegate
    {
        public byte[]? TrustedCertificate(string deviceId) => core.Trust.Get(deviceId)?.CertificateDer;
        public bool HasLink(string deviceId) => core.WithDevice(deviceId, d => d.Online);
        public bool DialsFrom(string deviceId) => core.Trust.Get(deviceId) is not null || core.IsSearching;
        public void OnLink(Link link) => core.Attach(link);
        public IReadOnlyList<string> KnownAddresses() =>
            core.Trust.All().Select(t => t.LastIp).Where(ip => ip.Length > 0).ToArray();
    }
}

// Helpers for packet bodies that features share.
public static class Bodies
{
    public static JsonObject Of(params (string Key, JsonNode? Value)[] fields)
    {
        var o = new JsonObject();
        foreach (var (key, value) in fields)
        {
            o[key] = value;
        }
        return o;
    }
}
