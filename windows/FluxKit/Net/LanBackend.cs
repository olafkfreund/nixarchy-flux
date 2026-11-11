using System.Net;
using System.Net.NetworkInformation;
using System.Net.Security;
using System.Net.Sockets;

namespace FluxKit;

// Ports and discovery scope of the LAN backend.
public sealed record LanConfig
{
    // The UDP port that receives identity broadcasts.
    public int UdpPort { get; init; } = 12100;

    // The UDP port of computers that this device announces itself to.
    public int PeerUdpPort { get; init; } = 12100;

    // The TCP port range for links.
    public int FirstTcpPort { get; init; } = 12100;
    public int LastTcpPort { get; init; } = 12108;

    // The TCP ports of computers that this device dials after a UDP identity.
    // fluxd listens on 12100 to 12108. In loopback mode the dial goes to any
    // port on 127.0.0.1, for a headless fluxd in a test.
    public int FirstPeerTcpPort { get; init; } = 12100;
    public int LastPeerTcpPort { get; init; } = 12108;

    // Announces only to 127.0.0.1 and skips mDNS, for tests against a
    // headless fluxd.
    public bool LoopbackOnly { get; init; }

    // Reads FLUX_UDP_PORT, FLUX_PEER_UDP_PORT, FLUX_TCP_PORT, and FLUX_LOOPBACK=1.
    public static LanConfig FromEnvironment(Func<string, string?>? env = null)
    {
        env ??= Environment.GetEnvironmentVariable;
        var c = new LanConfig();
        if (int.TryParse(env("FLUX_UDP_PORT"), out var udp))
        {
            c = c with { UdpPort = udp };
        }
        if (int.TryParse(env("FLUX_PEER_UDP_PORT"), out var peer))
        {
            c = c with { PeerUdpPort = peer };
        }
        if (int.TryParse(env("FLUX_TCP_PORT"), out var tcp))
        {
            c = c with { FirstTcpPort = tcp, LastTcpPort = tcp + 8 };
        }
        return c with { LoopbackOnly = env("FLUX_LOOPBACK") == "1" };
    }
}

// What the backend asks of the core.
public interface ILanBackendDelegate
{
    // Returns the pinned certificate of a trusted computer, or null.
    byte[]? TrustedCertificate(string deviceId);

    // Reports whether a live link to the computer exists.
    bool HasLink(string deviceId);

    // Reports whether a UDP identity from the computer may start a dial: a
    // trusted computer, or any computer while a search runs.
    bool DialsFrom(string deviceId);

    // Receives a new link after TLS and the identity check.
    void OnLink(Link link);

    // Returns the addresses of trusted computers to contact directly.
    IReadOnlyList<string> KnownAddresses();
}

// The LAN backend. It broadcasts the identity over UDP, accepts TCP links,
// connects to computers that broadcast, and runs the TLS handshake.
// LanBackend.swift of the Mac app has the same rules.
public sealed class LanBackend
{
    // The most connections before their link starts. A new connection closes
    // the oldest one, so that idle connections cannot block a computer that
    // connects.
    const int MaxHandshakes = 16;

    // The most dials that did not connect yet.
    const int MaxDials = 8;

    // The shortest time between 2 dials to 1 computer.
    static readonly TimeSpan DialInterval = TimeSpan.FromSeconds(1);

    // The most device IDs that the dial interval remembers.
    const int MaxDialTargets = 256;

