using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FluxKit;

// One Flux network packet. On the wire, a packet is one JSON object followed
// by a newline. internal/proto/packet.go in the Go source is the reference.
public sealed class Packet
{
    // The most nested JSON levels that a packet may have.
    const int MaxDepth = 64;

    public string Type { get; }
    public JsonObject Body { get; }
    public long Id { get; init; } = Now();
    public long PayloadSize { get; init; }

    // The port of a payload that the computer listens on.
    public int PayloadPort { get; init; }

    // The token of a tunnel payload. This device listens, and the computer
    // connects.
    public string? PayloadTunnel { get; init; }

    public Packet(string type, JsonObject? body = null)
    {
        Type = type;
        Body = body ?? [];
    }

    public static long Now() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public bool HasPayload => PayloadSize != 0 && (PayloadPort > 0 || PayloadTunnel is not null);

    // Returns the packet as one line with a trailing newline.
    public byte[] Serialize()
    {
        var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteNumber("id", Id);
            w.WriteString("type", Type);
            w.WritePropertyName("body");
            Body.WriteTo(w);
            if (PayloadSize != 0 && (PayloadPort > 0 || PayloadTunnel is not null))
            {
                w.WriteNumber("payloadSize", PayloadSize);
                w.WriteStartObject("payloadTransferInfo");
                if (PayloadPort > 0)
                {
                    w.WriteNumber("port", PayloadPort);
                }
                else
                {
                    w.WriteString("tunnel", PayloadTunnel);
                }
                w.WriteEndObject();
            }
            w.WriteEndObject();
        }
        buffer.WriteByte((byte)'\n');
        return buffer.ToArray();
    }

    // Parses one packet line. It returns null for a line that is not a packet.
    public static Packet? Parse(ReadOnlySpan<byte> line)
    {
        JsonNode? root;
        try
        {
            var reader = new Utf8JsonReader(line, new JsonReaderOptions { MaxDepth = MaxDepth });
            root = JsonNode.Parse(ref reader);
        }
        catch (JsonException)
        {
            return null;
        }
        if (root is not JsonObject obj || Json.String(obj["type"]) is not { Length: > 0 } type)
        {
            return null;
        }
        // The body leaves the root, so that a feature can put it in another
        // object.
        var body = obj["body"] as JsonObject;
        if (body is not null)
        {
            obj.Remove("body");
        }
        var info = obj["payloadTransferInfo"] as JsonObject;
        var port = Json.Int(info?["port"]) ?? 0;
        var tunnel = Json.String(info?["tunnel"]);
        return new Packet(type, body)
        {
            Id = Json.Long(obj["id"]) ?? (long.TryParse(Json.String(obj["id"]), out var id) ? id : 0),
            PayloadSize = Json.Long(obj["payloadSize"]) ?? 0,
            PayloadPort = port,
            PayloadTunnel = port > 0 || string.IsNullOrEmpty(tunnel) ? null : tunnel,
        };
    }

    public static Packet? Parse(string line) => Parse(System.Text.Encoding.UTF8.GetBytes(line));

    public string? String(string key) => Json.String(Body[key]);
    public bool? Bool(string key) => Json.Bool(Body[key]);
    public int? Int(string key) => Json.Int(Body[key]);
    public long? Long(string key) => Json.Long(Body[key]);
    public double? Double(string key) => Json.Double(Body[key]);
    public bool Has(string key) => Body.ContainsKey(key);
    public JsonObject? Object(string key) => Body[key] as JsonObject;
    public JsonArray? Array(string key) => Body[key] as JsonArray;
    public IReadOnlyList<string> Strings(string key) => Json.Strings(Body[key]);
}

// Typed reads of JSON nodes. Each read returns null for a node of another kind.
public static class Json
{
    public static string? String(JsonNode? n) =>
        n is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : null;

    public static bool? Bool(JsonNode? n) => n?.GetValueKind() switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => null,
    };

    // A value that the code made holds an int or a double, and a parsed value
    // holds a JsonElement, so the reads go through the JSON text.
    public static double? Double(JsonNode? n) =>
        n is JsonValue v && v.GetValueKind() == JsonValueKind.Number &&
        double.TryParse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    // A number without a fraction that fits a long. 3.0 counts, 3.5 does not.
    public static long? Long(JsonNode? n)
    {
        if (n is not JsonValue v || v.GetValueKind() != JsonValueKind.Number)
        {
            return null;
        }
        var text = v.ToJsonString();
        if (long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l))
        {
            return l;
        }
        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) &&
            double.IsFinite(d) && Math.Floor(d) == d && d >= long.MinValue && d < long.MaxValue)
        {
            return (long)d;
        }
        return null;
    }

    public static int? Int(JsonNode? n) => Long(n) is { } l && l >= int.MinValue && l <= int.MaxValue ? (int)l : null;

    // The strings of an array. Other items are skipped.
    public static IReadOnlyList<string> Strings(JsonNode? n) =>
        n is JsonArray a ? a.Select(String).OfType<string>().ToArray() : [];
}
