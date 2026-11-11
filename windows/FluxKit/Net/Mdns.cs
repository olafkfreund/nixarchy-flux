using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using Makaretu.Dns;

namespace FluxKit;

// mDNS for the _flux._udp service. This device publishes its link port, and
// browses for computers. For each computer that it finds, it sends its UDP
// identity, and the computer then connects. Bonjour.swift of the Mac app has
// the same job.
public sealed class Mdns : IDisposable
{
    const string Service = "_flux._udp";
    const string Suffix = "._flux._udp.local";

    // The most records that the cache holds, and the longest time it keeps one.
    const int MaxRecords = 512;
    static readonly TimeSpan MaxTtl = TimeSpan.FromMinutes(5);

    readonly string selfId;
    readonly Action<string> found;
    readonly Lock gate = new();
    readonly Dictionary<string, (ResourceRecord Record, DateTimeOffset Expires)> records = [];
    readonly HashSet<string> queried = [];
    readonly HashSet<string> announced = [];
    ServiceDiscovery? discovery;
    ServiceProfile? profile;
    bool browsing;

    public Mdns(string selfId, Action<string> found)
    {
        this.selfId = selfId;
        this.found = found;
    }

    // Publishes this device with its name, type, and link port. A second call
    // replaces the first.
    public void Publish(string name, string type, int port)
    {
        var addresses = LanAddresses.Advertised();
        lock (gate)
        {
            try
            {
                if (discovery is null)
                {
                    discovery = new ServiceDiscovery();
                    discovery.Mdns.AnswerReceived += OnAnswer;
                }
                if (profile is not null)
                {
                    discovery.Unadvertise(profile);
                }
                profile = new ServiceProfile(selfId, Service, (ushort)port, addresses);
                profile.AddProperty("id", selfId);
                profile.AddProperty("name", name);
                profile.AddProperty("type", type);
                profile.AddProperty("protocol", Identity.Version.ToString(System.Globalization.CultureInfo.InvariantCulture));
                discovery.Advertise(profile);
                discovery.Announce(profile);
            }
            catch (Exception e) when (e is SocketException or InvalidOperationException or NetworkInformationException)
            {
                FluxLog.Error("net", $"mDNS failed: {e.Message}");
            }
        }
    }

    // Asks the network for Flux computers. Each answer that names an address
    // goes to found while the browse runs.
    public void Browse()
    {
        lock (gate)
        {
            browsing = true;
            queried.Clear();
            announced.Clear();
            try
            {
                discovery?.QueryServiceInstances(Service);
            }
            catch (Exception e) when (e is SocketException or InvalidOperationException)
            {
                FluxLog.Info("net", $"mDNS query failed: {e.Message}");
            }
        }
    }

    public void StopBrowsing()
    {
        lock (gate)
        {
            browsing = false;
        }
    }

    void OnAnswer(object? sender, MessageEventArgs e)
    {
        var addresses = new List<string>();
        var questions = new List<(DomainName, DnsType)>();
        lock (gate)
        {
            if (!browsing || discovery is null)
            {
                return;
            }
            var now = DateTimeOffset.UtcNow;
            Add(e.Message.Answers.Concat(e.Message.AdditionalRecords), now);
            foreach (var (name, type) in Missing(now))
            {
                if (queried.Count < 128 && queried.Add($"{type}:{name}"))
                {
                    questions.Add((name, type));
                }
            }
            foreach (var ip in Computers(now))
            {
                if (announced.Add(ip))
                {
                    addresses.Add(ip);
                }
            }
        }
        foreach (var (name, type) in questions)
        {
            try
            {
                discovery?.Mdns.SendQuery(name, type: type);
            }
            catch (Exception ex) when (ex is SocketException or InvalidOperationException)
            {
            }
        }
        foreach (var ip in addresses)
        {
            found(ip);
        }
    }

    void Add(IEnumerable<ResourceRecord> list, DateTimeOffset now)
    {
        foreach (var record in list)
        {
            var key = $"{record.Type}:{record.Name}:{record}";
            if (record.TTL <= TimeSpan.Zero)
            {
                records.Remove(key);
                continue;
            }
            if (records.Count >= MaxRecords && !records.ContainsKey(key))
            {
                continue;
            }
            records[key] = (record, now.Add(record.TTL > MaxTtl ? MaxTtl : record.TTL));
        }
        foreach (var key in records.Where(r => r.Value.Expires <= now).Select(r => r.Key).ToArray())
        {
            records.Remove(key);
        }
    }

