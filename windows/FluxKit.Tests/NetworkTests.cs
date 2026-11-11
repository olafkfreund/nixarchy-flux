using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using FluxKit;

namespace FluxKit.Tests;

// Links between 2 backends on loopback. On a Windows runner, these tests
// check the TLS handshake of Schannel in both roles.
public class NetworkTests
{
    public NetworkTests() => FluxLog.Sink = (level, category, message) => TestCore.Log.Enqueue($"{level} {category}: {message}");

    sealed class Owner : ILanBackendDelegate
    {
        public ConcurrentQueue<Link> Links { get; } = new();
        public byte[]? Pinned { get; set; }
        public byte[]? TrustedCertificate(string deviceId) => Pinned;
        public bool HasLink(string deviceId) => false;
        public bool DialsFrom(string deviceId) => true;
        public void OnLink(Link link) => Links.Enqueue(link);
        public IReadOnlyList<string> KnownAddresses() => [];
    }

    sealed class Side : IDisposable
    {
        public LocalCertificate Certificate { get; }
        public Owner Owner { get; } = new();
        public LanBackend Backend { get; }
        readonly string dir = CoreTests.TempDir();

        public Side(int udpPort, int peerUdpPort, int tcpPort)
        {
            Certificate = LocalCertificate.LoadOrCreate(dir, new PlainKeyProtector());
            var config = new LanConfig
            {
                UdpPort = udpPort,
                PeerUdpPort = peerUdpPort,
                FirstTcpPort = tcpPort,
                LastTcpPort = tcpPort + 4,
                LoopbackOnly = true,
            };
            Backend = new LanBackend(new FluxTls(Certificate), config, port => new Identity(
                Certificate.DeviceId, "side", "desktop", 8, [PacketType.Tunnel, PacketType.Ping], [PacketType.Ping], port), Owner);
        }

        public void Dispose()
        {
            Backend.Stop();
            Certificate.Dispose();
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task BackendsLinkAndExchangePackets()
    {
        // The announcements go to a port that nobody reads, so only the
        // dial below makes a link.
        using var a = new Side(29131, 29199, 29140);
        using var b = new Side(29132, 29199, 29150);
        a.Backend.Start();
        b.Backend.Start();
        a.Backend.Connect("127.0.0.1", b.Backend.TcpPort, null);
        await Wait.UntilAsync(() => !a.Owner.Links.IsEmpty && !b.Owner.Links.IsEmpty, "the links");
        a.Owner.Links.TryPeek(out var la);
        b.Owner.Links.TryPeek(out var lb);
        Assert.True(la!.Outgoing);
        Assert.False(lb!.Outgoing);
        Assert.Equal(b.Certificate.DeviceId, la.Identity.DeviceId);
        Assert.Equal(b.Certificate.CertificateDer, la.PeerCertificate);
        Assert.Equal(a.Certificate.CertificateDer, lb.PeerCertificate);

        var received = new ConcurrentQueue<Packet>();
        var closed = new TaskCompletionSource();
        lb.Start(received.Enqueue, () => closed.TrySetResult());
        la.Start(_ => { }, () => { });
        Assert.True(await la.SendFlushedAsync(new Packet(PacketType.Ping, new JsonObject { ["message"] = "hi" })));
        // A line above the unpaired limit ends the link, and a paired link takes it.
        lb.SetPaired(true);
        la.Send(new Packet(PacketType.Ping, new JsonObject { ["message"] = new string('x', 100_000) }));
        await Wait.UntilAsync(() => received.Count == 2, "the packets");
        Assert.Equal("hi", received.First().String("message"));
        la.Close();
        await closed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(lb.IsOpen);
    }

    [Fact]
    public async Task UdpIdentityMakesADial()
    {
        // The announcements of a go to a port that nobody reads.
        using var a = new Side(29231, 29299, 29240);
        using var b = new Side(29232, 29231, 29250);
        a.Backend.Start();
        // The start of b announces b to a, and a dials b.
        b.Backend.Start();
        await Wait.UntilAsync(() => !a.Owner.Links.IsEmpty && !b.Owner.Links.IsEmpty, "the links after the announcement");
        a.Owner.Links.TryPeek(out var la);
        Assert.True(la!.Outgoing);
        Assert.Equal(b.Certificate.DeviceId, la.Identity.DeviceId);
    }

    [Fact]
    public async Task PinnedCertificateRefusesAnother()
    {
        using var a = new Side(29331, 29399, 29340);
        using var b = new Side(29332, 29399, 29350);
        a.Owner.Pinned = [1, 2, 3];
        a.Backend.Start();
        b.Backend.Start();
        a.Backend.Connect("127.0.0.1", b.Backend.TcpPort, null);
        await Task.Delay(1500);
        Assert.True(a.Owner.Links.IsEmpty);
    }

    [Fact]
    public async Task PayloadServerTakesOnlyTheExpectedCertificate()
    {
        var dir = CoreTests.TempDir();
        using var server = LocalCertificate.LoadOrCreate(Path.Combine(dir, "s"), new PlainKeyProtector());
        using var client = LocalCertificate.LoadOrCreate(Path.Combine(dir, "c"), new PlainKeyProtector());
        using var stranger = LocalCertificate.LoadOrCreate(Path.Combine(dir, "x"), new PlainKeyProtector());
        var tls = new FluxTls(server);
        using var payload = PayloadServer.Open(tls, client.CertificateDer, 29400, 29420);
        var accepted = payload.AcceptAsync(TimeSpan.FromSeconds(10));

        // A stranger connects first and is closed.
        try
        {
            await SendAsync(stranger, payload.Port, [9, 9]);
        }
        catch (Exception e) when (e is IOException or System.Security.Authentication.AuthenticationException or System.Net.Sockets.SocketException)
        {
        }
        await Task.Delay(300);
        Assert.False(accepted.IsCompleted);

        var data = Enumerable.Range(0, 300_000).Select(i => (byte)i).ToArray();
        var send = SendAsync(client, payload.Port, data);
        var stream = await accepted;
        var target = new MemoryStream();
        await stream.ReceiveAsync(target, data.Length, null, default);
        await stream.DisposeAsync();
        await send;
        Assert.Equal(data, target.ToArray());
        Directory.Delete(dir, true);
    }

    // Connects as the TLS client, as fluxd does for a payload, and sends the bytes.
    static async Task SendAsync(LocalCertificate certificate, int port, byte[] data)
    {
        using var tcp = new System.Net.Sockets.TcpClient();
        await tcp.ConnectAsync("127.0.0.1", port);
        await using var ssl = await new FluxTls(certificate).ClientAsync(tcp.GetStream(), "flux-payload", default);
        await ssl.WriteAsync(data);
        await ssl.FlushAsync();
        await ssl.ShutdownAsync();
        // Read to the end, so that the close sends no reset for unread data,
        // such as a TLS 1.3 session ticket. Windows resets the connection
        // when the server closes before it reads this close_notify. The data
        // arrived before that.
        var rest = new byte[1024];
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            while (await ssl.ReadAsync(rest, wait.Token) > 0)
            {
            }
        }
        catch (IOException)
        {
        }
    }
}
