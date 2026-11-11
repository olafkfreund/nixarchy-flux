using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json.Nodes;

namespace FluxKit;

// The active Omarchy theme of a computer, from a flux.theme packet. The packet
// body is {"name", "mode": "dark" or "light", "colors": {key: "#rrggbb"},
// "border": {"colors": ["#rrggbbaa", ...], "angle": deg}}. The color keys are
// the keys of colors.toml, for example background, foreground, accent, muted,
// and red. Any key can be missing. docs/omarchy.md, section "Theme packet",
// describes the body. OmarchyTheme.swift of the Mac app has the same rules.
public sealed record OmarchyTheme
{
    // The most colors that Flux keeps from 1 theme.
    public const int MaxColors = 64;

    // The most border colors that Flux keeps. Hyprland takes 10.
    public const int MaxBorder = 10;

    // The longest theme name that Flux keeps, in characters.
    public const int MaxName = 64;

    public OmarchyTheme(string name, bool? dark, IReadOnlyDictionary<string, int> colors, IEnumerable<int>? border = null, double? borderAngle = null)
    {
        Name = name;
        Dark = dark;
        Colors = colors.ToImmutableSortedDictionary(StringComparer.Ordinal);
        Border = ValueList.From(border ?? []);
        BorderAngle = borderAngle;
    }

    public string Name { get; init; }

    // True for "dark", false for "light", null when the computer does not tell.
    public bool? Dark { get; init; }

    // The colors by their colors.toml key, as 0xRRGGBB.
    public ImmutableSortedDictionary<string, int> Colors { get; init; }

    // The colors of the active Hyprland border, as 0xRRGGBB, or empty.
    public ValueList<int> Border { get; init; }

    // The angle of the border gradient in degrees, or null for corner to corner.
    public double? BorderAngle { get; init; }

    // The color of a colors.toml key, or null.
    public int? this[string key] => Colors.TryGetValue(key, out var c) ? c : null;

    // The dictionary compares by reference, so the record compares its items.
    public bool Equals(OmarchyTheme? other) =>
        other is not null && Name == other.Name && Dark == other.Dark && Border.Equals(other.Border) &&
        BorderAngle == other.BorderAngle && Colors.Count == other.Colors.Count &&
        Colors.All(c => other.Colors.TryGetValue(c.Key, out var v) && v == c.Value);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Name);
        hash.Add(Dark);
        hash.Add(Border);
        hash.Add(BorderAngle);
        foreach (var (key, value) in Colors)
        {
            hash.Add(key);
            hash.Add(value);
        }
        return hash.ToHashCode();
    }

    // Reads a theme from a flux.theme packet body. It ignores a key or a color
    // that it cannot read. It returns null when the body has no readable
    // color.
    public static OmarchyTheme? Parse(JsonObject body)
    {
        var colors = new Dictionary<string, int>();
        if (body["colors"] is JsonObject map)
        {
            // An object has no fixed order, so the sorted keys decide which
            // colors stay at the limit.
            foreach (var key in map.Select(e => e.Key).Order(StringComparer.Ordinal))
            {
                if (colors.Count >= MaxColors)
                {
                    break;
                }
                if (ValidKey(key) && ColorMath.ParseColor(Json.String(map[key])) is { } color)
                {
                    colors[key] = color;
                }
            }
        }
        var borderObject = body["border"] as JsonObject;
        var border = (borderObject?["colors"] as JsonArray ?? [])
            .Select(n => ColorMath.ParseColor(Json.String(n)))
            .OfType<int>()
            .Take(MaxBorder)
            .ToArray();
        double? angle = null;
        if (borderObject?["angle"] is { } value && AngleValue(value) is { } a && double.IsFinite(a))
        {
            angle = (a % 360 + 360) % 360;
        }
        if (colors.Count == 0 && border.Length == 0)
        {
            return null;
        }
        var name = Json.String(body["name"]) is { } text ? Prefix(text.Trim(), MaxName) : "";
        bool? dark = Json.String(body["mode"])?.ToLowerInvariant() switch
        {
            "dark" => true,
            "light" => false,
            _ => null,
        };
        return new OmarchyTheme(name, dark, colors, border, angle);
    }

    // A number, or a string such as "-90deg" without the suffix "deg".
    static double? AngleValue(JsonNode value)
    {
        if (Json.Double(value) is { } d)
        {
            return d;
        }
        if (Json.String(value) is not { } text)
        {
            return null;
        }
        if (text.EndsWith("deg", StringComparison.Ordinal))
        {
            text = text[..^3];
        }
        return double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
    }

    // 1 to 32 characters: a to z first, then a to z, 0 to 9, or "_".
    internal static bool ValidKey(string key) =>
        key.Length is > 0 and <= 32 && char.IsAsciiLetterLower(key[0]) &&
        key.Skip(1).All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '_');

    // The first count characters of text. A character is what the user sees
    // as 1 character, so that the cut does not split it.
    internal static string Prefix(string text, int count)
    {
        var info = new StringInfo(text);
        return info.LengthInTextElements <= count ? text : info.SubstringByTextElements(0, count);
    }

    // The theme in the packet form, for the saved book.
    public JsonObject ToJson()
    {
        var output = new JsonObject { ["name"] = Name };
        if (Dark is { } dark)
        {
            output["mode"] = dark ? "dark" : "light";
        }
        var map = new JsonObject();
        foreach (var (key, value) in Colors)
        {
            map[key] = ColorMath.Hex(value);
        }
        output["colors"] = map;
        if (Border.Count > 0 || BorderAngle is not null)
        {
            var b = new JsonObject { ["colors"] = new JsonArray(Border.Select(c => (JsonNode)ColorMath.Hex(c)).ToArray()) };
            if (BorderAngle is { } angle)
            {
                b["angle"] = angle;
            }
            output["border"] = b;
        }
        return output;
    }
}