    ResourceRecord[] Live(DateTimeOffset now) => records.Values.Where(v => v.Expires > now).Select(v => v.Record).ToArray();

    static string Name(DomainName name) => name.ToString().TrimEnd('.').ToLowerInvariant();

    // The addresses of the Flux computers in the cache: a service instance
    // with a link port of fluxd, protocol 8, and a desktop or laptop type.
    IEnumerable<string> Computers(DateTimeOffset now)
    {
        var live = Live(now);
        foreach (var srv in live.OfType<SRVRecord>().Where(r => Name(r.Name).EndsWith(Suffix, StringComparison.Ordinal)))
        {
            if (srv.Port is < 12100 or > 12108)
            {
                continue;
            }
            var txt = live.OfType<TXTRecord>().FirstOrDefault(r => Name(r.Name) == Name(srv.Name));
            if (txt is null)
            {
                continue;
            }
            var properties = txt.Strings.Select(s => s.Split('=', 2)).Where(p => p.Length == 2).GroupBy(p => p[0])
                .ToDictionary(g => g.Key, g => g.First()[1], StringComparer.Ordinal);
            if (!properties.TryGetValue("id", out var id) || id == selfId || properties.GetValueOrDefault("protocol") != "8" ||
                properties.GetValueOrDefault("type") is not ("desktop" or "laptop"))
            {
                continue;
            }
            foreach (var a in live.OfType<ARecord>().Where(r => Name(r.Name) == Name(srv.Target) && LanAddresses.IsLocal(r.Address)))
            {
                yield return a.Address.ToString();
            }
        }
    }

    // The records that the cache misses for a full answer.
    IEnumerable<(DomainName, DnsType)> Missing(DateTimeOffset now)
    {
        var live = Live(now);
        var instances = live.OfType<PTRRecord>().Where(r => Name(r.Name) == "_flux._udp.local").Select(r => r.DomainName)
            .Concat(live.OfType<SRVRecord>().Where(r => Name(r.Name).EndsWith(Suffix, StringComparison.Ordinal)).Select(r => r.Name))
            .DistinctBy(Name).ToArray();
        foreach (var instance in instances)
        {
            var srv = live.OfType<SRVRecord>().FirstOrDefault(r => Name(r.Name) == Name(instance));
            if (srv is null)
            {
                yield return (instance, DnsType.SRV);
            }
            else if (!live.OfType<ARecord>().Any(r => Name(r.Name) == Name(srv.Target)))
            {
                yield return (srv.Target, DnsType.A);
            }
            if (!live.OfType<TXTRecord>().Any(r => Name(r.Name) == Name(instance)))
            {
                yield return (instance, DnsType.TXT);
            }
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            browsing = false;
            if (discovery is null)
            {
                return;
            }
            try
            {
                if (profile is not null)
                {
                    discovery.Unadvertise(profile);
                }
                discovery.Mdns.AnswerReceived -= OnAnswer;
                discovery.Dispose();
            }
            catch (Exception e) when (e is SocketException or InvalidOperationException or ObjectDisposedException)
            {
            }
            discovery = null;
            profile = null;
        }
    }
}

// The IPv4 addresses of this computer and the rules for LAN addresses.
public static class LanAddresses
{
    // True for an address of a private, link-local, or CGNAT network. Flux
    // dials and announces only to these.
    public static bool IsLocal(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            return false;
        }
        var b = address.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] <= 31) || (b[0] == 192 && b[1] == 168) ||
               (b[0] == 169 && b[1] == 254) || (b[0] == 100 && b[1] >= 64 && b[1] <= 127);
    }

    // True for an address of the Tailscale range, 100.64.0.0/10.
    public static bool IsTailscale(IPAddress address)
    {
        var b = address.MapToIPv4().GetAddressBytes();
        return b[0] == 100 && b[1] >= 64 && b[1] <= 127;
    }

    // The LAN addresses that mDNS publishes. A Tailscale address does not
    // reach a computer on the LAN, so mDNS leaves it out.
    public static IReadOnlyList<IPAddress> Advertised()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address)
                .Where(a => IsLocal(a) && !IsTailscale(a)).Distinct().ToArray();
        }
        catch (NetworkInformationException)
        {
            return [];
        }
    }
}
