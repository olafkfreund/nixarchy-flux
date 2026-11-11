using System.Text.Json.Nodes;

namespace FluxKit;

// The pairing state of one computer.
public enum PairState
{
    None,
    Requested,
    Incoming,
    Paired,
}

// The computers and addresses whose incoming pairing request ended without a
// pairing, for example after a reject or a timeout. Their next requests end
// at once for the cooldown, so that a host on the network cannot hold the
// only open request or show a new request each time.
internal sealed class PairCooldown
{
    public TimeSpan Seconds { get; init; } = Device.PairCooldownTime;

    // The time of each end, by device ID and by address.
    readonly Dictionary<string, TimeSpan> ended = [];

    public void Add(string id, string ip, TimeSpan now)
    {
        foreach (var key in ended.Where(e => now - e.Value >= Seconds).Select(e => e.Key).ToArray())
        {
            ended.Remove(key);
        }
        foreach (var key in Keys(id, ip))
        {
            ended[key] = now;
        }
    }

    public bool Blocks(string id, string ip, TimeSpan now) =>
        Keys(id, ip).Any(key => ended.TryGetValue(key, out var at) && now - at < Seconds);

    static string[] Keys(string id, string ip) => ip.Length == 0 ? ["id " + id] : ["id " + id, "ip " + ip];
}

// One remote computer. The core holds one object per device ID. The core lock
// guards all fields.
public sealed class Device
{
    // How long a pairing request from this device waits for an answer.
    public static readonly TimeSpan OutgoingPairTimeout = TimeSpan.FromSeconds(30);

    // How long an incoming pairing request stays open.
    public static readonly TimeSpan IncomingPairTimeout = TimeSpan.FromSeconds(25);

    // The largest clock difference that a pairing request may have, in seconds.
    public const long MaxTimestampDifference = 1800;

    // How long a host waits before its next pairing request counts, after its
    // incoming request ended without a pairing.
    public static readonly TimeSpan PairCooldownTime = TimeSpan.FromSeconds(30);

    readonly FluxCore core;
    Link? link;
    PairState pairState = PairState.None;
    CancellationTokenSource? pairTimer;
    long pairTimestamp;

    internal Device(FluxCore core, Identity identity)
    {
        this.core = core;
        Identity = identity;
    }

    public Identity Identity { get; internal set; }
    public byte[]? Certificate { get; internal set; }
    public string LastIp { get; internal set; } = "";
    public string PairKey { get; private set; } = "";

    // The certificate that PairKey comes from. A pairing pins only this
    // certificate, the one behind the key that the user compared.
    internal byte[]? PairCertificate { get; private set; }

    public Link? Link
    {
        get => link;
        internal set
        {
            link = value;
            link?.SetPaired(Paired);
        }
    }

    public PairState PairState
    {
        get => pairState;
        internal set
        {
            var old = pairState;
            pairState = value;
            link?.SetPaired(Paired);
            // An incoming request that ends without a pairing makes its host
            // wait, see PairCooldown.
            if (old == PairState.Incoming && value == PairState.None)
            {
                core.IncomingPairEnded(this);
            }
        }
    }

    public string Id => Identity.DeviceId;
    public string Name => Identity.DeviceName;
    public bool Online => link?.IsOpen == true;
    public bool Paired => pairState == PairState.Paired;

    // Sends a packet. It returns false when the computer is not paired or has
    // no open link. A peer that gets a plugin packet before pairing answers
    // with an unpair, so only pair packets go to an unpaired computer.
    public bool Send(Packet p)
    {
        if (!Paired && p.Type != PacketType.Pair)
        {
            return false;
        }
        if (link is not { IsOpen: true } l)
        {
            return false;
        }
        l.Send(p);
        return true;
    }

    // True when the computer accepts packets of the type.
    public bool Accepts(string type) => Identity.Accepts(type);

    internal DeviceSnapshot Snapshot() => new(
        Id,
        Identity.DeviceName,
        Identity.DeviceType,
        link?.Address ?? LastIp,
        Identity.IsFlux,
        Paired,
        Online,
        pairState,
        PairKey,
        ValueList.From(Identity.Incoming),
        ValueList.From(Identity.Outgoing),
        Identity.App,
        Identity.AppVersion);

