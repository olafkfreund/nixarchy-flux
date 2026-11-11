using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using FluxKit;

namespace Flux;

// Computers: this computer, the paired computers, and the computers to pair.
// A click on a paired computer sets the scope. Its page holds Unpair. The
// dock shows the search, with Search again. ComputersPage.swift of the Mac
// app has the same layout.
public sealed class ComputersPage : Page
{
    readonly ScrollViewer scroll = Ui.Scroll(new Border());
    // The end of the last search that this page saw, or null.
    DateTimeOffset? lastSearch;
    bool wasSearching;
    readonly DispatcherTimer clock = new() { Interval = TimeSpan.FromSeconds(30) };

    public ComputersPage(AppModel model) : base(model)
    {
        Watch(Batteries.Model(model));
        clock.Tick += (_, _) => Invalidate();
        Loaded += (_, _) => clock.Start();
        Unloaded += (_, _) => clock.Stop();
    }

    public override string Title => "Computers";

    public override string Subtitle
    {
        get
        {
            var online = Shared.OnlineText(Model);
            var scoped = Model.Paired.Count == 0 ? "" : $"{Shared.ScopeName(Model)} in scope";
            return Texts.ContextLine(online.Length == 0 ? "No computer is paired" : online, scoped);
        }
    }

    protected override UIElement Build()
    {
        var tn = Tn.Current;
        var searching = Model.State.Searching;
        if (wasSearching && !searching)
        {
            lastSearch = DateTimeOffset.Now;
        }
        wasSearching = searching;

        var items = new List<UIElement?> { ThisComputer() };
        items.Add(Parts.ListHeading("Paired"));
        if (Model.Paired.Count == 0)
        {
            items.Add(Ui.Text("No computer is paired. Pair one below. A paired computer connects by itself.", 13, color: tn.Sub, wrap: true).Margin(4, 0, 4, 0));
        }
        foreach (var d in Model.Paired)
        {
            items.Add(ComputerRow(d));
        }
        items.Add(Parts.ListHeading("Available"));
        foreach (var d in Model.Available)
        {
            items.Add(Shared.AvailableRow(d, () => Model.Push(new Route.Pairing(d.Id))));
        }
        if (Model.Available.Count == 0 && !searching)
        {
            items.Add(Ui.Text(Model.Paired.Count == 0
                ? "No computers found. Open Flux on the computer, and use the same network as this computer."
                : "No other computers found.", 13, color: tn.Sub, wrap: true).Margin(4, 0, 4, 0));
        }
        items.Add(Parts.ListHeading("Settings"));
        items.Add(Parts.ToolRow(Icons.Settings, "Settings", "Theme, sync, notifications, and agents", () => App.OpenSettings()));
        scroll.Content = Ui.Column(Ui.V(Metrics.Gap, items));

        var dock = new DockPanel();
        var line = Parts.ProgressLine(slide: searching, color: tn.Yellow);
        DockPanel.SetDock(line, Dock.Top);
        dock.Children.Add(line);
        var bottom = SearchDock(searching);
        DockPanel.SetDock(bottom, Dock.Bottom);
        dock.Children.Add(bottom);
        Ui.Detach(scroll);
        dock.Children.Add(scroll);
        return dock;
    }

    // This computer: its name, and whether computers can reach it.
    UIElement ThisComputer()
    {
        var tn = Tn.Current;
        var on = Model.State.Enabled;
        return Ui.H(12,
            Ui.Icon(Model.Core.Host.DeviceType == "laptop" ? Icons.Laptop : Icons.Desktop, 19, tn.Cyan, 26),
            Ui.V(2,
                Ui.Text(Model.State.DeviceName, 14, FontWeights.SemiBold),
                Ui.Text(on ? "Visible to computers on this network" : "Flux is off. Computers cannot reach this computer.", 12,
                    color: on ? tn.Sub : tn.Yellow, wrap: true)).Fill(),
            on ? null : new FluxButton("Turn on Flux", ButtonKind.Outlined, () => Model.Core.Enabled = true)).Margin(4, 10, 4, 2);
    }

