using System.Text;
using System.Text.Json.Nodes;
using FluxKit;

namespace FluxKit.Tests;

public class ProtocolTests
{
    static Packet Parse(string json) => Packet.Parse(json) ?? throw new InvalidDataException(json);

    [Fact]
    public void PacketRoundTripKeepsFields()
    {
        var p = new Packet(PacketType.Share, new JsonObject { ["filename"] = "a.txt", ["numberOfFiles"] = 2 })
        {
            Id = 42,
            PayloadSize = 10,
            PayloadPort = 12070,
        };
        var bytes = p.Serialize();
        Assert.Equal((byte)'\n', bytes[^1]);
        var back = Packet.Parse(bytes.AsSpan(0, bytes.Length - 1))!;
        Assert.Equal(PacketType.Share, back.Type);
        Assert.Equal(42, back.Id);
        Assert.Equal("a.txt", back.String("filename"));
        Assert.Equal(2, back.Int("numberOfFiles"));
        Assert.Equal(10, back.PayloadSize);
        Assert.Equal(12070, back.PayloadPort);
        Assert.Null(back.PayloadTunnel);
        Assert.True(back.HasPayload);
    }

    [Fact]
    public void PacketWritesTunnelOnlyWithoutPort()
    {
        var p = new Packet(PacketType.Share) { PayloadSize = 5, PayloadTunnel = "abc" };
        var back = Packet.Parse(p.Serialize())!;
        Assert.Equal("abc", back.PayloadTunnel);
        Assert.Equal(0, back.PayloadPort);
        var both = Parse("""{"id":1,"type":"t","body":{},"payloadSize":5,"payloadTransferInfo":{"port":12071,"tunnel":"x"}}""");
        Assert.Equal(12071, both.PayloadPort);
        Assert.Null(both.PayloadTunnel);
    }

    [Fact]
    public void PacketWithoutPayloadSizeWritesNoTransferInfo()
    {
        var text = Encoding.UTF8.GetString(new Packet(PacketType.Ping) { PayloadPort = 12070 }.Serialize());
        Assert.DoesNotContain("payloadTransferInfo", text);
    }

    [Fact]
    public void PacketReadsIdAsNumberOrString()
    {
        Assert.Equal(7, Parse("""{"id":"7","type":"t","body":{}}""").Id);
        Assert.Equal(8, Parse("""{"id":8,"type":"t"}""").Id);
        Assert.Empty(Parse("""{"id":8,"type":"t"}""").Body);
    }

    [Theory]
    [InlineData("""[]""")]
    [InlineData("""{"id":1}""")]
    [InlineData("""{"id":1,"type":""}""")]
    [InlineData("""{"id":1,"type":5}""")]
    [InlineData("""not json""")]
    public void PacketRejectsLinesThatAreNotPackets(string line) => Assert.Null(Packet.Parse(line));

    [Fact]
    public void PacketRejectsDeepNesting()
    {
        var deep = new string('[', 100) + new string(']', 100);
        Assert.Null(Packet.Parse($$$"""{"id":1,"type":"t","body":{"x":{{{deep}}}}}"""));
    }

    [Fact]
    public void JsonReadsWholeNumbersOnly()
    {
        var p = Parse("""{"id":1,"type":"t","body":{"a":3.0,"b":3.5,"c":"3","d":true,"e":["x",1,"y"]}}""");
        Assert.Equal(3, p.Int("a"));
        Assert.Null(p.Int("b"));
        Assert.Null(p.Int("c"));
        Assert.Equal(3.5, p.Double("b"));
        Assert.True(p.Bool("d"));
        Assert.Null(p.Bool("a"));
        Assert.Equal(["x", "y"], p.Strings("e"));
    }

    static Packet IdentityPacket(string id, string type = "desktop", string[]? incoming = null, int version = 8) =>
        new(PacketType.Identity, new JsonObject
        {
            ["deviceId"] = id,
            ["deviceName"] = "omarchy",
            ["deviceType"] = type,
            ["protocolVersion"] = version,
            ["incomingCapabilities"] = new JsonArray((incoming ?? [PacketType.Tunnel]).Select(c => (JsonNode)c).ToArray()),
            ["outgoingCapabilities"] = new JsonArray(),
            ["tcpPort"] = 12100,
            ["app"] = "fluxd",
            ["appVersion"] = "0.17.0",
        });

    [Fact]
    public void IdentityParsesFluxd()
    {
        var id = Identity.From(IdentityPacket(new string('a', 32)))!;
        Assert.Equal("omarchy", id.DeviceName);
        Assert.Equal(12100, id.TcpPort);
        Assert.Equal("fluxd", id.App);
        Assert.True(id.IsFlux);
    }

