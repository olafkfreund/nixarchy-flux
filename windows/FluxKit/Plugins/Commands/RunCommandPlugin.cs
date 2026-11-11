using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FluxKit;

// A command that the computer publishes.
public sealed record RemoteCommand(string Key, string Name, string Command)
{
    // The commands of a flux.runcommand packet. The computer sends
    // commandList as a JSON string, in the order that it shows them.
    public static IReadOnlyList<RemoteCommand> List(Packet p)
    {
        if (p.String("commandList") is not { } text)
        {
            return [];
        }
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            return [];
        }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return [];
            }
            var list = new List<RemoteCommand>();
            foreach (var key in JsonKeys.Ordered(text))
            {
                if (root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Object)
                {
                    list.Add(new RemoteCommand(key, Text(value, "name") ?? key, Text(value, "command") ?? ""));
                }
            }
            return list;
        }
    }

    static string? Text(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

// Reads the member names of a JSON object text in order. A parser of the
// whole text can lose the order or refuse a name that comes twice.
internal static class JsonKeys
{
    // The top-level keys of the object, first occurrence first. It returns
    // what it read before the first syntax error.
    public static IReadOnlyList<string> Ordered(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var i = 0;
        var keys = new List<string>();
        var seen = new HashSet<string>();

        void SkipSpace()
        {
            while (i < bytes.Length && bytes[i] is 0x20 or 0x09 or 0x0A or 0x0D)
            {
                i++;
            }
        }

        // Moves past a string that starts at i and returns its range, quotes
        // included, or null at the end of the text.
        Range? SkipString()
        {
            var start = i;
            i++;
            while (i < bytes.Length)
            {
                switch (bytes[i])
                {
                    case 0x5C:
                        i += 2;
                        break;
                    case 0x22:
                        i++;
                        return start..i;
                    default:
                        i++;
                        break;
                }
            }
            return null;
        }

        // Moves past one value, up to the comma or brace that ends it.
        void SkipValue()
        {
            var depth = 0;
            while (i < bytes.Length)
            {
                switch (bytes[i])
                {
                    case 0x22:
                        SkipString();
                        continue;
                    case 0x7B or 0x5B:
                        depth++;
                        break;
                    case 0x7D or 0x5D:
                        if (depth == 0)
                        {
                            return;
                        }
                        depth--;
                        break;
                    case 0x2C when depth == 0:
                        return;
                }
                i++;
            }
        }

        SkipSpace();
        if (i >= bytes.Length || bytes[i] != 0x7B)
        {
            return keys;
        }
        i++;
        while (true)
        {
            SkipSpace();
            if (i >= bytes.Length || bytes[i] != 0x22 || SkipString() is not { } raw || Decode(bytes.AsSpan(raw)) is not { } key)
            {
                return keys;
            }
            if (seen.Add(key))
            {
                keys.Add(key);
            }
            SkipSpace();
            if (i >= bytes.Length || bytes[i] != 0x3A)
            {
                return keys;
            }
            i++;
            SkipValue();
            if (i >= bytes.Length || bytes[i] != 0x2C)
            {
                return keys;
            }
            i++;
        }
    }

    // Decodes one JSON string with its quotes, or returns null.
    static string? Decode(ReadOnlySpan<byte> raw)
    {
        try
        {
            var reader = new Utf8JsonReader(raw);
            return reader.Read() && reader.TokenType == JsonTokenType.String ? reader.GetString() : null;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }
}

// The commands of each computer, for the UI.
public sealed class CommandsModel : Model
{
    ImmutableDictionary<string, ValueList<RemoteCommand>> lists = ImmutableDictionary<string, ValueList<RemoteCommand>>.Empty;

    // The command list of each computer, by device ID.
    public ImmutableDictionary<string, ValueList<RemoteCommand>> Lists
    {
        get => lists;
        internal set => Set(ref lists, value);
    }

    // The commands of the computer, or null until its list arrives.
    public ValueList<RemoteCommand>? Commands(string deviceId) => lists.TryGetValue(deviceId, out var l) ? l : null;
}

// flux.runcommand.request out, flux.runcommand in: this computer runs the
// commands that the computer publishes. RunCommandPlugin.swift of the Mac app
// has the same rules.
public sealed class RunCommandPlugin : IFluxPlugin
{
    FluxCore? core;

    public CommandsModel Model { get; } = new();

    public IReadOnlyList<string> Incoming { get; } = [PacketType.RunCommand];
    public IReadOnlyList<string> Outgoing { get; } = [PacketType.RunCommandRequest];

    public void Attach(FluxCore core) => this.core = core;

    public void Handle(Packet packet, Device device)
    {
        var id = device.Id;
        var list = ValueList.From(RemoteCommand.List(packet));
        core?.Host.Post(() => Model.Lists = Model.Lists.SetItem(id, list));
    }

    // Asks the computer for its commands.
    public void Request(string deviceId) =>
        core?.Send(new Packet(PacketType.RunCommandRequest, new JsonObject { ["requestCommandList"] = true }), deviceId);

    // Runs the command on the computer.
    public void Run(string deviceId, RemoteCommand command)
    {
        if (core is null)
        {
            return;
        }
        if (core.Send(new Packet(PacketType.RunCommandRequest, new JsonObject { ["key"] = command.Key }), deviceId))
        {
            FluxLog.Info("commands", $"sent command {command.Key}");
            core.Toast($"Ran “{command.Name}”");
        }
        else
        {
            FluxLog.Info("commands", $"command {command.Key} not sent, no open link");
            core.Toast("Not connected. Try again in a moment");
        }
    }
}
