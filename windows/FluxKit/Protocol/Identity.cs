using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace FluxKit;

// The body of a flux.identity packet.
public sealed record Identity(
    string DeviceId,
    string DeviceName,
    string DeviceType,
    int ProtocolVersion,
    IReadOnlyList<string> Incoming,
    IReadOnlyList<string> Outgoing,
    int TcpPort = 0,
    string App = "",
    string AppVersion = "")
{
    // The protocol version that Flux speaks.
    public const int Version = 8;

    // The largest identity line that Flux sends or reads.
    public const int MaxLine = 8192;

    // Returns the identity packet. Only the UDP broadcast carries tcpPort.
    // The plain-text line on a new TCP connection also names the device that
    // it answers with target.
    public Packet ToPacket(bool withPort = false, Identity? target = null)
    {
        var body = new JsonObject
        {
            ["deviceId"] = DeviceId,
            ["deviceName"] = DeviceName,
            ["deviceType"] = DeviceType,
            ["protocolVersion"] = ProtocolVersion,
            ["incomingCapabilities"] = new JsonArray(Incoming.Select(c => (JsonNode)c).ToArray()),
            ["outgoingCapabilities"] = new JsonArray(Outgoing.Select(c => (JsonNode)c).ToArray()),
        };
        if (withPort && TcpPort > 0)
        {
            body["tcpPort"] = TcpPort;
        }
        if (App.Length > 0)
        {
            body["app"] = App;
        }
        if (AppVersion.Length > 0)
        {
            body["appVersion"] = AppVersion;
        }
        if (target is not null)
        {
            body["targetDeviceId"] = target.DeviceId;
            body["targetProtocolVersion"] = target.ProtocolVersion;
        }
        return new Packet(PacketType.Identity, body);
    }

    // True when the peer is an Omarchy computer that runs fluxd. fluxd is a
    // desktop or laptop that accepts flux.tunnel. Flux for Android also
    // accepts flux.tunnel, but it is a phone or tablet. Flux for Windows is a
    // remote for Omarchy, so it connects and pairs only with these peers.
    public bool IsFlux => DeviceType is "desktop" or "laptop" && Incoming.Contains(PacketType.Tunnel);

    // True when the peer accepts packets of the type.
    public bool Accepts(string type) => Incoming.Contains(type);

    // Parses an identity packet. It returns null for a packet without a
    // valid device ID or without a protocol version.
    public static Identity? From(Packet p)
    {
        if (p.Type != PacketType.Identity || p.String("deviceId") is not { } id || !ValidDeviceId(id) ||
            p.Int("protocolVersion") is not { } version)
        {
            return null;
        }
        return new Identity(
            id,
            CleanName(p.String("deviceName") ?? "unnamed", "Computer"),
            p.String("deviceType") ?? "desktop",
            version,
            p.Strings("incomingCapabilities"),
            p.Strings("outgoingCapabilities"),
            p.Int("tcpPort") ?? 0,
            p.String("app") ?? "",
            p.String("appVersion") ?? "");
    }

    // Reports whether the ID has the Flux device ID format.
    public static bool ValidDeviceId(string id) =>
        id.Length is >= 32 and <= 38 && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');

    // Removes the characters that Flux does not allow in a device name, and
    // control and direction characters, and limits the name to 32 characters.
    public static string CleanName(string name, string fallback = "Windows")
    {
        var kept = new StringBuilder();
        foreach (var r in name.EnumerateRunes())
        {
            if (r.IsBmp && "\"',;:.!?()[]<>".Contains((char)r.Value))
            {
                continue;
            }
            var category = Rune.GetUnicodeCategory(r);
            if (category is UnicodeCategory.Control or UnicodeCategory.Format)
            {
                continue;
            }
            kept.Append(r.ToString());
        }
        var limited = new StringBuilder();
        foreach (var r in kept.ToString().Trim().EnumerateRunes().Take(32))
        {
            limited.Append(r.ToString());
        }
        var result = limited.ToString().Trim();
        return result.Length == 0 ? fallback : result;
    }

    // Records compare lists by reference, so compare the capabilities by value.
    public bool Equals(Identity? other) =>
        other is not null && DeviceId == other.DeviceId && DeviceName == other.DeviceName && DeviceType == other.DeviceType &&
        ProtocolVersion == other.ProtocolVersion && Incoming.SequenceEqual(other.Incoming) &&
        Outgoing.SequenceEqual(other.Outgoing) && TcpPort == other.TcpPort && App == other.App && AppVersion == other.AppVersion;

    public override int GetHashCode() => HashCode.Combine(DeviceId, DeviceName, ProtocolVersion, TcpPort);
}