    // A paired computer: its link, its address and battery, and a note while
    // it is offline. A click sets the scope to it, or back to all computers
    // when it is in scope. The border is 2 px accent in scope. The chevron
    // opens its page, and the menu holds Retry and Unpair.
    UIElement ComputerRow(DeviceSnapshot d)
    {
        var tn = Tn.Current;
        var inScope = Model.Scope == d.Id;
        var battery = Batteries.Of(Model, d.Id);
        var link = d.Online ? (Texts.IsTailscale(d.Ip) ? "Connected · Tailscale" : "Connected · local network") : "Offline";
        var address = Texts.ContextLine(d.Ip, battery is { } b ? $"battery {b.Charge}%" + (b.Charging ? ", charging" : "") : "");
        var info = Ui.V(4,
            Ui.H(8, Ui.Text(d.Name, 15, FontWeights.SemiBold).Fill(), inScope ? Ui.Text("In scope", 11, FontWeights.Medium, tn.Accent) : null),
            Ui.H(7, d.Online ? Parts.LinkDot(true) : Parts.RingMark(), Ui.Text(link, 12, color: tn.Sub)),
            address.Length > 0 ? Ui.Text(address, 11, color: tn.Sub, mono: true) : null,
            d.Online ? null : Ui.Text($"Check that Flux runs on {d.Name}, and that both are on the same network.", 12, color: tn.Sub, wrap: true));
        var main = Ui.H(12, Ui.Icon(Texts.Symbol(d), 20, d.Online ? tn.Accent : tn.Sub, 28).Top().Margin(0, 1, 0, 0), info.Fill());
        var scopeButton = new PlainButton(Ui.Box(main, 14, 12, 4, 12), () => Model.SetScope(inScope ? null : d.Id))
            .Named($"{d.Name}, {Texts.LinkText(d, battery?.Charge, battery?.Charging == true)}")
            .Help(inScope ? "Show all computers" : $"Show only {d.Name}");
        var open = new PlainButton(Ui.Icon(Icons.ChevronRight, 13, tn.Sub).Width(32).Height(32), () => Model.Push(new Route.Computer(d.Id)))
            .Named($"Open the page of {d.Name}").Help($"Open the page of {d.Name}");
        open.VerticalAlignment = VerticalAlignment.Top;
        open.Margin = new Thickness(0, 12, 8, 0);
        var tile = Ui.Tile(Ui.H(4, scopeButton.Fill(), open), tn.Tile, inScope ? tn.Accent : tn.Line, 0, borderWidth: inScope ? 2 : 1);
        tile.MinHeight = 56;
        var menu = new ContextMenu();
        menu.Items.Add(MenuItem(inScope ? "Show all computers" : $"Show only {d.Name}", () => Model.SetScope(inScope ? null : d.Id)));
        menu.Items.Add(MenuItem($"Open the page of {d.Name}", () => Model.Push(new Route.Computer(d.Id))));
        if (!d.Online)
        {
            menu.Items.Add(MenuItem("Retry", Model.Retry));
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(MenuItem($"Unpair {d.Name}…", () => Shared.ConfirmUnpair(Model, d, this)));
        tile.ContextMenu = menu;
        return tile;
    }

    static MenuItem MenuItem(string header, Action run)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => run();
        return item;
    }

