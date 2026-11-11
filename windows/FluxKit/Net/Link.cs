using System.Net.Security;
using System.Net.Sockets;
using System.Threading.Channels;

namespace FluxKit;

// An open TLS link to one computer.
public sealed class Link
{
    // The largest packet line that Flux reads from a paired computer.
    public const int MaxLine = 16 * 1024 * 1024;

    // The largest packet line that Flux reads from a computer that is not
    // paired, also the identity inside TLS. Only pair packets are useful
    // before a pairing, so a stranger cannot make this device hold 16 MiB.
    public const int MaxUnpairedLine = 64 * 1024;

    readonly Socket socket;
    readonly SslStream ssl;
    readonly LineReader reader;
    readonly CancellationTokenSource cts = new();
    readonly Channel<(byte[] Data, TaskCompletionSource<bool>? Done)> writes =
        Channel.CreateUnbounded<(byte[], TaskCompletionSource<bool>?)>(new UnboundedChannelOptions { SingleReader = true });
    readonly Lock gate = new();
    bool started;
    bool closed;
    Action<Packet>? onPacket;
    Action? onClose;

    public Identity Identity { get; }
    public byte[] PeerCertificate { get; }

    // The IP address of the peer.
    public string Address { get; }

    // True when this device opened the connection.
    public bool Outgoing { get; }

    // When the link finished its handshake.
    public DateTime Started { get; } = DateTime.UtcNow;

    internal Link(Socket socket, SslStream ssl, LineReader reader, Identity identity, byte[] peerCertificate, bool outgoing)
    {
        this.socket = socket;
        this.ssl = ssl;
        this.reader = reader;
        Identity = identity;
        PeerCertificate = peerCertificate;
        Outgoing = outgoing;
        Address = (socket.RemoteEndPoint as System.Net.IPEndPoint)?.Address.MapToIPv4().ToString() ?? "";
    }

    // Sets the line limit for a paired or an unpaired computer.
    public void SetPaired(bool paired) => reader.Limit = paired ? MaxLine : MaxUnpairedLine;

    // Starts to read packets and to write the queue. Both callbacks run on a
    // thread of the pool. onClose runs once, after the last packet.
    public void Start(Action<Packet> onPacket, Action onClose)
    {
        lock (gate)
        {
            if (started)
            {
                return;
            }
            started = true;
            if (closed)
            {
                onClose();
                return;
            }
            this.onPacket = onPacket;
            this.onClose = onClose;
        }
        _ = Task.Run(ReadLoopAsync);
        _ = Task.Run(WriteLoopAsync);
    }

    async Task ReadLoopAsync()
    {
        try
        {
            while (!cts.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cts.Token).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }
                if (line.Length == 0 || Packet.Parse(line) is not { } p)
                {
                    continue;
                }
                Action<Packet>? callback;
                lock (gate)
                {
                    callback = onPacket;
                }
                callback?.Invoke(p);
            }
        }
        catch (Exception e) when (e is IOException or InvalidDataException or OperationCanceledException or ObjectDisposedException or SocketException)
        {
            if (!cts.IsCancellationRequested)
            {
                FluxLog.Info("net", $"link to {Identity.DeviceName} ended: {e.Message}");
            }
        }
        Close();
    }

    async Task WriteLoopAsync()
    {
        try
        {
            await foreach (var (data, done) in writes.Reader.ReadAllAsync(cts.Token).ConfigureAwait(false))
            {
                await ssl.WriteAsync(data, cts.Token).ConfigureAwait(false);
                await ssl.FlushAsync(cts.Token).ConfigureAwait(false);
                done?.TrySetResult(true);
            }
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException or SocketException)
        {
        }
        while (writes.Reader.TryRead(out var rest))
        {
            rest.Done?.TrySetResult(false);
        }
        Close();
    }

    // Sends a packet. Writes keep their order.
    public void Send(Packet p)
    {
        if (IsOpen)
        {
            writes.Writer.TryWrite((p.Serialize(), null));
        }
    }

    // Sends a packet and waits until the link wrote it. It returns false when
    // the link is closed or the write fails.
    public async Task<bool> SendFlushedAsync(Packet p)
    {
        if (!IsOpen)
        {
            return false;
        }
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!writes.Writer.TryWrite((p.Serialize(), done)))
        {
            return false;
        }
        return await done.Task.ConfigureAwait(false);
    }

    public bool IsOpen
    {
        get
        {
            lock (gate)
            {
                return !closed;
            }
        }
    }

    // The time in which a second link to the same computer counts as a
    // simultaneous connection and not as a reconnect.
    internal static readonly TimeSpan RaceWindow = TimeSpan.FromSeconds(5);

    // Decides between 2 links to the same computer, as lan.Preferred in fluxd.
    // When both devices connect to each other at the same time, each side can
    // keep a different socket and close the other one, so both links die. In
    // the race window, both sides keep the link that the device with the
    // larger ID opened. After the window, the new link always wins, because
    // the old socket can be dead without an error.
    internal static bool KeepsOld(bool oldOutgoing, bool nextOutgoing, TimeSpan oldAge, string selfId, string peerId)
    {
        if (oldAge > RaceWindow)
        {
            return false;
        }
        var larger = string.CompareOrdinal(selfId, peerId) > 0 ? selfId : peerId;
        return (oldOutgoing ? selfId : peerId) == larger && (nextOutgoing ? selfId : peerId) != larger;
    }

    public void Close()
    {
        Action? callback;
        lock (gate)
        {
            if (closed)
            {
                return;
            }
            closed = true;
            callback = onClose;
            // The callbacks can hold the link, so a closed link drops them.
            onPacket = null;
            onClose = null;
        }
        writes.Writer.TryComplete();
        cts.Cancel();
        try
        {
            ssl.Dispose();
            socket.Dispose();
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
        {
        }
        callback?.Invoke();
    }
}
