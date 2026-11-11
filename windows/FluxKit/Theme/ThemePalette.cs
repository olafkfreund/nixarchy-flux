namespace FluxKit;

// The colors of 1 Flux palette, as 0xRRGGBB. Of makes a palette from the
// theme of a computer, and TokyoNight and TokyoNightDay are the palettes
// without one. Roles gives each color as an Rgb value for the brushes of the
// WPF app. ThemePalette.swift of the Mac app has the same rules.
//
// The contrast contract:
// - Text and Sub reach 4.5:1 on each of the Containers: the Surfaces, Line,
//   and AccentTile.
// - The Fills, from Accent to Yellow, reach 4.5:1 as text on the Surfaces and
//   AccentTile. On Line, they reach 3:1, so use them there for icons and
//   borders only.
// - OnAccent reaches 4.5:1 on each fill.
// - Dim reaches 3:1 on Bg, Tile, and TileHi. It is not for text.
// - TermBlue reaches 4.5:1 on the Surfaces.
// - Each Border color reaches 3:1 on Bg and Tile.
public sealed record ThemePalette(
    // The name of the theme, for example "tokyo-night".
    string Name,
    bool Dark,
    // The page.
    int Bg,
    // A tile that is off, and the background of the agent output.
    int OffTile,
    int Tile,
    // A tile that stands out, a menu, or a dialog.
    int TileHi,
    // The border of a tile, and a tonal container for Text and Sub.
    int Line,
    int LineHi,
    // The body ink. It is at least TextStep away from Sub.
    int Text,
    // The second ink, for hints, labels, and placeholders.
    int Sub,
    // For borders and disabled states only.
    int Dim,
    // The ink on a fill of Accent or a semantic color.
    int OnAccent,
    // The primary color, for actions and selection.
    int Accent,
    // A tile with the hue of Accent and the luminance of TileHi, for a
    // selected item.
    int AccentTile,
    int Cyan,
    int Green,
    int Magenta,
    int Orange,
    // Red means "needs you" or an error, nothing else.
    int Red,
    int Yellow,
    // The blue of the terminal, for ANSI blue in the agent output. The Accent
    // can be another color.
    int TermBlue,
    // The colors of the active border gradient.
    ValueList<int> Border,
    // The angle of the border gradient in degrees, as in Hyprland, or null
    // for corner to corner.
    double? BorderAngle)
{
    // The surfaces that the tiles and the page use.
    public IReadOnlyList<int> Surfaces => [Bg, OffTile, Tile, TileHi];

    // Each color that holds Text and Sub: the Surfaces, Line, and AccentTile.
    public IReadOnlyList<int> Containers => [.. Surfaces, Line, AccentTile];

    // The fills that take OnAccent text, in this order.
    public IReadOnlyList<int> Fills => [Accent, Cyan, Green, Magenta, Orange, Red, Yellow];

    // Each color role by its name, in a fixed order. The WPF app makes 1
    // brush for each role.
    public IReadOnlyList<(string Role, Rgb Color)> Roles =>
    [
        ("Bg", Rgb.Of(Bg)), ("OffTile", Rgb.Of(OffTile)), ("Tile", Rgb.Of(Tile)), ("TileHi", Rgb.Of(TileHi)),
        ("Line", Rgb.Of(Line)), ("LineHi", Rgb.Of(LineHi)), ("Text", Rgb.Of(Text)), ("Sub", Rgb.Of(Sub)),
        ("Dim", Rgb.Of(Dim)), ("OnAccent", Rgb.Of(OnAccent)), ("Accent", Rgb.Of(Accent)),
        ("AccentTile", Rgb.Of(AccentTile)), ("Cyan", Rgb.Of(Cyan)), ("Green", Rgb.Of(Green)),
        ("Magenta", Rgb.Of(Magenta)), ("Orange", Rgb.Of(Orange)), ("Red", Rgb.Of(Red)), ("Yellow", Rgb.Of(Yellow)),
        ("TermBlue", Rgb.Of(TermBlue)),
    ];

    // The colors of the border gradient as Rgb values.
    public IReadOnlyList<Rgb> BorderColors => Border.Select(Rgb.Of).ToArray();

    // The contrast that text needs, WCAG 2.2 AA.
    public const double TextContrast = 4.5;

    // The contrast that icons, borders, and large text need, WCAG 2.2 AA.
    public const double NonTextContrast = 3.0;

    // The least contrast of Text against Sub, so that the 2 inks look different.
    public const double TextStep = 1.4;

    // The least OKLAB distance of the accent from red. Below it, the accent
    // looks like red.
    public const double MinAccentDistance = 0.08;

    // The contrast of a surface step against the page: the least, the most,
    // and the value to make.
    readonly record struct Step(double Least, double Most, double Make);

    // 1 choice of the accent: the Source color of the theme, the AccentTile
    // that it tints, the Needs of a fill as text with that tile, and the
    // guarded Accent and Red.
    sealed record AccentTrial(int Source, int AccentTile, IReadOnlyList<ContrastNeed> Needs, int Accent, int Red)
    {
        public double Apart => ColorMath.Distance(Accent, Red);
    }

    // How much of the accent the accent tile takes.
    const double AccentTileMix = 0.18;

    // The least chroma of a color that replaces the accent, so that it is not
    // a gray.
    const double MinAccentChroma = 0.05;

    // Below this background luminance, a theme is dark.
    const double DarkLuminance = 0.179;

    // The contrast of white on a dark page, or of black on a light page, at
    // least.
    const double BgHeadroom = 10.0;

    // The same for a tile, so that the text and the colors can reach their
    // contrast.
    const double TileHeadroom = 7.0;

    // The same for Line, which holds text.
    const double LineHeadroom = 6.0;

    static readonly Step TileDark = new(1.06, 1.25, 1.12);
    static readonly Step HiDark = new(1.10, 1.40, 1.22);
    static readonly Step TileLight = new(1.04, 1.12, 1.07);
    static readonly Step HiLight = new(1.08, 1.20, 1.13);
    const double LineMix = 0.08;
    const double LineHiMix = 0.22;
    const double SubMix = 0.3;
    const double DimMix = 0.55;

    // The most chroma of the body ink in a light theme, so that it reads as a
    // neutral.
    const double NeutralChroma = 0.045;

    // The theme colors that can replace an accent that looks like red, in order.
    static readonly string[] AccentStandins = ["blue", "bright_blue", "cyan", "bright_cyan", "magenta", "bright_magenta"];

    // A fixed palette. Only AccentTile is computed.
    static ThemePalette Spec(string name, bool dark, int bg, int offTile, int tile, int tileHi, int line, int lineHi,
        int text, int sub, int dim, int onAccent, int accent, int cyan, int green, int magenta, int orange, int red, int yellow) =>
        new(name, dark, bg, offTile, tile, tileHi, line, lineHi, text, sub, dim, onAccent, accent,
            ColorMath.Tint(tileHi, accent, AccentTileMix), cyan, green, magenta, orange, red, yellow, accent,
            ValueList.From(new[] { accent, cyan }), null);

    // Tokyo Night, the default Omarchy theme. The tiles are lighter than the
    // page.
    public static readonly ThemePalette TokyoNight = Spec(
        "tokyo-night", true,
        bg: 0x16161E, offTile: 0x1A1B26, tile: 0x1F2335, tileHi: 0x24283B, line: 0x292E42, lineHi: 0x3B4261,
        text: 0xC0CAF5, sub: 0x8B94BE, dim: 0x66709B, onAccent: 0x16161E,
        accent: 0x7AA2F7, cyan: 0x7DCFFF, green: 0x9ECE6A, magenta: 0xBB9AF7, orange: 0xFF9E64, red: 0xF7768E,
        yellow: 0xE0AF68);

    // Tokyo Night Day. The tiles are darker steps of the page, and the body
    // ink is a neutral slate.
    public static readonly ThemePalette TokyoNightDay = Spec(
        "tokyo-night-day", false,
        bg: 0xE1E2E7, offTile: 0xDDDEE4, tile: 0xD9DBE1, tileHi: 0xD4D5DC, line: 0xC4C8DA, lineHi: 0xA8AECB,
        text: 0x343B58, sub: 0x44518A, dim: 0x70769E, onAccent: 0xFFFFFF,
        accent: 0x2457B8, cyan: 0x006486, green: 0x496529, magenta: 0x7B31CF, orange: 0x914A00, red: 0xBA0046,
        yellow: 0x765729);

    static ContrastNeed[] Needs(double ratio, IEnumerable<int> colors) => colors.Select(c => new ContrastNeed(c, ratio)).ToArray();

    // Makes the palette of a computer theme through the contrast guard. The
    // background decides whether the theme is dark. The mode of the theme
    // counts only when the background is missing. The guard moves only the
    // lightness of a color, so the hue stays. It runs the guard about 30
    // times, so do not call it while the UI draws.
    public static ThemePalette Of(OmarchyTheme theme)
    {
        var rawBg = theme["background"];
        var dark = rawBg is { } b ? ColorMath.Luminance(b) < DarkLuminance : theme.Dark ?? true;
        var basePalette = dark ? TokyoNight : TokyoNightDay;
        var extreme = dark ? 0xFFFFFF : 0x000000;
        int Surface(int c, double headroom) => ColorMath.Guarded(c, [new ContrastNeed(extreme, headroom)], lighter: !dark);
        var bg = Surface(rawBg ?? basePalette.Bg, BgHeadroom);
        var bgLum = ColorMath.Luminance(bg);
        bool InkSide(int c) => dark ? ColorMath.Luminance(c) > bgLum : ColorMath.Luminance(c) < bgLum;
        var fg = theme["foreground"] ?? basePalette.Text;
        // The surfaces step toward the ink. A foreground on the wrong side
        // gives no direction.
        var ink = InkSide(fg) ? fg : extreme;
        int StepOf(int? candidate, Step s)
        {
            if (candidate is { } c && InkSide(c))
            {
                var r = ColorMath.Contrast(bg, c);
                if (r >= s.Least && r <= s.Most)
                {
                    return c;
                }
                if (r > s.Most)
                {
                    return ColorMath.StepTo(bg, c, s.Most);
                }
            }
            return ColorMath.StepTo(bg, ink, s.Make);
        }
        int rawTile;
        int rawHi;
        if (dark)
        {
            rawHi = StepOf(theme["lighter_background"], HiDark);
            rawTile = StepOf(ColorMath.Mix(bg, rawHi, 0.6), TileDark);
        }
        else
        {
            rawTile = StepOf(theme["dark_background"], TileLight);
            var hi = StepOf(theme["darker_background"], HiLight);
            rawHi = ColorMath.Luminance(hi) < ColorMath.Luminance(rawTile) ? hi : StepOf(null, HiLight);
        }
        var tile = Surface(rawTile, TileHeadroom);
        var tileHi = Surface(rawHi, TileHeadroom);
        var offTile = Surface(ColorMath.Mix(bg, tile, 0.5), TileHeadroom);
        var line = Surface(ColorMath.Mix(tileHi, ink, LineMix), LineHeadroom);
        var lineHi = ColorMath.Mix(tileHi, ink, LineHiMix);
        int[] surfaces = [bg, offTile, tile, tileHi];

        int Semantic(string key, int fallback) => theme[key] ?? theme["bright_" + key] ?? fallback;
        var themeAccent = theme["accent"] ?? theme["blue"] ?? basePalette.Accent;
        var rawRed = Semantic("red", basePalette.Red);

        // The accent tile takes the hue of the accent, so the needs of each
        // ink depend on the accent.
        AccentTrial Trial(int source)
        {
            var accentTile = ColorMath.Tint(tileHi, source, AccentTileMix);
            ContrastNeed[] n = [.. Needs(TextContrast, [.. surfaces, accentTile]), new ContrastNeed(line, NonTextContrast)];
            return new AccentTrial(source, accentTile, n,
                ColorMath.Guarded(source, n, lighter: dark),
                ColorMath.Guarded(rawRed, n, lighter: dark));
        }
        int[] others = [.. AccentStandins.Select(k => theme[k]).OfType<int>(), basePalette.Accent];
        var chosen = PickAccent(Trial(themeAccent), others, Trial);
        var accentTile = chosen.AccentTile;
        var fillNeeds = chosen.Needs;
        var inkNeeds = Needs(TextContrast, [.. surfaces, line, accentTile]);

        var body = dark ? fg : ColorMath.LimitChroma(fg, NeutralChroma);
        var firstText = ColorMath.Guarded(body, inkNeeds, lighter: dark);
        var sub = ColorMath.Guarded(ColorMath.Mix(firstText, bg, SubMix), inkNeeds, lighter: dark);
        var text = ColorMath.Guarded(firstText, [.. inkNeeds, new ContrastNeed(sub, TextStep)], lighter: dark);
        var dim = ColorMath.Guarded(theme["muted"] ?? ColorMath.Mix(text, bg, DimMix), Needs(NonTextContrast, [bg, tile, tileHi]), lighter: dark);

        int[] sources =
        [
            chosen.Source, Semantic("cyan", basePalette.Cyan), Semantic("green", basePalette.Green),
            Semantic("magenta", basePalette.Magenta), Semantic("orange", basePalette.Orange), rawRed,
            Semantic("yellow", basePalette.Yellow),
        ];
        var colors = sources.Select(c => ColorMath.Guarded(c, fillNeeds, lighter: dark)).ToArray();

        // The ink on a fill: the light or the dark ink, whichever reads better
        // on the accent.
        var lightInk = ColorMath.Luminance(text) > ColorMath.Luminance(bg) ? text : bg;
        var darkInk = lightInk == text ? bg : text;
        var light = ColorMath.Contrast(colors[0], lightInk) >= ColorMath.Contrast(colors[0], darkInk);
        var onAccent = ColorMath.Guarded(light ? lightInk : darkInk, Needs(TextContrast, colors), lighter: light);
        ContrastNeed[] onAccentNeeds = [.. fillNeeds, new ContrastNeed(onAccent, TextContrast)];
        colors = colors.Select(c => ColorMath.Contrast(c, onAccent) >= TextContrast ? c : ColorMath.Guarded(c, onAccentNeeds, lighter: !light)).ToArray();

        var termBlue = ColorMath.Guarded(theme["blue"] ?? theme["bright_blue"] ?? basePalette.TermBlue, Needs(TextContrast, surfaces), lighter: dark);
        var borderNeeds = Needs(NonTextContrast, [bg, tile]);
        // The theme accent stays in the gradient, so that the theme stays
        // recognizable.
        var ownAccent = chosen.Source == themeAccent ? colors[0] : ColorMath.Guarded(themeAccent, fillNeeds, lighter: dark);
        IReadOnlyList<int> borderSource = theme.Border.Count == 0 ? [ownAccent, colors[1]] : theme.Border;
        var border = ValueList.From(borderSource.Select(c => ColorMath.Guarded(c, borderNeeds, lighter: dark)));
        return new ThemePalette(theme.Name, dark, bg, offTile, tile, tileHi, line, lineHi, text, sub, dim, onAccent,
            colors[0], accentTile, colors[1], colors[2], colors[3], colors[4], colors[5], colors[6], termBlue, border,
            theme.Border.Count == 0 ? null : theme.BorderAngle);
    }

    // Keeps the accent apart from red. When first is too close to red, it
    // takes the first of others that has a visible chroma, reaches its
    // contrast, and is far enough from red. When no color is far enough, it
    // takes the color that is farthest from red.
    static AccentTrial PickAccent(AccentTrial first, IReadOnlyList<int> others, Func<int, AccentTrial> trial)
    {
        if (first.Apart >= MinAccentDistance)
        {
            return first;
        }
        var best = first;
        foreach (var c in others)
        {
            var t = trial(c);
            if (ColorMath.ToOklch(t.Accent).C < MinAccentChroma || !ColorMath.Meets(t.Accent, t.Needs))
            {
                continue;
            }
            if (t.Apart >= MinAccentDistance)
            {
                return t;
            }
            if (t.Apart > best.Apart)
            {
                best = t;
            }
        }
        return best;
    }
}
