using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Flux;

// The parts of the shell kit: TiledKit.swift and PageKit.swift of the Mac
// app, in the sizes of the Windows app.
public static class Parts
{
    // The large tool at the top of Control: an icon at the top, the label
    // and its line at the bottom, on tile-hi.
    public static PlainButton MasterTool(string glyph, string title, string line, Action onClick, bool enabled = true)
    {
        var tn = Tn.Current;
        var body = new Grid { MinHeight = 80 };
        body.Children.Add(Ui.Icon(glyph, 24, enabled ? tn.Accent : tn.Sub).Align(HorizontalAlignment.Left, VerticalAlignment.Top));
        body.Children.Add(Ui.V(3,
            Ui.Text(title, 17, FontWeights.SemiBold),
            Ui.Text(line, 13, color: tn.Sub, wrap: true)).Align(HorizontalAlignment.Stretch, VerticalAlignment.Bottom).Margin(0, 38, 0, 0));
        var tile = Ui.Tile(body, tn.TileHi, tn.LineHi, 16);
        var b = new PlainButton(tile, onClick) { IsEnabled = enabled };
        return b.Named(title);
    }

    // A tool row: an icon, a label, a line, and a badge of the items that
    // need the user.
    public static Border ToolRowLabel(string glyph, string title, string line, int badge = 0, bool enabled = true)
    {
        var tn = Tn.Current;
        var row = Ui.H(12,
            Ui.Icon(glyph, 17, enabled ? tn.Accent : tn.Sub, 24),
            Ui.V(2, Ui.Text(title, 13, FontWeights.SemiBold), Ui.Text(line, 11, color: tn.Sub, wrap: true)).Fill(),
            badge > 0 ? NeedsBadge(badge) : null);
        var tile = Ui.Tile(row, tn.Tile, tn.Line, 0);
        tile.Padding = new Thickness(14, 8, 14, 8);
        tile.MinHeight = 44;
        return tile;
    }

    public static PlainButton ToolRow(string glyph, string title, string line, Action onClick, int badge = 0, bool enabled = true)
    {
        var b = new PlainButton(ToolRowLabel(glyph, title, line, badge, enabled), onClick) { IsEnabled = enabled };
        return b.Named(title).Help(line);
    }

    // A tool of a grid: an icon, a label, and a line.
    public static PlainButton GridTool(string glyph, string title, string line, Action onClick, bool enabled = true, bool small = false)
    {
        var tn = Tn.Current;
        UIElement body = small
            ? Ui.V(4, Ui.Icon(glyph, 18, enabled ? tn.Accent : tn.Sub), Ui.Text(title, 12, FontWeights.SemiBold, align: TextAlignment.Center, wrap: true)).MinHeight(56)
            : Ui.V(8, Ui.Icon(glyph, 18, enabled ? tn.Accent : tn.Sub).Left(),
                Ui.V(2, Ui.Text(title, 13, FontWeights.SemiBold), line.Length > 0 ? Ui.Text(line, 11, color: tn.Sub, wrap: true) : null)).MinHeight(76);
        var tile = Ui.Tile(body, tn.Tile, tn.Line, 12);
        var b = new PlainButton(tile, onClick) { IsEnabled = enabled };
        return b.Named(title).Help(line.Length > 0 ? line : title);
    }

    // Lays out the elements in rows of the column count, with the gap.
    public static Grid GridRows(IReadOnlyList<UIElement> items, int columns)
    {
        var grid = new Grid();
        for (var c = 0; c < columns; c++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }
        var rows = (items.Count + columns - 1) / columns;
        for (var r = 0; r < rows; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }
        for (var i = 0; i < items.Count; i++)
        {
            var e = items[i];
            Ui.Detach(e);
            if (e is FrameworkElement fe)
            {
                var col = i % columns;
                var row = i / columns;
                fe.Margin = new Thickness(col == 0 ? 0 : Metrics.Gap / 2, row == 0 ? 0 : Metrics.Gap / 2,
                                          col == columns - 1 ? 0 : Metrics.Gap / 2, row == rows - 1 ? 0 : Metrics.Gap / 2);
            }
            Grid.SetColumn(e, i % columns);
            Grid.SetRow(e, i / columns);
            grid.Children.Add(e);
        }
        return grid;
    }

