using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using FluxKit;

namespace Flux;

// The media page of 1 computer: the players as pills, the album art, the
// title, the artist, and the album, and the dock with the position,
// Previous, Play or Pause, Next, and the volume. A player that sends no
// volume shows no volume row. MediaPage.swift of the Mac app has the same
// layout.
public sealed class MediaPage : Page
{
    readonly string deviceId;
    readonly DispatcherTimer tick = new() { Interval = TimeSpan.FromSeconds(0.5) };
    readonly DispatcherTimer ask = new() { Interval = TimeSpan.FromSeconds(10) };
    readonly Slider position = new() { Minimum = 0, Maximum = 1, IsMoveToPointEnabled = true };
    readonly Slider volume = new() { Minimum = 0, Maximum = 100, IsMoveToPointEnabled = true };
    readonly TextBlock at = Ui.Text("", 10.5, mono: true);
    readonly TextBlock length = Ui.Text("", 10.5, mono: true);
    readonly TextBlock level = Ui.Text("", 10.5, mono: true);
    readonly Image art = new() { Stretch = Stretch.UniformToFill };
    Uri? artShown;
    bool dragging;
    DateTime volumeSent;

    public MediaPage(AppModel model, string deviceId) : base(model)
    {
        this.deviceId = deviceId;
        Watch(Plugin?.Model);
        tick.Tick += (_, _) => Tick();
        ask.Tick += (_, _) => RequestPlayers();
        Loaded += (_, _) =>
        {
            tick.Start();
            ask.Start();
            RequestPlayers();
        };
        Unloaded += (_, _) =>
        {
            tick.Stop();
            ask.Stop();
        };
        position.AddHandler(Thumb.DragStartedEvent, new RoutedEventHandler((_, _) =>
        {
            dragging = true;
            Hold = true;
        }));
        position.AddHandler(Thumb.DragCompletedEvent, new RoutedEventHandler((_, _) =>
        {
            dragging = false;
            Seek();
            Hold = false;
        }));
        volume.GotMouseCapture += (_, _) => Hold = true;
        volume.LostMouseCapture += (_, _) => Hold = false;
        position.PreviewMouseLeftButtonUp += (_, _) =>
        {
            if (!dragging)
            {
                Seek();
            }
        };
        System.Windows.Automation.AutomationProperties.SetName(position, "Position");
        System.Windows.Automation.AutomationProperties.SetName(volume, "Volume");
        volume.ValueChanged += (_, e) =>
        {
            if (!volume.IsMouseCaptureWithin && !volume.IsKeyboardFocusWithin)
            {
                return;
            }
            level.Text = $"{(int)e.NewValue}%";
            // At most 1 request each 150 ms during a drag.
            if (DateTime.UtcNow - volumeSent >= TimeSpan.FromMilliseconds(150))
            {
                volumeSent = DateTime.UtcNow;
                Plugin?.SetVolume(deviceId, (int)Math.Round(e.NewValue));
            }
        };
        volume.PreviewMouseLeftButtonUp += (_, _) => Plugin?.SetVolume(deviceId, (int)Math.Round(volume.Value));
    }

    MprisPlugin? Plugin => Model.Core.Plugin<MprisPlugin>();
    DeviceSnapshot? Device => Model.PairedDevice(deviceId);

    public override string Title => "Media";

    public override string Subtitle => Texts.ContextLine(Plugin?.Model.Media(deviceId).Player?.Name ?? "", Device?.Name ?? "");

    void RequestPlayers()
    {
        if (Device is { Online: true })
        {
            Plugin?.RequestPlayers(deviceId);
        }
    }

    void Seek()
    {
        if (Plugin?.Model.Media(deviceId).Player is { CanSeek: true, Length: > 0 } p)
        {
            Plugin.Seek(deviceId, (long)(position.Value * p.Length));
        }
    }

    // Moves the position and the clock between the packets of the player.
    void Tick()
    {
        if (Plugin?.Model.Media(deviceId).Player is not { } p)
        {
            return;
        }
        var now = p.PositionAt(MprisPlugin.Now);
        if (!dragging && p.Length > 0)
        {
            position.Value = Math.Clamp((double)now / p.Length, 0, 1);
        }
        at.Text = Clock(dragging ? (long)(position.Value * p.Length) : now);
        length.Text = Clock(p.Length);
    }

