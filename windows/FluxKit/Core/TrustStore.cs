using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace FluxKit;

// A paired computer. Flux pins its certificate.
public sealed record TrustedDevice(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("type")] string Type,
    // The certificate in base64 DER.
    [property: JsonPropertyName("certificate")] string Certificate,
    [property: JsonPropertyName("lastIp")] string LastIp = "",
    [property: JsonPropertyName("isFlux")] bool IsFlux = false)
{
    [JsonIgnore]
    public byte[]? CertificateDer
    {
        get
        {
            try
            {
                return Convert.FromBase64String(Certificate);
            }
            catch (FormatException)
            {
                return null;
            }
        }
    }
}

// The list of paired computers, stored as JSON in the data directory.
public sealed class TrustStore
{
    readonly string path;
    readonly Lock gate = new();
    readonly Dictionary<string, TrustedDevice> devices = [];

    // True when the file exists but does not read. A save would replace the
    // pairings in it, so this store does not save.
    readonly bool unreadable;

    // Reads the file. An entry that does not decode, or whose certificate
    // does not decode, is not paired. When the file loses an entry this way,
    // a copy stays in trusted.json.bad, because the next save writes only the
    // good entries.
    public TrustStore(string path)
    {
        this.path = path;
        byte[] data;
        try
        {
            data = File.ReadAllBytes(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (File.Exists(path))
            {
                unreadable = true;
                FluxLog.Error("core", $"cannot read the paired computers in {path}: {e.Message}");
            }
            return;
        }
        var (list, dropped) = Decode(data);
        foreach (var d in list)
        {
            devices[d.Id] = d;
        }
        if (dropped == 0)
        {
            return;
        }
        try
        {
            File.Copy(path, path + ".bad", overwrite: true);
            FluxLog.Error("core", $"{dropped} paired computers in {path} do not read and are not paired. A copy of the file is in trusted.json.bad");
        }
        catch (IOException e)
        {
            FluxLog.Error("core", $"{dropped} paired computers in {path} do not read, and the copy failed: {e.Message}");
        }
    }

    // Decodes the entries one at a time. It returns the good entries and the
    // number of entries that it dropped, or 1 when the file is not a JSON list.
    internal static (List<TrustedDevice> Devices, int Dropped) Decode(byte[] data)
    {
        JsonArray? array;
        try
        {
            array = JsonNode.Parse(data) as JsonArray;
        }
        catch (JsonException)
        {
            array = null;
        }
        if (array is null)
        {
            return ([], 1);
        }
        var good = new List<TrustedDevice>();
        foreach (var entry in array)
        {
            try
            {
                if (entry?.Deserialize<TrustedDevice>() is { Id: not null, Name: not null, Type: not null, Certificate: not null } d &&
                    d.CertificateDer is { Length: > 0 })
                {
                    good.Add(d);
                }
            }
            catch (JsonException)
            {
            }
        }
        return (good, array.Count - good.Count);
    }

    public TrustedDevice? Get(string id)
    {
        lock (gate)
        {
            return devices.GetValueOrDefault(id);
        }
    }

    public IReadOnlyList<TrustedDevice> All()
    {
        lock (gate)
        {
            return devices.Values.ToArray();
        }
    }

    // Adds or replaces a computer. It returns false when the file could not
    // be written. The computer stays trusted until Flux quits.
    public bool Put(TrustedDevice d)
    {
        lock (gate)
        {
            devices[d.Id] = d;
            return Save();
        }
    }

    public void Update(string id, Func<TrustedDevice, TrustedDevice> change)
    {
        lock (gate)
        {
            if (!devices.TryGetValue(id, out var old))
            {
                return;
            }
            var updated = change(old);
            if (updated != old)
            {
                devices[id] = updated;
                Save();
            }
        }
    }

    public void Remove(string id)
    {
        lock (gate)
        {
            if (devices.Remove(id))
            {
                Save();
            }
        }
    }

    bool Save()
    {
        if (unreadable)
        {
            FluxLog.Error("core", $"did not save the paired computers: {path} did not read at the start");
            return false;
        }
        try
        {
            var list = devices.Values.OrderBy(d => d.Id, StringComparer.Ordinal).ToArray();
            FileStore.WriteAtomic(path, JsonSerializer.SerializeToUtf8Bytes(list), secret: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            FluxLog.Error("core", $"cannot save the paired computers in {path}: {e.Message}");
            return false;
        }
    }
}