    // The heading of a group of tools or rows.
    public static TextBlock SectionLabel(string text)
    {
        var t = Ui.Text(text, 13, FontWeights.Medium).Margin(4, 16, 0, 2);
        AutomationProperties.SetHeadingLevel(t, AutomationHeadingLevel.Level2);
        return t;
    }

    // The heading of a group of rows: the title, and the trailing text in mono.
    public static Stack ListHeading(string title, string trailing = "", UIElement? action = null)
    {
        var t = Ui.Text(title, 13, FontWeights.Medium);
        AutomationProperties.SetHeadingLevel(t, AutomationHeadingLevel.Level2);
        return Ui.H(8, t, Ui.Spacer(8), trailing.Length > 0 ? Ui.Text(trailing, 10.5, color: Tn.Current.Sub, mono: true) : null, action)
            .Margin(4, 12, 4, 2);
    }

    // The link state of a computer: a green dot when it is online, else a ring.
    public static Shape LinkDot(bool online, double size = 8) =>
        online ? Ui.Dot(Tn.Current.Green, size) : Ui.Ring(Tn.Current.Sub, size);

    // The mark of something that is off or paused: a ring.
    public static Shape RingMark(double size = 9) => Ui.Ring(Tn.Current.LineHi, size, 2);

    // The battery of a computer as a dot: green while it charges, orange at
    // 20% or less, yellow at 50% or less, else cyan. Never red.
    public static Shape BatteryDot(int charge, bool charging, double size = 8)
    {
        var tn = Tn.Current;
        var color = charging ? tn.Green : charge <= 20 ? tn.Orange : charge <= 50 ? tn.Yellow : tn.Cyan;
        return Ui.Dot(color, size);
    }

    // The count of the items that need the user, in on-accent on red.
    public static Border NeedsBadge(int count)
    {
        var b = new Border
        {
            Child = Ui.Text(count > 9 ? "9+" : count.ToString(System.Globalization.CultureInfo.InvariantCulture), 11,
                FontWeights.Bold, Tn.Current.OnAccent, mono: true),
            Background = Tn.Current.Red,
            CornerRadius = new CornerRadius(Metrics.SmallCorner),
            Padding = new Thickness(6, 1, 6, 1),
            VerticalAlignment = VerticalAlignment.Center,
        };
        return b.Named(count == 1 ? "1 needs you" : $"{count} need you");
    }

    // A command to run on the computer, with a copy button.
    public static Border CommandBlock(string command)
    {
        var tn = Tn.Current;
        FluxButton? copy = null;
        copy = new FluxButton("Copy", ButtonKind.Text, () =>
        {
            Clipboard.SetText(command);
            copy!.Content = Ui.Text("Copied", 13, FontWeights.SemiBold, tn.Green).Margin(8, 5, 8, 5);
        });
        var row = Ui.H(8, Ui.SelectableText(command, 12, color: tn.Text).Fill(), copy);
        var tile = Ui.Tile(row, tn.OffTile, tn.Line, 0, Metrics.SmallCorner);
        tile.Padding = new Thickness(12, 2, 2, 2);
        return tile;
    }

    // The spinner of the app: a ring of 3 quarters that turns, or stands
    // when Windows turns animations off.
    public static FrameworkElement Spinner(double size = 14, Brush? color = null)
    {
        var arc = new System.Windows.Shapes.Path
        {
            Stroke = color ?? Tn.Current.Accent,
            StrokeThickness = 2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            Width = size,
            Height = size,
            Stretch = Stretch.None,
            RenderTransformOrigin = new Point(0.5, 0.5),
        };
        var r = size / 2 - 1;
        var c = size / 2;
        arc.Data = new PathGeometry([new PathFigure(new Point(c, c - r),
            [new ArcSegment(new Point(c - r, c), new Size(r, r), 0, true, SweepDirection.Clockwise, true)], false)]);
        if (!Motion.Reduced)
        {
            var rotate = new RotateTransform();
            arc.RenderTransform = rotate;
            rotate.BeginAnimation(RotateTransform.AngleProperty,
                new DoubleAnimation(0, 360, new Duration(TimeSpan.FromSeconds(0.9))) { RepeatBehavior = RepeatBehavior.Forever });
        }
        return arc.Named("Busy");
    }

