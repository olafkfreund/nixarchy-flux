using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;

namespace Flux;

// The scope in the header: the name of the computer in scope or "All
// computers", with a link dot and a battery dot for each computer, at most 4.
// A click opens the choices. The scope filters the chat and Control, and it
// picks the theme of the Computer setting. ScopeMenu.swift of the Mac app has
// the same rules.
public sealed class ScopeMenu : View
{
    readonly AppModel model;
    readonly Popup popup = new() { StaysOpen = false, AllowsTransparency = true, Placement = PlacementMode.Bottom };

    public ScopeMenu(AppModel model)
    {
        this.model = model;
        Watch(model);
        Watch(Batteries.Model(model));
    }

    protected override UIElement Build()
    {
        var tn = Tn.Current;
        var paired = model.Paired;
        var current = model.PairedDevice(model.Scope);
        var name = current?.Name ?? "All computers";
        var shown = current is not null ? [current] : paired.Take(4).ToArray();
        var marks = new List<UIElement?> { Ui.Text(name, 13, FontWeights.SemiBold) };
        foreach (var d in shown)
        {
            var battery = Batteries.Of(model, d.Id);
            marks.Add(Ui.H(3, Parts.LinkDot(d.Online), d.Online && battery is { } b ? Parts.BatteryDot(b.Charge, b.Charging) : null));
        }
        marks.Add(Ui.Icon(Icons.ChevronDown, 10, tn.Sub));
        var chip = new Border
        {
            Child = Ui.H(8, marks),
            Background = tn.Tile,
            BorderBrush = tn.Line,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(Metrics.ChipCorner),
            Padding = new Thickness(10, 0, 8, 0),
            MinHeight = Metrics.ButtonHeight,
        };
        var button = new PlainButton(chip, Open).Named("Scope: " + name).Help("Change the scope");
        button.HorizontalAlignment = HorizontalAlignment.Right;
        popup.PlacementTarget = button;
        return button;
    }

    void Open()
    {
        var tn = Tn.Current;
        var rows = new List<UIElement?>
        {
            Row(null, "All computers", model.Paired.Count == 1 ? "1 paired computer" : $"{model.Paired.Count} paired computers"),
        };
        foreach (var d in model.Paired)
        {
            var battery = Batteries.Of(model, d.Id);
            rows.Add(Row(d.Id, d.Name, Texts.LinkText(d, battery?.Charge, battery?.Charging == true)));
        }
        popup.Child = new Border
        {
            Child = Ui.V(2, rows),
            Background = tn.TileHi,
            BorderBrush = tn.LineHi,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(Metrics.ChipCorner),
            Padding = new Thickness(6),
            Width = 280,
        };
        popup.IsOpen = true;
    }

    UIElement Row(string? id, string name, string detail)
    {
        var tn = Tn.Current;
        var selected = model.Scope == id;
        var check = Ui.Icon(Icons.Check, 12, tn.Accent, 14);
        check.Opacity = selected ? 1 : 0;
        var body = Ui.H(10, check, Ui.V(1,
            Ui.Text(name, 13, selected ? FontWeights.SemiBold : FontWeights.Normal),
            Ui.Text(detail, 11, color: tn.Sub)).Fill());
        var tile = new Border
        {
            Child = body,
            Background = selected ? tn.AccentTile : Brushes.Transparent,
            CornerRadius = new CornerRadius(Metrics.SmallCorner),
            Padding = new Thickness(8, 6, 8, 6),
            MinHeight = Metrics.RowHeight,
        };
        return new PlainButton(tile, () =>
        {
            model.SetScope(id);
            popup.IsOpen = false;
        }).Named($"{name}, {detail}");
    }
}