    // A position or a length in milliseconds as "3:19" or "1:02:03".
    public static string Clock(long ms)
    {
        var s = (int)(Math.Max(ms, 0) / 1000);
        var h = s / 3600;
        var m = s / 60 % 60;
        var sec = s % 60;
        return h > 0 ? $"{h}:{m:00}:{sec:00}" : $"{m}:{sec:00}";
    }

    protected override UIElement Build()
    {
        var tn = Tn.Current;
        if (Device is not { } d || Plugin is not { } plugin)
        {
            return Parts.Unavailable(Icons.Desktop, "The computer is gone", "The computer is no longer paired.");
        }
        var media = plugin.Model.Media(d.Id);
        var player = media.Player;
        var layout = new DockPanel();
        if (player is not null && d.Online && media.Players.Count > 1)
        {
            var pills = Ui.H(8, media.Players.Select(name =>
            {
                var on = name == player.Name;
                var state = media.States.TryGetValue(name, out var s) ? s : null;
                return (UIElement)Pill(name, on, state is null ? "" : state.Playing ? "playing" : "paused",
                    state?.Playing == true ? Parts.LinkDot(true) : Parts.RingMark(), () =>
                    {
                        if (!on)
                        {
                            plugin.Select(d.Id, name);
                        }
                    });
            })).Margin(Metrics.Gutter, 8, Metrics.Gutter, 10);
            DockPanel.SetDock(pills, System.Windows.Controls.Dock.Top);
            layout.Children.Add(pills);
        }
        if (!d.Online)
        {
            layout.Children.Add(Parts.Unavailable(Icons.Wifi, $"{d.Name} is offline", $"The player controls show when {d.Name} is online."));
            return layout;
        }
        if (player is null)
        {
            layout.Children.Add(Parts.Unavailable(Icons.Audio, "Nothing is playing", $"Play music or a video on {d.Name}. The controls show here."));
            return layout;
        }
        var line = Parts.ProgressLine(player.Length > 0 ? Math.Clamp((double)player.PositionAt(MprisPlugin.Now) / player.Length, 0, 1) : null, tn.Green);
        DockPanel.SetDock(line, System.Windows.Controls.Dock.Top);
        layout.Children.Add(line);
        var dock = Dock(d, plugin, player);
        DockPanel.SetDock(dock, System.Windows.Controls.Dock.Bottom);
        layout.Children.Add(dock);
        layout.Children.Add(NowPlaying(player));
        Tick();
        return layout;
    }

    UIElement NowPlaying(RemotePlayer player)
    {
        var tn = Tn.Current;
        LoadArt(player.ArtUri);
        var cover = new Border
        {
            Child = art,
            Width = 220,
            Height = 220,
            CornerRadius = new CornerRadius(16),
            BorderBrush = tn.Line,
            BorderThickness = new Thickness(1),
            Background = tn.Tile,
            ClipToBounds = true,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        Ui.Detach(art);
        var text = Ui.V(4,
            Ui.SelectableText(player.Title.Length == 0 ? "Unknown title" : player.Title, 22, mono: false, color: tn.Text),
            player.Artist.Length > 0 ? Ui.Text(player.Artist, 14, color: tn.Sub) : null,
            player.Album.Length > 0 ? Ui.Text(player.Album, 11.5, color: tn.Sub, mono: true) : null);
        var column = Ui.V(18, cover, text);
        column.MaxWidth = 560;
        column.VerticalAlignment = VerticalAlignment.Bottom;
        return Ui.Scroll(column.Margin(20));
    }

    void LoadArt(Uri? uri)
    {
        if (uri == artShown)
        {
            return;
        }
        artShown = uri;
        art.Source = null;
        if (uri is null)
        {
            return;
        }
        _ = LoadArtAsync(uri);
    }

    async Task LoadArtAsync(Uri uri)
    {
        var bytes = await AlbumArtLoader.LoadAsync(uri);
        if (bytes is null || uri != artShown)
        {
            return;
        }
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(bytes);
            image.DecodePixelWidth = 440;
            image.EndInit();
            image.Freeze();
            art.Source = image;
        }
        catch (Exception e) when (e is NotSupportedException or IOException or ArgumentException or System.Runtime.InteropServices.COMException)
        {
            FluxLog.Info("app", "the album art did not decode");
        }
    }

