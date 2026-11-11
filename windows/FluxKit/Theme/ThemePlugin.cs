using System.Text.Json.Nodes;

namespace FluxKit;

// The Omarchy themes of the paired computers and the theme setting, for the
// UI. ThemePlugin changes it on the UI thread.
public sealed class ThemeModel : Model
{
    ThemeBook book = new();
    ThemeMode mode = ThemeMode.Computer;

    internal ThemeModel()
    {
    }

    // The theme of each computer and the last theme, as a copy. Each change
    // replaces the copy. Each entry holds its palette.
    public ThemeBook Book
    {
        get => book;
        private set
        {
            book = value;
            Changed();
            Changed(nameof(Last));
        }
    }

    // The last theme, which the app draws without a scope, or null.
    public ComputerTheme? Last => book.Current(null);

    // The theme setting.
    public ThemeMode Mode
    {
        get => mode;
        internal set => Set(ref mode, value);
    }

    // The theme of the computer in scope, or the last theme without a scope.
    public ComputerTheme? Current(string? scope) => book.Current(scope);

    // The name of the theme of each computer, by device ID.
    public IReadOnlyDictionary<string, string> Names() => book.Names();

    // The palette to draw for the scope, after the theme setting.
    // systemDark is true when Windows uses the dark app mode.
    public ThemePalette Palette(string? scope, bool systemDark) => mode.Palette(Current(scope)?.Palette, systemDark);

    internal bool Put(string id, OmarchyTheme theme) => Change(b => b.Put(id, theme));

    internal bool Forget(string id) => Change(b => b.Forget(id));

    internal void Load(JsonNode? saved, Func<string, bool> keep)
    {
        var next = new ThemeBook();
        next.Load(saved, keep);
        Book = next;
    }

    // Changes a copy of the book, so that the UI never sees a book change
    // under it, and publishes the copy when it changed.
    bool Change(Func<ThemeBook, bool> change)
    {
        var next = book.Copy();
        if (!change(next))
        {
            return false;
        }
        Book = next;
        return true;
    }
}

// flux.theme in: the computer sends its active Omarchy theme when it connects
// and when the theme changes. docs/omarchy.md, section "Theme packet",
// describes it. Flux keeps the theme of each paired computer in the
// settings, so that the next start draws the theme at once. ThemePlugin.swift
// of the Mac app has the same rules.
public sealed class ThemePlugin : IFluxPlugin
{
    // The key of the saved book in core.Settings. The Mac app uses the same key.
    public const string SettingsKey = "theme.computers";

    FluxCore? core;

    public IReadOnlyList<string> Incoming { get; } = [PacketType.Theme];
    public IReadOnlyList<string> Outgoing { get; } = [];

    public ThemeModel Model { get; } = new();

    // Loads the saved book and the theme setting. The core attaches the
    // plugins before the UI binds to the model, so the load runs at once,
    // and the first frame draws the saved theme.
    public void Attach(FluxCore core)
    {
        this.core = core;
        var paired = core.Trust.All().Select(t => t.Id).ToHashSet();
        Load(core.Settings.Node(SettingsKey), paired);
        Model.Mode = ThemeModes.Parse(core.Settings.String(ThemeModes.SettingsKey));
    }

    // Saves the theme setting. Call it on the UI thread.
    public void SetMode(ThemeMode mode)
    {
        core?.Settings.Set(ThemeModes.SettingsKey, mode.Key());
        Model.Mode = mode;
    }

    // An unpair from either side forgets the theme of the computer. A link
    // that only drops keeps it. The core lock is held.
    public void OnDisconnected(Device device)
    {
        if (device.Paired || core is null)
        {
            return;
        }
        var id = device.Id;
        core.Host.Post(() => Forget(id));
    }

    // The core lock is held, on a network thread. The UI thread keeps the
    // order of the packets.
    public void Handle(Packet packet, Device device)
    {
        if (packet.Type != PacketType.Theme || OmarchyTheme.Parse(packet.Body) is not { } theme || core is null)
        {
            return;
        }
        var id = device.Id;
        core.Host.Post(() => Receive(theme, id));
    }

    // Takes a theme from a computer and saves the book when it changed.
    internal void Receive(OmarchyTheme theme, string deviceId)
    {
        if (!Model.Put(deviceId, theme))
        {
            return;
        }
        Save();
        FluxLog.Info("theme", $"theme {theme.Name} from {deviceId}");
    }

    // Forgets the theme of a computer that is no longer paired.
    internal void Forget(string deviceId)
    {
        if (Model.Forget(deviceId))
        {
            Save();
        }
    }

    // Restores the saved book. It keeps only the computers in paired.
    internal void Load(JsonNode? saved, IReadOnlySet<string> paired) => Model.Load(saved, paired.Contains);

    void Save() => core?.Settings.Set(SettingsKey, Model.Book.ToJson());
}