    // Pairing

    // Returns the key that a request with the timestamp shows, before it is sent.
    internal string PreviewKey(long timestamp) =>
        Certificate is { } peer ? Certificates.VerificationKey(core.Local.CertificateDer, peer, timestamp) : "";

    // Sends a pairing request with the timestamp that the dialog showed.
    internal void RequestPair(long timestamp)
    {
        if (!Online || Paired)
        {
            return;
        }
        pairTimestamp = timestamp;
        PairState = PairState.Requested;
        ComputeKey();
        Send(new Packet(PacketType.Pair, new JsonObject { ["pair"] = true, ["timestamp"] = pairTimestamp }));
        ArmTimer(OutgoingPairTimeout);
    }

    // The user accepted an incoming request.
    internal void AcceptPair()
    {
        if (pairState != PairState.Incoming)
        {
            return;
        }
        // PairingDone refuses a changed certificate and answers pair false.
        if (KeyCertificateHolds)
        {
            Send(new Packet(PacketType.Pair, new JsonObject { ["pair"] = true }));
        }
        PairingDone();
    }

    // True when the link still shows the certificate behind the key.
    bool KeyCertificateHolds => KeyHolds(PairCertificate, Certificate, link?.PeerCertificate);

    // Reports whether the computer and its link still show the certificate
    // behind the key. Without a key or a link it is false.
    internal static bool KeyHolds(byte[]? pairCertificate, byte[]? certificate, byte[]? linkCertificate) =>
        pairCertificate is not null && certificate is not null && linkCertificate is not null &&
        pairCertificate.AsSpan().SequenceEqual(certificate) && pairCertificate.AsSpan().SequenceEqual(linkCertificate);

    // The user canceled a request or rejected an incoming request.
    internal void CancelPair()
    {
        if (pairState is not (PairState.Requested or PairState.Incoming))
        {
            return;
        }
        Send(new Packet(PacketType.Pair, new JsonObject { ["pair"] = false }));
        ResetPair();
    }

    internal void Unpair()
    {
        Send(new Packet(PacketType.Pair, new JsonObject { ["pair"] = false }));
        core.Trust.Remove(Id);
        ResetPair();
    }

    // The message after a computer ends the pairing.
    internal static string UnpairedText(string computer) => $"{computer} unpaired this computer";

    // The message after a new link ends a pairing. After an incoming request,
    // the computer waits for the cooldown before its next request counts, so
    // the message names the path that works.
    internal static string PairStoppedText(string computer, bool incoming) => incoming
        ? $"The pairing with {computer} stopped. Pair again from this computer, or wait {(int)PairCooldownTime.TotalSeconds} seconds"
        : $"The pairing with {computer} stopped. Try again";

    // Handles a flux.pair packet.
    internal void OnPairPacket(Packet p)
    {
        if (p.Bool("pair") != true)
        {
            var wasPaired = Paired;
            if (wasPaired)
            {
                core.Trust.Remove(Id);
            }
            if (pairState == PairState.Requested)
            {
                core.Toast($"{Name} rejected the pairing");
            }
            else if (wasPaired)
            {
                core.Toast(UnpairedText(Name));
            }
            ResetPair();
            if (wasPaired)
            {
                core.DidUnpair(this);
            }
            return;
        }
        switch (pairState)
        {
            case PairState.Requested:
                PairingDone();
                break;
            case PairState.Incoming:
                break;
            case PairState.Paired:
                // The computer lost the pairing, for example after a
                // reinstall. Forget the old trust, end the sessions of the
                // pairing, and show the request again.
                core.Trust.Remove(Id);
                PairState = PairState.None;
                core.DidUnpair(this);
                Incoming(p);
                break;
            case PairState.None:
                Incoming(p);
                break;
        }
    }

    // Reports whether a pairing timestamp is within the allowed clock difference.
    internal static bool TimestampFresh(long ts, long now) =>
        ts >= now - MaxTimestampDifference && ts <= now + MaxTimestampDifference;