    UIElement Dock(DeviceSnapshot d, MprisPlugin plugin, RemotePlayer player)
    {
        var tn = Tn.Current;
        var rows = new List<UIElement?>();
        if (player.Length > 0)
        {
            position.IsEnabled = player.CanSeek;
            Ui.Detach(position);
            Ui.Detach(at);
            Ui.Detach(length);
            at.Foreground = tn.Sub;
            length.Foreground = tn.Sub;
            rows.Add(Ui.V(4, position, Ui.H(0, at, Ui.Spacer(), length)).Margin(4, 0, 4, 0));
        }
        var keys = new Grid { Height = 56 };
        keys.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        keys.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.4, GridUnitType.Star) });
        keys.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        AddKey(keys, 0, Icons.Previous, "Previous", tn.Line, tn.Text, 20, () => plugin.Action(d.Id, "Previous"));
        AddKey(keys, 1, player.Playing ? Icons.Pause : Icons.Play, player.Playing ? "Pause" : "Play", tn.Green, tn.OnAccent, 24,
            () => plugin.Action(d.Id, "PlayPause"));
        AddKey(keys, 2, Icons.Next, "Next", tn.Line, tn.Text, 20, () => plugin.Action(d.Id, "Next"));
        rows.Add(keys);
        if (player.Volume is { } v)
        {
            if (!volume.IsMouseCaptureWithin)
            {
                volume.Value = v;
                level.Text = $"{v}%";
            }
            Ui.Detach(volume);
            Ui.Detach(level);
            level.Foreground = tn.Sub;
            level.MinWidth = 34;
            level.TextAlignment = TextAlignment.Right;
            rows.Add(Ui.H(12, Ui.Icon(Icons.Volume, 13, tn.Sub), volume.Fill(), level).Margin(4, 0, 4, 0).MinHeight(36));
        }
        return Parts.PageDock(Ui.V(10, rows), 16);
    }

    static void AddKey(Grid grid, int column, string glyph, string help, Brush fill, Brush ink, double size, Action run)
    {
        var tile = new Border
        {
            Child = Ui.Icon(glyph, size, ink),
            Background = fill,
            CornerRadius = new CornerRadius(16),
            Margin = new Thickness(column == 0 ? 0 : 4, 0, column == 2 ? 0 : 4, 0),
        };
        var key = new PlainButton(tile, run).Named(help).Help(help);
        Grid.SetColumn(key, column);
        grid.Children.Add(key);
    }

    // A pill of a strip: a mark, the label, and the meta in mono. The
    // selected pill has the accent.
    public static UIElement Pill(string label, bool selected, string meta, UIElement mark, Action run, bool dim = false, string help = "")
    {
        var tn = Tn.Current;
        var row = Ui.H(7, mark, Ui.Text(label, 12, FontWeights.Medium), meta.Length > 0 ? Ui.Text(meta, 10.5, color: tn.Sub, mono: true) : null);
        var pill = new Border
        {
            Child = row,
            Background = selected ? tn.AccentTile : tn.Tile,
            BorderBrush = selected ? tn.Accent : tn.Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(15),
            Padding = new Thickness(10, 0, 11, 0),
            Height = 30,
        };
        var b = new PlainButton(pill, run).Help(help).Dimmed(dim);
        System.Windows.Automation.AutomationProperties.SetItemStatus(b, selected ? "Selected" : "");
        return b;
    }
}

