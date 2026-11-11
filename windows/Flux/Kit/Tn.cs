using System.Windows;
using System.Windows.Media;
using FluxKit;

namespace Flux;

// The palette of DESIGN.md as frozen WPF brushes. FluxKit fills the roles
// from the theme of the computer through the contrast guard. Each view takes
// its colors from Tn.
public sealed class ThemeColors
{
    public ThemePalette Palette { get; }
    public bool Dark => Palette.Dark;
    public SolidColorBrush Bg { get; }
    public SolidColorBrush OffTile { get; }
    public SolidColorBrush Tile { get; }
    public SolidColorBrush TileHi { get; }
    public SolidColorBrush Line { get; }
    public SolidColorBrush LineHi { get; }
    public SolidColorBrush Text { get; }
    public SolidColorBrush Sub { get; }
    public SolidColorBrush Dim { get; }
    public SolidColorBrush OnAccent { get; }
    public SolidColorBrush Accent { get; }
    public SolidColorBrush AccentTile { get; }
    public SolidColorBrush Cyan { get; }
    public SolidColorBrush Green { get; }
    public SolidColorBrush Magenta { get; }
    public SolidColorBrush Orange { get; }
    public SolidColorBrush Red { get; }
    public SolidColorBrush Yellow { get; }
    public SolidColorBrush TermBlue { get; }
    public IReadOnlyList<Color> Border { get; }

    public ThemeColors(ThemePalette p)
    {
        Palette = p;
        Bg = Brush(p.Bg);
        OffTile = Brush(p.OffTile);
        Tile = Brush(p.Tile);
        TileHi = Brush(p.TileHi);
        Line = Brush(p.Line);
        LineHi = Brush(p.LineHi);
        Text = Brush(p.Text);
        Sub = Brush(p.Sub);
        Dim = Brush(p.Dim);
        OnAccent = Brush(p.OnAccent);
        Accent = Brush(p.Accent);
        AccentTile = Brush(p.AccentTile);
        Cyan = Brush(p.Cyan);
        Green = Brush(p.Green);
        Magenta = Brush(p.Magenta);
        Orange = Brush(p.Orange);
        Red = Brush(p.Red);
        Yellow = Brush(p.Yellow);
        TermBlue = Brush(p.TermBlue);
        Border = p.Border.Select(ColorOf).ToArray();
    }

    public static Color ColorOf(int rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    static SolidColorBrush Brush(int rgb)
    {
        var b = new SolidColorBrush(ColorOf(rgb));
        b.Freeze();
        return b;
    }

    // The 2 px border of the pairing guide: the theme border gradient at its
    // angle, or a solid color with 1 stop.
    public Brush ActiveBorder()
    {
        if (Border.Count < 2)
        {
            return Border.Count == 1 ? new SolidColorBrush(Border[0]) : Accent;
        }
        var angle = Palette.BorderAngle ?? 45;
        var rad = angle * Math.PI / 180;
        var dx = Math.Cos(rad) / 2;
        var dy = Math.Sin(rad) / 2;
        var stops = new GradientStopCollection();
        for (var i = 0; i < Border.Count; i++)
        {
            stops.Add(new GradientStop(Border[i], (double)i / (Border.Count - 1)));
        }
        var brush = new LinearGradientBrush(stops, new Point(0.5 - dx, 0.5 - dy), new Point(0.5 + dx, 0.5 + dy));
        brush.Freeze();
        return brush;
    }
}

// The theme in effect. A change makes the open views draw again.
public static class Tn
{
    public static ThemeColors Current { get; private set; } = new(ThemePalette.TokyoNight);

    // Called on the UI thread after the palette changed.
    public static event Action? Changed;

    public static void Apply(ThemePalette palette)
    {
        if (palette == Current.Palette)
        {
            return;
        }
        Current = new ThemeColors(palette);
        Changed?.Invoke();
    }
}

// The type faces. Segoe UI sets what Flux says. Cascadia Mono sets what the
// computer says. Segoe Fluent Icons draws the icons, with the Windows 10 set
// as the fallback.
public static class Faces
{
    public static readonly FontFamily Body = new("Segoe UI Variable Text, Segoe UI");
    public static readonly FontFamily Mono = new("Cascadia Mono, Consolas");
    public static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");
}

// The numbers of the tiled layout, in device-independent pixels. They are
// the macOS values of DESIGN.md, because both are pointer desktops.
public static class Metrics
{
    public const double Gap = 8;
    public const double Gutter = 12;
    public const double TileCorner = 12;
    public const double SmallCorner = 8;
    public const double ChipCorner = 10;
    public const double MaxContentWidth = 840;
    public const double DisabledAlpha = 0.55;
    public const double RowHeight = 36;
    public const double ButtonHeight = 28;
}

// The 1 motion of the shell: 200 ms with the standard easing, or none when
// Windows turns animations off.
public static class Motion
{
    public static bool Reduced => !SystemParameters.ClientAreaAnimation;
    public static readonly Duration Standard = new(TimeSpan.FromMilliseconds(200));
}