    // How long the identity exchange and the TLS handshake may take.
    static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);

    // The TCP keepalive of fluxd: the first probe after 10 idle seconds, then
    // 1 probe each 5 seconds. After 3 probes without an answer, the system
    // closes the link, so a link that a network change broke does not stay.
    const int KeepAliveIdle = 10;
    const int KeepAliveInterval = 5;
    const int KeepAliveCount = 3;

    readonly FluxTls tls;
    readonly Func<int, Identity> identity;
    readonly ILanBackendDelegate? owner;
    readonly Lock gate = new();
    bool running;
    int generation;
    int tcpPort;
    bool listeningUdp;
    TcpListener? server;
    Socket? udp;
    CancellationTokenSource? stopping;
    readonly Dictionary<string, DateTime> lastAttempt = [];
    readonly List<Socket> pending = [];
    int dials;

    public LanConfig Config { get; }

    public LanBackend(FluxTls tls, LanConfig config, Func<int, Identity> identity, ILanBackendDelegate owner)
    {
        this.tls = tls;
        Config = config;
        this.identity = identity;
        this.owner = owner;
    }

    public int TcpPort
    {
        get
        {
            lock (gate)
            {
                return tcpPort;
            }
        }
    }

    // True when this app owns the UDP port and hears broadcasts.
    public bool ListeningUdp
    {
        get
        {
            lock (gate)
            {
                return listeningUdp;
            }
        }
    }

    string LocalDeviceId => tls.Local.DeviceId;

    bool IsRunning
    {
        get
        {
            lock (gate)
            {
                return running;
            }
        }
    }

    public void Start()
    {
        int current;
        lock (gate)
        {
            if (running)
            {
                return;
            }
            running = true;
            current = ++generation;
        }
        var listener = OpenServer();
        var (socket, listening) = OpenUdp();
        var cts = new CancellationTokenSource();
        lock (gate)
        {
            // A stop during the binds wins. The new sockets close.
            if (!running || generation != current)
            {
                listener?.Stop();
                socket?.Dispose();
                return;
            }
            server = listener;
            tcpPort = (listener?.LocalEndpoint as IPEndPoint)?.Port ?? 0;
            udp = socket;
            listeningUdp = listening;
            stopping = cts;
        }
        if (listener is not null)
        {
            _ = Task.Run(() => AcceptLoopAsync(listener, cts.Token));
        }
        if (socket is not null)
        {
            _ = Task.Run(() => UdpLoopAsync(socket, cts.Token));
        }
        Broadcast();
    }

    // Closes the listeners and every connection before its link. The core
    // closes the links.
    public void Stop()
    {
        TcpListener? listener;
        Socket? socket;
        Socket[] open;
        CancellationTokenSource? cts;
        lock (gate)
        {
            running = false;
            listener = server;
            socket = udp;
            open = pending.ToArray();
            cts = stopping;
            server = null;
            udp = null;
            stopping = null;
            tcpPort = 0;
            pending.Clear();
            lastAttempt.Clear();
        }
        cts?.Cancel();
        listener?.Stop();
        socket?.Dispose();
        foreach (var s in open)
        {
            s.Dispose();
        }
    }

    TcpListener? OpenServer()
    {
        for (var port = Config.FirstTcpPort; port <= Config.LastTcpPort; port++)
        {
            var listener = new TcpListener(IPAddress.Any, port);
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    // Another program cannot take the port with SO_REUSEADDR.
                    listener.ExclusiveAddressUse = true;
                }
                listener.Start();
                return listener;
            }
            catch (SocketException)
            {
                listener.Stop();
            }
        }
        FluxLog.Error("net", $"no free TCP port in {Config.FirstTcpPort}-{Config.LastTcpPort}");
        return null;
    }

    (Socket?, bool) OpenUdp()
    {
        Socket Make()
        {
            var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp) { EnableBroadcast = true };
            if (OperatingSystem.IsWindows())
            {
                // A reset from an earlier send to a closed port would end the
                // next receive.
                const int SioUdpConnreset = -1744830452;
                s.IOControl(SioUdpConnreset, [0, 0, 0, 0], null);
            }
            return s;
        }
        var socket = Make();
        try
        {
            socket.Bind(new IPEndPoint(IPAddress.Any, Config.UdpPort));
            return (socket, true);
        }
        catch (SocketException e)
        {
            socket.Dispose();
            FluxLog.Info("net", $"UDP {Config.UdpPort} is in use: {e.Message}. Flux only announces itself.");
        }
        socket = Make();
        try
        {
            socket.Bind(new IPEndPoint(IPAddress.Any, 0));
            return (socket, false);
        }
        catch (SocketException)
        {
            socket.Dispose();
            return (null, false);
        }
    }

    // Tracks a connection until its link starts. It returns false when the
    // backend stopped. With too many connections, the oldest one closes.
    bool Track(Socket s)
    {
        Socket? evicted = null;
        lock (gate)
        {
            if (!running)
            {
                return false;
            }
            pending.Add(s);
            if (pending.Count > MaxHandshakes)
            {
                evicted = pending[0];
                pending.RemoveAt(0);
            }
        }
        evicted?.Dispose();
        return true;
    }

    void Untrack(Socket s)
    {
        lock (gate)
        {
            pending.Remove(s);
        }
    }

    static void SetKeepAlive(Socket s)
    {
        s.NoDelay = true;
        s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        s.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, KeepAliveIdle);
        s.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, KeepAliveInterval);
        s.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, KeepAliveCount);
    }

    // Sends the identity to every broadcast address and to known computers.
    public void Broadcast()
    {
        var targets = BroadcastTargets(
            Config.LoopbackOnly,
            Config.LoopbackOnly ? [] : BroadcastAddresses(),
            Config.LoopbackOnly ? [] : owner?.KnownAddresses() ?? []);
        foreach (var t in targets)
        {
            AnnounceTo(t);
        }
    }

    // The addresses that an announcement goes to, each once.
    internal static IReadOnlyList<string> BroadcastTargets(bool loopbackOnly, IReadOnlyList<string> interfaces, IReadOnlyList<string> known)
    {
        if (loopbackOnly)
        {
            return ["127.0.0.1"];
        }
        return new[] { "255.255.255.255" }.Concat(interfaces).Concat(known).Where(a => a.Length > 0).Distinct().ToArray();
    }

    // Sends the identity to one address, for example a host that mDNS found.
    public void AnnounceTo(string ip)
    {
        Socket? socket;
        int port;
        lock (gate)
        {
            socket = udp;
            port = tcpPort;
        }
        if (socket is null || port <= 0 || !IPAddress.TryParse(ip, out var address))
        {
            return;
        }
        var data = identity(port).ToPacket(withPort: true).Serialize();
        try
        {
            socket.SendTo(data, new IPEndPoint(address, Config.PeerUdpPort));
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException)
        {
            FluxLog.Debug("net", $"UDP send to {ip} failed: {e.Message}");
        }
    }

    // The broadcast address of each IPv4 network of this computer.
    static IReadOnlyList<string> BroadcastAddresses()
    {
        var list = new List<string>();
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up || nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
                {
                    continue;
                }
                foreach (var u in nic.GetIPProperties().UnicastAddresses)
                {
                    if (u.Address.AddressFamily != AddressFamily.InterNetwork || u.IPv4Mask is null)
                    {
                        continue;
                    }
                    var a = u.Address.GetAddressBytes();
                    var m = u.IPv4Mask.GetAddressBytes();
                    var b = new byte[4];
                    for (var i = 0; i < 4; i++)
                    {
                        b[i] = (byte)(a[i] | ~m[i]);
                    }
                    list.Add(new IPAddress(b).ToString());
                }
            }
        }
        catch (NetworkInformationException)
        {
        }
        return list;
    }

    async Task UdpLoopAsync(Socket socket, CancellationToken ct)
    {
        var buffer = new byte[Identity.MaxLine];
        while (!ct.IsCancellationRequested)
        {
            SocketReceiveFromResult r;
            try
            {
                r = await socket.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), ct).ConfigureAwait(false);
            }
            catch (SocketException)
            {
                // A datagram that was too large, or a reset. The next one counts.
                continue;
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException)
            {
                return;
            }
            OnDatagram(buffer.AsSpan(0, r.ReceivedBytes), (IPEndPoint)r.RemoteEndPoint);
        }
    }

    void OnDatagram(ReadOnlySpan<byte> data, IPEndPoint from)
    {
        if (!IsRunning || Packet.Parse(data) is not { } p || Identity.From(p) is not { } id || id.DeviceId == LocalDeviceId || !id.IsFlux)
        {
            return;
        }
        var ip = from.Address.MapToIPv4().ToString();
        if (!DialAllowed(ip, id.TcpPort, Config) || owner is null || !owner.DialsFrom(id.DeviceId) || owner.HasLink(id.DeviceId))
        {
            return;
        }
        var now = DateTime.UtcNow;
        lock (gate)
        {
            foreach (var key in lastAttempt.Where(e => now - e.Value >= DialInterval).Select(e => e.Key).ToArray())
            {
                lastAttempt.Remove(key);
            }
            if (lastAttempt.ContainsKey(id.DeviceId) || lastAttempt.Count >= MaxDialTargets)
            {
                return;
            }
            lastAttempt[id.DeviceId] = now;
        }
        Connect(ip, id.TcpPort, id);
    }

    // Reports whether a UDP identity may make this device dial the port. The
    // address is the source of the datagram, which a LAN host can fake, so
    // the dial goes only to the ports of fluxd. In loopback mode it goes only
    // to 127.0.0.1.
    internal static bool DialAllowed(string ip, int port, LanConfig config)
    {
        if (config.LoopbackOnly)
        {
            return ip.StartsWith("127.", StringComparison.Ordinal) && port is >= 1 and <= 65535;
        }
        return port >= config.FirstPeerTcpPort && port <= config.LastPeerTcpPort;
    }

    // Connects to a computer that sent its identity over UDP. This side sends
    // its identity in plain text and is then the TLS server.
    public void Connect(string host, int port, Identity? udpIdentity)
    {
        if (port is < 1 or > 65535)
        {
            return;
        }
        lock (gate)
        {
            if (!running || dials >= MaxDials)
            {
                return;
            }
            dials++;
        }
        _ = Task.Run(() => DialAsync(host, port, udpIdentity));
    }

    async Task DialAsync(string host, int port, Identity? udpIdentity)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        var connected = false;
        try
        {
            SetKeepAlive(socket);
            if (!Track(socket))
            {
                socket.Dispose();
                return;
            }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await socket.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
            connected = true;
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException or ObjectDisposedException)
        {
            FluxLog.Info("net", $"link to {host}:{port} failed: {e.Message}");
            Untrack(socket);
            socket.Dispose();
        }
        finally
        {
            lock (gate)
            {
                dials--;
            }
        }
        if (!connected)
        {
            return;
        }
        await HandshakeAsync(socket, udpIdentity, dialed: true).ConfigureAwait(false);
    }

    async Task AcceptLoopAsync(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            Socket socket;
            try
            {
                socket = await listener.AcceptSocketAsync(ct).ConfigureAwait(false);
            }
            catch (SocketException)
            {
                continue;
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }
            try
            {
                SetKeepAlive(socket);
            }
            catch (SocketException)
            {
                socket.Dispose();
                continue;
            }
            if (!Track(socket))
            {
                socket.Dispose();
                continue;
            }
            _ = Task.Run(() => HandshakeAsync(socket, null, dialed: false));
        }
    }

    // Runs the identity exchange and the TLS handshake, then gives the link
    // to the core. The side that opened the connection sends its identity in
    // plain text first and is the TLS server.
    async Task HandshakeAsync(Socket socket, Identity? udpIdentity, bool dialed)
    {
        var remote = (socket.RemoteEndPoint as IPEndPoint)?.Address.MapToIPv4().ToString() ?? "?";
        var stream = new NetworkStream(socket, ownsSocket: false);
        SslStream? ssl = null;
        try
        {
            using var deadline = new CancellationTokenSource(HandshakeTimeout);
            var ct = deadline.Token;
            Identity? plain = udpIdentity;
            if (dialed)
            {
                var line = identity(0).ToPacket(target: udpIdentity).Serialize();
                await stream.WriteAsync(line, ct).ConfigureAwait(false);
                ssl = await tls.ServerAsync(stream, ct).ConfigureAwait(false);
            }
            else
            {
                var line = await LineReader.ReadExactLineAsync(stream, Identity.MaxLine, ct).ConfigureAwait(false);
                if (Packet.Parse(line) is not { } packet || Identity.From(packet) is not { } id)
                {
                    throw new InvalidDataException("bad identity");
                }
                if (id.DeviceId == LocalDeviceId)
                {
                    throw new InvalidDataException("a link to this device");
                }
                // A computer that answers a broadcast names the device it wants.
                if (packet.String("targetDeviceId") is { } target && target != LocalDeviceId)
                {
                    throw new InvalidDataException($"identity is for {target}");
                }
                plain = id;
                ssl = await tls.ClientAsync(stream, id.DeviceId, ct).ConfigureAwait(false);
            }
            if (!IsRunning)
            {
                throw new OperationCanceledException("backend stopped");
            }
            // Both sides write the identity at once, so that neither side
            // waits for the other.
            await ssl.WriteAsync(identity(0).ToPacket().Serialize(), ct).ConfigureAwait(false);
            await ssl.FlushAsync(ct).ConfigureAwait(false);
            var reader = new LineReader(ssl, Link.MaxUnpairedLine);
            var secureLine = await reader.ReadLineAsync(ct).ConfigureAwait(false) ?? throw new EndOfStreamException();
            if (Packet.Parse(secureLine) is not { } securePacket || Identity.From(securePacket) is not { } secure)
            {
                throw new InvalidDataException("bad identity after TLS");
            }
            if (plain is not null && plain.DeviceId != secure.DeviceId)
            {
                throw new InvalidDataException("device ID changed after TLS");
            }
            if (plain is not null && plain.ProtocolVersion != secure.ProtocolVersion)
            {
                throw new InvalidDataException("protocol version changed after TLS");
            }
            var link = Finish(socket, ssl, reader, secure, dialed);
            Untrack(socket);
            if (!IsRunning)
            {
                link.Close();
                return;
            }
            owner?.OnLink(link);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or OperationCanceledException or
                                  ObjectDisposedException or SocketException or System.Security.Authentication.AuthenticationException)
        {
            FluxLog.Info("net", $"link {(dialed ? "to" : "from")} {remote} failed: {e.Message}");
            Untrack(socket);
            if (ssl is not null)
            {
                await ssl.DisposeAsync().ConfigureAwait(false);
            }
            await stream.DisposeAsync().ConfigureAwait(false);
            socket.Dispose();
        }
    }

    // Checks the identity after TLS and makes the link. The core checks the
    // pinned certificate again under its lock.
    Link Finish(Socket socket, SslStream ssl, LineReader reader, Identity id, bool outgoing)
    {
        var cert = FluxTls.PeerCertificate(ssl) ?? throw new InvalidDataException("peer sent no certificate");
        var cn = Certificates.CommonName(cert);
        if (cn != id.DeviceId)
        {
            throw new InvalidDataException($"certificate CN {cn ?? "none"} does not match {id.DeviceId}");
        }
        if (owner?.TrustedCertificate(id.DeviceId) is { } pinned && !pinned.AsSpan().SequenceEqual(cert))
        {
            throw new InvalidDataException($"{id.DeviceName} presented a different certificate");
        }
        return new Link(socket, ssl, reader, id, cert, outgoing);
    }
}