// The player of a computer at the bottom of Control: the art, the title, the
// artist, Play or Pause, and Next. A click opens the media page.
public static class MediaDock
{
    public static UIElement? Build(AppModel model)
    {
        if (model.Core.Plugin<MprisPlugin>() is not { } mpris)
        {
            return null;
        }
        var scoped = model.ConnectedPaired.Where(d => (model.Scope is null || d.Id == model.Scope) && d.Accepts(PacketType.MprisRequest)
            && mpris.Model.Media(d.Id).Player is not null).ToArray();
        var device = scoped.FirstOrDefault(d => mpris.Model.Media(d.Id).Player?.Playing == true) ?? scoped.FirstOrDefault();
        if (device is null || mpris.Model.Media(device.Id).Player is not { } player)
        {
            return null;
        }
        var tn = Tn.Current;
        var info = Ui.V(1,
            Ui.Text(player.Title.Length == 0 ? "Unknown title" : player.Title, 13, FontWeights.SemiBold),
            Ui.Text(Texts.ContextLine(player.Artist, device.Name), 11, color: tn.Sub));
        var open = new PlainButton(Ui.H(10, Ui.Icon(Icons.Audio, 18, tn.Green, 24), info.Fill()), () =>
            model.Push(new Route.Feature(Card.Media, device.Id))).Named("Open the media of " + device.Name);
        var play = new FluxButton("", ButtonKind.Tonal, () => mpris.Action(device.Id, "PlayPause"), player.Playing ? Icons.Pause : Icons.Play)
            .Named(player.Playing ? "Pause" : "Play");
        var next = new FluxButton("", ButtonKind.Tonal, () => mpris.Action(device.Id, "Next"), Icons.Next).Named("Next");
        return Parts.PageDock(Ui.H(8, open.Fill(), play, next), 12);
    }

    // True when the dock shows a player, so that Control leaves out the Media tool.
    public static bool Shows(AppModel model) => Build(model) is not null;
}

// The commands that a computer publishes, with Run. CommandsSection.swift of
// the Mac app has the same list.
public sealed class CommandsPage : Page
{
    readonly string deviceId;
    string? ran;

    public CommandsPage(AppModel model, string deviceId) : base(model)
    {
        this.deviceId = deviceId;
        Watch(model.Core.Plugin<RunCommandPlugin>()?.Model);
        Loaded += (_, _) =>
        {
            if (Model.PairedDevice(deviceId) is { Online: true })
            {
                Model.Core.Plugin<RunCommandPlugin>()?.Request(deviceId);
            }
        };
    }

    public override string Title => "Commands";
    public override string Subtitle => Model.PairedDevice(deviceId)?.Name ?? "";

    protected override UIElement Build()
    {
        var tn = Tn.Current;
        if (Model.PairedDevice(deviceId) is not { } d || Model.Core.Plugin<RunCommandPlugin>() is not { } plugin)
        {
            return Parts.Unavailable(Icons.Desktop, "The computer is gone", "The computer is no longer paired.");
        }
        var items = new List<UIElement?>
        {
            Parts.ListHeading("Commands", action: new FluxButton("Refresh", ButtonKind.Text, () => plugin.Request(d.Id), Icons.Refresh, d.Online)),
        };
        var commands = plugin.Model.Commands(d.Id);
        if (!d.Online)
        {
            items.Add(Ui.Text($"The commands show when {d.Name} is online.", 13, color: tn.Sub).Margin(4, 0, 4, 0));
        }
        else if (commands is null)
        {
            items.Add(Ui.H(10, Parts.Spinner(), Ui.Text($"Loading the commands of {d.Name}", 13, color: tn.Sub)).Margin(4, 0, 4, 0));
        }
        else if (commands.Count == 0)
        {
            items.Add(Ui.V(2, Ui.Text("No commands yet", 13), Ui.Text($"On {d.Name}, open Flux and add commands in Phone commands. They show here.", 12, color: tn.Sub, wrap: true)).Margin(4, 0, 4, 0));
        }
        else
        {
            foreach (var c in commands)
            {
                var done = ran == c.Key;
                var run = new FluxButton(done ? "Done" : "Run", done ? ButtonKind.Text : ButtonKind.Tonal, () =>
                {
                    plugin.Run(d.Id, c);
                    ran = c.Key;
                    Invalidate();
                    _ = Task.Delay(1600).ContinueWith(_ => Dispatcher.BeginInvoke(() =>
                    {
                        ran = null;
                        Invalidate();
                    }));
                }, done ? Icons.Check : Icons.Play).Help($"Run {c.Name} on {d.Name}");
                var row = Ui.H(10, Ui.V(2, Ui.Text(c.Name, 13, FontWeights.SemiBold), Ui.Text(c.Command, 11, color: tn.Sub, mono: true)).Fill(), run);
                items.Add(Ui.Tile(row, padding: 12));
            }
        }
        return Ui.Scroll(Ui.Column(Ui.V(Metrics.Gap, items), 8, 24));
    }
}
