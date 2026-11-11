using System.Text.Json.Nodes;

namespace FluxKit;

// The theme of 1 computer and its palette after the contrast guard.
public sealed record ComputerTheme(string DeviceId, OmarchyTheme Theme, ThemePalette Palette)
{
    // The name of the theme, for example "tokyo-night". It can be empty.
    public string Name => Theme.Name;
}

// The Omarchy themes of the computers, and the rules that pick the theme that
// the Computer setting draws. ThemeBook.swift of the Mac app has the same
// rules.
//
// A computer sends its theme on each connect, so a theme that comes again
// changes nothing. The last theme is the theme that changed most recently.
// Without a scope, the app draws the last theme. A reconnect, or the first
// theme of a newly paired computer, does not change the last theme.
public sealed class ThemeBook
{
    // The device IDs in the order of change. The last one changed most recently.
    readonly List<string> order = [];
    readonly Dictionary<string, ComputerTheme> themes = [];

    // The computer whose theme the app draws without a scope.
    public string? LastId { get; private set; }

    // Takes the theme that the computer id sent. The theme becomes the last
    // theme when no last theme exists, when this computer sent the last
    // theme, or when the theme on this computer changed. It returns true when
    // the book changed and must be saved.
    public bool Put(string id, OmarchyTheme theme)
    {
        themes.TryGetValue(id, out var old);
        if (old is not null && old.Theme == theme)
        {
            if (LastId is not null)
            {
                return false;
            }
            LastId = id;
            return true;
        }
        Add(id, theme);
        if (LastId is null || LastId == id || old is not null)
        {
            LastId = id;
        }
        return true;
    }

    // Forgets the theme of a computer that is no longer paired. When it was
    // the last theme, the remaining theme that changed most recently takes
    // its place. It returns true when the book changed and must be saved.
    public bool Forget(string id)
    {
        if (!themes.Remove(id))
        {
            return false;
        }
        order.Remove(id);
        if (LastId == id)
        {
            LastId = order.Count > 0 ? order[^1] : null;
        }
        return true;
    }

    // The theme to draw. With a scope, it is the theme of that computer, or
    // null when that computer sent no theme. Without a scope, it is the last
    // theme.
    public ComputerTheme? Current(string? scope) =>
        (scope ?? LastId) is { } key ? themes.GetValueOrDefault(key) : null;

    // The theme of the computer id, or null.
    public ComputerTheme? Theme(string id) => themes.GetValueOrDefault(id);

    // The name of the theme of each computer, by device ID.
    public IReadOnlyDictionary<string, string> Names() => themes.ToDictionary(t => t.Key, t => t.Value.Name);

    // The book in the form that Load reads:
    // {"last": id, "computers": [{"id": id, "theme": <packet form>}]}.
    // "last" is absent when no last theme exists.
    public JsonObject ToJson()
    {
        var output = new JsonObject();
        if (LastId is not null)
        {
            output["last"] = LastId;
        }
        var computers = new JsonArray();
        foreach (var id in order)
        {
            if (themes.TryGetValue(id, out var t))
            {
                computers.Add(new JsonObject { ["id"] = id, ["theme"] = t.Theme.ToJson() });
            }
        }
        output["computers"] = computers;
        return output;
    }

    // Restores the book from saved, the form of ToJson. It keeps only the
    // computers that keep accepts, for example the paired computers. It
    // ignores an entry that it cannot read.
    public void Load(JsonNode? saved, Func<string, bool>? keep = null)
    {
        themes.Clear();
        order.Clear();
        LastId = null;
        if (saved is not JsonObject root)
        {
            return;
        }
        foreach (var entry in (root["computers"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (Json.String(entry["id"]) is not { } id || !(keep?.Invoke(id) ?? true) ||
                entry["theme"] is not JsonObject body || OmarchyTheme.Parse(body) is not { } theme)
            {
                continue;
            }
            Add(id, theme);
        }
        LastId = Json.String(root["last"]) is { } last && themes.ContainsKey(last) ? last : order.Count > 0 ? order[^1] : null;
    }

    // A copy that later changes of this book do not reach.
    public ThemeBook Copy()
    {
        var copy = new ThemeBook { LastId = LastId };
        copy.order.AddRange(order);
        foreach (var (id, t) in themes)
        {
            copy.themes[id] = t;
        }
        return copy;
    }

    // Puts the theme of id at the end of the order, with its new palette.
    void Add(string id, OmarchyTheme theme)
    {
        order.Remove(id);
        order.Add(id);
        themes[id] = new ComputerTheme(id, theme, ThemePalette.Of(theme));
    }
}