    // The line of 2 px under the header. With a fraction, the part in the
    // color shows the progress. With slide, a bar of 40% moves over it.
    public static Grid ProgressLine(double? fraction = null, Brush? color = null, bool slide = false)
    {
        var tn = Tn.Current;
        var grid = new Grid { Height = 2, ClipToBounds = true, Background = tn.Line };
        if (fraction is { } f)
        {
            var bar = new Border { Background = color ?? tn.Accent, HorizontalAlignment = HorizontalAlignment.Left };
            grid.SizeChanged += (_, e) => bar.Width = e.NewSize.Width * Math.Clamp(f, 0, 1);
            grid.Children.Add(bar);
        }
        else if (slide)
        {
            var bar = new Border { Background = color ?? tn.Accent, HorizontalAlignment = HorizontalAlignment.Left };
            var move = new TranslateTransform();
            bar.RenderTransform = move;
            grid.SizeChanged += (_, e) =>
            {
                bar.Width = e.NewSize.Width * 0.4;
                if (!Motion.Reduced)
                {
                    move.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-bar.Width, e.NewSize.Width,
                        new Duration(TimeSpan.FromSeconds(1.4))) { RepeatBehavior = RepeatBehavior.Forever });
                }
            };
            grid.Children.Add(bar);
        }
        return grid;
    }

    // The key that both sides show while they pair, in groups of 4 digits.
    public static Border KeyView(string key)
    {
        var tn = Tn.Current;
        var text = Ui.SelectableText(key.Length == 0 ? "---- ---- ---- ----" : FluxKit.Certificates.FormatKey(key), 26, color: tn.Text);
        text.FontWeight = FontWeights.SemiBold;
        text.TextWrapping = TextWrapping.NoWrap;
        text.HorizontalContentAlignment = HorizontalAlignment.Center;
        return new Border
        {
            Child = new Viewbox { Child = text, StretchDirection = StretchDirection.DownOnly, MaxHeight = 40 },
            Background = tn.Line,
            CornerRadius = new CornerRadius(Metrics.ChipCorner),
            Padding = new Thickness(20, 10, 20, 10),
            HorizontalAlignment = HorizontalAlignment.Center,
        }.Named("Key " + key);
    }

    // The frame of the dock at the bottom of a page: the tile color with
    // round corners at the top and a line along the top edge.
    public static Border PageDock(UIElement content, double top = 14)
    {
        var tn = Tn.Current;
        Ui.Detach(content);
        return new Border
        {
            Child = new Border { Child = content, MaxWidth = Metrics.MaxContentWidth, HorizontalAlignment = HorizontalAlignment.Stretch },
            Background = tn.Tile,
            BorderBrush = tn.Line,
            BorderThickness = new Thickness(1, 1, 1, 0),
            CornerRadius = new CornerRadius(20, 20, 0, 0),
            Padding = new Thickness(Metrics.Gutter, top, Metrics.Gutter, 10),
        };
    }

    // A message in the center of a page, such as "The computer is gone".
    public static Stack Unavailable(string glyph, string title, string text = "")
    {
        var tn = Tn.Current;
        return Ui.V(8,
            Ui.Icon(glyph, 32, tn.Sub),
            Ui.Text(title, 17, FontWeights.SemiBold, align: TextAlignment.Center),
            text.Length > 0 ? Ui.Text(text, 13, color: tn.Sub, wrap: true, align: TextAlignment.Center) : null)
            .Align(HorizontalAlignment.Center, VerticalAlignment.Center).Margin(40);
    }
}