    [Theory]
    [InlineData("short")]
    [InlineData("0123456789abcdef0123456789abcdef0123456")]
    [InlineData("0123456789abcdef0123456789abcde!")]
    public void IdentityRejectsBadIds(string id) => Assert.Null(Identity.From(IdentityPacket(id)));

    [Fact]
    public void IdentityNeedsAVersion()
    {
        var p = IdentityPacket(new string('a', 32));
        p.Body.Remove("protocolVersion");
        Assert.Null(Identity.From(p));
    }

    [Fact]
    public void OnlyComputersWithTunnelsAreFlux()
    {
        Assert.False(Identity.From(IdentityPacket(new string('a', 32), type: "phone"))!.IsFlux);
        Assert.False(Identity.From(IdentityPacket(new string('a', 32), incoming: [PacketType.Ping]))!.IsFlux);
        Assert.True(Identity.From(IdentityPacket(new string('a', 32), type: "laptop"))!.IsFlux);
    }

    [Fact]
    public void CleanNameRemovesBadCharacters()
    {
        Assert.Equal("My PC", Identity.CleanName("My. PC!"));
        Assert.Equal("ab", Identity.CleanName("a‮b\u0007"));
        Assert.Equal("Windows", Identity.CleanName("..."));
        Assert.Equal(32, Identity.CleanName(new string('x', 40)).Length);
        // A name of emoji keeps whole characters.
        Assert.Equal(32, Identity.CleanName(string.Concat(Enumerable.Repeat("😀", 40))).EnumerateRunes().Count());
    }

    [Fact]
    public void IdentityPacketCarriesPortAndTarget()
    {
        var own = new Identity(new string('b', 32), "pc", "laptop", 8, [PacketType.Ping], [PacketType.Tunnel], 12101, "windows", "1.0");
        var target = Identity.From(IdentityPacket(new string('a', 32)))!;
        var p = own.ToPacket(withPort: true, target: target);
        Assert.Equal(12101, p.Int("tcpPort"));
        Assert.Equal(target.DeviceId, p.String("targetDeviceId"));
        Assert.Equal(8, p.Int("targetProtocolVersion"));
        Assert.Equal("windows", p.String("app"));
        Assert.Null(own.ToPacket().Int("tcpPort"));
        Assert.Equal(own, Identity.From(own.ToPacket(withPort: true)));
    }

    [Fact]
    public void VerificationKeyMatchesTheGoVector()
    {
        byte[] a = [0x30, 0x82, 0x01, 0x22, 0x80];
        byte[] b = [0x30, 0x82, 0x01, 0x22, 0x7f];
        Assert.Equal("5EE6825F974ED59A", Certificates.VerificationKeyOfKeys(a, b, 1790000000));
        Assert.Equal("5EE6825F974ED59A", Certificates.VerificationKeyOfKeys(b, a, 1790000000));
        Assert.Equal("5BB22DB11047F34B", Certificates.VerificationKeyOfKeys(a, b, 0));
        Assert.Equal("5EE6 825F 974E D59A", Certificates.FormatKey("5EE6825F974ED59A"));
    }

    [Fact]
    public void CertificateKeepsItsIdAcrossLoads()
    {
        var dir = Path.Combine(Path.GetTempPath(), "flux-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var first = LocalCertificate.LoadOrCreate(dir, new PlainKeyProtector());
            using var second = LocalCertificate.LoadOrCreate(dir, new PlainKeyProtector());
            Assert.True(Identity.ValidDeviceId(first.DeviceId));
            Assert.Equal(first.DeviceId, second.DeviceId);
            Assert.Equal(first.CertificateDer, second.CertificateDer);
            Assert.Equal(first.DeviceId, Certificates.CommonName(first.CertificateDer));
            using var other = LocalCertificate.LoadOrCreate(dir + "-b", new PlainKeyProtector());
            var key = Certificates.VerificationKey(first.CertificateDer, other.CertificateDer, 1727260000);
            Assert.Equal(16, key.Length);
            Assert.Equal(key, Certificates.VerificationKey(other.CertificateDer, first.CertificateDer, 1727260000));
            Assert.NotEqual(key, Certificates.VerificationKey(first.CertificateDer, other.CertificateDer, 1727260001));
        }
        finally
        {
            Directory.Delete(dir, true);
            if (Directory.Exists(dir + "-b"))
            {
                Directory.Delete(dir + "-b", true);
            }
        }
    }

    [Fact]
    public void SubjectPublicKeyInfoIsTheRawField()
    {
        var dir = Path.Combine(Path.GetTempPath(), "flux-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var c = LocalCertificate.LoadOrCreate(dir, new PlainKeyProtector());
            Assert.Equal(c.Certificate.PublicKey.ExportSubjectPublicKeyInfo(), Certificates.SubjectPublicKeyInfo(c.CertificateDer));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
