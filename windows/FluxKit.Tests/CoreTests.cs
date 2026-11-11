using System.Text;
using FluxKit;

namespace FluxKit.Tests;

public class CoreTests
{
    [Fact]
    public async Task LineReaderSplitsLinesAcrossReads()
    {
        var reader = new LineReader(new ChunkedStream(Encoding.UTF8.GetBytes("one\ntwo\n\nthree"), 2), 100);
        Assert.Equal("one", Encoding.UTF8.GetString((await reader.ReadLineAsync(default))!));
        Assert.Equal("two", Encoding.UTF8.GetString((await reader.ReadLineAsync(default))!));
        Assert.Empty((await reader.ReadLineAsync(default))!);
        // A line without a newline at the end of the stream does not count.
        Assert.Null(await reader.ReadLineAsync(default));
    }

    [Fact]
    public async Task LineReaderEnforcesAndRaisesTheLimit()
    {
        var data = Encoding.UTF8.GetBytes(new string('a', 50) + "\n" + new string('b', 200) + "\n");
        var reader = new LineReader(new MemoryStream(data), 64);
        Assert.Equal(50, (await reader.ReadLineAsync(default))!.Length);
        reader.Limit = 300;
        Assert.Equal(200, (await reader.ReadLineAsync(default))!.Length);
        var strict = new LineReader(new MemoryStream(data), 10);
        await Assert.ThrowsAsync<InvalidDataException>(async () => await strict.ReadLineAsync(default));
    }

    [Fact]
    public async Task LineReaderGrowsForLongLines()
    {
        var line = new string('x', 100_000);
        var reader = new LineReader(new ChunkedStream(Encoding.UTF8.GetBytes(line + "\nend\n"), 7000), 200_000);
        Assert.Equal(line, Encoding.UTF8.GetString((await reader.ReadLineAsync(default))!));
        Assert.Equal("end", Encoding.UTF8.GetString((await reader.ReadLineAsync(default))!));
    }

    [Fact]
    public async Task ExactLineReadLeavesTheRest()
    {
        var stream = new MemoryStream(Encoding.UTF8.GetBytes("id\nTLS"));
        Assert.Equal("id", Encoding.UTF8.GetString(await LineReader.ReadExactLineAsync(stream, 100, default)));
        Assert.Equal(3, stream.Length - stream.Position);
    }

    [Fact]
    public void TrustStoreDropsBadEntriesAndKeepsACopy()
    {
        var dir = TempDir();
        var path = Path.Combine(dir, "trusted.json");
        var good = Convert.ToBase64String([1, 2, 3]);
        File.WriteAllText(path, $$"""[{"id":"a","name":"A","type":"desktop","certificate":"{{good}}","isFlux":true},{"id":"b","name":"B","type":"desktop","certificate":"%%%"},{"oops":1}]""");
        var store = new TrustStore(path);
        Assert.Single(store.All());
        Assert.Equal([1, 2, 3], store.Get("a")!.CertificateDer);
        Assert.True(File.Exists(path + ".bad"));
        store.Remove("a");
        Assert.Empty(new TrustStore(path).All());
    }

    [Fact]
    public void TrustStoreSavesAndUpdates()
    {
        var path = Path.Combine(TempDir(), "trusted.json");
        var store = new TrustStore(path);
        Assert.True(store.Put(new TrustedDevice("a", "A", "laptop", Convert.ToBase64String([9]), "10.0.0.2", true)));
        store.Update("a", t => t with { LastIp = "10.0.0.3" });
        var again = new TrustStore(path).Get("a")!;
        Assert.Equal("10.0.0.3", again.LastIp);
        Assert.True(again.IsFlux);
    }

    [Fact]
    public void LinkRefusalChecksPinsAndOpenPairings()
    {
        byte[] a = [1];
        byte[] b = [2];
        Assert.Null(FluxCore.LinkRefusal(a, null, PairState.None, null, null));
        Assert.NotNull(FluxCore.LinkRefusal(a, b, PairState.Paired, null, null));
        Assert.Null(FluxCore.LinkRefusal(a, a, PairState.Paired, null, null));
        Assert.NotNull(FluxCore.LinkRefusal(a, null, PairState.None, null, b));
        Assert.NotNull(FluxCore.LinkRefusal(a, null, PairState.Incoming, b, null));
        Assert.Null(FluxCore.LinkRefusal(a, null, PairState.Requested, a, null));
        // An entry whose certificate does not read matches no certificate.
        Assert.NotNull(FluxCore.LinkRefusal(a, [], PairState.Paired, null, null));
    }

    [Fact]
    public void PairCooldownBlocksByIdAndAddress()
    {
        var c = new PairCooldown();
        c.Add("dev", "10.0.0.2", TimeSpan.FromSeconds(100));
        Assert.True(c.Blocks("dev", "", TimeSpan.FromSeconds(110)));
        Assert.True(c.Blocks("other", "10.0.0.2", TimeSpan.FromSeconds(110)));
        Assert.False(c.Blocks("other", "10.0.0.3", TimeSpan.FromSeconds(110)));
        Assert.False(c.Blocks("dev", "10.0.0.2", TimeSpan.FromSeconds(131)));
    }