    void Incoming(Packet p)
    {
        // A host whose last request ended without a pairing waits. The
        // refusal shows no notification.
        if (core.PairCooldownBlocks(this))
        {
            FluxLog.Info("core", $"refused a pairing request from {Name}: a request from this computer or address ended less than {(int)PairCooldownTime.TotalSeconds} seconds ago");
            Send(new Packet(PacketType.Pair, new JsonObject { ["pair"] = false }));
            return;
        }
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (p.Long("timestamp") is not { } ts || !TimestampFresh(ts, now))
        {
            Send(new Packet(PacketType.Pair, new JsonObject { ["pair"] = false }));
            core.Toast(p.Long("timestamp") is null
                ? $"Pairing refused: {Name} sent no timestamp"
                : $"Pairing refused: the clock of {Name} is wrong");
            return;
        }
        // 1 request at a time, so that other computers cannot bury it.
        if (core.HasIncomingPair(except: Id))
        {
            FluxLog.Info("core", $"refused a pairing request from {Name}: another request is open");
            Send(new Packet(PacketType.Pair, new JsonObject { ["pair"] = false }));
            return;
        }
        pairTimestamp = ts;
        ComputeKey();
        PairState = PairState.Incoming;
        ArmTimer(core.IncomingPairTime);
        core.NotifyPairRequest(this);
    }

    // Pins the certificate behind the key. A link that changed its
    // certificate after the key was shown ends the pairing instead.
    void PairingDone()
    {
        pairTimer?.Cancel();
        if (!KeyCertificateHolds || PairCertificate is not { } cert)
        {
            FluxLog.Error("core", $"pairing with {Name} refused: the certificate changed after the key was shown");
            Send(new Packet(PacketType.Pair, new JsonObject { ["pair"] = false }));
            core.Toast($"Pairing with {Name} failed. Try again");
            ResetPair();
            return;
        }
        PairState = PairState.Paired;
        var saved = core.Trust.Put(new TrustedDevice(
            Id,
            Identity.DeviceName,
            Identity.DeviceType,
            Convert.ToBase64String(cert),
            link?.Address ?? "",
            Identity.IsFlux));
        core.Toast(saved
            ? $"Paired with {Name}"
            : $"Paired with {Name}, but Flux cannot save the pairing. Pair again after Flux restarts");
        core.OnPaired(this);
    }

    // Ends an open pairing on this side and sends nothing.
    internal void ResetPair()
    {
        pairTimer?.Cancel();
        pairTimer = null;
        PairState = PairState.None;
        PairKey = "";
        PairCertificate = null;
    }

    void ArmTimer(TimeSpan delay)
    {
        pairTimer?.Cancel();
        var timer = new CancellationTokenSource();
        pairTimer = timer;
        _ = Task.Delay(delay, timer.Token).ContinueWith(t =>
        {
            if (t.IsCanceled)
            {
                return;
            }
            core.Locked(() =>
            {
                if (timer.IsCancellationRequested)
                {
                    return;
                }
                if (pairState == PairState.Requested)
                {
                    Send(new Packet(PacketType.Pair, new JsonObject { ["pair"] = false }));
                    core.Toast($"Pairing with {Name} timed out");
                }
                if (pairState is PairState.Requested or PairState.Incoming)
                {
                    ResetPair();
                }
            });
        }, TaskScheduler.Default);
    }

    // Sets the key for the current certificate and remembers that certificate.
    void ComputeKey()
    {
        PairCertificate = Certificate;
        PairKey = Certificate is { } peer ? Certificates.VerificationKey(core.Local.CertificateDer, peer, pairTimestamp) : "";
    }
}

// A snapshot of one computer for the UI.
public sealed record DeviceSnapshot(
    string Id,
    string Name,
    string Type,
    string Ip,
    bool IsFlux,
    bool Paired,
    bool Online,
    PairState PairState,
    string PairKey,
    ValueList<string> Incoming,
    ValueList<string> Outgoing,
    string App = "",
    string AppVersion = "")
{
    // True when the computer accepts packets of the type.
    public bool Accepts(string type) => Incoming.Contains(type);
}