    // The dock of Computers: the search that runs, or the number of computers
    // to pair and the time of the last search, with Search again.
    UIElement SearchDock(bool searching)
    {
        var tn = Tn.Current;
        var available = Model.Available.Count;
        UIElement mark = searching ? Parts.Spinner(15, tn.Yellow) : Ui.Icon(Icons.Wifi, 15, tn.Sub);
        var title = searching ? "Looking for computers" : available switch
        {
            0 => "No computer to pair",
            1 => "1 computer to pair",
            _ => $"{available} computers to pair",
        };
        var line = searching || lastSearch is null ? "on this network" : "last search " + Texts.Ago(lastSearch.Value, DateTimeOffset.Now);
        var row = Ui.H(12,
            Ui.Box(mark, 0, 0, 0, 0).Width(20),
            Ui.V(2, Ui.Text(title, 13, FontWeights.SemiBold), Ui.Text(line, 10.5, color: tn.Sub, mono: true)).Fill(),
            new FluxButton("Search again", ButtonKind.Tonal, Model.Retry, enabled: !searching)).Margin(4, 0, 4, 0).MinHeight(40);
        return Parts.PageDock(row);
    }
}

// The parts that more pages use.
public static class Shared
{
    // "2 of 3 online" for the paired computers, or "" when none is paired.
    public static string OnlineText(AppModel model)
    {
        var paired = model.Paired;
        return paired.Count == 0 ? "" : $"{paired.Count(d => d.Online)} of {paired.Count} online";
    }

    // The name of the computer in scope, or "all computers".
    public static string ScopeName(AppModel model) => model.PairedDevice(model.Scope)?.Name ?? "all computers";

    // A computer to pair: a dashed yellow border. A click opens the pairing page.
    public static UIElement AvailableRow(DeviceSnapshot d, Action open)
    {
        var tn = Tn.Current;
        var incoming = d.PairState == PairState.Incoming;
        var line = incoming ? "Wants to pair. Compare the key."
            : d.PairState == PairState.Requested ? "Waiting for the computer"
            : d.Online ? "Select to pair" : "Offline";
        var row = Ui.H(12,
            Ui.Icon(incoming ? Icons.Error : Icons.Add, 17, tn.Yellow, 28),
            Ui.V(2, Ui.Text(d.Name, 15, FontWeights.SemiBold), Ui.Text(line, 12, color: tn.Yellow)).Fill());
        var dash = new System.Windows.Shapes.Rectangle
        {
            Stroke = tn.Yellow,
            StrokeThickness = 1.5,
            StrokeDashArray = [4, 3.3],
            RadiusX = Metrics.TileCorner,
            RadiusY = Metrics.TileCorner,
        };
        var grid = new Grid { MinHeight = 52 };
        grid.Children.Add(new Border { Background = tn.Bg, CornerRadius = new CornerRadius(Metrics.TileCorner) });
        grid.Children.Add(dash);
        grid.Children.Add(Ui.Box(row, 14, 10, 14, 10));
        return new PlainButton(grid, open).Named($"{d.Name}, {line}").Help("Opens the pairing page");
    }

    // The search row: a spinner while Flux searches, else the result and Search again.
    public static UIElement ScanRow(AppModel model, bool first, bool noneFound, string hint)
    {
        var tn = Tn.Current;
        if (model.State.Searching)
        {
            return Ui.H(10, Parts.Spinner(), Ui.Text("Looking for computers", 13, color: tn.Sub)).MinHeight(32).Margin(4, 0, 4, 0);
        }
        return Ui.V(4,
            noneFound ? Ui.Text(first ? "No computers found" : "No other computers found", 13, FontWeights.SemiBold) : null,
            noneFound ? Ui.Text(hint, 12, color: tn.Sub, wrap: true) : null,
            new FluxButton("Search again", ButtonKind.Text, model.Retry, Icons.Refresh).Margin(-8, 0, 0, 0)).Margin(4, 0, 4, 0);
    }

    // Asks before an unpair, then unpairs.
    public static void ConfirmUnpair(AppModel model, DeviceSnapshot d, DependencyObject from)
    {
        if (Dialogs.Confirm(Window.GetWindow(from), $"Unpair {d.Name}?", model.UnpairMessage(d), "Unpair", destructive: true))
        {
            model.Unpair(d.Id);
        }
    }
}