    [Fact]
    public void PairingRulesMatchTheMac()
    {
        Assert.True(Device.TimestampFresh(1000, 2800));
        Assert.False(Device.TimestampFresh(1000, 2801));
        Assert.True(Device.KeyHolds([1], [1], [1]));
        Assert.False(Device.KeyHolds([1], [1], [2]));
        Assert.False(Device.KeyHolds(null, [1], [1]));
    }

    [Fact]
    public void CrossedDialsKeepTheLinkOfTheLargerId()
    {
        var age = TimeSpan.FromSeconds(1);
        // This device has the larger ID: keep the old link when this device opened it.
        Assert.True(Link.KeepsOld(true, false, age, "b", "a"));
        Assert.False(Link.KeepsOld(false, true, age, "b", "a"));
        // The peer has the larger ID: keep the old link when the peer opened it.
        Assert.True(Link.KeepsOld(false, true, age, "a", "b"));
        Assert.False(Link.KeepsOld(true, false, age, "a", "b"));
        // After the race window, the new link wins.
        Assert.False(Link.KeepsOld(true, false, TimeSpan.FromSeconds(6), "b", "a"));
    }

    [Fact]
    public void BroadcastTargetsAreUnique()
    {
        Assert.Equal(["127.0.0.1"], LanBackend.BroadcastTargets(true, ["10.0.0.255"], ["10.0.0.2"]));
        Assert.Equal(["255.255.255.255", "10.0.0.255", "10.0.0.2"],
            LanBackend.BroadcastTargets(false, ["10.0.0.255"], ["10.0.0.2", "10.0.0.255", ""]));
    }

    [Fact]
    public void DialsGoOnlyToFluxdPorts()
    {
        var lan = new LanConfig();
        Assert.True(LanBackend.DialAllowed("10.0.0.2", 12100, lan));
        Assert.False(LanBackend.DialAllowed("10.0.0.2", 22, lan));
        var loopback = lan with { LoopbackOnly = true };
        Assert.True(LanBackend.DialAllowed("127.0.0.1", 28720, loopback));
        Assert.False(LanBackend.DialAllowed("10.0.0.2", 12100, loopback));
    }

    [Fact]
    public void LanConfigReadsTheEnvironment()
    {
        var env = new Dictionary<string, string> { ["FLUX_UDP_PORT"] = "28731", ["FLUX_PEER_UDP_PORT"] = "28716", ["FLUX_LOOPBACK"] = "1" };
        var c = LanConfig.FromEnvironment(k => env.GetValueOrDefault(k));
        Assert.Equal(28731, c.UdpPort);
        Assert.Equal(28716, c.PeerUdpPort);
        Assert.True(c.LoopbackOnly);
    }

    [Fact]
    public void LanAddressesKnowPrivateRanges()
    {
        Assert.True(LanAddresses.IsLocal(System.Net.IPAddress.Parse("192.168.1.4")));
        Assert.True(LanAddresses.IsLocal(System.Net.IPAddress.Parse("172.20.0.1")));
        Assert.False(LanAddresses.IsLocal(System.Net.IPAddress.Parse("8.8.8.8")));
        Assert.True(LanAddresses.IsTailscale(System.Net.IPAddress.Parse("100.100.1.1")));
    }

    [Fact]
    public void SettingsPersist()
    {
        var path = Path.Combine(TempDir(), "settings.json");
        var s = new FluxSettings(path);
        Assert.True(s.Bool("enabled", true));
        s.Set("enabled", false);
        Assert.False(new FluxSettings(path).Bool("enabled", true));
        File.WriteAllText(path, "{broken");
        Assert.True(new FluxSettings(path).Bool("enabled", true));
        Assert.True(File.Exists(path + ".bad"));
    }

    [Fact]
    public void ValueListComparesItems()
    {
        Assert.Equal(ValueList.From(["a", "b"]), ValueList.From(new List<string> { "a", "b" }));
        Assert.NotEqual(ValueList.From(["a"]), ValueList.From(["b"]));
        var s1 = new CoreState { Devices = ValueList.From([Snapshot("a")]) };
        var s2 = new CoreState { Devices = ValueList.From([Snapshot("a")]) };
        Assert.Equal(s1, s2);
    }

    static DeviceSnapshot Snapshot(string id) =>
        new(id, "n", "desktop", "", true, true, true, PairState.Paired, "", ValueList.From(["flux.ping"]), ValueList<string>.Empty);

    internal static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "flux-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // Returns at most chunk bytes for each read.
    sealed class ChunkedStream(byte[] data, int chunk) : MemoryStream(data)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, chunk)], ct);
    }
}
