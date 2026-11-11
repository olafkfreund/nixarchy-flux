using System.Text.Json;
using System.Text.Json.Nodes;

namespace FluxKit;

// Where Flux keeps its identity and settings.
public sealed record FluxPaths(string Data)
{
    // %LOCALAPPDATA%\Flux, or FLUX_DATA_DIR for tests.
    public static FluxPaths Standard()
    {
        var custom = Environment.GetEnvironmentVariable("FLUX_DATA_DIR");
        if (!string.IsNullOrEmpty(custom))
        {
            return new FluxPaths(Path.GetFullPath(custom));
        }
        return new FluxPaths(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Flux"));
    }

    public string File(string name) => Path.Combine(Data, name);
}

// The settings of Flux, one JSON object in settings.json. Each change saves
// the file. A file that does not read starts empty, and its copy stays in
// settings.json.bad.
public sealed class FluxSettings
{
    readonly string path;
    readonly Lock gate = new();
    readonly JsonObject values;

    // Called after a change, with the key, on the thread of the change.
    public event Action<string>? Changed;

    public FluxSettings(string path)
    {
        this.path = path;
        values = Read(path);
    }

    static JsonObject Read(string path)
    {
        if (!System.IO.File.Exists(path))
        {
            return [];
        }
        try
        {
            return JsonNode.Parse(System.IO.File.ReadAllBytes(path)) as JsonObject ?? throw new JsonException("not an object");
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            FluxLog.Error("core", $"cannot read the settings in {path}: {e.Message}");
            try
            {
                System.IO.File.Copy(path, path + ".bad", overwrite: true);
            }
            catch (IOException)
            {
            }
            return [];
        }
    }

    public bool Bool(string key, bool fallback)
    {
        lock (gate)
        {
            return Json.Bool(values[key]) ?? fallback;
        }
    }

    public string? String(string key)
    {
        lock (gate)
        {
            return Json.String(values[key]);
        }
    }

    public long? Long(string key)
    {
        lock (gate)
        {
            return Json.Long(values[key]);
        }
    }

    // A copy of the stored node, or null.
    public JsonNode? Node(string key)
    {
        lock (gate)
        {
            return values[key]?.DeepClone();
        }
    }

    public void Set(string key, JsonNode? value)
    {
        lock (gate)
        {
            if (value is null)
            {
                if (!values.Remove(key))
                {
                    return;
                }
            }
            else
            {
                if (JsonNode.DeepEquals(values[key], value))
                {
                    return;
                }
                values[key] = value.DeepClone();
            }
            Save();
        }
        Changed?.Invoke(key);
    }

    void Save()
    {
        try
        {
            FileStore.WriteAtomic(path, JsonSerializer.SerializeToUtf8Bytes(values, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            FluxLog.Error("core", $"cannot save the settings in {path}: {e.Message}");
        }
    }
}
