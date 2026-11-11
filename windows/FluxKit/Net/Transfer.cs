using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Text.Json.Nodes;

namespace FluxKit;

// Lets through at most 1 progress report per interval. A fast transfer
// otherwise redraws the UI hundreds of times per second. The caller sends the
// final report itself.
public struct ProgressThrottle
{
    static readonly long Interval = Stopwatch.Frequency / 10;
    long last;
    bool any;

    public bool Due()
    {
        var now = Stopwatch.GetTimestamp();
        if (any && now < last + Interval)
        {
            return false;
        }
        any = true;
        last = now;
        return true;
    }
}

// An open TLS byte stream: one payload transfer or one tunnel.
public sealed class TlsStream(SslStream ssl, Socket socket, byte[] peerCertificate) : IAsyncDisposable
{
    public SslStream Stream { get; } = ssl;
    public byte[] PeerCertificate { get; } = peerCertificate;

    // Reads size bytes into the target, or until the peer closes when size is
    // negative. A stream that ends early throws IOException.
    public async Task ReceiveAsync(Stream target, long size, Action<long>? progress, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        long done = 0;
        var throttle = new ProgressThrottle();
        while (size < 0 || done < size)
        {
            var want = size < 0 ? buffer.Length : (int)Math.Min(buffer.Length, size - done);
            var n = await Stream.ReadAsync(buffer.AsMemory(0, want), ct).ConfigureAwait(false);
            if (n == 0)
            {
                break;
            }
            await target.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
            done += n;
            if (progress is not null && throttle.Due())
            {
                progress(done);
            }
        }
        progress?.Invoke(done);
        if (size >= 0 && done < size)
        {
            throw new IOException($"payload ended at {done} of {size} bytes");
        }
    }

    // Writes size bytes from the source, or the source to its end when size
    // is negative, then ends the TLS stream so that the peer reads the end.
    public async Task SendAsync(Stream source, long size, Action<long>? progress, CancellationToken ct)
    {
        var buffer = new byte[64 * 1024];
        long done = 0;
        var throttle = new ProgressThrottle();
        while (size < 0 || done < size)
        {
            var want = size < 0 ? buffer.Length : (int)Math.Min(buffer.Length, size - done);
            var n = await source.ReadAsync(buffer.AsMemory(0, want), ct).ConfigureAwait(false);
            if (n == 0)
            {
                break;
            }
            await Stream.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
            done += n;
            if (progress is not null && throttle.Due())
            {
                progress(done);
            }
        }
        await Stream.FlushAsync(ct).ConfigureAwait(false);
        progress?.Invoke(done);
        if (size >= 0 && done < size)
        {
            throw new IOException($"file ended at {done} of {size} bytes");
        }
        await FinishAsync(ct).ConfigureAwait(false);
    }

    // Sends the TLS close_notify and the TCP end, and waits a short time for
    // the peer to close, so that the last bytes reach it.
    public async Task FinishAsync(CancellationToken ct)
    {
        try
        {
            await Stream.ShutdownAsync().ConfigureAwait(false);
            socket.Shutdown(SocketShutdown.Send);
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
            wait.CancelAfter(TimeSpan.FromSeconds(5));
            var drain = new byte[1024];
            while (await Stream.ReadAsync(drain, wait.Token).ConfigureAwait(false) > 0)
            {
            }
        }
        catch (Exception e) when (e is IOException or SocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            await Stream.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
        }
        socket.Dispose();
    }
}

// A listener that accepts 1 TLS connection. This side is the TLS server, and
// the peer must present the expected certificate.
//
// A connection that fails its handshake, or that shows another certificate,
// closes alone. The listener waits for the peer until the timeout, so a port
// scan or a stranger cannot take the transfer.
public sealed class PayloadServer : IDisposable
{
    // The TCP port range for payload servers and tunnels.
    public const int FirstPort = 12070;
    public const int LastPort = 12099;

    // How long a connection may take for its TLS handshake.
    static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);

    // The most connections in their handshake. A new one closes the oldest.
    const int MaxPending = 8;

    readonly TcpListener listener;
    readonly FluxTls tls;
    readonly byte[] expected;
    readonly CancellationTokenSource cts = new();
    readonly TaskCompletionSource<TlsStream> result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    readonly Lock gate = new();
    readonly List<Socket> pending = [];

    public int Port { get; }

    PayloadServer(TcpListener listener, FluxTls tls, byte[] expected)
    {
        this.listener = listener;
        this.tls = tls;
        this.expected = expected;
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoopAsync);
    }

    // Opens a listener on the first free port in the payload range.
    public static PayloadServer Open(FluxTls tls, byte[] expected, int first = FirstPort, int last = LastPort)
    {
        for (var port = first; port <= last; port++)
        {
            var listener = new TcpListener(IPAddress.Any, port);
            try
            {
                if (OperatingSystem.IsWindows())
                {
                    listener.ExclusiveAddressUse = true;
                }
                listener.Start();
                return new PayloadServer(listener, tls, expected);
            }
            catch (SocketException)
            {
                listener.Stop();
            }
        }
        throw new IOException($"no free payload port in {first}-{last}");
    }

    async Task AcceptLoopAsync()
    {
        while (!cts.IsCancellationRequested)
        {
            Socket socket;
            try
            {
                socket = await listener.AcceptSocketAsync(cts.Token).ConfigureAwait(false);
            }
            catch (SocketException)
            {
                continue;
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }
            Socket? evicted = null;
            lock (gate)
            {
                pending.Add(socket);
                if (pending.Count > MaxPending)
                {
                    evicted = pending[0];
                    pending.RemoveAt(0);
                }
            }
            evicted?.Dispose();
            _ = Task.Run(() => HandshakeAsync(socket));
        }
    }

    async Task HandshakeAsync(Socket socket)
    {
        SslStream? ssl = null;
        try
        {
            socket.NoDelay = true;
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cts.Token);
            deadline.CancelAfter(HandshakeTimeout);
            ssl = await tls.ServerAsync(new NetworkStream(socket, ownsSocket: false), deadline.Token).ConfigureAwait(false);
            var cert = FluxTls.PeerCertificate(ssl);
            if (cert is null || !cert.AsSpan().SequenceEqual(expected))
            {
                throw new InvalidDataException("another certificate");
            }
            lock (gate)
            {
                pending.Remove(socket);
            }
            if (result.TrySetResult(new TlsStream(ssl, socket, cert)))
            {
                Dispose();
                return;
            }
            throw new InvalidDataException("a second connection");
        }
        catch (Exception e) when (e is IOException or InvalidDataException or OperationCanceledException or
                                  ObjectDisposedException or SocketException or System.Security.Authentication.AuthenticationException)
        {
            FluxLog.Info("net", $"closed a payload connection: {e.Message}");
            lock (gate)
            {
                pending.Remove(socket);
            }
            if (ssl is not null)
            {
                await ssl.DisposeAsync().ConfigureAwait(false);
            }
            socket.Dispose();
        }
    }

    // Waits for the peer, then closes the listener and the other connections.
    public async Task<TlsStream> AcceptAsync(TimeSpan timeout, CancellationToken ct = default)
    {
        using var wait = CancellationTokenSource.CreateLinkedTokenSource(ct);
        wait.CancelAfter(timeout);
        try
        {
            return await result.Task.WaitAsync(wait.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("the computer did not connect in time");
        }
        finally
        {
            Dispose();
        }
    }

    public void Dispose()
    {
        Socket[] open;
        lock (gate)
        {
            open = pending.ToArray();
            pending.Clear();
        }
        cts.Cancel();
        listener.Stop();
        foreach (var s in open)
        {
            s.Dispose();
        }
        result.TrySetException(new OperationCanceledException("the payload listener is closed"));
    }
}

// Flux tunnels. The computer asks this device to listen, because a firewall
// on the computer can block incoming connections. This device opens a TLS
// listener and answers with flux.tunnel {id, port}, or {id, error}. The
// computer then connects.
public static class Tunnel
{
    // How long a tunnel listener waits for the computer.
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public static Packet Ready(string token, int port) =>
        new(PacketType.Tunnel, new JsonObject { ["id"] = token, ["port"] = port });

    public static Packet Failed(string token, string error) =>
        new(PacketType.Tunnel, new JsonObject { ["id"] = token, ["error"] = error });

    // Opens a listener for the token, sends flux.tunnel with its port through
    // announce, and waits for 1 connection from the computer with the
    // expected certificate.
    public static async Task<TlsStream> AcceptAsync(FluxTls tls, byte[] expected, string token, Action<Packet> announce,
                                                    TimeSpan? timeout = null, CancellationToken ct = default)
    {
        PayloadServer server;
        try
        {
            server = PayloadServer.Open(tls, expected);
        }
        catch (IOException e)
        {
            announce(Failed(token, e.Message));
            throw;
        }
        announce(Ready(token, server.Port));
        return await server.AcceptAsync(timeout ?? Timeout, ct).ConfigureAwait(false);
    }
}
